using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.InputSystem;

namespace PeakSplitScreen
{
    /// <summary>
    /// The game keeps "the local player" in static fields. While code that belongs to
    /// player 2 runs, we point those statics at player 2 and put them back afterwards.
    /// </summary>
    internal static class SplitContext
    {
        public static bool Active { get; private set; }

        private static Character _savedCharacter;
        private static Player _savedPlayer;
        private static MainCamera _savedCamera;
        public static MainCamera SavedCamera => _savedCamera;
        private static Character _savedSpec;

        // CharacterInput's static InputAction fields, and player 2's equivalents.
        private static FieldInfo[] _actionFields;
        private static InputAction[] _savedActions;
        private static InputAction[] _p2Actions;
        private static InputAction[] _savedHotbar;
        private static InputAction[] _p2Hotbar;

        public static void ResetInputCache()
        {
            _p2Actions = null;
            _p2Hotbar = null;
        }

        /// <summary>Enter player 2's context. Returns false if it was already active.</summary>
        public static bool Enter()
        {
            if (Active || SplitManager.P2Character == null) return false;
            Active = true;

            _savedCharacter = Character.localCharacter;
            _savedPlayer = Player.localPlayer;
            _savedCamera = MainCamera.instance;
            _savedSpec = MainCameraMovement.specCharacter;
            MainCameraMovement.specCharacter = SplitManager.P2Spec;

            Character.localCharacter = SplitManager.P2Character;
            if (SplitManager.P2Player != null) Player.localPlayer = SplitManager.P2Player;
            if (SplitManager.P2MainCamera != null) MainCamera.instance = SplitManager.P2MainCamera;
            SwapActions(true);
            SplitExtras.EnterSwaps();
            return true;
        }

        public static void Exit()
        {
            if (!Active) return;
            SplitExtras.ExitSwaps();
            SwapActions(false);
            Character.localCharacter = _savedCharacter;
            Player.localPlayer = _savedPlayer;
            MainCamera.instance = _savedCamera;
            MainCameraMovement.specCharacter = _savedSpec;
            Active = false;
        }

        private static void SwapActions(bool toP2)
        {
            var asset = SplitManager.P2Actions;
            if (asset == null) return;

            if (_actionFields == null)
            {
                var list = new List<FieldInfo>();
                foreach (var f in typeof(CharacterInput).GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                    if (f.FieldType == typeof(InputAction)) list.Add(f);
                _actionFields = list.ToArray();
                _savedActions = new InputAction[_actionFields.Length];
            }

            if (toP2)
            {
                if (CharacterInput.action_move == null) return; // game has not initialised input yet
                if (_p2Actions == null)
                {
                    _p2Actions = new InputAction[_actionFields.Length];
                    for (int i = 0; i < _actionFields.Length; i++)
                    {
                        var p1 = (InputAction)_actionFields[i].GetValue(null);
                        _p2Actions[i] = p1 == null ? null : asset.FindAction(p1.name);
                    }
                    var hot = CharacterInput.hotbarActions;
                    _p2Hotbar = new InputAction[hot.Length];
                    for (int i = 0; i < hot.Length; i++)
                        _p2Hotbar[i] = hot[i] == null ? null : asset.FindAction(hot[i].name);
                }
                for (int i = 0; i < _actionFields.Length; i++)
                {
                    _savedActions[i] = (InputAction)_actionFields[i].GetValue(null);
                    if (_p2Actions[i] != null) _actionFields[i].SetValue(null, _p2Actions[i]);
                }
                _savedHotbar = CharacterInput.hotbarActions;
                CharacterInput.hotbarActions = _p2Hotbar;
            }
            else
            {
                if (_savedHotbar == null) return;
                for (int i = 0; i < _actionFields.Length; i++)
                    _actionFields[i].SetValue(null, _savedActions[i]);
                CharacterInput.hotbarActions = _savedHotbar;
                _savedHotbar = null;
            }
        }
    }
}
