using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace PeakSplitScreen
{
    /// <summary>
    /// World hazards (poison clouds, fog, wind, lava, sun, traps) only ever look at "the local
    /// player". After each one has done its work for player 1, do the same part again for player 2.
    /// </summary>
    internal static class Hazards
    {
        private const BindingFlags All = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        private static Harmony _h;
        private static bool _busy;

        private sealed class Slot { public float time; public bool inZone; public bool priority; }
        private sealed class Num { public float value; }
        private sealed class Wind { public bool inside; public float factor; }

        private static readonly ConditionalWeakTable<StatusEmitter, Slot> Emitters = new ConditionalWeakTable<StatusEmitter, Slot>();
        private static readonly ConditionalWeakTable<MonoBehaviour, Num> Counters = new ConditionalWeakTable<MonoBehaviour, Num>();
        private static readonly ConditionalWeakTable<WindChillZone, Wind> Winds = new ConditionalWeakTable<WindChillZone, Wind>();

        public static void Apply(Harmony h)
        {
            _h = h;
            Post(typeof(StatusEmitter), "Update", nameof(EmitterPost));
            Post(typeof(Fog), "Update", nameof(FogPost));
            Post(typeof(FogSphere), "Update", nameof(FogSpherePost));
            Post(typeof(WindChillZone), "Update", nameof(WindUpdatePost));
            Post(typeof(WindChillZone), "FixedUpdate", nameof(WindFixedPost));
            Post(typeof(Lava), "Update", nameof(LavaUpdatePost));
            Post(typeof(Lava), "FixedUpdate", nameof(LavaFixedPost));
            Post(typeof(HotSun), "Update", nameof(HotSunPost));
            Post(typeof(InjurySphere), "Update", nameof(InjurySpherePost));
            Post(typeof(BananaPeel), "Update", nameof(BananaPost));
            Wrap(typeof(Campfire), "Update", nameof(CampfirePre), nameof(CampfirePost));

            // Traps that react to a touch but only if the toucher is "the local player".
            Wrap(typeof(SwingingAxe), "OnCollisionEnter", nameof(CollisionPre), nameof(ContextFinal), true);
            Wrap(typeof(SlipperyJellyfish), "OnTriggerEnter", nameof(TriggerPre), nameof(ContextFinal), true);
            Wrap(typeof(TombTrigger), "OnTriggerEnter", nameof(TriggerPre), nameof(ContextFinal), true);
            Wrap(typeof(BreakableBridge), "OnBridgeCollision", nameof(CharacterPre), nameof(ContextFinal), true);

            // Each player has their own "which of the overlapping clouds counts" bookkeeping.
            FieldInfo overlap = typeof(StatusEmitter).GetField("overlapPreventedStatusEmitters", All);
            if (overlap != null) SplitExtras.RegisterSwap(overlap, new Dictionary<string, StatusEmitter>());
        }

        private static void Post(Type t, string method, string postfix)
        {
            MethodInfo m = AccessTools.DeclaredMethod(t, method);
            if (m == null) { Plugin.Log.LogWarning($"Hazard not found, player 2 is immune to it: {t.Name}.{method}"); return; }
            try { _h.Patch(m, postfix: new HarmonyMethod(typeof(Hazards).GetMethod(postfix, All))); }
            catch (Exception e) { Plugin.Log.LogWarning($"Could not patch {t.Name}.{method}: {e.Message}"); }
        }

        private static void Wrap(Type t, string method, string prefix, string post, bool finalizer = false)
        {
            MethodInfo m = AccessTools.DeclaredMethod(t, method);
            if (m == null) { Plugin.Log.LogWarning($"Hazard not found, player 2 is immune to it: {t.Name}.{method}"); return; }
            var pre = new HarmonyMethod(typeof(Hazards).GetMethod(prefix, All));
            var after = new HarmonyMethod(typeof(Hazards).GetMethod(post, All));
            try
            {
                if (finalizer) _h.Patch(m, prefix: pre, finalizer: after);
                else _h.Patch(m, prefix: pre, postfix: after);
            }
            catch (Exception e) { Plugin.Log.LogWarning($"Could not patch {t.Name}.{method}: {e.Message}"); }
        }

        /// <summary>Become player 2 for a moment. False if this already is player 2's turn (or there is no player 2).</summary>
        private static bool Begin()
        {
            if (_busy || !SplitManager.Active || SplitContext.Active) return false;
            if (!SplitContext.Enter()) return false;
            _busy = true;
            return true;
        }

        private static void End()
        {
            _busy = false;
            SplitContext.Exit();
        }

        private static void Report(string what, Exception e)
        {
            Plugin.Log.LogWarning($"Hazard for player 2 failed ({what}): {e.Message}");
        }

        // ------------------------------------------------------------------ status clouds

        private static void EmitterPost(StatusEmitter __instance)
        {
            if (!Begin()) return;
            try
            {
                StatusEmitter e = __instance;
                if (!Emitters.TryGetValue(e, out Slot slot))
                {
                    Character p2 = Character.localCharacter;
                    if (Vector3.Distance(p2.Center, e.transform.position) >= e.radius + e.outerFade) return;
                    slot = new Slot();
                    Emitters.Add(e, slot);
                }
                float time = e.timeSinceLastTick; bool inZone = e.inZone; bool priority = e.isOverlapPriority;
                e.timeSinceLastTick = slot.time; e.inZone = slot.inZone; e.isOverlapPriority = slot.priority;
                try { e.Update(); }
                finally
                {
                    slot.time = e.timeSinceLastTick; slot.inZone = e.inZone; slot.priority = e.isOverlapPriority;
                    e.timeSinceLastTick = time; e.inZone = inZone; e.isOverlapPriority = priority;
                }
            }
            catch (Exception ex) { Report("status cloud", ex); }
            finally { End(); }
        }

        // ------------------------------------------------------------------ the fog

        private static void FogPost(Fog __instance)
        {
            if (__instance.stops == null || !__instance.enabled) return;
            if (!Begin()) return;
            try
            {
                __instance.MakePlayerCold();
                if (__instance.fogParticles != null)
                    Character.localCharacter.data.isInFog = Character.localCharacter.Center.y < __instance.transform.position.y;
            }
            catch (Exception ex) { Report("fog", ex); }
            finally { End(); }
        }

        private static void FogSpherePost(FogSphere __instance)
        {
            Character p2 = SplitManager.P2Character;
            if (p2 == null || SplitContext.Active || !Application.isPlaying) return;
            try
            {
                p2.data.isInFog = false;
                if (!Mathf.Approximately(__instance.ENABLE, 1f)) return;
                if (Vector3.Distance(__instance.fogPoint, p2.Center) <= __instance.currentSize) return;
                const float rate = 0.0105f;
                if (p2.data.isSkeleton) p2.refs.afflictions.AddStatus(CharacterAfflictions.STATUSTYPE.Injury, rate / 8f * Time.deltaTime);
                else p2.refs.afflictions.AddStatus(CharacterAfflictions.STATUSTYPE.Cold, rate * Time.deltaTime);
                p2.data.isInFog = true;
            }
            catch (Exception ex) { Report("fog sphere", ex); }
        }

        // ------------------------------------------------------------------ wind

        private static void WindUpdatePost(WindChillZone __instance)
        {
            Character p2 = SplitManager.P2Character;
            if (p2 == null || SplitContext.Active) return;
            try
            {
                Wind w = Winds.GetOrCreateValue(__instance);
                w.inside = __instance.windZoneBounds.Contains(p2.Center);
                w.factor = 0f;
                if (!w.inside || !__instance.windActive) return;
                float saved = __instance.windPlayerFactor;
                __instance.ApplyStatus(p2);
                w.factor = __instance.windPlayerFactor;
                __instance.windPlayerFactor = saved;
            }
            catch (Exception ex) { Report("wind", ex); }
        }

        private static void WindFixedPost(WindChillZone __instance)
        {
            Character p2 = SplitManager.P2Character;
            if (p2 == null || SplitContext.Active) return;
            try
            {
                if (!Winds.TryGetValue(__instance, out Wind w) || !w.inside || !__instance.windActive) return;
                if (__instance.timeSpentWaiting < __instance.delayBeforeForce) return;
                float saved = __instance.windPlayerFactor;
                __instance.windPlayerFactor = w.factor;
                try { __instance.AddWindForceToCharacter(p2, __instance.currentForceMult); }
                finally { __instance.windPlayerFactor = saved; }
            }
            catch (Exception ex) { Report("wind force", ex); }
        }

        // ------------------------------------------------------------------ lava, sun, small stuff

        private static void LavaUpdatePost(Lava __instance)
        {
            if (!Begin()) return;
            try
            {
                Num n = Counters.GetOrCreateValue(__instance);
                float saved = __instance.counter;
                __instance.counter = n.value;
                try { __instance.Heat(); }
                finally { n.value = __instance.counter; __instance.counter = saved; }
            }
            catch (Exception ex) { Report("lava heat", ex); }
            finally { End(); }
        }

        private static void LavaFixedPost(Lava __instance)
        {
            if (!Begin()) return;
            try { __instance.DoEffects(); }
            catch (Exception ex) { Report("lava", ex); }
            finally { End(); }
        }

        private static void HotSunPost(HotSun __instance)
        {
            if (!Application.isPlaying || !Begin()) return;
            try
            {
                Num n = Counters.GetOrCreateValue(__instance);
                float saved = __instance.counter;
                __instance.counter = n.value;
                try { __instance.Update(); }
                finally { n.value = __instance.counter; __instance.counter = saved; }
            }
            catch (Exception ex) { Report("sun", ex); }
            finally { End(); }
        }

        private static void InjurySpherePost(InjurySphere __instance)
        {
            if (!Begin()) return;
            try { __instance.Update(); }
            catch (Exception ex) { Report("injury zone", ex); }
            finally { End(); }
        }

        private static void BananaPost(BananaPeel __instance)
        {
            if (!Begin()) return;
            try
            {
                __instance.counter -= Time.deltaTime;   // the rerun below adds it again
                __instance.Update();
            }
            catch (Exception ex) { Report("banana peel", ex); }
            finally { End(); }
        }

        // ------------------------------------------------------------------ campfire

        private static void CampfirePre(Campfire __instance, out bool __state)
        {
            __state = __instance.fireHasStarted;
        }

        private static void CampfirePost(Campfire __instance, bool __state)
        {
            if (!__instance.Lit) return;
            bool justLit = !__state && __instance.fireHasStarted;
            if (!Begin()) return;
            try
            {
                Character p2 = Character.localCharacter;
                Num n = Counters.GetOrCreateValue(__instance);
                float saved = __instance._timebuffLastApplied;
                __instance._timebuffLastApplied = n.value;
                try { __instance.ApplyCampfireProtection(); }
                finally { n.value = __instance._timebuffLastApplied; __instance._timebuffLastApplied = saved; }

                if (justLit && Vector3.Distance(__instance.transform.position, p2.Center) <= __instance.moraleBoostRadius)
                {
                    p2.refs.afflictions.AdjustStatus(CharacterAfflictions.STATUSTYPE.Injury, -0.2f);
                    p2.refs.afflictions.AdjustStatus(CharacterAfflictions.STATUSTYPE.Petrify, -0.2f);
                }
            }
            catch (Exception ex) { Report("campfire", ex); }
            finally { End(); }
        }

        // ------------------------------------------------------------------ touch traps

        private static void CollisionPre(Collision __0, out bool __state)
        {
            __state = __0 != null && __0.collider != null && Touches(__0.collider) && Begin();
        }

        private static void TriggerPre(Collider __0, out bool __state)
        {
            __state = __0 != null && Touches(__0) && Begin();
        }

        private static void CharacterPre(Character __0, out bool __state)
        {
            __state = __0 != null && ReferenceEquals(__0, SplitManager.P2Character) && Begin();
        }

        private static void ContextFinal(bool __state)
        {
            if (__state) End();
        }

        private static bool Touches(Collider c)
        {
            Character p2 = SplitManager.P2Character;
            return p2 != null && ReferenceEquals(c.transform.root, p2.transform.root);
        }
    }
}
