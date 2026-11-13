using System;
using System.Collections.Generic;
using Peak;
using UnityEngine;
using Zorro.Core;
using Object = UnityEngine.Object;

namespace PeakSplitScreen
{
    /// <summary>
    /// Fog and weather visuals for player 2's half of the screen.
    ///
    /// The game drives these from "the local player": global shader values (weather blend, gloom
    /// safe zone, fog thinning near a dispel item, the player position) and particle systems that
    /// are moved onto the player (rain, snow, wind, the old rising fog). This class works out the
    /// same values for whoever player 2's camera is watching, swaps them in while that camera
    /// renders, and keeps a second copy of the particles around player 2 that only their camera sees.
    /// </summary>
    internal static class P2Fog
    {
        private static readonly int WeatherBlend = Shader.PropertyToID("_WeatherBlend");
        private static readonly int WindTexture = Shader.PropertyToID("_WindTexture");
        private static readonly int WindTint = Shader.PropertyToID("WindTint");
        private static readonly int WindSky = Shader.PropertyToID("WindSkyBrightnessValue");
        private static readonly int WindTexInfluence = Shader.PropertyToID("WindTextureInfluence");
        private static readonly int WindFogDensity = Shader.PropertyToID("WindFogDensity");
        private static readonly int WindFogTexDensity = Shader.PropertyToID("WindFogTextureDensity");
        private static readonly int WindMix = Shader.PropertyToID("WindMixInfluence");
        private static readonly int WindSpeed = Shader.PropertyToID("WindSpeed");
        private static readonly int WindAxis = Shader.PropertyToID("WindRotationAxis");
        private static readonly int WindAngle = Shader.PropertyToID("WindRotationAngle");
        private static readonly int WindScale = Shader.PropertyToID("WindSphereScale");
        private static readonly int GlobalWind = Shader.PropertyToID("GlobalWind");
        private static readonly int SafeZone = Shader.PropertyToID("FogSafeZone");
        private static readonly int SafeZonePos = Shader.PropertyToID("SafeZonePosition");
        private static readonly int GloomVis = Shader.PropertyToID("GloomVisibiltyOffset");
        private static readonly int CloseMod = Shader.PropertyToID("CloseDistanceMod");
        private static readonly int PlayerPos = Shader.PropertyToID("PlayerPos");
        private static readonly int FogHeight = Shader.PropertyToID("FogHeight");
        private static readonly int Alpha = Shader.PropertyToID("_Alpha");
        private static readonly int LavaHeight = Shader.PropertyToID("LavaHeight");
        private static readonly int CutAmount = Shader.PropertyToID("FogCutoutAmount");
        private static readonly int CutMin = Shader.PropertyToID("FogCutoutMin");
        private static readonly int CutMax = Shader.PropertyToID("FogCutoutMax");
        private static readonly int CutPos = Shader.PropertyToID("FogCutoutPosition");

        private static readonly int[] Floats =
        {
            WeatherBlend, WindSky, WindTexInfluence, WindFogDensity, WindFogTexDensity, WindMix,
            WindAngle, WindScale, GlobalWind, SafeZone, GloomVis, CloseMod,
            LavaHeight, CutAmount, CutMin, CutMax
        };
        private static readonly int[] Vectors = { WindTint, WindSpeed, WindAxis, SafeZonePos, PlayerPos, CutPos };

        private static readonly float[] SavedFloats = new float[Floats.Length];
        private static readonly Vector4[] SavedVectors = new Vector4[Vectors.Length];
        private static Texture _savedTexture;

        /// <summary>A copy of some of the game's particles that follows player 2.</summary>
        private sealed class Copy
        {
            public Object source;                 // the StormVisual or Fog this was made from
            public GameObject holder;
            public ParticleSystem[] parts = new ParticleSystem[0];
            public Renderer[] mine = new Renderer[0];
            public Renderer[] theirs = new Renderer[0];
            public bool[] theirsOff = new bool[0];
            public Renderer quad;
            public Material quadMat;
            public float windFactor;
            public float windIntensity;
            public bool playing;
        }

        private static readonly List<Copy> Copies = new List<Copy>();
        private static StormVisual[] _storms = new StormVisual[0];
        private static DispelFogField[] _dispels = new DispelFogField[0];
        private static Fog _fog;
        private static PlayerShaderParams _playerParams;
        private static float _nextScan;

        // Player 2's own running values.
        private static float _blend;
        private static FogConfig _fogConfig;
        private static float _wind;
        private static float _safeBlend = 1f;
        private static float _visOffset;
        private static Vector3 _safePos;
        private static bool _hasSafePos;
        private static float _closeFog = 1f;
        private static bool _ready;
        private static bool _gloom;   // the game only drives the gloom values in some levels
        private static bool _applied;
        private static Vector3 _fogSavedPos;
        private static bool _fogMoved;
        private static int _appliedFrames;

        // Storm sky darkening, lava haze and the fog cutout.
        private static float _rainFactor, _snowFactor;
        private static bool _skyChanged;
        private static float _sunSaved, _moonSaved, _flareSaved;
        private static LavaPost _lava;
        private static bool _lavaChanged, _lavaRendSaved, _lavaMoved;
        private static Vector3 _lavaPosSaved;
        private static Quaternion _lavaRotSaved;
        private static FogCutoutHandler _cutout;
        private static int _cutIdx;
        private static float _cutAmount;

        /// <summary>Who player 2's camera is looking at, or null when it shows player 1's view.</summary>
        private static Character Viewer()
        {
            if (!SplitManager.Active) return null;
            Character p2 = SplitManager.P2Character;
            if (p2 == null || SplitManager.P2Spec != null) return null;
            return p2;
        }

        public static void Tick()
        {
            Character v = Viewer();
            if (v == null)
            {
                if (_ready) Reset();
                return;
            }
            _ready = true;
            if (Time.unscaledTime >= _nextScan)
            {
                _nextScan = Time.unscaledTime + 2f;
                Scan();
            }
            Vector3 pos = v.Center;
            float dt = Time.deltaTime;

            // Weather: rain, snow and wind zones.
            StormVisual inStorm = null;
            float rain = 0f, snow = 0f;
            for (int i = Copies.Count - 1; i >= 0; i--)
            {
                Copy c = Copies[i];
                if (c.source == null || c.holder == null) { Drop(i); continue; }
                StormVisual sv = c.source as StormVisual;
                if (sv != null)
                {
                    if (TickStorm(c, sv, pos, dt)) inStorm = sv;
                    if (sv.stormType == StormVisual.StormType.Snow) snow = Mathf.Max(snow, c.windFactor);
                    else rain = Mathf.Max(rain, c.windFactor);
                }
                else TickFog(c, (Fog)c.source, pos);
            }
            if (inStorm != null && inStorm.fogConfig != null)
            {
                FogConfig f = inStorm.fogConfig;
                _fogConfig = f;
                _blend = f.useWindChillZoneIntensity && f.zone != null
                    ? f.zone.windIntensity
                    : Mathf.MoveTowards(_blend, f.maxVal, dt * 0.3f);
            }
            else _blend = Mathf.MoveTowards(_blend, 0f, dt * 0.3f);
            _rainFactor = rain;
            _snowFactor = snow;

            // Fog cutout: the game steps through its zones as the player moves up the mountain.
            FogCutoutZone cut = CutZone(pos);
            if (cut != null)
                _cutAmount = Mathf.MoveTowards(_cutAmount, cut.amount, dt * Mathf.Max(0.01f, cut.amount) / Mathf.Max(0.05f, _cutout.fadeTime));

            // Gloom: clear air around lit safe zones.
            _gloom = FogSafeZoneManager.instance != null && FogSafeZoneManager.instance.isActiveAndEnabled;
            float target = 0f;
            if (GloomSafeZone.TryGetClosestSafeZone(pos, out GloomSafeZone zone, out float dist, false))
            {
                target = Mathf.Clamp01((dist - zone.visualRadius) / zone.falloff);
                _safePos = zone.transform.position;
                _hasSafePos = true;
            }
            _safeBlend = Mathf.Lerp(_safeBlend, target, dt);
            float vis = 0f;
            List<GloomSafeZone> all = GloomSafeZone.ALL_GLOOM_SAFE_ZONES;
            for (int i = 0; i < all.Count; i++)
            {
                if (all[i] != null && all[i].isLit)
                    vis += Mathf.Lerp(1f, 0f, Mathf.InverseLerp(4f, 12f, Vector3.Distance(all[i].transform.position, pos)));
            }
            _visOffset = Mathf.Lerp(_visOffset, Mathf.Clamp01(vis) * 4.5f, dt * 4f);

            // Fog thins out next to something that dispels it.
            float dispel = 0f;
            for (int i = 0; i < _dispels.Length; i++)
            {
                DispelFogField d = _dispels[i];
                if (d == null || !d.isActiveAndEnabled) continue;
                float far = Vector3.Distance(pos, d.transform.position);
                if (far <= d.outerRadius) dispel = Mathf.Max(dispel, Mathf.InverseLerp(d.outerRadius, d.innerRadius, far));
            }
            _closeFog = Mathf.Lerp(_closeFog, Mathf.Lerp(1f, 5f, dispel), dt);
        }

        private static bool TickStorm(Copy c, StormVisual sv, Vector3 pos, float dt)
        {
            WindChillZone zone = sv.zone;
            if (zone == null || !sv.isActiveAndEnabled)
            {
                Play(c, false);
                c.windFactor = 0f;
                if (c.quad != null) c.quad.enabled = false;
                return false;
            }
            bool inside = zone.windZoneBounds.Contains(pos);
            bool storm = inside && zone.windActive;
            if (storm)
            {
                if (sv.useWindChillZoneIntensity)
                {
                    c.windIntensity = Mathf.Clamp01(zone.windIntensity * sv.windchillZoneMult);
                    c.windFactor = Mathf.Lerp(c.windFactor, c.windIntensity, dt / 2f);
                    if (zone.windIntensity > 0.1f) Play(c, true);
                    _wind = c.windIntensity;
                }
                else
                {
                    Play(c, true);
                    c.windFactor = Mathf.Lerp(c.windFactor, Mathf.Clamp01(zone.hasBeenActiveFor * 0.2f), dt);
                }
            }
            else
            {
                if (!sv.useWindChillZoneIntensity || zone.windIntensity < 0.1f) Play(c, false);
                c.windIntensity = Mathf.Lerp(c.windIntensity, 0f, dt);
                if (sv.useWindChillZoneIntensity) _wind = c.windIntensity;
                c.windFactor = Mathf.Lerp(c.windFactor, 0f, dt);
            }
            if (inside)
            {
                Transform t = c.holder.transform;
                if (zone.currentWindDirection != Vector3.zero) t.SetPositionAndRotation(pos, Quaternion.LookRotation(zone.currentWindDirection));
                else t.position = pos;
            }
            if (c.quad != null)
            {
                c.quad.enabled = inside;
                if (inside && c.quadMat != null) c.quadMat.SetFloat(Alpha, c.windFactor);
            }
            return storm;
        }

        private static void TickFog(Copy c, Fog fog, Vector3 pos)
        {
            if (!fog.isActiveAndEnabled) { Play(c, false); return; }
            c.holder.transform.position = pos;
            Play(c, pos.y < fog.fogHeight);
        }

        private static void Play(Copy c, bool on)
        {
            if (c.playing == on) return;
            c.playing = on;
            for (int i = 0; i < c.parts.Length; i++)
            {
                if (c.parts[i] == null) continue;
                if (on) c.parts[i].Play(true);
                else c.parts[i].Stop(true);
            }
        }

        // ------------------------------------------------------------------ per camera

        public static void BeginCamera(Camera cam)
        {
            if (_applied || !_ready || !ReferenceEquals(cam, SplitManager.P2Cam)) return;
            Character v = Viewer();
            if (v == null) return;
            _applied = true;
            _appliedFrames++;

            for (int i = 0; i < Floats.Length; i++) SavedFloats[i] = Shader.GetGlobalFloat(Floats[i]);
            for (int i = 0; i < Vectors.Length; i++) SavedVectors[i] = Shader.GetGlobalVector(Vectors[i]);
            _savedTexture = Shader.GetGlobalTexture(WindTexture);

            Shader.SetGlobalFloat(WeatherBlend, _blend);
            Shader.SetGlobalFloat(GlobalWind, _wind);
            FogConfig f = _fogConfig;
            if (f != null && _blend > 0f)
            {
                Shader.SetGlobalTexture(WindTexture, f.windTexture);
                Shader.SetGlobalColor(WindTint, f.windTint);
                Shader.SetGlobalFloat(WindSky, f.windSkyBrightnessValue);
                Shader.SetGlobalFloat(WindTexInfluence, f.windTextureInfluence);
                Shader.SetGlobalFloat(WindFogDensity, f.windFogDensity);
                Shader.SetGlobalFloat(WindFogTexDensity, f.WindFogTextureDensity);
                Shader.SetGlobalFloat(WindMix, f.windMixInfluence);
                Shader.SetGlobalVector(WindSpeed, new Vector4(f.windSpeed.x, f.windSpeed.y, 0f, 0f));
                Vector3 fwd = f.transform.forward;
                Vector3 axis = -Vector3.Cross(Vector3.up, fwd);
                float angle = Vector3.Angle(Vector3.up, fwd);
                if (f.straightDown) { axis = Vector3.forward; angle = 180f; }
                Shader.SetGlobalVector(WindAxis, new Vector4(axis.x, axis.y, axis.z, 0f));
                Shader.SetGlobalFloat(WindAngle, angle);
                Shader.SetGlobalFloat(WindScale, f.windSphereScale);
            }
            if (_gloom)
            {
                Shader.SetGlobalFloat(SafeZone, 1f - _safeBlend);
                Shader.SetGlobalFloat(GloomVis, _visOffset);
                if (_hasSafePos) Shader.SetGlobalVector(SafeZonePos, _safePos);
            }
            if (Singleton<OrbFogHandler>.Instance != null) Shader.SetGlobalFloat(CloseMod, _closeFog);
            if (_playerParams != null) Shader.SetGlobalVector(PlayerPos, v.Center + _playerParams.playerCenterOffset);

            ApplySky();
            ApplyLava(cam);
            FogCutoutZone cut = CutZone(v.Center);
            if (cut != null && !ReferenceEquals(cut, _cutout.currentCutoutZone))
            {
                Shader.SetGlobalVector(CutPos, cut.transform.position);
                Shader.SetGlobalFloat(CutMin, cut.min);
                Shader.SetGlobalFloat(CutMax, cut.max);
                Shader.SetGlobalFloat(CutAmount, _cutAmount);
            }

            // The old rising fog is a sheet that sits under the player.
            _fogMoved = false;
            if (_fog != null && _fog.isActiveAndEnabled && _fog.stops != null)
            {
                Transform t = _fog.transform;
                _fogSavedPos = t.position;
                Vector3 p = v.Center;
                t.position = new Vector3(p.x, _fog.fogHeight, Mathf.Clamp(p.z, -10000f, 870f));
                _fogMoved = true;
            }

            for (int i = 0; i < Copies.Count; i++)
            {
                Copy c = Copies[i];
                for (int j = 0; j < c.mine.Length; j++) if (c.mine[j] != null) c.mine[j].forceRenderingOff = false;
                for (int j = 0; j < c.theirs.Length; j++)
                {
                    Renderer r = c.theirs[j];
                    if (r == null) continue;
                    c.theirsOff[j] = r.forceRenderingOff;
                    r.forceRenderingOff = true;
                }
            }
        }

        public static void EndCamera(Camera cam)
        {
            if (!_applied || !ReferenceEquals(cam, SplitManager.P2Cam)) return;
            Restore();
        }

        private static void Restore()
        {
            if (!_applied) return;
            _applied = false;
            for (int i = 0; i < Floats.Length; i++) Shader.SetGlobalFloat(Floats[i], SavedFloats[i]);
            for (int i = 0; i < Vectors.Length; i++) Shader.SetGlobalVector(Vectors[i], SavedVectors[i]);
            Shader.SetGlobalTexture(WindTexture, _savedTexture);
            _savedTexture = null;
            RestoreSkyAndLava();
            if (_fogMoved && _fog != null) _fog.transform.position = _fogSavedPos;
            _fogMoved = false;
            for (int i = 0; i < Copies.Count; i++)
            {
                Copy c = Copies[i];
                for (int j = 0; j < c.mine.Length; j++) if (c.mine[j] != null) c.mine[j].forceRenderingOff = true;
                for (int j = 0; j < c.theirs.Length; j++) if (c.theirs[j] != null) c.theirs[j].forceRenderingOff = c.theirsOff[j];
            }
        }

        // ------------------------------------------------------------------ sky, lava, cutout

        private static int _skyFrames;

        /// <summary>The cutout zone player 2 has reached, or null when the level has none.</summary>
        private static FogCutoutZone CutZone(Vector3 pos)
        {
            if (_cutout == null || !_cutout.isActiveAndEnabled) return null;
            FogCutoutZone[] zones = _cutout.cutoutZones;
            if (zones == null || zones.Length == 0) return null;
            if (_cutIdx >= zones.Length) _cutIdx = zones.Length - 1;
            while (_cutIdx + 1 < zones.Length && zones[_cutIdx] != null && zones[_cutIdx + 1] != null
                   && pos.z > zones[_cutIdx].transform.position.z + zones[_cutIdx].transitionPoint)
                _cutIdx++;
            return zones[_cutIdx];
        }

        /// <summary>A storm dims the sun, moon and lens flare for whoever stands in it.</summary>
        private static void ApplySky()
        {
            _skyChanged = false;
            DayNightManager d = DayNightManager.instance;
            if (d == null || d.sun == null || d.moon == null || d.currentProfile == null || d.newProfile == null) return;
            float theirs = -(d.snowstormWindFactor * 1.75f + d.rainstormWindFactor * 1.25f);
            float mine = -(_snowFactor * 1.75f + _rainFactor * 1.25f);
            if (Mathf.Abs(theirs - mine) < 0.005f) return;
            _skyChanged = true;
            _skyFrames++;
            _sunSaved = d.sun.intensity;
            _moonSaved = d.moon.intensity;
            float a = d.sun.color.a;
            float strength = Mathf.Lerp(d.currentProfile.sunIntensity, d.newProfile.sunIntensity, d.profileBlend);
            d.sun.intensity = Mathf.Max(0.015f, (a * 2f - 1f) * 0.5f * strength + mine);
            d.moon.intensity = Mathf.Max(0.015f, (1f - a * 2f) * 0.5f * strength + mine);
            if (d.lensFlare != null)
            {
                _flareSaved = d.lensFlare.intensity;
                d.lensFlare.intensity = (a - 0.5f) * 2f * d.lensFlareStr + mine;
            }
        }

        /// <summary>The lava haze picks its height and whether it shows from where the camera is.</summary>
        private static void ApplyLava(Camera cam)
        {
            _lavaChanged = false;
            _lavaMoved = false;
            LavaPost l = _lava;
            if (l == null || !l.isActiveAndEnabled || l.lava1 == null || l.lava2 == null || l.rend == null) return;
            _lavaChanged = true;
            float z = cam.transform.position.z;
            if (l.thresholdTransform != null)
            {
                bool first = z < l.thresholdTransform.position.z;
                if (first != l.firstIsActive) Shader.SetGlobalFloat(LavaHeight, first ? l.lava1.position.y : l.lava2.position.y);
            }
            _lavaRendSaved = l.rend.enabled;
            if (l.lavaFadeIn != null) l.rend.enabled = z >= l.lavaFadeIn.position.z;
            // If the haze sheet rides on player 1's camera, put it in the same spot on player 2's.
            MainCamera main = MainCamera.instance;
            Transform t = l.rend.transform;
            if (main != null && t.IsChildOf(main.transform))
            {
                _lavaPosSaved = t.position;
                _lavaRotSaved = t.rotation;
                Transform mt = main.transform, ct = cam.transform;
                t.SetPositionAndRotation(ct.TransformPoint(mt.InverseTransformPoint(t.position)),
                    ct.rotation * (Quaternion.Inverse(mt.rotation) * t.rotation));
                _lavaMoved = true;
            }
        }

        private static void RestoreSkyAndLava()
        {
            if (_skyChanged)
            {
                DayNightManager d = DayNightManager.instance;
                if (d != null)
                {
                    if (d.sun != null) d.sun.intensity = _sunSaved;
                    if (d.moon != null) d.moon.intensity = _moonSaved;
                    if (d.lensFlare != null) d.lensFlare.intensity = _flareSaved;
                }
                _skyChanged = false;
            }
            if (_lavaChanged && _lava != null && _lava.rend != null)
            {
                _lava.rend.enabled = _lavaRendSaved;
                if (_lavaMoved) _lava.rend.transform.SetPositionAndRotation(_lavaPosSaved, _lavaRotSaved);
            }
            _lavaChanged = false;
            _lavaMoved = false;
        }

        // ------------------------------------------------------------------ copies

        private static void Scan()
        {
            _storms = Object.FindObjectsByType<StormVisual>(FindObjectsSortMode.None);
            _dispels = Object.FindObjectsByType<DispelFogField>(FindObjectsSortMode.None);
            _fog = Object.FindAnyObjectByType<Fog>();
            _playerParams = Object.FindAnyObjectByType<PlayerShaderParams>();
            _lava = Object.FindAnyObjectByType<LavaPost>();
            _cutout = Object.FindAnyObjectByType<FogCutoutHandler>();
            for (int i = 0; i < _storms.Length; i++)
            {
                StormVisual sv = _storms[i];
                if (sv == null || sv.zone == null || Has(sv)) continue;   // zone is set in Start
                try { Copies.Add(Make(sv, sv.transform, sv.part, sv.quadRend)); }
                catch (Exception e) { Plugin.Log.LogError("P2 weather copy of " + sv.name + ": " + e); Copies.Add(new Copy { source = sv, holder = new GameObject("P2 weather (failed)") }); }
            }
            if (_fog != null && _fog.fogParticles != null && !Has(_fog))
            {
                try { Copies.Add(Make(_fog, _fog.fogParticles.transform, new[] { _fog.fogParticles }, null)); }
                catch (Exception e) { Plugin.Log.LogError("P2 fog copy: " + e); Copies.Add(new Copy { source = _fog, holder = new GameObject("P2 fog (failed)") }); }
            }
        }

        private static bool Has(Object source)
        {
            for (int i = 0; i < Copies.Count; i++) if (ReferenceEquals(Copies[i].source, source)) return true;
            return false;
        }

        private static Copy Make(Object source, Transform anchor, ParticleSystem[] parts, MeshRenderer quad)
        {
            var c = new Copy { source = source, holder = new GameObject("P2 copy of " + source.name) };
            c.holder.SetActive(false);   // nothing in the copies wakes up until it has been stripped
            c.holder.transform.SetPositionAndRotation(anchor.position, anchor.rotation);
            var theirs = new List<Renderer>();
            var copies = new List<GameObject>();
            if (parts != null)
            {
                foreach (ParticleSystem p in parts)
                {
                    if (p == null) continue;
                    bool nested = false;
                    foreach (ParticleSystem other in parts)
                        if (other != null && other != p && p.transform.IsChildOf(other.transform)) nested = true;
                    if (nested) continue;
                    theirs.AddRange(p.GetComponentsInChildren<Renderer>(true));
                    copies.Add(Clone(p.gameObject, c.holder.transform));
                }
            }
            if (quad != null)
            {
                theirs.Add(quad);
                GameObject q = Clone(quad.gameObject, c.holder.transform);
                copies.Add(q);
                c.quad = q.GetComponent<Renderer>();
            }
            var mine = new List<Renderer>();
            var systems = new List<ParticleSystem>();
            foreach (GameObject g in copies)
            {
                mine.AddRange(g.GetComponentsInChildren<Renderer>(true));
                systems.AddRange(g.GetComponentsInChildren<ParticleSystem>(true));
            }
            foreach (ParticleSystem s in systems)
            {
                ParticleSystem.MainModule main = s.main;
                main.playOnAwake = false;
            }
            foreach (Renderer r in mine) r.forceRenderingOff = true;
            c.parts = systems.ToArray();
            c.mine = mine.ToArray();
            c.theirs = theirs.ToArray();
            c.theirsOff = new bool[c.theirs.Length];
            c.holder.SetActive(true);
            if (c.quad != null)
            {
                c.quadMat = c.quad.material;
                c.quad.enabled = false;
            }
            Plugin.Log.LogInfo($"Player 2 weather copy of {source.name}: {c.parts.Length} particle systems, quad={(c.quad != null)}.");
            return c;
        }

        private static GameObject Clone(GameObject original, Transform holder)
        {
            GameObject g = Object.Instantiate(original, holder);
            g.transform.SetPositionAndRotation(original.transform.position, original.transform.rotation);
            // Keep only what draws: scripts, sound and networking stay with the original.
            foreach (MonoBehaviour m in g.GetComponentsInChildren<MonoBehaviour>(true)) Object.DestroyImmediate(m);
            foreach (AudioSource a in g.GetComponentsInChildren<AudioSource>(true)) Object.DestroyImmediate(a);
            foreach (Collider col in g.GetComponentsInChildren<Collider>(true)) Object.DestroyImmediate(col);
            g.SetActive(true);
            return g;
        }

        private static void Drop(int i)
        {
            Copy c = Copies[i];
            if (c.holder != null) Object.Destroy(c.holder);
            if (c.quadMat != null) Object.Destroy(c.quadMat);
            Copies.RemoveAt(i);
        }

        public static void Reset()
        {
            Restore();
            for (int i = Copies.Count - 1; i >= 0; i--) Drop(i);
            _storms = new StormVisual[0];
            _dispels = new DispelFogField[0];
            _fog = null;
            _playerParams = null;
            _lava = null;
            _cutout = null;
            _cutIdx = 0;
            _cutAmount = 0f;
            _rainFactor = 0f;
            _snowFactor = 0f;
            _fogConfig = null;
            _blend = 0f;
            _wind = 0f;
            _safeBlend = 1f;
            _visOffset = 0f;
            _hasSafePos = false;
            _closeFog = 1f;
            _nextScan = 0f;
            _ready = false;
        }

        public static string Status()
        {
            int playing = 0;
            foreach (Copy c in Copies) if (c.playing) playing++;
            return $"fog: storms={_storms.Length} copies={Copies.Count} playing={playing} blend={_blend:0.00} wind={_wind:0.00} safe={1f - _safeBlend:0.00} close={_closeFog:0.00}"
                 + $" sky={_rainFactor:0.00}/{_snowFactor:0.00} sun={(DayNightManager.instance != null && DayNightManager.instance.sun != null ? DayNightManager.instance.sun.intensity : -1f):0.00} skyApplied={_skyFrames} lava={(_lava != null)} cut={(_cutout != null ? _cutIdx.ToString() : "none")}"
                 + $" oldFog={(_fog != null && _fog.isActiveAndEnabled)} applied={_appliedFrames} gloom={_gloom}"
                 + $" | P1 globals blend={Shader.GetGlobalFloat(WeatherBlend):0.00} wind={Shader.GetGlobalFloat(GlobalWind):0.00} safe={Shader.GetGlobalFloat(SafeZone):0.00} close={Shader.GetGlobalFloat(CloseMod):0.00}";
        }
    }
}
