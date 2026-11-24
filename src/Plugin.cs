using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace PeakSplitScreen
{
    [BepInPlugin(Guid, "Spleak", Version)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "orchen17.peak.splitscreen";
        public const string Version = "0.1.0";
        private const string OldGuid = "local.peak.splitscreen";

        internal static ManualLogSource Log;
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<bool> SideBySide;
        internal static ConfigEntry<bool> P2HudEnabled;
        internal static ConfigEntry<bool> TwoPlayers;
        internal static ConfigEntry<bool> P2Sound;
        internal static ConfigEntry<string> P2Cosmetics;
        internal static ConfigEntry<string> DebugScript;
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
            // Settings saved under the mod's earlier ID carry over once.
            try
            {
                string old = System.IO.Path.Combine(Paths.ConfigPath, OldGuid + ".cfg");
                if (System.IO.File.Exists(old) && !System.IO.File.Exists(Config.ConfigFilePath))
                {
                    System.IO.File.Copy(old, Config.ConfigFilePath);
                    Config.Reload();
                }
            }
            catch (System.Exception e) { Log.LogWarning("Could not carry over old settings: " + e.Message); }
            // Test options live in their own file, which is read if present and never created.
            ConfigFile dbg = new ConfigFile(System.IO.Path.Combine(Paths.ConfigPath, Guid + ".debug.cfg"), false);
            dbg.SaveOnConfigSet = false;
            Enabled = Config.Bind("General", "Enabled", true,
                "Spawn a second, gamepad-controlled player when playing OFFLINE with a gamepad connected.");
            SideBySide = Config.Bind("General", "SideBySide", true,
                "true = left/right split, false = top/bottom split.");
            P2HudEnabled = Config.Bind("General", "Player2Hud", true,
                "Give player 2 their own HUD (stamina, items, prompts) in their half of the window.");
            TwoPlayers = Config.Bind("General", "TwoPlayers", true,
                "Set by the title menu: Play Split-Screen turns this on, Play Solo turns it off. Also decides how a saved offline run continues.");
            P2Sound = Config.Bind("General", "Player2Sound", true,
                "Let both players hear what is around them: each sound plays for whichever player is nearer. Music stays shared.");
            P2Cosmetics = Config.Bind("Player2", "Cosmetics", "",
                "Player 2's look, saved when they change it in their passport (skin, accessory, eyes, mouth, outfit, hat, sash, medal). Leave empty for a random look.");
            DebugScript = dbg.Bind("Debug", "Script", "basics",
                "Testing only: what the virtual gamepad does (basics, passport, run, fog).");
            DebugAutoOffline = dbg.Bind("Debug", "AutoStartOffline", false,
                "Testing only: automatically start an offline game from the main menu.");
            DebugNoGamepad = dbg.Bind("Debug", "SpawnWithoutGamepad", false,
                "Testing only: spawn player 2 even when no gamepad is connected.");

            DebugMute = dbg.Bind("Debug", "Mute", false, "Testing only: silence the game.");
            DebugScreenshots = dbg.Bind("Debug", "Screenshots", false,
                "Testing only: save a screenshot every few seconds to BepInEx/splitscreen-shots.");
            DebugFakeGamepad = dbg.Bind("Debug", "FakeGamepad", false,
                "Testing only: create a scripted virtual gamepad that drives player 2.");

            Harmony = new Harmony(Guid);
            Patches.Apply(Harmony);
            MenuOption.Apply(Harmony);
            P2Passport.Apply(Harmony);
            P2Audio.Apply(Harmony);
            Log.LogInfo("Spleak loaded. Pick Play Split-Screen on the title menu with a gamepad connected.");
        }

        private void Update()
        {
            try { DebugTools.Tick(); }
            catch (System.Exception e) { Log.LogError(e); }
            try { SplitManager.Tick(); }
            catch (System.Exception e) { SplitManager.Fail("Tick", e); }
            try { P2Passport.Tick(); }
            catch (System.Exception e) { Log.LogError("Passport: " + e); }
            P2Audio.Watchdog();
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
                    // Press whichever menu button matches the last choice.
                    if (TwoPlayers.Value && MenuOption.SplitButton != null) MenuOption.SplitButton.onClick.Invoke();
                    else if (menu.playSoloButton != null) menu.playSoloButton.onClick.Invoke();
                    else menu.PlaySoloClicked();
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
            try { P2Fog.Tick(); }
            catch (System.Exception e) { Log.LogError("Fog: " + e); }
        }
    }
}
