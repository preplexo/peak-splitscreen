using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Photon.Pun;
using UnityEngine;
using Zorro.ControllerSupport;

namespace PeakSplitScreen
{
    internal static class Patches
    {
        private const BindingFlags All = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        private static Harmony _h;

        public static void Apply(Harmony h)
        {
            _h = h;
            Hazards.Apply(h);
            Type me = typeof(Patches);

            // Player 2's network objects count as "mine".
            h.Patch(AccessTools.PropertySetter(typeof(PhotonView), nameof(PhotonView.ControllerActorNr)),
                postfix: new HarmonyMethod(me.GetMethod(nameof(ControllerPostfix), All)));
            h.Patch(AccessTools.PropertySetter(typeof(PhotonView), nameof(PhotonView.OwnerActorNr)),
                postfix: new HarmonyMethod(me.GetMethod(nameof(OwnerPostfix), All)));

            // Things the game does once for "the" local player that must not happen again for player 2.
            var skip = new HarmonyMethod(me.GetMethod(nameof(SkipWhileSpawning), All));
            TryPatch(AccessTools.Method(typeof(VoiceClientHandler), "LocalPlayerAssigned"), skip, "VoiceClientHandler.LocalPlayerAssigned");
            TryPatch(AccessTools.Method(typeof(Player), "AssignUserID"), skip, "Player.AssignUserID");
            TryPatch(AccessTools.Method(typeof(Player), "AssignPlatformFamily"), skip, "Player.AssignPlatformFamily");
            TryPatch(AccessTools.Method(typeof(Player), "AssignProductUserID"), skip, "Player.AssignProductUserID");

            TryPatch(AccessTools.Method(typeof(InputHandler), nameof(InputHandler.GetCurrentUsedInputScheme)),
                new HarmonyMethod(me.GetMethod(nameof(SchemePrefix), All)), "InputHandler.GetCurrentUsedInputScheme");
            TryPatch(AccessTools.Method(typeof(HideTheBody), "Update"),
                new HarmonyMethod(me.GetMethod(nameof(SkipWhileActive), All)), "HideTheBody.Update");

            TryPatch(AccessTools.Method(typeof(GUIManager), nameof(GUIManager.UpdatePaused)),
                new HarmonyMethod(me.GetMethod(nameof(PausePrefix), All)), "GUIManager.UpdatePaused");

            // Offline, Photon drops RPCs addressed to anyone but the one real player. Deliver player 2's locally.
            TryPatch(AccessTools.Method(typeof(PhotonNetwork), "RPC", new[] { typeof(PhotonView), typeof(string), typeof(RpcTarget), typeof(Photon.Realtime.Player), typeof(bool), typeof(object[]) }),
                new HarmonyMethod(me.GetMethod(nameof(RpcTargetPrefix), All)), "PhotonNetwork.RPC");

            // The selection wheels read the stick from one global action; give player 2's wheels their own pad.
            var wheel = new HarmonyMethod(me.GetMethod(nameof(WheelPostfix), All));
            foreach (Type t in new[] { typeof(EmoteWheel), typeof(BackpackWheel) })
            {
                MethodInfo m = AccessTools.DeclaredMethod(t, "Update");
                if (m == null) Plugin.Log.LogWarning("Method not found, not patched: " + t.Name + ".Update");
                else h.Patch(m, postfix: wheel);
            }
        }

        private static UnityEngine.InputSystem.InputAction _p2Wheel;
        private static UnityEngine.InputSystem.InputActionAsset _p2WheelAsset;

        private static void WheelPostfix(UIWheel __instance)
        {
            if (!SplitManager.Active || SplitManager.P2Actions == null) return;
            if (!SplitManager.IsP2(__instance) || !__instance.gameObject.activeInHierarchy) return;
            if (!ReferenceEquals(_p2WheelAsset, SplitManager.P2Actions))
            {
                _p2WheelAsset = SplitManager.P2Actions;
                _p2Wheel = _p2WheelAsset.FindAction("NavigateWheel");
            }
            if (_p2Wheel == null) return;
            Vector2 v = _p2Wheel.ReadValue<Vector2>();
            bool entered = SplitContext.Enter();
            try { __instance.TestSelectSliceGamepad(v); }
            finally { if (entered) SplitContext.Exit(); }
        }

        /// <summary>Player 2's HUD copy has no pause menu of its own; pausing opens the shared one.</summary>
        private static bool PausePrefix(GUIManager __instance)
        {
            if (!P2Hud.IsClone(__instance)) return true;
            Character c = Character.localCharacter;
            if (c != null && c.input.pauseWasPressed) { c.input.pauseWasPressed = false; P2Hud.RequestPause(); }
            return false;
        }

        private static void TryPatch(MethodBase m, HarmonyMethod prefix, string name)
        {
            if (m == null) { Plugin.Log.LogWarning("Method not found, not patched: " + name); return; }
            _h.Patch(m, prefix: prefix);
        }

        private static void ControllerPostfix(PhotonView __instance)
        {
            if (SplitManager.IsP2Actor(__instance.controllerActorNr)) __instance.IsMine = true;
        }

        private static void OwnerPostfix(PhotonView __instance)
        {
            if (SplitManager.IsP2Actor(__instance.ownerActorNr)) __instance.AmOwner = true;
        }

        private static bool SkipWhileSpawning() => !SplitManager.Spawning;

        private static bool SkipWhileActive() => !SplitManager.Active;

        private static void RpcTargetPrefix(ref Photon.Realtime.Player player)
        {
            if (player != null && SplitManager.IsP2Actor(player.ActorNumber)) player = PhotonNetwork.LocalPlayer;
        }

        private static bool SchemePrefix(ref InputScheme __result)
        {
            if (!SplitContext.Active) return true;
            __result = InputScheme.Gamepad;
            return false;
        }

        // ------------------------------------------------------------------ context patches

        private static readonly HashSet<Type> SeenTypes = new HashSet<Type>();
        private static readonly HashSet<string> Messages = new HashSet<string> { "Start", "Update", "LateUpdate", "FixedUpdate" };

        /// <summary>
        /// Wrap the per-frame methods and RPCs of every game script found on this object so that,
        /// when they run for player 2, the game's "local player" statics point at player 2.
        /// </summary>
        public static void PatchContextFor(GameObject root)
        {
            Assembly game = typeof(Character).Assembly;
            var prefix = new HarmonyMethod(typeof(Patches).GetMethod(nameof(ContextPrefix), All));
            var finalizer = new HarmonyMethod(typeof(Patches).GetMethod(nameof(ContextFinalizer), All));
            int count = 0, failed = 0;

            foreach (MonoBehaviour mb in root.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (mb == null) continue;
                for (Type t = mb.GetType(); t != null && t.Assembly == game; t = t.BaseType)
                {
                    if (!SeenTypes.Add(t)) continue;
                    if (t.ContainsGenericParameters) continue;
                    foreach (MethodInfo m in t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    {
                        if (m.IsAbstract || m.IsGenericMethod || m.GetMethodBody() == null) continue;
                        bool wanted = (Messages.Contains(m.Name) && m.GetParameters().Length == 0)
                                      || m.IsDefined(typeof(PunRPC), false);
                        if (!wanted) continue;
                        try { _h.Patch(m, prefix: prefix, finalizer: finalizer); count++; }
                        catch (Exception e) { failed++; Plugin.Log.LogWarning($"Could not wrap {t.Name}.{m.Name}: {e.Message}"); }
                    }
                }
            }
            if (count > 0 || failed > 0)
                Plugin.Log.LogInfo($"Wrapped {count} methods for player 2 context on '{root.name}' ({failed} failed).");
        }

        private static void ContextPrefix(MonoBehaviour __instance, out bool __state)
        {
            __state = false;
            if (SplitContext.Active) return;
            bool p2 = __instance is IsLookedAt tag ? P2Hud.TagRunsAsP2(tag) : SplitManager.IsP2(__instance);
            if (!p2) return;
            __state = SplitContext.Enter();
        }

        private static void ContextFinalizer(bool __state)
        {
            if (__state) SplitContext.Exit();
        }
    }
}
