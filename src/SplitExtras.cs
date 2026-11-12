using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace PeakSplitScreen
{
    /// <summary>
    /// Things player 2 needs beyond their own Character/Player objects: an interaction
    /// pass of their own, the item they hold, and (see P2Hud) a HUD of their own.
    /// </summary>
    internal static class SplitExtras
    {
        public static Interaction P2Interaction;

        // Root objects (besides player 2's Character and Player) whose scripts run as player 2.
        private static readonly List<Transform> Roots = new List<Transform>();
        private static Transform _heldRoot;
        private static Item _held;

        // Game singletons that have a player 2 twin; swapped by SplitContext.
        private static readonly List<FieldInfo> SwapFields = new List<FieldInfo>();
        private static readonly List<object> SwapP2 = new List<object>();
        private static object[] _swapSaved = new object[0];

        public static bool IsExtraRoot(Transform root)
        {
            if (ReferenceEquals(root, _heldRoot)) return true;
            for (int i = 0; i < Roots.Count; i++)
                if (ReferenceEquals(root, Roots[i])) return true;
            return false;
        }

        public static void AddRoot(GameObject go)
        {
            Roots.Add(go.transform.root);
            Patches.PatchContextFor(go);
        }

        public static void RemoveRoot(GameObject go)
        {
            if (go != null) Roots.Remove(go.transform.root);
            Roots.RemoveAll(t => t == null);
        }

        public static void RegisterSwap(FieldInfo field, object p2Value)
        {
            for (int i = 0; i < SwapFields.Count; i++)
                if (SwapFields[i] == field) { SwapP2[i] = p2Value; return; }
            SwapFields.Add(field);
            SwapP2.Add(p2Value);
            _swapSaved = new object[SwapFields.Count];
        }

        public static void UnregisterSwaps(Predicate<object> match)
        {
            for (int i = SwapFields.Count - 1; i >= 0; i--)
                if (match(SwapP2[i])) { SwapFields.RemoveAt(i); SwapP2.RemoveAt(i); }
            _swapSaved = new object[SwapFields.Count];
        }

        public static void EnterSwaps()
        {
            for (int i = 0; i < SwapFields.Count; i++)
            {
                object p2 = SwapP2[i];
                _swapSaved[i] = SwapFields[i].GetValue(null);
                if (p2 is UnityEngine.Object o && o == null) { _swapSaved[i] = null; continue; }
                SwapFields[i].SetValue(null, p2);
            }
        }

        public static void ExitSwaps()
        {
            for (int i = 0; i < SwapFields.Count; i++)
            {
                if (_swapSaved[i] == null) continue;
                SwapFields[i].SetValue(null, _swapSaved[i]);
                _swapSaved[i] = null;
            }
        }

        // ------------------------------------------------------------------ per frame

        public static void Tick()
        {
            EnsureInteraction();
            TrackHeldItem();
            P2Hud.Tick();
        }

        /// <summary>The game has one Interaction object that serves "the" local player. Player 2 gets a twin.</summary>
        private static void EnsureInteraction()
        {
            if (P2Interaction != null) return;
            Interaction p1 = Interaction.instance;
            if (p1 == null) return;

            var go = new GameObject("SplitScreen P2 Interaction");
            go.SetActive(false);
            P2Interaction = go.AddComponent<Interaction>();
            P2Interaction.distance = p1.distance;
            P2Interaction.area = p1.area;
            P2Interaction.maxCharacterInteractAngle = p1.maxCharacterInteractAngle;
            AddRoot(go);
            RegisterSwap(typeof(Interaction).GetField("instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic), P2Interaction);
            go.SetActive(true);          // Awake overwrites the singleton...
            Interaction.instance = p1;   // ...so put player 1's back.
            Plugin.Log.LogInfo("Player 2 can now interact with the world.");
        }

        /// <summary>The item in player 2's hands is a separate object; its scripts must run as player 2 too.</summary>
        private static void TrackHeldItem()
        {
            Character c = SplitManager.P2Character;
            Item item = c != null && c.data != null ? c.data.currentItem : null;
            if (ReferenceEquals(item, _held)) return;
            _held = item;
            if (item == null) { _heldRoot = null; return; }
            _heldRoot = item.transform.root;
            Patches.PatchContextFor(item.gameObject);
        }

        public static void OnP2Lost()
        {
            P2Hud.Destroy();
            _held = null;
            _heldRoot = null;
            if (P2Interaction != null)
            {
                RemoveRoot(P2Interaction.gameObject);
                UnityEngine.Object.Destroy(P2Interaction.gameObject);
            }
            UnregisterSwaps(v => ReferenceEquals(v, P2Interaction));
            P2Interaction = null;
            Roots.RemoveAll(t => t == null);
        }
    }
}
