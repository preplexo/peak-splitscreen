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
                AudioListener.pause = false; // P2Audio also zeroes the volume right before each mix
            }

            if (Plugin.DebugFakeGamepad.Value) FakePad();

            // Menu screenshots: keep the split-screen sign highlighted.
            if ((Plugin.DebugScript.Value ?? "").Trim().ToLowerInvariant() == "menushot" && MenuOption.SplitButton != null
                && UnityEngine.EventSystems.EventSystem.current != null
                && UnityEngine.EventSystems.EventSystem.current.currentSelectedGameObject != MenuOption.SplitButton.gameObject)
                MenuOption.SplitButton.Select();

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

            if ((Plugin.DebugScript.Value ?? "").Trim().ToLowerInvariant() == "twopads" && (_p1VirtualPad == null || !_p1VirtualPad.added))
            {
                _p1VirtualPad = InputSystem.AddDevice<Gamepad>("SplitScreen Virtual Pad P1");
                Plugin.Log.LogInfo("[debug] Added player 1 virtual gamepad.");
            }

            if (!SplitManager.Active) { _activeSince = -1f; _once.Clear(); return; }
            if (_activeSince < 0f) _activeSince = Time.unscaledTime;
            float t = Time.unscaledTime - _activeSince;
            if (!_dumped && GUIManager.instance != null) { _dumped = true; DumpHud(); }

            var state = new GamepadState();
            string script = (Plugin.DebugScript.Value ?? "").Trim().ToLowerInvariant();
            if (script == "passport") { PassportScript(t, ref state); InputSystem.QueueStateEvent(_virtualPad, state); return; }
            if (script == "run") { RunScript(t, ref state); InputSystem.QueueStateEvent(_virtualPad, state); return; }
            if (script == "fog") { FogScript(t); InputSystem.QueueStateEvent(_virtualPad, state); return; }
            if (script == "photo") { PhotoScript(t); InputSystem.QueueStateEvent(_virtualPad, state); return; }
            if (script == "twopads") { TwoPadsScript(t, ref state); InputSystem.QueueStateEvent(_virtualPad, state); return; }
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
                Plugin.Log.LogInfo("[debug] virtual pad: " + phase + " | " + ItemInfo() + " | " + SplitManager.Status() + " | " + Health() + " | " + P2Audio.Status());
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

        private static Gamepad _p1VirtualPad;
        private static string _twoPhase = "";
        private static bool _keyDown;

        /// <summary>Two virtual pads: checks each pad drives only its own player, and keyboard/pad switching for player 1.</summary>
        private static void TwoPadsScript(float t, ref GamepadState p2)
        {
            var p1 = new GamepadState();
            bool key = false;
            string phase;
            if (t < 2f) phase = "idle";
            else if (t < 5f) { phase = "both pads mirrored (must not claim a p1 pad)"; p1.leftStick = p2.leftStick = new Vector2(0f, 1f); }
            else if (t < 7f) phase = "idle 2";
            else if (t < 10f) { phase = "p1 pad forward"; p1.leftStick = new Vector2(0f, 1f); }
            else if (t < 12f) phase = "idle 3";
            else if (t < 15f) { phase = "p2 pad forward"; p2.leftStick = new Vector2(0f, 1f); }
            else if (t < 17f) phase = "idle 4";
            else if (t < 20f) { phase = "p1 pad turn"; p1.rightStick = new Vector2(1f, 0f); }
            else if (t < 22f) phase = "idle 5";
            else if (t < 25f) { phase = "keyboard W"; key = true; }
            else if (t < 27f) phase = "idle 6";
            else if (t < 30f) { phase = "p1 pad back"; p1.leftStick = new Vector2(0f, -1f); }
            else phase = "done";

            if (_p1VirtualPad != null && _p1VirtualPad.added) InputSystem.QueueStateEvent(_p1VirtualPad, p1);
            if (key != _keyDown && Keyboard.current != null)
            {
                _keyDown = key;
                InputSystem.QueueStateEvent(Keyboard.current, key ? new KeyboardState(Key.W) : new KeyboardState());
            }
            if (phase != _twoPhase)
            {
                Character c1 = Character.localCharacter;
                Character c2 = SplitManager.P2Character;
                Plugin.Log.LogInfo($"[debug] twopads end of '{_twoPhase}': p1={(c1 != null ? c1.Center.ToString() : "none")} p1look={(c1 != null ? c1.data.lookValues.ToString() : "none")}"
                    + $" p2={(c2 != null ? c2.Center.ToString() : "none")} p2look={(c2 != null ? c2.data.lookValues.ToString() : "none")}"
                    + $" p1scheme={Zorro.ControllerSupport.InputHandler.GetCurrentUsedInputScheme()} p1pad={(SplitManager.P1Pad != null ? SplitManager.P1Pad.displayName : "none")}"
                    + $" p2pad={(SplitManager.Pad != null ? SplitManager.Pad.displayName : "none")}");
                _twoPhase = phase;
            }
        }

        private static GameObject _zone;
        private static readonly System.Collections.Generic.HashSet<string> _once = new System.Collections.Generic.HashSet<string>();

        private static bool Once(float t, float at, string key) => t >= at && _once.Add(key);
        private static bool Tap(float t, float at) => t >= at && t < at + 0.15f;

        private static void Say(string what)
        {
            Plugin.Log.LogInfo("[debug] " + what + " | " + P2Passport.Status() + " | held=" + (SplitManager.P2Character.data.currentItem != null ? SplitManager.P2Character.data.currentItem.name : "none")
                + " p1block=" + (GUIManager.instance != null && GUIManager.instance.windowBlockingInput) + " p2block=" + (P2Hud.CloneGui != null && P2Hud.CloneGui.windowBlockingInput)
                + " cursor=" + Cursor.lockState + " scene=" + UnityEngine.SceneManagement.SceneManager.GetActiveScene().name);
        }

        /// <summary>Player 2 opens their passport and changes something on every tab.</summary>
        private static void PassportScript(float t, ref GamepadState state)
        {
            if (Once(t, 6.5f, "pre")) Say("before opening");
            if (t >= 7f && t < 7.6f) Press(ref state, "UsePrimary");
            if (Once(t, 8.8f, "opened")) Say("after use");
            for (int i = 0; i < 8; i++)
            {
                float b = 9f + i * 3f;
                if (Tap(t, b)) state = state.WithButton(i % 2 == 0 ? GamepadButton.DpadRight : GamepadButton.DpadDown);
                if (Tap(t, b + 0.4f)) state = state.WithButton(GamepadButton.DpadRight);
                if (Once(t, b + 0.75f, "moved" + i)) Say("tab " + i + " moved");
                if (Tap(t, b + 0.8f)) state = state.WithButton(GamepadButton.South);
                if (Tap(t, b + 1.4f)) state = state.WithButton(GamepadButton.DpadDown);
                if (Tap(t, b + 2.0f)) state.leftStick = new Vector2(-1f, 0f);
                if (Once(t, b + 2.3f, "tab" + i)) Say("tab " + i + " picked");
                if (Tap(t, b + 2.4f)) state = state.WithButton(GamepadButton.RightShoulder);
            }
            if (Tap(t, 33.5f)) state = state.WithButton(GamepadButton.LeftShoulder);
            if (Once(t, 34.5f, "end")) Say("before closing");
            if (Tap(t, 35f)) state = state.WithButton(GamepadButton.East);
            if (Once(t, 37f, "closed")) Say("after closing");
            if (t >= 38f && t < 40f) state.leftStick = new Vector2(0f, 1f);
            if (Once(t, 40f, "walked")) Say("after walking");
            if (t >= 41f && t < 41.6f) Press(ref state, "UsePrimary");
            if (Once(t, 43f, "reopened")) Say("reopened");
            if (Once(t, 43.3f, "hatsash")) HatSashCheck();
            if (Tap(t, 44f)) state = state.WithButton(GamepadButton.Start);
            if (Once(t, 46f, "done")) Say("done (closed with start; pause menu should not be open: paused=" + (P2Hud.CloneGui != null && P2Hud.CloneGui.pauseMenu != null && P2Hud.CloneGui.pauseMenu.activeSelf) + ")");
        }

        /// <summary>Start a real run from the lobby, then have player 2 wander around the island.</summary>
        private static void RunScript(float t, ref GamepadState state)
        {
            string scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
            if (scene == "Airport")
            {
                if (Once(t, 8f, "start"))
                {
                    AirportCheckInKiosk kiosk = Object.FindAnyObjectByType<AirportCheckInKiosk>();
                    Plugin.Log.LogInfo("[debug] starting a run, kiosk=" + (kiosk != null));
                    if (kiosk != null) kiosk.StartGame(0);
                }
                return;
            }
            float c = t % 20f;
            if (c < 8f) state.leftStick = new Vector2(0f, 1f);
            else if (c < 10f) { state.leftStick = new Vector2(0f, 1f); Press(ref state, "Jump"); }
            else if (c < 12f) state.rightStick = new Vector2(0.6f, 0f);
            else if (c < 13f) Press(ref state, "Interact");
            else if (c < 16f) { state.leftStick = new Vector2(0f, 1f); Press(ref state, "Sprint"); }
            else if (c < 17f) Press(ref state, "Drop");
            if (Once(t, Mathf.Floor(t / 10f) * 10f, "s" + (int)(t / 10f)))
                Plugin.Log.LogInfo("[debug] run t=" + t.ToString("0") + " " + ItemInfo() + " | " + SplitManager.Status() + " | " + Health() + " | " + P2Audio.Status());
        }

        /// <summary>Pick something on the Hat and Sash tabs for player 2 and report whether it took.</summary>
        private static void HatSashCheck()
        {
            PassportManager pm = PassportManager.instance;
            Customization all = Zorro.Core.Singleton<Customization>.Instance;
            if (pm == null || all == null || !pm.isOpen || !P2Passport.P2Owns) { Plugin.Log.LogInfo("[debug] hat/sash check skipped: passport not open for player 2"); return; }
            CharacterCustomizationData d = P2Passport.P2Data();
            CustomizationOption fit = all.fits[Mathf.Clamp(d.currentOutfit, 0, all.fits.Length - 1)];
            Plugin.Log.LogInfo("[debug] hat/sash check: outfit " + d.currentOutfit + " overrides hat=" + fit.overrideHat + " (to " + fit.overrideHatIndex + ")");
            foreach (Customization.Type type in new[] { Customization.Type.Hat, Customization.Type.Sash })
            {
                CustomizationOption[] list = all.GetList(type);
                int open = 0;
                string which = "";
                for (int i = 0; i < list.Length; i++) if (!list[i].IsLocked) { open++; which += i + " "; }
                int before = type == Customization.Type.Hat ? d.currentHat : d.currentSash;
                pm.OpenTab(type);
                bool forced = false;
                PassportButton pick = FindButton(pm, before, false);
                if (pick == null)
                {
                    forced = true;
                    pm.testUnlockAll = true;      // only to prove the code path; switched back below
                    pm.SetButtons();
                    pick = FindButton(pm, before, true);
                }
                int want = pick != null ? pick.currentIndex : -1;
                if (pick != null) pick.button.onClick.Invoke();
                d = P2Passport.P2Data();
                int after = type == Customization.Type.Hat ? d.currentHat : d.currentSash;
                Plugin.Log.LogInfo("[debug] " + type + ": options=" + list.Length + " unlocked=" + open + " [" + which.Trim() + "] before=" + before + " clicked=" + want
                    + (forced ? " (locked option, unlock forced for the test)" : "") + " after=" + after + " -> " + (after == want && want != before ? "CHANGED" : "NOT CHANGED")
                    + " | hat shown=" + HatShown());
                if (forced)
                {
                    PassportButton back = null;
                    foreach (PassportButton b in pm.buttons) if (b.isActiveAndEnabled && b.currentIndex == before) back = b;
                    if (back != null) back.button.onClick.Invoke();
                    pm.testUnlockAll = false;
                    pm.SetButtons();
                }
            }
            Say("after hat/sash check");
        }

        private static string HatShown()
        {
            var hats = SplitManager.P2Character.refs.customization.refs.playerHats;
            if (hats == null) return "?";
            for (int i = 0; i < hats.Length; i++) if (hats[i] != null && hats[i].gameObject.activeSelf) return i.ToString();
            return "none";
        }

        private static PassportButton FindButton(PassportManager pm, int current, bool allowLocked)
        {
            foreach (PassportButton b in pm.buttons)
            {
                if (b == null || !b.isActiveAndEnabled || b.currentOption == null || b.currentIndex == current) continue;
                if (b.currentOption.IsLocked && !allowLocked) continue;
                return b;
            }
            return null;
        }

        private static float _levelAt = -1f;
        private static WindChillZone _testZone;

        /// <summary>Start a run, go somewhere with weather, and put the players on opposite sides of it.</summary>
        private static void FogScript(float t)
        {
            string scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
            if (scene == "Airport")
            {
                _levelAt = -1f;
                if (Once(t, 8f, "start"))
                {
                    AirportCheckInKiosk kiosk = Object.FindAnyObjectByType<AirportCheckInKiosk>();
                    Plugin.Log.LogInfo("[debug] starting a run, kiosk=" + (kiosk != null));
                    if (kiosk != null) kiosk.StartGame(0);
                }
                return;
            }
            if (_levelAt < 0f) _levelAt = t;
            float l = t - _levelAt;
            Character p1 = Character.localCharacter, p2 = SplitManager.P2Character;
            if (p1 == null || p2 == null) return;
            if (Once(l, 12f, "dump1")) FogDump("on the beach");
            if (Once(l, 14f, "jump"))
            {
                Plugin.Log.LogInfo("[debug] jumping to the tropics");
                MapHandler.JumpToSegment(Segment.Tropics);
            }
            if (Once(l, 26f, "wind"))
            {
                FogDump("in the tropics");
                foreach (WindChillZone z in Object.FindObjectsByType<WindChillZone>(FindObjectsSortMode.None))
                    if (z.isActiveAndEnabled) _testZone = z;
                if (_testZone != null && !_testZone.windActive) _testZone.untilSwitch = -1f;
                Plugin.Log.LogInfo("[debug] test zone=" + (_testZone != null ? _testZone.name + " bounds=" + _testZone.windZoneBounds : "none"));
            }
            if (l < 72f && _testZone != null)
            {
                _testZone.windActive = true;          // hold the storm on for the wind part of the test
                _testZone.timeUntilNextWind = 200f;
                _testZone.untilSwitch = 100f;
                Bounds b = _testZone.windZoneBounds;
                if (Once(l, 28f, "homes")) { _home1 = p1.Center; _home2 = p2.Center; }
                // 30-50: player 2 stands in the storm. 50-70: player 1 does.
                if (Once(l, 30f, "p2in")) p2.WarpPlayer(GroundIn(b), false);
                if (Once(l, 50f, "swap")) { p2.WarpPlayer(_home2, false); p1.WarpPlayer(GroundIn(b), false); }
                if (Once(l, 70f, "p1back")) p1.WarpPlayer(_home1, false);
                if (l >= 26f && Once(l, Mathf.Floor(l / 4f) * 4f, "f" + (int)(l / 4f)))
                    Plugin.Log.LogInfo("[debug] fog l=" + l.ToString("0") + " zoneActive=" + _testZone.windActive + " intensity=" + _testZone.windIntensity.ToString("0.00")
                        + " p1in=" + b.Contains(p1.Center) + " p2in=" + b.Contains(p2.Center) + " | " + P2Fog.Status());
                return;
            }
            if (Once(l, 74f, "caldera")) { Plugin.Log.LogInfo("[debug] jumping to the caldera"); MapHandler.JumpToSegment(Segment.Caldera); }
            if (Once(l, 88f, "dump3")) FogDump("in the caldera");
            if (Once(l, 92f, "kiln")) { Plugin.Log.LogInfo("[debug] jumping to the kiln"); MapHandler.JumpToSegment(Segment.TheKiln); }
            if (Once(l, 106f, "dump4")) FogDump("in the kiln");
            // Player 2 passes out and then dies standing on the ground.
            if (Once(l, 110f, "p2out")) { Plugin.Log.LogInfo("[debug] p2 passes out at " + p2.Center); p2.PassOutInstantly(); }
            if (Once(l, 118f, "p2dead") && SplitContext.Enter()) { try { Character.Die(); } finally { SplitContext.Exit(); } }
            if (l >= 72f && l < 130f && Once(l, Mathf.Floor(l / 4f) * 4f, "g" + (int)(l / 4f)))
                Plugin.Log.LogInfo("[debug] fog l=" + l.ToString("0") + " p1=" + p1.Center + " p2=" + p2.Center + " p2dead=" + p2.data.dead + " p2out=" + p2.data.passedOut + " | " + P2Fog.Status());
        }

        private static Vector3 _home1, _home2;

        /// <summary>Stand the two players facing each other in a few places, for screenshots.</summary>
        private static void PhotoScript(float t)
        {
            string scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
            if (scene == "Airport")
            {
                _levelAt = -1f;
                if (Once(t, 8f, "start"))
                {
                    AirportCheckInKiosk kiosk = Object.FindAnyObjectByType<AirportCheckInKiosk>();
                    if (kiosk != null) kiosk.StartGame(0);
                }
                return;
            }
            if (_levelAt < 0f) _levelAt = t;
            float l = t - _levelAt;
            Character p1 = Character.localCharacter, p2 = SplitManager.P2Character;
            if (p1 == null || p2 == null) return;
            // Each stop: 0 arrive, 4 place player 2, 6-20 face each other, screenshots run every 4 s.
            Segment[] stops = { Segment.Beach, Segment.Tropics, Segment.Alpine };
            int stop = (int)(l / 24f);
            if (stop >= stops.Length) return;
            float s = l - stop * 24f;
            if (stop > 0 && Once(l, stop * 24f, "jump" + stop)) MapHandler.JumpToSegment(stops[stop]);
            if (Once(l, stop * 24f + 6f, "place" + stop))
            {
                Vector3 fwd = p1.data.lookDirection_Flat;
                if (fwd.sqrMagnitude < 0.01f) fwd = Vector3.forward;
                Vector3 at = p1.Center + fwd.normalized * 3.2f + Vector3.up * 3f;
                if (Physics.Raycast(at, Vector3.down, out RaycastHit hit, 10f, ~0, QueryTriggerInteraction.Ignore)) at = hit.point + Vector3.up * 1.2f;
                p2.WarpPlayer(at, false);
            }
            if (s >= 8f && s < 22f)
            {
                Vector3 d = p2.Center - p1.Center;
                d.y = 0f;
                if (d.sqrMagnitude > 0.01f)
                {
                    Vector3 look1 = HelperFunctions.DirectionToLook(d.normalized + Vector3.down * 0.12f);
                    Vector3 look2 = HelperFunctions.DirectionToLook(-d.normalized + Vector3.down * 0.12f);
                    p1.data.lookValues = new Vector2(look1.x, look1.y);
                    p2.data.lookValues = new Vector2(look2.x, look2.y);
                }
                if (Once(l, Mathf.Floor(l / 4f) * 4f, "ph" + (int)(l / 4f)))
                    Plugin.Log.LogInfo("[debug] photo stop=" + stop + " s=" + s.ToString("0") + " p1=" + p1.Center + " p2=" + p2.Center);
            }
        }

        /// <summary>A spot on the ground inside the zone, so a warped player does not fall.</summary>
        private static Vector3 GroundIn(Bounds b)
        {
            for (int i = 0; i < 40; i++)
            {
                float x = Mathf.Lerp(b.min.x, b.max.x, 0.3f + 0.4f * ((i * 7) % 10) / 10f);
                float z = Mathf.Lerp(b.min.z, b.max.z, 0.3f + 0.4f * ((i * 3) % 10) / 10f);
                if (Physics.Raycast(new Vector3(x, b.max.y - 5f, z), Vector3.down, out RaycastHit hit, b.size.y - 10f, ~0, QueryTriggerInteraction.Ignore)
                    && hit.normal.y > 0.8f && b.Contains(hit.point + Vector3.up * 3f))
                    return hit.point + Vector3.up * 1.5f;
            }
            return b.center;
        }

        private static void FogDump(string where)
        {
            var sb = new System.Text.StringBuilder("[debug] fog objects " + where + ":");
            foreach (StormVisual s in Object.FindObjectsByType<StormVisual>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                sb.Append("\n  StormVisual " + s.name + " active=" + s.isActiveAndEnabled + " type=" + s.stormType + " parts=" + (s.part != null ? s.part.Length : 0)
                    + " quad=" + (s.quadRend != null) + " zoneIntensity=" + s.useWindChillZoneIntensity
                    + " sim=" + (s.part != null && s.part.Length > 0 && s.part[0] != null ? s.part[0].main.simulationSpace.ToString() : "?"));
            foreach (WindChillZone z in Object.FindObjectsByType<WindChillZone>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                sb.Append("\n  WindChillZone " + z.name + " active=" + z.isActiveAndEnabled + " wind=" + z.windActive + " bounds=" + z.windZoneBounds);
            Fog fog = Object.FindAnyObjectByType<Fog>(FindObjectsInactive.Include);
            sb.Append("\n  Fog=" + (fog != null ? "yes enabled=" + fog.isActiveAndEnabled + " stops=" + (fog.stops != null) + " particles=" + (fog.fogParticles != null) : "no"));
            FogSphere sphere = Object.FindAnyObjectByType<FogSphere>(FindObjectsInactive.Include);
            sb.Append("\n  FogSphere=" + (sphere != null ? "yes size=" + sphere.currentSize.ToString("0") + " enable=" + sphere.ENABLE : "no"));
            sb.Append("\n  DispelFogField=" + Object.FindObjectsByType<DispelFogField>(FindObjectsInactive.Include, FindObjectsSortMode.None).Length
                + " PlayerShaderParams=" + (Object.FindAnyObjectByType<PlayerShaderParams>() != null)
                + " GloomSafeZones=" + Peak.GloomSafeZone.ALL_GLOOM_SAFE_ZONES.Count
                + " p1=" + Character.localCharacter.Center + " p2=" + SplitManager.P2Character.Center);
            LavaPost lava = Object.FindAnyObjectByType<LavaPost>(FindObjectsInactive.Include);
            sb.Append("\n  LavaPost=" + (lava != null ? "yes enabled=" + lava.isActiveAndEnabled + " rend=" + (lava.rend != null && lava.rend.enabled) + " parent=" + (lava.transform.parent != null ? lava.transform.parent.name : "none")
                + " onMainCam=" + (MainCamera.instance != null && lava.transform.IsChildOf(MainCamera.instance.transform)) + " first=" + lava.firstIsActive : "no"));
            FogCutoutHandler cut = Object.FindAnyObjectByType<FogCutoutHandler>(FindObjectsInactive.Include);
            sb.Append("\n  FogCutoutHandler=" + (cut != null ? "yes enabled=" + cut.isActiveAndEnabled + " zones=" + (cut.cutoutZones != null ? cut.cutoutZones.Length : 0) + " index=" + cut.index
                + " current=" + (cut.currentCutoutZone != null ? cut.currentCutoutZone.name : "none") : "no"));
            Plugin.Log.LogInfo(sb.ToString());
        }

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
