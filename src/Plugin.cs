using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace PeakSplitScreen
{
    [BepInPlugin(Guid, "PEAK Split Screen", "0.1.0")]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "local.peak.splitscreen";

        internal static ManualLogSource Log;
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<bool> SideBySide;
        internal static ConfigEntry<bool> P2HudEnabled;
        internal static ConfigEntry<bool> DebugAutoOffline;
        internal static ConfigEntry<bool> DebugNoGamepad;
        internal static ConfigEntry<bool> DebugMute;
        internal static ConfigEntry<bool> DebugScreenshots;
        internal static ConfigEntry<bool> DebugFakeGamepad;
        internal static Harmony Harmony;

        private float _menuSeenAt = -1f;
        private bool _autoStarted;
        private float _nextStatus;

        private void Awake()
        {
            Log = Logger;
            Enabled = Config.Bind("General", "Enabled", true,
                "Spawn a second, gamepad-controlled player when playing OFFLINE with a gamepad connected.");
            SideBySide = Config.Bind("General", "SideBySide", true,
                "true = left/right split, false = top/bottom split.");
            P2HudEnabled = Config.Bind("General", "Player2Hud", true,
                "Give player 2 their own HUD (stamina, items, prompts) in their half of the window.");
            DebugAutoOffline = Config.Bind("Debug", "AutoStartOffline", false,
                "Testing only: automatically start an offline game from the main menu.");
            DebugNoGamepad = Config.Bind("Debug", "SpawnWithoutGamepad", false,
                "Testing only: spawn player 2 even when no gamepad is connected.");

            DebugMute = Config.Bind("Debug", "Mute", false, "Testing only: silence the game.");
            DebugScreenshots = Config.Bind("Debug", "Screenshots", false,
                "Testing only: save a screenshot every few seconds to BepInEx/splitscreen-shots.");
            DebugFakeGamepad = Config.Bind("Debug", "FakeGamepad", false,
                "Testing only: create a scripted virtual gamepad that drives player 2.");

            Harmony = new Harmony(Guid);
            Patches.Apply(Harmony);
            Log.LogInfo("PEAK Split Screen loaded. Start an OFFLINE game with a gamepad connected.");
        }

        private void Update()
        {
            try { DebugTools.Tick(); }
            catch (System.Exception e) { Log.LogError(e); }
            try { SplitManager.Tick(); }
            catch (System.Exception e) { SplitManager.Fail("Tick", e); }
            try { DebugTick(); }
            catch (System.Exception e) { Log.LogError(e); }
        }

        private void DebugTick()
        {
            if (DebugAutoOffline.Value && !_autoStarted)
            {
                MainMenu menu = FindAnyObjectByType<MainMenu>();
                if (menu == null) _menuSeenAt = -1f;
                else if (_menuSeenAt < 0f) _menuSeenAt = Time.unscaledTime;
                else if (Time.unscaledTime - _menuSeenAt > 6f)
                {
                    _autoStarted = true;
                    Log.LogInfo("[debug] Starting offline game automatically.");
                    menu.PlaySoloClicked();
                }
            }
            if ((DebugAutoOffline.Value || DebugNoGamepad.Value || DebugFakeGamepad.Value) && Time.unscaledTime >= _nextStatus)
            {
                _nextStatus = Time.unscaledTime + 5f;
                Log.LogInfo("[debug] " + SplitManager.Status());
            }
        }

        private void LateUpdate()
        {
            try { SplitManager.LateTick(); }
            catch (System.Exception e) { SplitManager.Fail("LateTick", e); }
        }
    }
}
