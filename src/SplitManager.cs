using System;
using System.Collections.Generic;
using Photon.Pun;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Zorro.ControllerSupport;
using Zorro.Core;
using PhotonPlayer = Photon.Realtime.Player;
using PhotonRoom = Photon.Realtime.Room;

namespace PeakSplitScreen
{
    /// <summary>Owns player 2: the fake Photon player, its objects, its input and its camera.</summary>
    internal static class SplitManager
    {
        public static PhotonPlayer P2Photon;
        public static Character P2Character;
        public static Player P2Player;
        public static Camera P2Cam;
        public static MainCamera P2MainCamera;
        public static InputActionAsset P2Actions;
        /// <summary>Who player 2 is watching while down or dead (null = nobody).</summary>
        public static Character P2Spec;
        public static bool Spawning;

        /// <summary>True while a second player exists in the world.</summary>
        public static bool Active => P2Character != null;

        private static PhotonRoom _room;
        private static Transform _p2CharRoot, _p2PlayerRoot;
        private static Gamepad _pad;
        public static Gamepad Pad => _pad != null && _pad.added ? _pad : null;
        private static float _spawnAt = -1f;
        private static int _failures;
        private static bool _disabled;
        private static bool _hooked;
        private static bool _p1InputRestricted;
        private static bool _setGlobalDevices;
        private static bool _wasActive;
        private static float _nextInputCheck;
        private static bool _devicesAssigned;

        public static void Fail(string where, Exception e)
        {
            Plugin.Log.LogError($"[{where}] {e}");
            if (++_failures >= 5 && !_disabled)
            {
                _disabled = true;
                Plugin.Log.LogError("Too many errors, split screen disabled until the game restarts.");
                try { Teardown(true); } catch (Exception e2) { Plugin.Log.LogError(e2); }
            }
        }

        public static bool IsP2Actor(int actorNumber)
        {
            return P2Photon != null && actorNumber == P2Photon.ActorNumber;
        }

        /// <summary>Does this component belong to player 2's Character or Player object?</summary>
        public static bool IsP2(Component c)
        {
            if ((object)_p2CharRoot == null) return false;
            Transform root = c.transform.root;
            return ReferenceEquals(root, _p2CharRoot) || ReferenceEquals(root, _p2PlayerRoot) || SplitExtras.IsExtraRoot(root);
        }

        public static string Status()
        {
            string scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
            Character p1 = Character.localCharacter;
            string s = $"scene={scene} offline={PhotonNetwork.OfflineMode} room={(PhotonNetwork.CurrentRoom != null)} pads={Gamepad.all.Count} p1={(p1 != null ? p1.Center.ToString() : "none")}";
            if (P2Character != null)
            {
                MainCamera main = MainCamera.instance;
                s += $" p2={P2Character.Center} p2look={P2Character.data.lookDirection} p2LocalOutsideCtx={P2Character.IsLocal}"
                   + $" mainRect={(main != null && main.cam != null ? main.cam.rect.ToString() : "none")}"
                   + $" p2Cam={(P2Cam != null ? P2Cam.rect + "@" + P2Cam.transform.position : "none")}"
                   + $" characters={PlayerHandler.GetAllPlayerCharacters().Count}";
            }
            return s;
        }

        // ------------------------------------------------------------------ update loop

        public static void Tick()
        {
            if (!_hooked)
            {
                _hooked = true;
                RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;
                RenderPipelineManager.endCameraRendering += OnEndCameraRendering;
                Application.onBeforeRender += OnBeforeRender;
            }

            PhotonRoom room = PhotonNetwork.CurrentRoom;
            bool allowed = Plugin.Enabled.Value && Plugin.TwoPlayers.Value && !_disabled && PhotonNetwork.OfflineMode && room != null;
            if (!allowed)
            {
                if (_wasActive || P2Photon != null) Teardown(true);
                return;
            }
            if (!ReferenceEquals(room, _room))
            {
                if (P2Photon != null) Teardown(true);
                _room = room;
            }

            Character p1 = Character.localCharacter;
            if (p1 == null)
            {
                _spawnAt = -1f;
                return;
            }

            if (P2Character == null)
            {
                if (_wasActive) OnP2Lost();
                if (Gamepad.all.Count == 0 && !Plugin.DebugNoGamepad.Value) { _spawnAt = -1f; return; }
                if (_spawnAt < 0f) { _spawnAt = Time.unscaledTime + 1.5f; return; }
                if (Time.unscaledTime < _spawnAt) return;
                _spawnAt = -1f;
                SpawnP2(p1);
                if (P2Character == null) return;
            }

            _wasActive = true;
            EnsureInput();
            EnsureCamera();
            SplitExtras.Tick();
            BodyVisibility.KeepShadowCasters(p1, P2Character);
        }

        public static void LateTick()
        {
            if (!Active) return;
            MainCamera main = MainCamera.instance;
            if (main == null || main.cam == null || P2Cam == null) return;
            bool side = Plugin.SideBySide.Value;
            main.cam.rect = side ? new Rect(0f, 0f, 0.5f, 1f) : new Rect(0f, 0.5f, 1f, 0.5f);
            P2Cam.rect = side ? new Rect(0.5f, 0f, 0.5f, 1f) : new Rect(0f, 0f, 1f, 0.5f);
            P2Cam.fieldOfView = main.cam.fieldOfView;
        }

        // ------------------------------------------------------------------ spawning

        private static void SpawnP2(Character p1)
        {
            PhotonRoom room = PhotonNetwork.CurrentRoom;
            if (P2Photon == null)
            {
                int actor = PhotonNetwork.LocalPlayer.ActorNumber + 1;
                var p = new PhotonPlayer("Player 2", actor, false);
                var props = p.CustomProperties ?? new ExitGames.Client.Photon.Hashtable();
                object family = null;
                var localProps = PhotonNetwork.LocalPlayer.CustomProperties;
                if (localProps != null && localProps.ContainsKey("PlatformFamily")) family = localProps["PlatformFamily"];
                props["PlatformFamily"] = family ?? "Windows";
                props["UserID"] = "1000000000000002";
                props["ProductUserID"] = "splitscreen-p2";
                p.CustomProperties = props;
                room.StorePlayer(p);
                P2Photon = p;
                Plugin.Log.LogInfo($"Added fake Photon player (actor {actor}). Room now has {room.PlayerCount} players.");
            }

            Character savedChar = Character.localCharacter;
            Player savedPlayer = Player.localPlayer;
            Spawning = true;
            try
            {
                Transform camT = MainCamera.instance != null ? MainCamera.instance.transform : p1.transform;
                Vector3 right = Vector3.ProjectOnPlane(camT.right, Vector3.up).normalized;
                Vector3 pos = PickSpawn(p1, right);

                GameObject charGo = NetSpawn("Character", pos, Quaternion.identity);
                P2Character = charGo.GetComponent<Character>();
                _p2CharRoot = charGo.transform.root;
                if (P2Character.data != null)
                {
                    P2Character.data.userID = (string)P2Photon.CustomProperties["UserID"];
                    P2Character.data.productUserID = (string)P2Photon.CustomProperties["ProductUserID"];
                    if (p1.data != null) P2Character.data.platformFamily = p1.data.platformFamily;
                }
                Patches.PatchContextFor(charGo);

                if (P2Player == null)
                {
                    GameObject playerGo = NetSpawn("Player", Vector3.zero, Quaternion.identity);
                    P2Player = playerGo.GetComponent<Player>();
                    _p2PlayerRoot = playerGo.transform.root;
                    Patches.PatchContextFor(playerGo);
                }
                Plugin.Log.LogInfo($"Spawned player 2 at {pos} (character view {P2Character.photonView.ViewID}, IsMine={P2Character.photonView.IsMine}).");
            }
            finally
            {
                Spawning = false;
                Character.localCharacter = savedChar;
                Player.localPlayer = savedPlayer;
            }
        }

        /// <summary>A free spot next to player 1: not behind or inside a wall, with floor underneath.</summary>
        private static Vector3 PickSpawn(Character p1, Vector3 right)
        {
            Vector3 origin = p1.Center;
            int mask = HelperFunctions.GetMask(HelperFunctions.LayerType.TerrainMap);
            Vector3 fwd = Vector3.Cross(right, Vector3.up);
            Vector3[] dirs = { right, -right, -fwd, fwd, (right - fwd).normalized, (-right - fwd).normalized, (right + fwd).normalized, (fwd - right).normalized };
            foreach (float dist in new[] { 1.5f, 1.1f })
                foreach (Vector3 d in dirs)
                {
                    Vector3 c = origin + d * dist;
                    if (Physics.SphereCast(origin + Vector3.up * 0.5f, 0.3f, d, out _, dist + 0.3f, mask, QueryTriggerInteraction.Ignore)) continue;
                    if (!Physics.Raycast(c + Vector3.up * 1.5f, Vector3.down, out RaycastHit ground, 4f, mask, QueryTriggerInteraction.Ignore)) continue;
                    if (Vector3.Angle(ground.normal, Vector3.up) > 40f) continue;
                    Vector3 g = ground.point;
                    if (Physics.CheckCapsule(g + Vector3.up * 0.55f, g + Vector3.up * 1.7f, 0.4f, mask, QueryTriggerInteraction.Ignore)) continue;
                    return g + Vector3.up * 1.3f;
                }
            Plugin.Log.LogWarning("No free spot next to player 1, spawning player 2 above them.");
            return origin + Vector3.up * 1.2f;
        }

        /// <summary>PhotonNetwork.Instantiate, but owned by player 2 and without a network event.</summary>
        private static GameObject NetSpawn(string prefab, Vector3 pos, Quaternion rot)
        {
            GameObject go = PhotonNetwork.PrefabPool.Instantiate(prefab, pos, rot);
            if (go == null) throw new Exception("Prefab not found: " + prefab);
            PhotonView[] views = go.GetPhotonViewsInChildren();
            if (views.Length == 0) throw new Exception("Prefab has no PhotonView: " + prefab);

            int first = 0;
            for (int i = 0; i < views.Length; i++)
            {
                // Allocate and assign one at a time: allocation only skips ids that are already registered.
                int id = PhotonNetwork.AllocateViewID(P2Photon.ActorNumber);
                if (i == 0) first = id;
                PhotonView v = views[i];
                v.ViewID = 0;
                v.sceneViewId = 0;
                v.isRuntimeInstantiated = true;
                v.InstantiationId = first;
                v.ViewID = id;
            }
            go.SetActive(true);

            var callbacks = go.GetComponents<IPunInstantiateMagicCallback>();
            if (callbacks.Length > 0)
            {
                var info = new PhotonMessageInfo(P2Photon, PhotonNetwork.ServerTimestamp, views[0]);
                foreach (var cb in callbacks) cb.OnPhotonInstantiate(info);
            }
            return go;
        }

        // ------------------------------------------------------------------ input

        private static void EnsureInput()
        {
            if (P2Actions == null)
            {
                InputActionAsset src = InputSystem.actions;
                P2Actions = UnityEngine.Object.Instantiate(src);
                P2Actions.name = "SplitScreen P2 Actions";
                try { P2Actions.LoadBindingOverridesFromJson(src.SaveBindingOverridesAsJson()); }
                catch (Exception e) { Plugin.Log.LogWarning("Could not copy rebinds to player 2: " + e.Message); }
                SplitContext.ResetInputCache();
                _pad = null;
                _devicesAssigned = false;
            }

            bool padLost = _pad != null && !_pad.added;
            bool padFound = _pad == null && Gamepad.all.Count > 0;
            if (padLost || padFound || !_devicesAssigned)
            {
                _devicesAssigned = true;
                _pad = DebugTools.VirtualPad ?? (Gamepad.all.Count > 0 ? Gamepad.all[0] : null);
                P2Actions.Disable();
                P2Actions.devices = _pad != null ? new InputDevice[] { _pad } : new InputDevice[0];
                P2Actions.Enable();
                Plugin.Log.LogInfo("Player 2 gamepad: " + (_pad != null ? _pad.displayName : "none"));
            }

            if (Time.unscaledTime < _nextInputCheck) return;
            _nextInputCheck = Time.unscaledTime + 1f;

            // Keep player 1 on keyboard + mouse so the pad only drives player 2.
            var devices = new List<InputDevice>();
            if (Keyboard.current != null) devices.Add(Keyboard.current);
            if (Mouse.current != null) devices.Add(Mouse.current);
            if (devices.Count == 0) return;

            PlayerInput pi = null;
            try { pi = RetrievableResourceSingleton<InputHandler>.Instance.PlayerInput; } catch { }
            bool first = !_p1InputRestricted;
            if (pi != null && pi.actions != null)
            {
                string scheme = null;
                foreach (var s in pi.actions.controlSchemes)
                    if (s.name.IndexOf("Keyboard", StringComparison.OrdinalIgnoreCase) >= 0) scheme = s.name;
                pi.neverAutoSwitchControlSchemes = true;
                if (scheme != null && (first || pi.currentControlScheme != scheme))
                    pi.SwitchCurrentControlScheme(scheme, devices.ToArray());
                if (first)
                    Plugin.Log.LogInfo($"Player 1 locked to scheme '{scheme}'. PlayerInput shares the global actions: {ReferenceEquals(pi.actions, InputSystem.actions)}");
            }
            if (pi == null || !ReferenceEquals(pi.actions, InputSystem.actions))
            {
                InputSystem.actions.devices = devices.ToArray();
                _setGlobalDevices = true;
            }
            _p1InputRestricted = true;
        }

        private static void RestoreInput()
        {
            if (_p1InputRestricted)
            {
                try
                {
                    PlayerInput pi = RetrievableResourceSingleton<InputHandler>.Instance.PlayerInput;
                    if (pi != null) pi.neverAutoSwitchControlSchemes = false;
                }
                catch { }
                if (_setGlobalDevices && InputSystem.actions != null) InputSystem.actions.devices = null;
                _setGlobalDevices = false;
                _p1InputRestricted = false;
            }
            if (P2Actions != null)
            {
                P2Actions.Disable();
                UnityEngine.Object.Destroy(P2Actions);
                P2Actions = null;
            }
            SplitContext.ResetInputCache();
            _pad = null;
        }

        // ------------------------------------------------------------------ camera

        private static void EnsureCamera()
        {
            MainCamera main = MainCamera.instance;
            if (main == null || main.cam == null || P2Cam != null) return;

            var go = new GameObject("SplitScreen P2 Camera");
            go.transform.SetPositionAndRotation(main.transform.position, main.transform.rotation);
            Camera cam = go.AddComponent<Camera>();
            cam.CopyFrom(main.cam);
            cam.depth = main.cam.depth + 0.01f;

            UniversalAdditionalCameraData src = main.cam.GetUniversalAdditionalCameraData();
            UniversalAdditionalCameraData dst = cam.GetUniversalAdditionalCameraData();
            int stack = 0;
            if (src != null && dst != null)
            {
                dst.renderPostProcessing = src.renderPostProcessing;
                dst.antialiasing = src.antialiasing;
                dst.antialiasingQuality = src.antialiasingQuality;
                dst.volumeLayerMask = src.volumeLayerMask;
                dst.renderShadows = src.renderShadows;
                dst.requiresColorOption = src.requiresColorOption;
                dst.requiresDepthOption = src.requiresDepthOption;
                dst.dithering = src.dithering;
                dst.stopNaN = src.stopNaN;
                dst.SetRenderer(src.m_RendererIndex);
                if (src.renderType == CameraRenderType.Base && src.cameraStack != null)
                {
                    foreach (Camera overlay in src.cameraStack)
                        if (overlay != null) { dst.cameraStack.Add(overlay); stack++; }
                }
            }

            MainCamera saved = MainCamera.instance;
            P2MainCamera = go.AddComponent<MainCamera>(); // its Awake overwrites the singleton
            MainCamera.instance = saved;
            P2Cam = cam;
            Plugin.Log.LogInfo($"Created player 2 camera (overlay cameras in stack: {stack}, target texture: {(cam.targetTexture != null ? cam.targetTexture.name : "none")}).");
        }

        private static void OnBeforeRender()
        {
            try
            {
                if (P2Cam == null || P2Character == null) return;
                Character c = P2Character;
                Vector3 dir = c.data != null ? c.data.lookDirection : Vector3.zero;
                if (dir.sqrMagnitude < 0.0001f) dir = c.transform.forward;
                bool down = c.data != null && (c.data.fullyPassedOut || c.data.dead);
                Quaternion rot = Quaternion.LookRotation(dir);
                Character p1 = SplitContext.Active ? null : Character.localCharacter;
                bool watchP1 = down && p1 != null && p1 != c && p1.data != null && !p1.data.dead && !p1.data.fullyPassedOut;
                P2Spec = watchP1 ? p1 : null;
                Vector3 pos;
                if (watchP1) pos = p1.GetSpectatePosition() + rot * new Vector3(0f, 0.5f, -2.5f);
                else if (down) pos = c.Center - dir.normalized * 3f + Vector3.up * 0.75f;
                else pos = c.GetCameraPos(-0.5f);
                P2Cam.transform.SetPositionAndRotation(pos, rot);
            }
            catch (Exception e) { Fail("OnBeforeRender", e); }
        }

        private static void OnBeginCameraRendering(ScriptableRenderContext ctx, Camera cam)
        {
            if (!Active) return;
            try
            {
                Character p1 = Character.localCharacter;
                MainCamera main = MainCamera.instance;
                Character viewer;
                if (ReferenceEquals(cam, P2Cam)) viewer = P2Character;
                else if (main != null && ReferenceEquals(cam, main.cam)) viewer = p1;
                else return;
                BodyVisibility.Apply(p1, viewer);
                BodyVisibility.Apply(P2Character, viewer);
                P2Fog.BeginCamera(cam);
            }
            catch (Exception e) { Fail("BeginCameraRendering", e); }
        }

        private static void OnEndCameraRendering(ScriptableRenderContext ctx, Camera cam)
        {
            try { P2Fog.EndCamera(cam); }
            catch (Exception e) { Fail("EndCameraRendering", e); }
        }

        // ------------------------------------------------------------------ teardown

        /// <summary>Player 2's objects went away (scene change): reset what depends on them.</summary>
        private static void OnP2Lost()
        {
            _wasActive = false;
            _p2CharRoot = null;
            P2Character = null;
            if (P2Player == null) { P2Player = null; _p2PlayerRoot = null; }
            if (P2Cam != null) UnityEngine.Object.Destroy(P2Cam.gameObject);
            P2Cam = null;
            P2MainCamera = null;
            MainCamera main = MainCamera.instance;
            if (main != null && main.cam != null) main.cam.rect = new Rect(0f, 0f, 1f, 1f);
            BodyVisibility.Reset();
            P2Fog.Reset();
            SplitExtras.OnP2Lost();
        }

        private static void Teardown(bool removePlayer)
        {
            if (P2Character != null) UnityEngine.Object.Destroy(P2Character.gameObject);
            if (P2Player != null) UnityEngine.Object.Destroy(P2Player.gameObject);
            P2Player = null;
            _p2PlayerRoot = null;
            OnP2Lost();
            RestoreInput();
            if (removePlayer && P2Photon != null)
            {
                try { if (_room != null && _room.Players.ContainsKey(P2Photon.ActorNumber)) _room.RemovePlayer(P2Photon); }
                catch (Exception e) { Plugin.Log.LogWarning("Could not remove fake player: " + e.Message); }
                P2Photon = null;
            }
            _room = null;
            _spawnAt = -1f;
            Plugin.Log.LogInfo("Split screen stopped.");
        }
    }

    /// <summary>
    /// The game hides the local player's own body. With two cameras, each camera must
    /// hide only its own player's body, so we set that right before each camera renders.
    /// </summary>
    internal static class BodyVisibility
    {
        private static readonly int VertexGhost = Shader.PropertyToID("_VertexGhost");

        private class Entry
        {
            public HideTheBody hider;
            public Material[] mats = new Material[0];
            public float refreshAt;
        }

        private static readonly Dictionary<Character, Entry> Entries = new Dictionary<Character, Entry>();

        public static void Reset()
        {
            foreach (var kv in Entries)
            {
                if (kv.Key != null && kv.Value.hider != null)
                {
                    try { kv.Value.hider.Refresh(); } catch { }
                }
            }
            Entries.Clear();
        }

        private static Entry Get(Character c)
        {
            if (!Entries.TryGetValue(c, out Entry e))
            {
                e = new Entry { hider = c.GetComponentInChildren<HideTheBody>(true) };
                Entries[c] = e;
            }
            if (e.hider != null && Time.unscaledTime >= e.refreshAt)
            {
                e.refreshAt = Time.unscaledTime + 1f;
                var list = new List<Material>();
                HideTheBody h = e.hider;
                Add(list, h.body); Add(list, h.headRend); Add(list, h.sash); Add(list, h.medal);
                if (h.costumes != null) foreach (var r in h.costumes) Add(list, r);
                if (h.face != null) foreach (var r in h.face.GetComponentsInChildren<Renderer>()) Add(list, r);
                if (h.refs != null && h.refs.playerHats != null) foreach (var r in h.refs.playerHats) Add(list, r);
                e.mats = list.ToArray();
            }
            return e;
        }

        private static void Add(List<Material> list, Renderer r)
        {
            if (r == null) return;
            foreach (Material m in r.materials) if (m != null) list.Add(m);
        }

        /// <summary>Called from Update: warm the caches and keep shadow casters on for both players.</summary>
        public static void KeepShadowCasters(Character a, Character b)
        {
            Keep(a); Keep(b);
        }

        private static void Keep(Character c)
        {
            if (c == null) return;
            Entry e = Get(c);
            if (e.hider == null) return;
            if (e.hider.shadowCaster != null && !e.hider.shadowCaster.activeSelf) e.hider.shadowCaster.SetActive(true);
            if (e.hider.shadowCasterHat != null && !e.hider.shadowCasterHat.activeSelf) e.hider.shadowCasterHat.SetActive(true);
        }

        public static void Apply(Character c, Character viewer)
        {
            if (c == null || !Entries.TryGetValue(c, out Entry e) || e.hider == null) return;
            bool hide = ReferenceEquals(c, viewer) && c.data != null && !c.data.fullyPassedOut && !c.data.dead;
            float v = hide ? 1f : 0f;
            Material[] mats = e.mats;
            for (int i = 0; i < mats.Length; i++)
                if (mats[i] != null) mats[i].SetFloat(VertexGhost, v);
        }
    }
}
