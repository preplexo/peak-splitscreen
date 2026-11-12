using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace PeakSplitScreen
{
    /// <summary>
    /// Player 2's HUD: a copy of the game's GUIManager object that runs as player 2, with both
    /// HUDs squeezed into their own half of the window.
    /// </summary>
    internal static class P2Hud
    {
        private const BindingFlags StaticAll = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        private static readonly string[] ConfinedCanvases = { "Canvas_HUD", "Canvas_Binoculars" };

        private static GameObject _clone;
        private static GUIManager _cloneGui;
        private static GUIManager _p1Gui;
        private static bool _pauseRequested;
        private static int _settleFrames;

        // Singleton fields that may get overwritten by the copy, with player 1's value.
        private static readonly List<FieldInfo> Candidates = new List<FieldInfo>();
        private static readonly List<object> CandidateP1 = new List<object>();

        // Canvas -> the half-window container its children were moved into.
        private static readonly List<Transform> Viewports = new List<Transform>();

        public static GUIManager CloneGui => _cloneGui;
        public static bool IsClone(GUIManager g) => _cloneGui != null && ReferenceEquals(g, _cloneGui);

        /// <summary>Player 2 pressed pause; the pause menu itself is shared and belongs to player 1's HUD.</summary>
        public static void RequestPause() => _pauseRequested = true;

        public static void Tick()
        {
            if (!Plugin.P2HudEnabled.Value) return;
            if (SplitManager.P2Character == null) return;
            GUIManager p1 = GUIManager.instance;
            if (p1 == null) return;

            if (_clone == null)
            {
                if (Character.localCharacter == null || Interaction.instance == null) return;
                try { Build(p1); }
                catch (Exception e)
                {
                    Plugin.Log.LogError("Could not build player 2's HUD: " + e);
                    Destroy();
                    Plugin.P2HudEnabled.Value = false;
                }
                return;
            }

            if (_settleFrames > 0) { _settleFrames--; FixSingletons(); }
            else if (!_tagsDone)
            {
                try { SetupNameTags(); }
                catch (Exception e) { Plugin.Log.LogWarning("Name tags could not be set up: " + e); }
            }

            if (_pauseRequested)
            {
                _pauseRequested = false;
                if (_p1Gui != null && !_p1Gui.pauseMenu.activeSelf && !LoadingScreenHandler.loading
                    && !_p1Gui.wheelActive && !_p1Gui.endScreen.isOpen)
                    _p1Gui.pauseMenu.SetActive(true);
            }
        }

        private static void Build(GUIManager p1)
        {
            _p1Gui = p1;
            var holder = new GameObject("SplitScreen HUD holder");
            holder.SetActive(false);
            _clone = UnityEngine.Object.Instantiate(p1.gameObject, holder.transform);
            _clone.name = "SplitScreen P2 GUIManager";
            _cloneGui = _clone.GetComponent<GUIManager>();
            _cloneGui.character = null;

            // Full-screen post effects are global; player 2's copy must not drive a second set.
            bool effects = false;
            try { effects = SetupEffects(); }
            catch (Exception e) { Plugin.Log.LogWarning("Per-player screen effects could not be set up: " + e); RestoreEffects(); }
            if (!effects) foreach (Volume v in _clone.GetComponentsInChildren<Volume>(true)) v.enabled = false;
            foreach (AudioSource a in _clone.GetComponentsInChildren<AudioSource>(true)) a.mute = true;

            SnapshotSingletons();

            bool entered = SplitContext.Enter();
            try { _clone.transform.SetParent(null, false); }   // wakes the copy up
            finally { if (entered) SplitContext.Exit(); }
            UnityEngine.Object.Destroy(holder);

            FixSingletons();
            _settleFrames = 10;
            SplitExtras.AddRoot(_clone);

            bool side = Plugin.SideBySide.Value;
            Confine(p1.transform, side ? new Vector2(0f, 0f) : new Vector2(0f, 0.5f), side ? new Vector2(0.5f, 1f) : new Vector2(1f, 1f));
            Confine(_clone.transform, side ? new Vector2(0.5f, 0f) : new Vector2(0f, 0f), side ? new Vector2(1f, 1f) : new Vector2(1f, 0.5f));
            Plugin.Log.LogInfo("Player 2 has their own HUD.");
        }

        // ------------------------------------------------------------------ name tags

        private static IsLookedAt _p1Tag;   // floats over player 1, drawn on player 2's HUD
        private static IsLookedAt _p2Tag;   // floats over player 2, drawn on player 1's HUD
        private static bool _tagsDone;

        /// <summary>The tag over player 1 is player 2's to look at, so that script runs as player 2 (and the reverse).</summary>
        public static bool TagRunsAsP2(IsLookedAt tag) => ReferenceEquals(tag, _p1Tag);

        /// <summary>
        /// The game switches off the name tag over "the local player". With two local players both got
        /// switched off; turn each back on for the other player's HUD.
        /// </summary>
        private static void SetupNameTags()
        {
            _tagsDone = true;
            Character p1 = Character.localCharacter, p2 = SplitManager.P2Character;
            if (p1 == null || p2 == null || _cloneGui == null || _p1Gui == null) return;
            IsLookedAt t1 = p1.GetComponentInChildren<IsLookedAt>(true);
            IsLookedAt t2 = p2.GetComponentInChildren<IsLookedAt>(true);
            UIPlayerNames n1 = _p1Gui.playerNames, n2 = _cloneGui.playerNames;
            if (t1 == null || t2 == null || n1 == null || n2 == null) return;
            if (!n1.isActiveAndEnabled || !n2.isActiveAndEnabled) return;

            if (!t2.gameObject.activeSelf)
            {
                n1.Init(t2.characterInteractible, r => t2.index = r);
                _p2Tag = t2;
                t2.gameObject.SetActive(true);
            }
            if (!t1.gameObject.activeSelf)
            {
                n2.Init(t1.characterInteractible, r => t1.index = r);
                _p1Tag = t1;
                t1.gameObject.SetActive(true);
            }
            Plugin.Log.LogInfo("Name tags are shown per player.");
        }

        // ------------------------------------------------------------------ screen effects

        private static readonly List<KeyValuePair<GameObject, int>> MovedLayers = new List<KeyValuePair<GameObject, int>>();
        private static readonly List<GameObject> EffectTwins = new List<GameObject>();
        private static UniversalAdditionalCameraData _p1CamData, _p2CamData;
        private static LayerMask _p1Mask, _p2Mask;

        /// <summary>
        /// Status tints, the pass-out fade and the fall blur are post-processing volumes that every
        /// camera picks up. Put each player's set on a layer only their own camera looks at.
        /// </summary>
        private static bool SetupEffects()
        {
            int a = -1, b = -1;
            for (int i = 31; i >= 8 && b < 0; i--)
            {
                if (!string.IsNullOrEmpty(LayerMask.LayerToName(i))) continue;
                if (a < 0) a = i; else b = i;
            }
            MainCamera main = MainCamera.instance;
            if (b < 0 || main == null || main.cam == null || SplitManager.P2Cam == null) return false;
            _p1CamData = main.cam.GetUniversalAdditionalCameraData();
            _p2CamData = SplitManager.P2Cam.GetUniversalAdditionalCameraData();
            if (_p1CamData == null || _p2CamData == null) return false;

            foreach (Volume v in _p1Gui.GetComponentsInChildren<Volume>(true)) Move(v.gameObject, a);
            foreach (Volume v in _clone.GetComponentsInChildren<Volume>(true)) v.gameObject.layer = b;
            foreach (PassOutPost p in UnityEngine.Object.FindObjectsByType<PassOutPost>(FindObjectsInactive.Include, FindObjectsSortMode.None)) Twin(p.gameObject, a, b);
            foreach (FallPost p in UnityEngine.Object.FindObjectsByType<FallPost>(FindObjectsInactive.Include, FindObjectsSortMode.None)) Twin(p.gameObject, a, b);

            _p1Mask = _p1CamData.volumeLayerMask;
            _p2Mask = _p2CamData.volumeLayerMask;
            _p1CamData.volumeLayerMask = (_p1Mask | (1 << a)) & ~(1 << b);
            _p2CamData.volumeLayerMask = (_p2Mask | (1 << b)) & ~(1 << a);
            Plugin.Log.LogInfo($"Screen effects are per player (layers {a}/{b}, {MovedLayers.Count} volumes, {EffectTwins.Count} copies).");
            return true;
        }

        private static void Move(GameObject go, int layer)
        {
            MovedLayers.Add(new KeyValuePair<GameObject, int>(go, go.layer));
            go.layer = layer;
        }

        private static void Twin(GameObject original, int a, int b)
        {
            if (SplitManager.IsP2(original.transform) || EffectTwins.Contains(original)) return;
            GameObject twin = UnityEngine.Object.Instantiate(original);
            twin.name = "SplitScreen P2 " + original.name;
            twin.layer = b;
            Volume v = twin.GetComponent<Volume>();
            if (v != null) v.weight = 0f;
            EffectTwins.Add(twin);
            SplitExtras.AddRoot(twin);
            Move(original, a);
        }

        private static void RestoreEffects()
        {
            foreach (var kv in MovedLayers) if (kv.Key != null) kv.Key.layer = kv.Value;
            MovedLayers.Clear();
            foreach (GameObject twin in EffectTwins)
            {
                if (twin == null) continue;
                SplitExtras.RemoveRoot(twin);
                UnityEngine.Object.Destroy(twin);
            }
            EffectTwins.Clear();
            if (_p1CamData != null) _p1CamData.volumeLayerMask = _p1Mask;
            if (_p2CamData != null) _p2CamData.volumeLayerMask = _p2Mask;
            _p1CamData = null;
            _p2CamData = null;
        }

        // ------------------------------------------------------------------ singletons

        private static void SnapshotSingletons()
        {
            Candidates.Clear();
            CandidateP1.Clear();
            var seen = new HashSet<Type>();
            foreach (MonoBehaviour mb in _clone.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (mb == null) continue;
                for (Type t = mb.GetType(); t != null && t != typeof(MonoBehaviour); t = t.BaseType)
                {
                    if (!seen.Add(t) || t.ContainsGenericParameters) continue;
                    if (t.Namespace != null && t.Namespace.StartsWith("UnityEngine")) continue;
                    foreach (FieldInfo f in t.GetFields(StaticAll))
                    {
                        if (f.IsLiteral || f.IsInitOnly) continue;
                        if (!typeof(Component).IsAssignableFrom(f.FieldType)) continue;
                        try { CandidateP1.Add(f.GetValue(null)); Candidates.Add(f); }
                        catch { /* static constructor trouble; not ours to fix */ }
                    }
                }
            }
        }

        /// <summary>Any singleton now pointing into the copy goes back to player 1 and becomes a per-player swap.</summary>
        private static void FixSingletons()
        {
            if (_clone == null) return;
            Transform root = _clone.transform;
            for (int i = Candidates.Count - 1; i >= 0; i--)
            {
                Component now = Candidates[i].GetValue(null) as Component;
                if (now == null || !ReferenceEquals(now.transform.root, root)) continue;
                Candidates[i].SetValue(null, CandidateP1[i]);
                SplitExtras.RegisterSwap(Candidates[i], now);
                Plugin.Log.LogInfo($"Per-player singleton: {Candidates[i].DeclaringType.Name}.{Candidates[i].Name}");
                Candidates.RemoveAt(i);
                CandidateP1.RemoveAt(i);
            }
        }

        // ------------------------------------------------------------------ layout

        private static void Confine(Transform gui, Vector2 min, Vector2 max)
        {
            foreach (string name in ConfinedCanvases)
            {
                Transform canvas = gui.Find(name);
                if (canvas == null) continue;
                var go = new GameObject("SplitScreen Viewport", typeof(RectTransform));
                go.layer = canvas.gameObject.layer;
                var rt = (RectTransform)go.transform;
                rt.SetParent(canvas, false);
                rt.anchorMin = min;
                rt.anchorMax = max;
                rt.offsetMin = Vector2.zero;
                rt.offsetMax = Vector2.zero;
                var kids = new List<Transform>();
                foreach (Transform child in canvas) if (child != rt) kids.Add(child);
                foreach (Transform child in kids) child.SetParent(rt, false);
                Viewports.Add(rt);
            }
        }

        private static void Unconfine()
        {
            foreach (Transform vp in Viewports)
            {
                if (vp == null) continue;
                Transform canvas = vp.parent;
                var kids = new List<Transform>();
                foreach (Transform child in vp) kids.Add(child);
                foreach (Transform child in kids) child.SetParent(canvas, false);
                UnityEngine.Object.Destroy(vp.gameObject);
            }
            Viewports.Clear();
        }

        public static void Destroy()
        {
            Unconfine();
            RestoreEffects();
            if (_p1Tag != null) _p1Tag.gameObject.SetActive(false);
            if (_p2Tag != null && SplitManager.P2Character != null) _p2Tag.gameObject.SetActive(false);
            _p1Tag = null;
            _p2Tag = null;
            _tagsDone = false;
            if (_clone != null)
            {
                Transform root = _clone.transform;
                SplitExtras.UnregisterSwaps(v => v is Component c && (c == null || ReferenceEquals(c.transform.root, root)));
                SplitExtras.RemoveRoot(_clone);
                UnityEngine.Object.Destroy(_clone);
            }
            _clone = null;
            _cloneGui = null;
            _p1Gui = null;
            _pauseRequested = false;
            Candidates.Clear();
            CandidateP1.Clear();
        }
    }
}
