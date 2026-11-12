using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;

namespace PeakSplitScreen
{
    /// <summary>Testing helpers, all off by default (see the [Debug] config section).</summary>
    internal static class DebugTools
    {
        private static Gamepad _virtualPad;
        public static Gamepad VirtualPad => _virtualPad != null && _virtualPad.added ? _virtualPad : null;
        private static float _nextShot;
        private static int _shot;
        private static float _activeSince = -1f;
        private static string _phase = "";
        private static string _pressLogged;
        private static bool _dumped;

        public static void Tick()
        {
            if (Plugin.DebugMute.Value)
            {
                AudioListener.volume = 0f;
                AudioListener.pause = true;
            }

            if (Plugin.DebugFakeGamepad.Value) FakePad();

            if (Plugin.DebugScreenshots.Value && Time.unscaledTime >= _nextShot)
            {
                _nextShot = Time.unscaledTime + 4f;
                string dir = Path.Combine(BepInEx.Paths.BepInExRootPath, "splitscreen-shots");
                Directory.CreateDirectory(dir);
                string scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
                ScreenCapture.CaptureScreenshot(Path.Combine(dir, $"shot{(_shot++ % 30):00}-{scene}.png"));
            }
        }

        /// <summary>A scripted virtual gamepad so player 2 can be tested without hardware.</summary>
        private static void FakePad()
        {
            if (_virtualPad == null || !_virtualPad.added)
            {
                Application.runInBackground = true;
                InputSystem.settings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
                _virtualPad = InputSystem.AddDevice<Gamepad>("SplitScreen Virtual Pad");
                Plugin.Log.LogInfo("[debug] Added virtual gamepad.");
            }

            if (!SplitManager.Active) { _activeSince = -1f; return; }
            if (_activeSince < 0f) _activeSince = Time.unscaledTime;
            float t = Time.unscaledTime - _activeSince;
            if (!_dumped && GUIManager.instance != null) { _dumped = true; DumpHud(); }

            var state = new GamepadState();
            string phase;
            if (t < 3f) phase = "idle";
            else if (t < 5f) { phase = "turn around"; state.rightStick = new Vector2(1f, 0f); }
            else if (t < 7f) { phase = "spawn test item"; if (_phase != phase) SpawnTestItem(); }
            else if (t < 7.8f) { phase = "press interact"; Press(ref state, "Interact"); }
            else if (t < 10f) phase = "holding item";
            else if (t < 12f) { phase = "emote wheel open"; Press(ref state, "Emote"); state.leftStick = new Vector2(0f, 1f); state.rightStick = new Vector2(0f, 1f); }
            else if (t < 13f) { phase = "emote wheel still open"; Press(ref state, "Emote"); state.leftStick = new Vector2(0f, 1f); state.rightStick = new Vector2(0f, 1f); }
            else if (t < 15f) phase = "emote wheel released";
            else if (t < 21f) { phase = "eat"; Press(ref state, "UsePrimary"); }
            else if (t < 22f) phase = "after eating";
            else if (t < 27f) { phase = "injury zone on p2"; if (_phase != phase) SpawnZone(); }
            else if (t < 28f) { phase = "injury zone gone"; if (_zone != null) Object.Destroy(_zone); }
            else if (t < 38f) { phase = "p2 passed out"; if (_phase != phase) SplitManager.P2Character.PassOutInstantly(); state.rightStick = new Vector2(0.3f, 0f); }
            else if (t < 39f) phase = "p2 still passed out";
            else if (t < 48f)
            {
                phase = "p2 dead";
                if (_phase != phase && SplitContext.Enter()) { try { Character.Die(); } finally { SplitContext.Exit(); } }
            }
            else phase = "done";

            if (phase != _phase)
            {
                _phase = phase;
                Plugin.Log.LogInfo("[debug] virtual pad: " + phase + " | " + ItemInfo() + " | " + SplitManager.Status() + " | " + Health());
            }
            if (((t >= 5f && t < 10f) || (t >= 34f && t < 37f)) && Time.unscaledTime >= _nextProbe)
            {
                _nextProbe = Time.unscaledTime + 0.5f;
                Plugin.Log.LogInfo("[debug] probe " + ItemInfo() + Probe());
            }
            if (t >= 5f && t < 8f && _testItem != null && _testItem.itemState == ItemState.Ground && SplitManager.P2Cam != null)
            {
                Transform ct = SplitManager.P2Cam.transform;   // keep the item on the crosshair while player 2 sways
                _testItem.transform.position = ct.position + ct.forward * _testDist + Vector3.up * _testUp;
                if (_testItem.rig != null) _testItem.rig.position = _testItem.transform.position;
                Physics.SyncTransforms();
            }
            InputSystem.QueueStateEvent(_virtualPad, state);
        }

        private static GameObject _zone;

        private static void SpawnZone()
        {
            Character c = SplitManager.P2Character;
            _zone = new GameObject("SplitScreen test injury zone");
            _zone.transform.position = c.data.groundPos;
            _zone.transform.localScale = Vector3.one * 1.6f;
            _zone.AddComponent<InjurySphere>();
            Plugin.Log.LogInfo("[debug] injury zone at " + c.data.groundPos + ", p1 is " + Vector3.Distance(Character.localCharacter.data.groundPos, c.data.groundPos).ToString("0.0") + " m away");
        }

        private static string One(Character c)
        {
            if (c == null || c.data == null) return "none";
            CharacterAfflictions a = c.refs.afflictions;
            if (a.m_inAirport) a.m_inAirport = false;   // statuses are switched off in the lobby; tests need them
            return "inj=" + a.GetCurrentStatus(CharacterAfflictions.STATUSTYPE.Injury).ToString("0.000")
                 + " hun=" + a.GetCurrentStatus(CharacterAfflictions.STATUSTYPE.Hunger).ToString("0.000")
                 + " out=" + c.data.passedOut + "/" + c.data.fullyPassedOut + " dead=" + c.data.dead;
        }

        private static string Health()
        {
            string s = "P1{" + One(Character.localCharacter) + "} P2{" + One(SplitManager.P2Character) + "}"
                     + " chars=" + Character.AllCharacters.Count
                     + " spec=" + (SplitManager.P2Spec != null ? SplitManager.P2Spec.name : "none")
                     + " p1spec=" + (MainCameraMovement.specCharacter != null ? MainCameraMovement.specCharacter.name : "none")
                     + " scene=" + UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
            GUIManager cg = P2Hud.CloneGui;
            if (cg != null && cg.emoteWheel != null)
            {
                EmoteWheel w = cg.emoteWheel.GetComponent<EmoteWheel>();
                s += " p2wheel=" + cg.emoteWheel.activeSelf + " chosen=" + (w != null && w.chosenEmoteData != null ? w.chosenEmoteData.emoteName : "none");
            }
            if (GUIManager.instance != null && GUIManager.instance.emoteWheel != null) s += " p1wheel=" + GUIManager.instance.emoteWheel.activeSelf;
            return s;
        }

        private static string Slots(Player p)
        {
            if (p == null) return "none";
            string s = "";
            foreach (ItemSlot slot in p.itemSlots) s += slot.IsEmpty() ? "[-]" : "[" + slot.GetPrefabName() + "]";
            return s;
        }

        private static string ItemInfo()
        {
            Character c = SplitManager.P2Character;
            if (c == null || c.data == null) return "no p2";
            Interaction i = SplitExtras.P2Interaction;
            return "held=" + (c.data.currentItem != null ? c.data.currentItem.name : "none")
                 + " slots=" + Slots(SplitManager.P2Player) + " p1slots=" + Slots(Player.localPlayer)
                 + " hovered=" + (i != null ? i.bestInteractableName : "none");
        }

        /// <summary>Hold an item in the air right in front of the second camera.</summary>
        private static void SpawnTestItem()
        {
            Camera cam = SplitManager.P2Cam;
            if (cam == null) return;
            Item prefab = null;
            foreach (Item it in Zorro.Core.SingletonAsset<ItemDatabase>.Instance.Objects)
            {
                if (it == null) continue;
                if (prefab == null) prefab = it;
                if (it.name.Contains("Apple") || it.name.Contains("Granola")) { prefab = it; break; }
            }
            if (prefab == null) { Plugin.Log.LogWarning("[debug] no item prefab found"); return; }
            float dist = 1.1f;
            Transform own = SplitManager.P2Character != null ? SplitManager.P2Character.transform.root : null;
            foreach (RaycastHit h in Physics.RaycastAll(cam.transform.position, cam.transform.forward, 2f, ~0, QueryTriggerInteraction.Ignore))
            {
                if (h.collider.transform.root == own) continue;
                if (h.distance - 0.25f < dist) dist = Mathf.Max(0.5f, h.distance - 0.25f);
            }
            Vector3 pos = cam.transform.position + cam.transform.forward * dist;
            GameObject go = Photon.Pun.PhotonNetwork.Instantiate("0_Items/" + prefab.name, pos, Quaternion.identity, 0);
            _testItem = go.GetComponent<Item>();
            _testItem.SetKinematicNetworked(true, pos, Quaternion.identity);
            _testDist = dist;
            // Centre the collider on the crosshair and keep the floating item from shoving player 2 around.
            Collider ic = go.GetComponentInChildren<Collider>();
            _testUp = ic != null ? pos.y - ic.bounds.center.y : 0f;
            if (ic != null && own != null)
                foreach (Collider pc in own.GetComponentsInChildren<Collider>(true)) Physics.IgnoreCollision(ic, pc, true);
            Plugin.Log.LogInfo("[debug] spawned test item " + prefab.name + " at " + pos + " dist=" + dist);
        }

        private static Item _testItem;
        private static float _testDist = 1f;
        private static float _testUp;
        private static float _nextProbe;

        private static string ColInfo()
        {
            string s = "";
            foreach (Collider col in _testItem.GetComponentsInChildren<Collider>(true))
                s += " col:" + col.name + "/" + col.GetType().Name + " en=" + col.enabled + " act=" + col.gameObject.activeInHierarchy + " trig=" + col.isTrigger + " L" + col.gameObject.layer + " c=" + col.bounds.center + " ext=" + col.bounds.extents.x.ToString("0.00");
            return s;
        }

        private static string Probe()
        {
            Interaction i = SplitExtras.P2Interaction;
            MainCamera mc = SplitManager.P2MainCamera;
            Character c = SplitManager.P2Character;
            string s = " best=" + (i != null ? i.bestInteractableName : "?")
                     + " mcam=" + (mc != null ? mc.transform.position + ">" + mc.transform.forward : "none")
                     + " p2cam=" + (SplitManager.P2Cam != null ? SplitManager.P2Cam.transform.position.ToString() : "none");
            if (_testItem != null)
                s += " item@" + _testItem.transform.position + " state=" + _testItem.itemState + " block=" + _testItem.blockInteraction
                   + " cols=" + _testItem.GetComponentsInChildren<Collider>().Length + " layer=" + _testItem.gameObject.layer + ColInfo();
            if (c != null)
                s += " out=" + c.data.passedOut + " sticky=" + (c.data.currentStickyItem != null)
                   + " interactPressed=" + c.input.interactIsPressed + " wasPressed=" + c.input.interactWasPressed;
            if (GUIManager.instance != null) s += " winBlock=" + GUIManager.instance.windowBlockingInput + " wheel=" + GUIManager.instance.wheelActive;
            GUIManager cg = P2Hud.CloneGui;
            if (cg != null) s += " p2winBlock=" + cg.windowBlockingInput + " p2wheel=" + cg.wheelActive;
            if (mc != null)
                foreach (RaycastHit h in Physics.RaycastAll(mc.transform.position, mc.transform.forward, 2f, ~0, QueryTriggerInteraction.Collide))
                    s += " hit:" + h.collider.name + "/" + h.collider.transform.root.name + "@" + h.distance.ToString("0.00") + "L" + h.collider.gameObject.layer;
            return s;
        }

        /// <summary>Press whatever virtual pad button the second player has bound to this action.</summary>
        private static void Press(ref GamepadState state, string actionName)
        {
            InputAction action = SplitManager.P2Actions != null ? SplitManager.P2Actions.FindAction(actionName) : null;
            if (action == null) return;
            foreach (InputControl control in action.controls)
            {
                if (control.device != _virtualPad) continue;
                switch (control.name)
                {
                    case "buttonSouth": state = state.WithButton(GamepadButton.South); break;
                    case "buttonWest": state = state.WithButton(GamepadButton.West); break;
                    case "buttonNorth": state = state.WithButton(GamepadButton.North); break;
                    case "buttonEast": state = state.WithButton(GamepadButton.East); break;
                    case "leftShoulder": state = state.WithButton(GamepadButton.LeftShoulder); break;
                    case "rightShoulder": state = state.WithButton(GamepadButton.RightShoulder); break;
                    case "leftStickPress": state = state.WithButton(GamepadButton.LeftStick); break;
                    case "rightStickPress": state = state.WithButton(GamepadButton.RightStick); break;
                    case "up": state = state.WithButton(GamepadButton.DpadUp); break;
                    case "down": state = state.WithButton(GamepadButton.DpadDown); break;
                    case "left": state = state.WithButton(GamepadButton.DpadLeft); break;
                    case "right": state = state.WithButton(GamepadButton.DpadRight); break;
                    case "leftTrigger": state.leftTrigger = 1f; break;
                    case "rightTrigger": state.rightTrigger = 1f; break;
                    default: continue;
                }
                if (_pressLogged != actionName) { _pressLogged = actionName; Plugin.Log.LogInfo($"[debug] {actionName} -> {control.name}"); }
                return;
            }
        }

        /// <summary>Write the HUD object tree to a file so it can be studied.</summary>
        private static void DumpHud()
        {
            var sb = new StringBuilder();
            GUIManager gui = GUIManager.instance;
            sb.AppendLine("GUIManager path: " + PathOf(gui.transform) + "  screen=" + Screen.width + "x" + Screen.height);
            sb.AppendLine("hudCanvas=" + (gui.hudCanvas != null ? PathOf(gui.hudCanvas.transform) : "null"));
            foreach (Canvas c in gui.transform.root.GetComponentsInChildren<Canvas>(true))
            {
                var scaler = c.GetComponent<UnityEngine.UI.CanvasScaler>();
                sb.AppendLine($"CANVAS {PathOf(c.transform)} mode={c.renderMode} cam={(c.worldCamera != null ? c.worldCamera.name : "null")} order={c.sortingOrder} root={c.isRootCanvas} active={c.gameObject.activeInHierarchy} enabled={c.enabled}"
                    + (scaler != null ? $" scaler={scaler.uiScaleMode} ref={scaler.referenceResolution} match={scaler.matchWidthOrHeight} screenMatch={scaler.screenMatchMode}" : ""));
            }
            var counts = new System.Collections.Generic.SortedDictionary<string, int>();
            foreach (MonoBehaviour mb in gui.transform.root.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (mb == null) continue;
                string n = mb.GetType().FullName;
                counts.TryGetValue(n, out int k);
                counts[n] = k + 1;
            }
            foreach (var kv in counts) sb.AppendLine($"TYPE {kv.Key} x{kv.Value}");
            Tree(gui.transform.root, 0, 3, sb);
            sb.AppendLine("Interaction path: " + (Interaction.instance != null ? PathOf(Interaction.instance.transform) : "null"));
            foreach (Camera cam in Camera.allCameras)
                sb.AppendLine($"CAMERA {PathOf(cam.transform)} depth={cam.depth} rect={cam.rect} target={(cam.targetTexture != null ? cam.targetTexture.name : "screen")}");
            File.WriteAllText(Path.Combine(BepInEx.Paths.BepInExRootPath, "splitscreen-hud-dump.txt"), sb.ToString());
            Plugin.Log.LogInfo("[debug] HUD tree written.");
        }

        private static string PathOf(Transform t)
        {
            string path = t.name;
            for (Transform p = t.parent; p != null; p = p.parent) path = p.name + "/" + path;
            return path;
        }

        private static void Tree(Transform t, int depth, int max, StringBuilder sb)
        {
            string comps = "";
            foreach (Component c in t.GetComponents<Component>())
                if (c != null && !(c is Transform) && !(c is CanvasRenderer)) comps += c.GetType().Name + ",";
            sb.AppendLine(new string(' ', depth * 2) + (t.gameObject.activeSelf ? "+ " : "- ") + t.name + " [" + comps + "]");
            if (depth >= max) return;
            foreach (Transform child in t) Tree(child, depth + 1, max, sb);
        }
    }
}
