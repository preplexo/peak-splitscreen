using System;
using System.Collections.Generic;
using HarmonyLib;
using Peak;
using UnityEngine;
using UnityEngine.LowLevel;
using UnityEngine.PlayerLoop;

namespace PeakSplitScreen
{
    /// <summary>
    /// Sound for two players through one pair of speakers.
    ///
    /// Unity has a single listener, which sits on player 1's camera, so anything near player 2
    /// would be silent or come from the wrong side. Just before Unity mixes a frame, every
    /// positional sound that player 2 can hear is re-levelled by hand: it gets the loudness of
    /// whichever player is nearer and is panned to where it sits for the players hearing it.
    /// The game's own values are put back at the very start of the next frame, before any game
    /// code runs, so the game never sees the adjusted numbers. Music and other non-positional
    /// sounds are left alone, so there is one soundtrack.
    /// </summary>
    internal static class P2Audio
    {
        private sealed class Entry
        {
            public AudioSource src;
            public Transform tf;
            public AnimationCurve rolloff;
            public AnimationCurve blendCurve; // only when the source blends 2D/3D by distance
            public bool applied;
            public float baseVol, baseBlend, basePan;
            public float setVol, setBlend, setPan;
            public bool seen;
        }

        private struct RestoreStep { }
        private struct ApplyStep { }

        private static readonly List<Entry> _entries = new List<Entry>();
        private static readonly Dictionary<int, Entry> _byId = new Dictionary<int, Entry>();
        private static float _nextScan;
        private static Transform _ear1;
        private static int _lastApplyFrame = -1;
        private static bool _warned;

        // Test read-outs.
        private static int _adjusted, _playing3d, _overwritten, _custom, _linear, _log, _curved;
        private static string _loudest = "none";
        private static float _bestScore;

        private static bool On => Plugin.P2Sound.Value && SplitManager.Active && SplitManager.P2Cam != null;

        public static void Apply(Harmony h)
        {
            h.Patch(AccessTools.Method(typeof(SFX_Player), nameof(SFX_Player.PlaySFX)),
                prefix: new HarmonyMethod(typeof(P2Audio), nameof(PlayPrefix)),
                finalizer: new HarmonyMethod(typeof(P2Audio), nameof(PlayFinalizer)));
            h.Patch(AccessTools.Method(typeof(EnvironmentLoopHelper), "Update"),
                prefix: new HarmonyMethod(typeof(P2Audio), nameof(LoopPrefix)));
            Install();
        }

        /// <summary>Called every frame: puts our two steps back if something rebuilt the player loop.</summary>
        public static void Watchdog()
        {
            if (Time.frameCount - _lastApplyFrame > 10 && Time.frameCount > 10) Install();
        }

        private static void Install()
        {
            try
            {
                PlayerLoopSystem loop = PlayerLoop.GetCurrentPlayerLoop();
                if (loop.subSystemList == null) return;
                bool restore = false, apply = false;
                for (int i = 0; i < loop.subSystemList.Length; i++)
                {
                    PlayerLoopSystem phase = loop.subSystemList[i];
                    var list = new List<PlayerLoopSystem>(phase.subSystemList ?? new PlayerLoopSystem[0]);
                    list.RemoveAll(s => s.type == typeof(RestoreStep) || s.type == typeof(ApplyStep));
                    if (phase.type == typeof(EarlyUpdate))
                    {
                        list.Insert(0, new PlayerLoopSystem { type = typeof(RestoreStep), updateDelegate = Restore });
                        restore = true;
                    }
                    else if (phase.type == typeof(PostLateUpdate))
                    {
                        int at = list.FindIndex(s => s.type == typeof(PostLateUpdate.UpdateAudio));
                        if (at < 0) at = list.Count;
                        list.Insert(at, new PlayerLoopSystem { type = typeof(ApplyStep), updateDelegate = BeforeMix });
                        apply = true;
                    }
                    phase.subSystemList = list.ToArray();
                    loop.subSystemList[i] = phase;
                }
                if (restore && apply)
                {
                    PlayerLoop.SetPlayerLoop(loop);
                    _lastApplyFrame = Time.frameCount;
                }
                else if (!_warned)
                {
                    _warned = true;
                    Plugin.Log.LogWarning("Two-player sound could not hook the frame loop; sound stays on player 1.");
                }
            }
            catch (Exception e)
            {
                if (!_warned) { _warned = true; Plugin.Log.LogWarning("Two-player sound unavailable: " + e.Message); }
            }
        }

        // ---- every frame -------------------------------------------------------------------

        private static void Restore()
        {
            for (int i = 0; i < _entries.Count; i++)
            {
                Entry e = _entries[i];
                if (!e.applied) continue;
                e.applied = false;
                if (e.src == null) continue;
                try
                {
                    // Only undo what is still ours; if the game wrote a new value since, keep it.
                    if (e.src.volume == e.setVol) e.src.volume = e.baseVol; else _overwritten++;
                    if (e.src.spatialBlend == e.setBlend)
                    {
                        // Writing the number flattens a distance curve, so put the curve itself back.
                        if (e.blendCurve != null) e.src.SetCustomCurve(AudioSourceCurveType.SpatialBlend, e.blendCurve);
                        else e.src.spatialBlend = e.baseBlend;
                    }
                    if (e.src.panStereo == e.setPan) e.src.panStereo = e.basePan;
                }
                catch { }
            }
        }

        private static void BeforeMix()
        {
            _lastApplyFrame = Time.frameCount;
            try
            {
                if (Plugin.DebugMute.Value) AudioListener.volume = 0f;
                Restore(); // normally a no-op; covers a frame where the early step did not run
                if (!On) { if (_entries.Count > 0) { _entries.Clear(); _byId.Clear(); } return; }
                Mix();
            }
            catch (Exception e)
            {
                if (!_warned) { _warned = true; Plugin.Log.LogWarning("Two-player sound: " + e); }
            }
        }

        private static void Mix()
        {
            if (_ear1 == null)
            {
                AudioListener l = UnityEngine.Object.FindAnyObjectByType<AudioListener>();
                if (l != null) _ear1 = l.transform;
                else if (MainCamera.instance != null) _ear1 = MainCamera.instance.transform;
                else return;
            }
            Transform ear2 = SplitManager.P2Cam.transform;

            if (Time.unscaledTime >= _nextScan)
            {
                _nextScan = Time.unscaledTime + 0.3f;
                Scan();
            }

            int adjusted = 0, playing = 0;
            float best = _bestScore;
            string bestName = "none";
            for (int i = 0; i < _entries.Count; i++)
            {
                Entry e = _entries[i];
                AudioSource s = e.src;
                if (s == null || !s.isActiveAndEnabled || s.mute || !s.isPlaying) continue;
                float blend = e.blendCurve == null ? s.spatialBlend : 1f;
                if (blend <= 0.01f) continue;
                playing++;

                Vector3 pos = e.tf.position;
                float g1 = Loudness(e, Vector3.Distance(_ear1.position, pos));
                float g2 = Loudness(e, Vector3.Distance(ear2.position, pos));
                if (e.blendCurve != null) blend = Mathf.Clamp01(e.blendCurve.Evaluate(Vector3.Distance(_ear1.position, pos) / Mathf.Max(s.maxDistance, 0.01f)));
                float sum = g1 + g2;
                if (sum < 0.0001f) continue;

                // How much of this sound belongs to player 2. Below a tenth it is left exactly as
                // the game plays it; from there it fades over to the hand-made mix.
                float share = g2 / sum;
                float k = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.1f, 0.4f, share));
                if (k <= 0f) continue;

                float pan = (g1 * Side(_ear1, pos) + g2 * Side(ear2, pos)) / sum * blend;
                float wanted = Mathf.Lerp(1f, Mathf.Max(g1, g2), blend);
                float newBlend = blend * (1f - k);
                float engine = Mathf.Lerp(1f, g1, newBlend);

                e.baseVol = s.volume;
                e.baseBlend = blend;
                e.basePan = s.panStereo;
                s.volume = e.baseVol * Mathf.Clamp01(wanted / Mathf.Max(engine, 0.0001f));
                s.spatialBlend = newBlend;
                s.panStereo = Mathf.Clamp(e.basePan + pan * k, -1f, 1f);
                e.setVol = s.volume;
                e.setBlend = s.spatialBlend;
                e.setPan = s.panStereo;
                e.applied = true;
                adjusted++;

                if (Plugin.DebugFakeGamepad.Value && k * g2 * e.baseVol > best)
                {
                    best = k * g2 * e.baseVol;
                    bestName = s.name + (s.clip != null ? "/" + s.clip.name : "") + " g1=" + g1.ToString("0.00") + " g2=" + g2.ToString("0.00")
                        + " vol=" + e.baseVol.ToString("0.00") + ">" + e.setVol.ToString("0.00") + " pan=" + e.setPan.ToString("0.00") + " k=" + k.ToString("0.00");
                }
            }
            _adjusted = adjusted;
            _playing3d = playing;
            if (best > _bestScore) { _bestScore = best; _loudest = bestName; }
        }

        private static void Scan()
        {
            for (int i = 0; i < _entries.Count; i++) _entries[i].seen = false;
            AudioSource[] all = UnityEngine.Object.FindObjectsByType<AudioSource>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            for (int i = 0; i < all.Length; i++)
            {
                AudioSource s = all[i];
                int id = s.GetInstanceID();
                if (_byId.TryGetValue(id, out Entry e)) { e.seen = true; continue; }
                e = new Entry { src = s, tf = s.transform, seen = true };
                if (s.rolloffMode == AudioRolloffMode.Custom)
                {
                    try { e.rolloff = s.GetCustomCurve(AudioSourceCurveType.CustomRolloff); } catch { }
                }
                try
                {
                    AnimationCurve bc = s.GetCustomCurve(AudioSourceCurveType.SpatialBlend);
                    if (bc != null && bc.length > 1) { e.blendCurve = bc; _curved++; }
                }
                catch { }
                if (s.rolloffMode == AudioRolloffMode.Custom) _custom++; else if (s.rolloffMode == AudioRolloffMode.Linear) _linear++; else _log++;
                _byId[id] = e;
                _entries.Add(e);
            }
            for (int i = _entries.Count - 1; i >= 0; i--)
            {
                Entry e = _entries[i];
                if (e.seen || e.applied) continue; // an applied one is dropped after it is restored
                _byId.Remove(e.src != null ? e.src.GetInstanceID() : 0);
                _entries.RemoveAt(i);
            }
            if (_entries.Count != _byId.Count)
            {
                // A destroyed source cannot report its id any more; rebuild the lookup.
                _byId.Clear();
                for (int i = _entries.Count - 1; i >= 0; i--)
                {
                    if (_entries[i].src == null) _entries.RemoveAt(i);
                    else _byId[_entries[i].src.GetInstanceID()] = _entries[i];
                }
            }
        }

        /// <summary>How loud Unity would play this source for a listener at the given distance (0..1).</summary>
        private static float Loudness(Entry e, float d)
        {
            AudioSource s = e.src;
            float max = Mathf.Max(s.maxDistance, 0.01f);
            float min = Mathf.Clamp(s.minDistance, 0.001f, max);
            switch (s.rolloffMode)
            {
                case AudioRolloffMode.Linear:
                    return max - min < 0.001f ? (d <= max ? 1f : 0f) : Mathf.Clamp01(1f - (d - min) / (max - min));
                case AudioRolloffMode.Custom:
                    return e.rolloff != null ? Mathf.Clamp01(e.rolloff.Evaluate(d / max)) : Mathf.Clamp01(1f - d / max);
                default:
                    return d <= min ? 1f : min / Mathf.Min(d, max);
            }
        }

        /// <summary>-1 = hard left, +1 = hard right, for a listener looking along the given transform.</summary>
        private static float Side(Transform ear, Vector3 pos)
        {
            Vector3 local = ear.InverseTransformPoint(pos);
            float d = local.magnitude;
            if (d < 0.05f) return 0f;
            return local.x / d * Mathf.Clamp01(d / 0.5f);
        }

        // ---- game code that decides by distance to "the" camera ------------------------------

        private static MainCamera P1Camera()
        {
            return SplitContext.Active ? SplitContext.SavedCamera : MainCamera.instance;
        }

        /// <summary>The game drops a sound that is out of earshot of the camera; use whichever player is nearer.</summary>
        private static void PlayPrefix(Vector3 position, out MainCamera __state)
        {
            __state = null;
            if (!On) return;
            MainCamera now = MainCamera.instance, p1 = P1Camera(), p2 = SplitManager.P2MainCamera;
            if (now == null || p1 == null || p2 == null) return;
            MainCamera near = (p2.transform.position - position).sqrMagnitude < (p1.transform.position - position).sqrMagnitude ? p2 : p1;
            if (near == now) return;
            __state = now;
            MainCamera.instance = near;
        }

        private static Exception PlayFinalizer(MainCamera __state, Exception __exception)
        {
            if (__state != null) MainCamera.instance = __state;
            return __exception;
        }

        /// <summary>Waterfalls, wind and other looping surroundings switch on near the camera; either player counts.</summary>
        private static bool LoopPrefix(EnvironmentLoopHelper __instance)
        {
            if (!On || __instance.loops == null) return true;
            MainCamera p1 = P1Camera();
            if (p1 == null) return true;
            Vector3 pos = __instance.transform.position;
            float d = Mathf.Min(Vector3.Distance(p1.transform.position, pos), Vector3.Distance(SplitManager.P2Cam.transform.position, pos));
            foreach (AudioSource a in __instance.loops)
            {
                if (a == null) continue;
                if (a.isPlaying) { if (d > a.maxDistance) a.Pause(); }
                else if (d < a.maxDistance) a.Play();
            }
            return false;
        }

        public static string Status()
        {
            string top = _loudest; _loudest = "none"; _bestScore = 0f;
            return "audio{tracked=" + _entries.Count + " playing3d=" + _playing3d + " adjusted=" + _adjusted + " overwritten=" + _overwritten
                + " rolloff(log/lin/custom)=" + _log + "/" + _linear + "/" + _custom + " blendCurves=" + _curved + " hooked=" + (Time.frameCount - _lastApplyFrame <= 2) + " listenerVol=" + AudioListener.volume.ToString("0.00")
                + " ear1=" + (_ear1 != null ? _ear1.name : "none") + " top=[" + top + "]}";
        }
    }
}
