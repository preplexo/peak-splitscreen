using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Photon.Pun;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using Zorro.Core;
using PhotonPlayer = Photon.Realtime.Player;

namespace PeakSplitScreen
{
    /// <summary>
    /// Player 2's passport. The game has one passport screen that edits "the local player's"
    /// cosmetics and saves them to Steam. Here player 2 gets a passport item of their own, the
    /// screen opens over their half and edits player 2's cosmetics instead, the gamepad drives it,
    /// and the choices are saved in the mod's settings file (never in player 1's Steam stats).
    /// </summary>
    internal static class P2Passport
    {
        private const BindingFlags All = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        /// <summary>The passport screen was last opened by player 2.</summary>
        public static bool P2Owns;
        private static int _depth;
        private static bool _entered;
        private static int _closedFrame = -10;

        public static bool InScope => _depth > 0;
        public static bool OpenForP2 => P2Owns && PassportManager.instance != null && PassportManager.instance.isOpen;
        /// <summary>Player 2's pause press belongs to the passport right now, not to the pause menu.</summary>
        public static bool EatsPause => OpenForP2 || Time.frameCount - _closedFrame <= 2;

        public static void Apply(Harmony h)
        {
            Type me = typeof(P2Passport);
            Patch(h, AccessTools.Method(typeof(Action_Passport), "RunAction"), me, nameof(UsePrefix), null, null);
            Patch(h, AccessTools.Method(typeof(PassportManager), "ToggleOpen"), me, null, nameof(TogglePostfix), null);
            foreach (string name in new[] { "Initialize", "OpenTab", "SetButtons", "SetOption" })
                Patch(h, AccessTools.Method(typeof(PassportManager), name), me, nameof(ScopePrefix), null, nameof(ScopeFinalizer));
            Patch(h, AccessTools.Method(typeof(PassportManager), "SetOption"), me, null, nameof(OptionPostfix), null);

            Patch(h, AccessTools.Method(typeof(PersistentPlayerDataService), "GetPlayerData", new[] { typeof(PhotonPlayer) }), me, nameof(PlayerArgPrefix), null, null);
            Patch(h, AccessTools.Method(typeof(PersistentPlayerDataService), "SetPlayerData", new[] { typeof(PhotonPlayer), typeof(PersistentPlayerData) }), me, nameof(PlayerArgPrefix), null, null);
            Patch(h, AccessTools.Method(typeof(AchievementManager), "SetSteamStat", new[] { typeof(STEAMSTATTYPE), typeof(int) }), me, nameof(SteamPrefix), null, null);

            Patch(h, AccessTools.PropertyGetter(typeof(MenuWindow), "blocksPlayerInput"), me, null, nameof(BlocksPostfix), null);
            Patch(h, AccessTools.PropertyGetter(typeof(MenuWindow), "showCursorWhileOpen"), me, null, nameof(CursorPostfix), null);
            Patch(h, AccessTools.Method(typeof(MenuWindow), "TestCloseViaInput"), me, nameof(CloseInputPrefix), null, null);
        }

        private static void Patch(Harmony h, MethodBase m, Type me, string prefix, string postfix, string finalizer)
        {
            if (m == null) { Plugin.Log.LogWarning("Passport: method not found, not patched (" + (prefix ?? postfix ?? finalizer) + ")"); return; }
            try
            {
                h.Patch(m,
                    prefix: prefix != null ? new HarmonyMethod(me.GetMethod(prefix, All)) : null,
                    postfix: postfix != null ? new HarmonyMethod(me.GetMethod(postfix, All)) : null,
                    finalizer: finalizer != null ? new HarmonyMethod(me.GetMethod(finalizer, All)) : null);
            }
            catch (Exception e) { Plugin.Log.LogWarning("Passport: could not patch " + m.DeclaringType.Name + "." + m.Name + ": " + e.Message); }
        }

        // ------------------------------------------------------------------ patches

        /// <summary>Whoever holds the passport item owns the screen; the other player cannot close it.</summary>
        private static bool UsePrefix(Action_Passport __instance)
        {
            PassportManager pm = PassportManager.instance;
            if (pm == null) return true;
            Character holder = __instance.item != null ? __instance.item.holderCharacter : null;
            bool p2 = SplitManager.Active && holder != null && ReferenceEquals(holder, SplitManager.P2Character);
            if (pm.isOpen || pm.closing) return p2 == P2Owns;
            P2Owns = p2;
            return true;
        }

        private static void TogglePostfix(PassportManager __instance)
        {
            if (!__instance.isOpen) { if (P2Owns) _closedFrame = Time.frameCount; return; }
            try { OnOpened(__instance); }
            catch (Exception e) { Plugin.Log.LogError("Passport open: " + e); }
        }

        private static void ScopePrefix()
        {
            if (!P2Owns || !SplitManager.Active) return;
            if (_depth++ == 0) _entered = SplitContext.Enter();
        }

        private static Exception ScopeFinalizer(Exception __exception)
        {
            if (_depth > 0 && --_depth == 0 && _entered) { _entered = false; SplitContext.Exit(); }
            return __exception;
        }

        private static void OptionPostfix()
        {
            if (P2Owns && SplitManager.Active) Save();
        }

        /// <summary>Inside the passport, "the local player" means player 2.</summary>
        private static void PlayerArgPrefix(ref PhotonPlayer player)
        {
            if (_depth > 0 && SplitManager.P2Photon != null && player != null && ReferenceEquals(player, PhotonNetwork.LocalPlayer))
                player = SplitManager.P2Photon;
        }

        /// <summary>Player 2's cosmetics must not overwrite player 1's saved ones.</summary>
        private static bool SteamPrefix() => _depth == 0;

        private static void BlocksPostfix(MenuWindow __instance, ref bool __result)
        {
            if (!__result || !SplitManager.Active || !(__instance is PassportManager)) return;
            __result = P2Owns == SplitContext.Active;   // only the owner stands still
        }

        private static void CursorPostfix(MenuWindow __instance, ref bool __result)
        {
            if (__result && SplitManager.Active && P2Owns && __instance is PassportManager) __result = false;
        }

        private static bool CloseInputPrefix(MenuWindow __instance)
        {
            return !(SplitManager.Active && P2Owns && __instance is PassportManager);
        }

        // ------------------------------------------------------------------ per-frame work

        private static PhotonPlayer _loadedFor;
        private static Character _seen;
        private static float _seenAt;
        private static Character _givenTo;
        private static string _passportName;
        private static bool _wasActive;

        public static void Tick()
        {
            if (!SplitManager.Active)
            {
                if (_wasActive)
                {
                    _wasActive = false;
                    PassportManager pm0 = PassportManager.instance;
                    if (P2Owns && pm0 != null && pm0.isOpen) pm0.Close();
                    P2Owns = false;
                    Unselect();
                    Layout(PassportManager.instance, false);
                }
                return;
            }
            _wasActive = true;

            if (!ReferenceEquals(_loadedFor, SplitManager.P2Photon) && SplitManager.P2Photon != null)
            {
                PersistentPlayerDataService svc = GameHandler.GetService<PersistentPlayerDataService>();
                if (svc != null && Singleton<Customization>.Instance != null)
                {
                    _loadedFor = SplitManager.P2Photon;
                    Load(svc);
                }
            }

            GivePassport();

            PassportManager pm = PassportManager.instance;
            if (pm != null && pm.isOpen && P2Owns) Navigate(pm);
            else if (_selected != null) Unselect();
        }

        /// <summary>The lobby hands player 1 a passport; do the same for player 2.</summary>
        private static void GivePassport()
        {
            Character c = SplitManager.P2Character;
            if (!ReferenceEquals(c, _seen))
            {
                _seen = c;
                _seenAt = Time.time;
                SpawnItemInHand giver = UnityEngine.Object.FindAnyObjectByType<SpawnItemInHand>();
                _passportName = giver != null && giver.item != null ? giver.item.gameObject.name : null;
            }
            if (_passportName == null || ReferenceEquals(_givenTo, c)) return;
            if (Time.time - _seenAt < 3f || c.data == null || c.data.dead || c.data.passedOut) return;
            _givenTo = c;
            if (c.data.currentItem != null) return;
            bool entered = SplitContext.Enter();
            try { c.refs.items.SpawnItemInHand(_passportName); }
            catch (Exception e) { Plugin.Log.LogWarning("Could not give player 2 a passport: " + e.Message); }
            finally { if (entered) SplitContext.Exit(); }
            Plugin.Log.LogInfo("Gave player 2 a passport (" + _passportName + ").");
        }

        // ------------------------------------------------------------------ saved cosmetics

        private static readonly Customization.Type[] Order =
        {
            Customization.Type.Skin, Customization.Type.Accessory, Customization.Type.Eyes, Customization.Type.Mouth,
            Customization.Type.Fit, Customization.Type.Hat, Customization.Type.Sash, Customization.Type.Medal,
        };

        private static void Load(PersistentPlayerDataService svc)
        {
            Customization all = Singleton<Customization>.Instance;
            int[] v = new int[Order.Length];
            string[] parts = (Plugin.P2Cosmetics.Value ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            bool saved = parts.Length == Order.Length;
            for (int i = 0; i < Order.Length; i++)
            {
                int n = -1;
                if (saved && !int.TryParse(parts[i].Trim(), out n)) n = -1;
                CustomizationOption[] list = all.GetList(Order[i]);
                bool medal = Order[i] == Customization.Type.Medal;
                bool sash = Order[i] == Customization.Type.Sash;
                if (medal) { v[i] = n == 1 && Singleton<AchievementManager>.Instance.AllSixtyFourAchievementsUnlocked() ? 1 : 0; continue; }
                if (n < 0 || list == null || n >= list.Length || (list[n] != null && list[n].IsLocked))
                    n = sash ? 0 : all.GetRandomUnlockedIndex(Order[i]);
                v[i] = n;
            }
            PersistentPlayerData data = svc.GetPlayerData(SplitManager.P2Photon.ActorNumber);
            CharacterCustomizationData d = data.customizationData ?? new CharacterCustomizationData();
            d.currentSkin = v[0]; d.currentAccessory = v[1]; d.currentEyes = v[2]; d.currentMouth = v[3];
            d.currentOutfit = v[4]; d.currentHat = v[5]; d.currentSash = v[6]; d.currentMedal = v[7];
            data.customizationData = d;
            svc.SetPlayerData(SplitManager.P2Photon, data);
            if (!saved) Save();
            Plugin.Log.LogInfo("Player 2 cosmetics " + (saved ? "restored: " : "picked at random: ") + Describe(d));
        }

        private static void Save()
        {
            CharacterCustomizationData d = P2Data();
            if (d == null) return;
            Plugin.P2Cosmetics.Value = Describe(d);
        }

        internal static CharacterCustomizationData P2Data()
        {
            PersistentPlayerDataService svc = GameHandler.GetService<PersistentPlayerDataService>();
            if (svc == null || SplitManager.P2Photon == null) return null;
            return svc.GetPlayerData(SplitManager.P2Photon.ActorNumber).customizationData;
        }

        internal static string Describe(CharacterCustomizationData d)
        {
            return d == null ? "none" : d.currentSkin + "," + d.currentAccessory + "," + d.currentEyes + "," + d.currentMouth + ","
                + d.currentOutfit + "," + d.currentHat + "," + d.currentSash + "," + d.currentMedal;
        }

        /// <summary>For the test log.</summary>
        public static string Status()
        {
            PassportManager pm = PassportManager.instance;
            PersistentPlayerDataService svc = GameHandler.GetService<PersistentPlayerDataService>();
            string p1 = svc != null && PhotonNetwork.LocalPlayer != null ? Describe(svc.GetPlayerData(PhotonNetwork.LocalPlayer.ActorNumber).customizationData) : "?";
            return "passport open=" + (pm != null && pm.isOpen) + " p2owns=" + P2Owns + " tab=" + (pm != null ? pm.activeType.ToString() : "?")
                + " sel=" + (_selected != null ? _selected.name : "none") + " p2=[" + Describe(P2Data()) + "] p1=[" + p1 + "] saved=[" + Plugin.P2Cosmetics.Value + "]";
        }

        // ------------------------------------------------------------------ the screen

        private static RectTransform _frame;   // holds the passport canvas's content so it can sit in one half

        private static void OnOpened(PassportManager pm)
        {
            bool p2 = P2Owns && SplitManager.Active;
            Character who = p2 ? SplitManager.P2Character : Character.localCharacter;
            if (who != null)
            {
                string name = who.characterName;
                if (pm.nameText != null) pm.nameText.text = name;
                if (pm.passportNumberText != null) pm.passportNumberText.text = PassportManager.GeneratePassportNumber(name);
                if (pm.medalButton != null && pm.medalButton.gameObject.activeSelf && who.refs.customization != null)
                    pm.medalButton.border.color = who.refs.customization.currentMedal == 1 ? pm.activeBorderColor : pm.inactiveBorderColor;
            }
            Layout(pm, SplitManager.Active);
            Unselect();
            _repeatAt = 0f;
            _lastDir = Vector2.zero;
            if (p2) Plugin.Log.LogInfo("Player 2 opened their passport.");
        }

        /// <summary>Fit the passport screen into its owner's half of the window (or the whole window when not split).</summary>
        private static void Layout(PassportManager pm, bool split)
        {
            if (pm == null || pm.uiObject == null) return;
            Canvas canvas = pm.uiObject.GetComponentInChildren<Canvas>(true);
            if (canvas == null) return;
            if (_frame == null)
            {
                if (!split) return;
                var go = new GameObject("SplitScreen Passport Frame", typeof(RectTransform));
                go.layer = canvas.gameObject.layer;
                _frame = (RectTransform)go.transform;
                _frame.SetParent(canvas.transform, false);
                var kids = new List<Transform>();
                foreach (Transform child in canvas.transform) if (child != _frame) kids.Add(child);
                foreach (Transform child in kids) child.SetParent(_frame, false);
            }
            Vector2 full = ((RectTransform)canvas.transform).rect.size;
            if (full.x < 1f || full.y < 1f) full = new Vector2(1280f, 720f);
            Vector2 min = Vector2.zero, max = Vector2.one;
            if (split)
            {
                bool side = Plugin.SideBySide.Value;
                if (P2Owns) { min = side ? new Vector2(0.5f, 0f) : new Vector2(0f, 0f); max = side ? new Vector2(1f, 1f) : new Vector2(1f, 0.5f); }
                else { min = side ? new Vector2(0f, 0f) : new Vector2(0f, 0.5f); max = side ? new Vector2(0.5f, 1f) : new Vector2(1f, 1f); }
            }
            Vector2 half = new Vector2(full.x * (max.x - min.x), full.y * (max.y - min.y));
            // The screen was drawn for a 16:9 window; shrink it when the half is smaller than that.
            float s = split ? Mathf.Min(1f, half.x / 1280f, half.y / 720f) : 1f;
            Vector2 centre = (min + max) * 0.5f;
            _frame.anchorMin = centre;
            _frame.anchorMax = centre;
            _frame.pivot = new Vector2(0.5f, 0.5f);
            _frame.anchoredPosition = Vector2.zero;
            _frame.sizeDelta = half / s;
            _frame.localScale = new Vector3(s, s, 1f);
        }

        // ------------------------------------------------------------------ gamepad control

        private static Selectable _selected;
        private static Vector3 _selectedScale;
        private static float _repeatAt;
        private static Vector2 _lastDir;

        private static void Unselect()
        {
            if (_selected != null) _selected.transform.localScale = _selectedScale;
            _selected = null;
        }

        private static void Select(Selectable s)
        {
            if (ReferenceEquals(s, _selected)) return;
            Unselect();
            _selected = s;
            if (s == null) return;
            _selectedScale = s.transform.localScale;
            s.transform.localScale = _selectedScale * 1.18f;
        }

        private static bool Usable(Selectable s)
        {
            return s != null && s.gameObject.activeInHierarchy && s.IsInteractable();
        }

        private static Vector2 ScreenPos(Selectable s)
        {
            RectTransform rt = (RectTransform)s.transform;
            return RectTransformUtility.WorldToScreenPoint(null, rt.TransformPoint(rt.rect.center));
        }

        private static void Navigate(PassportManager pm)
        {
            Gamepad pad = SplitManager.Pad;
            if (pad == null || pm.closing) return;

            if (!Usable(_selected))
            {
                Unselect();
                Selectable first = pm.buttons != null && pm.buttons.Length > 0 && pm.buttons[0] != null ? pm.buttons[0].button : null;
                if (!Usable(first))
                    foreach (Selectable s in pm.uiObject.GetComponentsInChildren<Selectable>(false)) if (Usable(s)) { first = s; break; }
                Select(first);
            }

            if (pad.buttonEast.wasPressedThisFrame || pad.startButton.wasPressedThisFrame)
            {
                _closedFrame = Time.frameCount;
                if (SplitManager.P2Character != null) SplitManager.P2Character.input.pauseWasPressed = false;
                Unselect();
                pm.Close();
                Plugin.Log.LogInfo("Player 2 closed their passport.");
                return;
            }

            int tab = (pad.rightShoulder.wasPressedThisFrame ? 1 : 0) - (pad.leftShoulder.wasPressedThisFrame ? 1 : 0);
            if (tab != 0 && pm.tabs != null && pm.tabs.Length > 0)
            {
                int cur = 0;
                for (int i = 0; i < pm.tabs.Length; i++) if (pm.tabs[i] != null && pm.tabs[i].type == pm.activeType) cur = i;
                int next = (cur + tab + pm.tabs.Length) % pm.tabs.Length;
                if (pm.tabs[next] != null) pm.tabs[next].SetTab();
                Unselect();
                return;
            }

            if (pad.rightTrigger.wasPressedThisFrame) { pm.PageNext(); Unselect(); return; }
            if (pad.leftTrigger.wasPressedThisFrame) { pm.PagePrev(); Unselect(); return; }

            if (pad.buttonSouth.wasPressedThisFrame && Usable(_selected))
            {
                Selectable s = _selected;
                Button b = s as Button;
                if (b != null) b.onClick.Invoke();
                else if (EventSystem.current != null)
                    ExecuteEvents.Execute(s.gameObject, new PointerEventData(EventSystem.current), ExecuteEvents.pointerClickHandler);
                return;
            }

            Vector2 raw = pad.dpad.ReadValue();
            if (raw.sqrMagnitude < 0.25f) raw = pad.leftStick.ReadValue();
            Vector2 dir = Vector2.zero;
            if (raw.magnitude > 0.55f)
                dir = Mathf.Abs(raw.x) > Mathf.Abs(raw.y) ? new Vector2(Mathf.Sign(raw.x), 0f) : new Vector2(0f, Mathf.Sign(raw.y));
            if (dir == Vector2.zero) { _lastDir = dir; return; }
            float now = Time.unscaledTime;
            if (dir == _lastDir && now < _repeatAt) return;
            _repeatAt = now + (dir == _lastDir ? 0.13f : 0.35f);
            _lastDir = dir;
            if (_selected == null) return;

            Vector2 from = ScreenPos(_selected);
            Selectable best = null;
            float bestScore = float.MaxValue;
            foreach (Selectable s in pm.uiObject.GetComponentsInChildren<Selectable>(false))
            {
                if (ReferenceEquals(s, _selected) || !Usable(s)) continue;
                Vector2 d = ScreenPos(s) - from;
                float along = Vector2.Dot(d, dir);
                if (along < 4f) continue;
                float across = (d - dir * along).magnitude;
                if (across > along * 2.5f) continue;
                float score = along + across * 2f;
                if (score < bestScore) { bestScore = score; best = s; }
            }
            if (best != null) Select(best);
        }
    }
}
