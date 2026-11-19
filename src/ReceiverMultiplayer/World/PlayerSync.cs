using System.Collections.Generic;
using ReceiverMultiplayer.Net;
using ReceiverMultiplayer.Session;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReceiverMultiplayer.World
{
    /// <summary>Marks a remote player's pill so enemies and bullets can tell whose body it is.</summary>
    sealed class RemotePlayerBody : MonoBehaviour
    {
        public ushort PlayerId;
    }

    /// <summary>
    /// Player presence: each player streams their own body pose (position, look, crouch) and everyone else
    /// draws them as the game's own pill. The host relays every pose to every client in batches.
    /// </summary>
    static class PlayerSync
    {
        const byte FlagDead = 1, FlagTeleport = 2, KnownFlags = FlagDead | FlagTeleport;
        const float KeyframeSeconds = 1f;
        // A player whose movement data stops is still drawn where they were last seen (they leave the session at the
        // 30 s connection timeout). This used to be 3 s, so a few seconds of lost packets made players vanish.
        const float PresenceTimeout = 20f;
        const float GapSeconds = 2f; // silence this long is logged (and logged again when data resumes)
        const int EntryBytes = 22;
        const int MaxEntriesPerPacket = 48; // 4 + 48 * 22 = 1060 bytes

        sealed class Remote
        {
            public ushort Id;
            public readonly PoseBuffer Buffer = new PoseBuffer();
            public byte Flags;
            public float LastHeard = -100f;
            public GameObject Body;
            public Collider Hitbox;
            public AimScript IgnoringFor;
            public Renderer Visual;
            public Material[] Opaque;
            public Material Fade;
            public float DeadAt = -1f;
            public GameObject Belt;
            public PoseSample LastPose;
            public bool HasPose, FirstPerson, GearVisible;
            public Material[] BaseOpaque;   // the game's own pill material (shared with the game: never changed)
            public Material PlainMaterial;  // this player's copy of it, faded by closeness
            public Material SkinMaterial;   // that material with the player's skin image, while they have one
            public Color BaseEmission;      // the game's pill glow, at full visibility
            public int SkinVersion = -1;
            public float Visibility = 1f;   // 0 (camera inside the body) .. 1 (far away)
            public float DeathVisibility;   // visibility when they died (a skin fades out from there)
            public bool GapReported;        // "no movement data" was logged for the current silence

            // host relay copy of the newest raw sample
            public ushort RawTick, RawYaw;
            public byte RawScale;
            public short RawPitch;
            public Vector3 RawPos;
            public bool Dirty, HasRaw;
        }

        static readonly Dictionary<ushort, Remote> _remotes = new Dictionary<ushort, Remote>();
        static readonly List<ushort> _scratch = new List<ushort>();
        static readonly NetWriter _w = new NetWriter(1200);
        static readonly Remote _self = new Remote(); // host: own relay entry
        static float _time, _nextKeyframe, _nextPrune;
        static int _sentSpawn = -1;
        static AimScript _hiddenFor, _lastMe;
        static float _meSince;

        // ================= sending =================

        public static void NetTick(NetSession s)
        {
            var me = LocalPlayer.Get();
            if (me == null) return;
            HideOwnPill(me);

            var t = me.transform;
            var cam = me.main_camera != null ? me.main_camera.transform : t;
            Vector3 pos = t.position;
            // A dead body can fall forever once its tile unloads; never send a position the host would reject.
            float lim = Protocol.MaxWorldCoord * 0.9f;
            pos = new Vector3(Mathf.Clamp(pos.x, -lim, lim), Mathf.Clamp(pos.y, -lim, lim), Mathf.Clamp(pos.z, -lim, lim));
            float yaw = cam.eulerAngles.y;
            float pitch = Mathf.DeltaAngle(0f, cam.eulerAngles.x);
            float scale = t.localScale.y;
            byte flags = LocalPlayer.Dead(me) ? FlagDead : (byte)0;
            bool teleport = _sentSpawn != LocalPlayer.SpawnCount;
            if (teleport) flags |= FlagTeleport;

            // Sent every tick (20 Hz, ~0.5 KB/s): skipping "unchanged" ticks left gaps in slow turns that made
            // spectating stutter, and the saving was tiny.
            _sentSpawn = LocalPlayer.SpawnCount;

            ushort rawYaw = Codec.Yaw(yaw);
            short rawPitch = Codec.Pitch(pitch);
            byte rawScale = Codec.UnitByte(scale, 1.5f);

            if (s.IsHost)
            {
                StoreRaw(_self, WorldSync.Tick16, pos, rawYaw, rawPitch, rawScale, flags);
                _self.Id = s.LocalId;
                _self.LastHeard = _time;
                return;
            }

            var w = WorldSync.Begin(_w, Msg.PlayerState);
            w.U16(WorldSync.Tick16);
            Codec.Pos(w, pos);
            w.U16(rawYaw);
            w.I16(rawPitch);
            w.U8(rawScale);
            w.U8(flags);
            s.SendToHost(w, Channel.Unreliable);
        }

        static void StoreRaw(Remote r, ushort tick, Vector3 pos, ushort yaw, short pitch, byte scale, byte flags)
        {
            r.RawTick = tick; r.RawPos = pos; r.RawYaw = yaw; r.RawPitch = pitch; r.RawScale = scale; r.Flags = flags;
            r.Dirty = true;
            r.HasRaw = true;
        }

        // ================= host =================

        public static void HostReceive(NetSession s, ushort from, ref NetReader rd)
        {
            ushort tick = rd.U16();
            Vector3 pos = Codec.Pos(ref rd);
            ushort yaw = rd.U16();
            short pitch = rd.I16();
            byte scale = rd.U8();
            byte flags = rd.U8();
            rd.End();
            if ((flags & ~KnownFlags) != 0) throw new ProtocolException("bad player flags");

            var r = GetOrAdd(from);
            if (!Feed(r, tick, pos, yaw, pitch, scale, flags)) return;
            StoreRaw(r, tick, pos, yaw, pitch, scale, flags);
        }

        /// <summary>Host: relay changed poses every tick, everyone's every second.</summary>
        public static void HostRelay(NetSession s)
        {
            bool keyframe = _time >= _nextKeyframe;
            if (keyframe) _nextKeyframe = _time + KeyframeSeconds;

            var w = _w;
            int count = 0, countAt = 0;
            foreach (var r in Entries())
            {
                if (!r.HasRaw || _time - r.LastHeard > PresenceTimeout) continue;
                if (!r.Dirty && !keyframe) continue;
                r.Dirty = false;
                if (count == 0)
                {
                    WorldSync.Begin(w, Msg.PlayerStates);
                    countAt = w.Length;
                    w.U8(0);
                }
                w.U16(r.Id);
                w.U16(r.RawTick);
                Codec.Pos(w, r.RawPos);
                w.U16(r.RawYaw);
                w.I16(r.RawPitch);
                w.U8(r.RawScale);
                w.U8(r.Flags);
                if (++count == MaxEntriesPerPacket)
                {
                    w.Buffer[countAt] = (byte)count;
                    s.SendToClients(w, Channel.Unreliable);
                    count = 0;
                }
            }
            if (count > 0)
            {
                w.Buffer[countAt] = (byte)count;
                s.SendToClients(w, Channel.Unreliable);
            }
        }

        static IEnumerable<Remote> Entries()
        {
            yield return _self;
            foreach (var r in _remotes.Values) yield return r;
        }

        // ================= client =================

        public static void ClientReceive(NetSession s, ref NetReader rd)
        {
            int n = rd.U8();
            if (n > MaxEntriesPerPacket || rd.Remaining != n * EntryBytes) throw new ProtocolException("bad pose batch");
            for (int i = 0; i < n; i++)
            {
                ushort id = rd.U16();
                ushort tick = rd.U16();
                Vector3 pos = Codec.Pos(ref rd);
                ushort yaw = rd.U16();
                short pitch = rd.I16();
                byte scale = rd.U8();
                byte flags = rd.U8();
                if ((flags & ~KnownFlags) != 0) throw new ProtocolException("bad player flags");
                if (id == s.LocalId) continue;
                Feed(GetOrAdd(id), tick, pos, yaw, pitch, scale, flags);
            }
            rd.End();
        }

        static bool Feed(Remote r, ushort rawTick, Vector3 pos, ushort yaw, short pitch, byte scale, byte flags)
        {
            long tick = r.Buffer.Unwrap.Next(rawTick);
            if (tick < 0) return false;
            float silent = _time - r.LastHeard;
            if (r.GapReported)
            {
                r.GapReported = false;
                Plugin.Log.LogInfo($"Movement data from {NameOf(r.Id)} again after {silent:0.0} s.");
            }
            if (r.Buffer.HasData && silent > 1f) SyncStats.Count("movement data gaps over 1 s");
            if ((flags & FlagTeleport) != 0) r.Buffer.Clear();
            float y = Codec.Yaw(yaw), p = Codec.Pitch(pitch);
            r.Buffer.Add(new PoseSample
            {
                Tick = tick,
                Pos = pos,
                R0 = Quaternion.Euler(0f, y, 0f),
                R1 = Quaternion.Euler(p, y, 0f),
                F0 = Mathf.Clamp(Codec.UnitByte(scale, 1.5f), 0.1f, 1.5f),
            });
            r.Flags = flags;
            r.LastHeard = _time;
            return true;
        }

        static string NameOf(ushort id) => NetSession.Current?.Players.Find(p => p.Id == id)?.Name ?? "a player";

        static Remote GetOrAdd(ushort id)
        {
            if (!_remotes.TryGetValue(id, out var r))
                _remotes[id] = r = new Remote { Id = id };
            return r;
        }

        // ================= every frame =================

        public static void Frame(float dt, bool inRun)
        {
            _time += dt;
            var s = NetSession.Current;
            if (_time >= _nextPrune)
            {
                _nextPrune = _time + 1f;
                Prune(s);
            }

            var me = LocalPlayer.Get();
            var cam = Camera.main;
            // A long frame here is this game stalling, not the others going quiet (their data from that time was
            // read just before this frame's clock caught up): say so instead of blaming them.
            bool ownStall = dt > GapSeconds;
            if (me != _lastMe) { _lastMe = me; _meSince = _time; }
            if (ownStall && inRun && me != null && _time - dt - _meSince > 5f) // not the level loading
                Plugin.Log.LogWarning($"This game froze for {dt:0.0} s (the others saw you stand still meanwhile).");
            foreach (var r in _remotes.Values)
            {
                if (ownStall && r.LastHeard >= _time - dt - 0.001f) r.LastHeard = _time; // heard in this frame's read
                if (inRun && me != null && r.Buffer.HasData && !r.GapReported && _time - r.LastHeard > GapSeconds)
                {
                    r.GapReported = true;
                    Plugin.Log.LogWarning($"No movement data from {NameOf(r.Id)} for {GapSeconds:0} s; drawing them where they were last seen.");
                }
                bool present = inRun && me != null && r.Buffer.HasData && _time - r.LastHeard <= PresenceTimeout;
                if (!present)
                {
                    if (r.Body != null) r.Body.SetActive(false);
                    r.HasPose = false;
                    GearSync.Hide(r.Id);
                    continue;
                }
                if (r.Body == null) r.Body = CreateBody(r.Id, me);
                if (r.IgnoringFor != me) IgnoreLocalCollisions(r, me);
                if (!r.Buffer.Sample(dt, out var pose)) continue;
                r.LastPose = pose;
                r.HasPose = true;

                var t = r.Body.transform;
                t.SetPositionAndRotation(pose.Pos, pose.R0);
                t.localScale = new Vector3(1f, pose.F0, 1f);
                UpdateSkin(r);
                UpdateDeathFade(r);

                // Watching through this player's eyes: hide their body and belt, keep what's in their hands.
                // Otherwise the closer the camera, the more see-through the body (gone only from inside it).
                bool firstPerson = Spectator.Active && Spectator.Target == r.Id;
                if (r.Visual != null && r.DeadAt < 0f)
                {
                    r.Visibility = Closeness(cam, r);
                    ApplyLook(r, r.Visibility);
                    r.Visual.enabled = !firstPerson && r.Visibility > 0f;
                }
                if (r.Belt != null) r.Belt.SetActive(!firstPerson);
                r.FirstPerson = firstPerson;
                r.GearVisible = r.Body.activeSelf && r.DeadAt < 0f;
            }
        }

        /// <summary>
        /// End of frame, after the game has moved its camera: put the spectator camera on the watched player's
        /// eyes, then place everyone's gear (some of it relative to that camera) from the same poses.
        /// </summary>
        public static void LateFrame()
        {
            var cam = Camera.main;
            if (Spectator.Active && cam != null && TryGetCameraPose(Spectator.Target, out var eye))
                cam.transform.SetPositionAndRotation(eye.position, eye.rotation);
            foreach (var r in _remotes.Values)
            {
                if (r.Body == null || !r.HasPose) continue;
                GearSync.Place(r.Id, r.Body.transform, r.LastPose, r.GearVisible, r.FirstPerson, _time, cam, r.Buffer.RenderTick);
            }
        }

        /// <summary>Present and alive (for spectating and targeting).</summary>
        public static bool IsAlive(ushort id) =>
            _remotes.TryGetValue(id, out var r) && r.HasPose && _time - r.LastHeard <= PresenceTimeout && (r.Flags & FlagDead) == 0;

        public static List<ushort> LiveIds()
        {
            var list = new List<ushort>();
            foreach (var r in _remotes.Values)
                if (IsAlive(r.Id)) list.Add(r.Id);
            return list;
        }

        /// <summary>The middle of this player's body as rendered here (for their footsteps and other body sounds).</summary>
        public static bool TryGetBodyPosition(ushort id, out Vector3 pos)
        {
            pos = default;
            if (!_remotes.TryGetValue(id, out var r) || !r.HasPose) return false;
            pos = r.LastPose.Pos;
            return true;
        }

        /// <summary>A present player's body object here (enemies aim at it; its rotation is their facing).</summary>
        public static bool TryGetBody(ushort id, out Transform body)
        {
            body = null;
            if (!_remotes.TryGetValue(id, out var r) || r.Body == null || !r.Body.activeInHierarchy || !r.HasPose) return false;
            body = r.Body.transform;
            return true;
        }

        /// <summary>A living player's body as drawn here right now: its centre and crouch height (vertical scale).</summary>
        public static bool TryGetLivePose(ushort id, out Vector3 pos, out float height)
        {
            pos = default;
            height = 1f;
            if (!IsAlive(id) || !_remotes.TryGetValue(id, out var r)) return false;
            pos = r.LastPose.Pos;
            height = r.LastPose.F0;
            return true;
        }

        /// <summary>
        /// Unwrap a 16-bit tick from this player onto the same timeline as their poses (so gear and pose samples
        /// line up exactly); before any pose has arrived, <paramref name="fallback"/> is used.
        /// </summary>
        public static long UnwrapNear(ushort id, ushort raw, ref TickUnwrapper fallback)
        {
            if (_remotes.TryGetValue(id, out var r) && r.Buffer.HasData)
            {
                long near = r.Buffer.Newest.Tick;
                return near + unchecked((short)(raw - (ushort)near));
            }
            return fallback.Next(raw);
        }

        /// <summary>Where this player's eyes are right now (as rendered here).</summary>
        public static bool TryGetCameraPose(ushort id, out Pose pose)
        {
            pose = default;
            if (!_remotes.TryGetValue(id, out var r) || !r.HasPose) return false;
            pose = GearSync.CameraFrame(r.LastPose.Pos, r.LastPose.F0, r.LastPose.R1);
            return true;
        }

        // The game's pill is Unity's Standard shader in premultiplied-transparent mode with alpha 0: the body itself
        // is invisible and only its faint grey glow and gloss show, which is why teammates are see-through. A skin
        // keeps that material and adds the image at partial opacity (plus a faint glow in its own colours, so it
        // still reads in the dark). Both fade with closeness; from inside the body nothing is drawn.
        const float SkinAlpha = 0.55f;   // skin opacity at full visibility
        const float SkinGlow = 0.18f;    // skin glow at full visibility (the plain pill glows 0.125 grey)
        const float NearVisibility = 0.15f, NearDistance = 0.6f, FarDistance = 6f, InsideRamp = 0.2f;
        static readonly int ColorId = Shader.PropertyToID("_Color");
        static readonly int EmissionColorId = Shader.PropertyToID("_EmissionColor");
        static readonly int EmissionMapId = Shader.PropertyToID("_EmissionMap");

        /// <summary>The player's chosen image wrapped onto their pill (the game's pill material with that texture).</summary>
        static void UpdateSkin(Remote r)
        {
            int version = Skins.SkinSync.VersionOf(r.Id);
            if (version == r.SkinVersion || r.Visual == null) return;
            r.SkinVersion = version;
            if (r.SkinMaterial != null) { Object.Destroy(r.SkinMaterial); r.SkinMaterial = null; }
            var baseMat = r.BaseOpaque != null && r.BaseOpaque.Length > 0 ? r.BaseOpaque[0] : null;
            if (baseMat == null)
            {
                r.Opaque = r.BaseOpaque;
                return;
            }
            if (r.PlainMaterial == null)
            {
                r.PlainMaterial = new Material(baseMat) { name = "MP_Pill" };
                r.BaseEmission = baseMat.HasProperty(EmissionColorId) ? baseMat.GetColor(EmissionColorId) : Color.black;
            }
            var tex = Skins.SkinSync.TextureFor(r.Id);
            if (tex != null)
            {
                r.SkinMaterial = new Material(baseMat) { mainTexture = tex, name = "MP_Skin" };
                if (r.SkinMaterial.HasProperty(EmissionMapId)) r.SkinMaterial.SetTexture(EmissionMapId, tex);
                r.Opaque = new[] { r.SkinMaterial };
            }
            else r.Opaque = new[] { r.PlainMaterial };
            if (r.Fade != null) { Object.Destroy(r.Fade); r.Fade = null; } // rebuilt with the new image if they die

            if (r.DeadAt < 0f)
            {
                ApplyLook(r, r.Visibility);
                r.Visual.sharedMaterials = r.Opaque;
            }
            else if (r.SkinMaterial != null)
            {
                ApplyLook(r, r.DeathVisibility);
                r.Visual.sharedMaterials = r.Opaque;
            }
            else
            {
                r.Fade = MakeFadeMaterial(r.Opaque);
                if (r.Fade != null) r.Visual.sharedMaterial = r.Fade;
            }
        }

        /// <summary>Opacity and glow for this visibility (0..1).</summary>
        static void ApplyLook(Remote r, float v)
        {
            if (r.SkinMaterial != null)
            {
                if (r.SkinMaterial.HasProperty(ColorId)) r.SkinMaterial.SetColor(ColorId, new Color(1f, 1f, 1f, SkinAlpha * v));
                if (r.SkinMaterial.HasProperty(EmissionColorId)) r.SkinMaterial.SetColor(EmissionColorId, new Color(SkinGlow * v, SkinGlow * v, SkinGlow * v, 1f));
            }
            if (r.PlainMaterial != null && r.PlainMaterial.HasProperty(EmissionColorId))
            {
                var e = r.BaseEmission;
                r.PlainMaterial.SetColor(EmissionColorId, new Color(e.r * v, e.g * v, e.b * v, e.a));
            }
        }

        /// <summary>
        /// How visible this body is from the camera: 1 from <see cref="FarDistance"/> out, easing down to
        /// <see cref="NearVisibility"/> at arm's length, and 0 only with the camera inside the body.
        /// </summary>
        static float Closeness(Camera cam, Remote r)
        {
            if (cam == null || r.Visual == null) return 1f;
            var t = r.Visual.transform;
            var s = t.lossyScale;
            float radius = 0.5f * Mathf.Max(Mathf.Abs(s.x), Mathf.Abs(s.z)); // the capsule mesh is 1 wide, 2 tall
            float seg = Mathf.Max(0f, Mathf.Abs(s.y) - radius);
            var centre = t.position;
            var eye = cam.transform.position;
            var onAxis = new Vector3(centre.x, Mathf.Clamp(eye.y, centre.y - seg, centre.y + seg), centre.z);
            float d = Vector3.Distance(eye, onAxis);

            if (d <= radius) return 0f;
            if (d < radius + InsideRamp) return NearVisibility * (d - radius) / InsideRamp;
            float k = Mathf.Clamp01((d - NearDistance) / (FarDistance - NearDistance));
            return NearVisibility + (1f - NearVisibility) * (1f - (1f - k) * (1f - k));
        }

        const float FadeSeconds = 1.5f;
        static Shader _fadeShader;
        static bool _fadeShaderLooked;

        /// <summary>A player who dies fades out where they fell instead of vanishing; bullets and enemies ignore them.</summary>
        static void UpdateDeathFade(Remote r)
        {
            bool dead = (r.Flags & FlagDead) != 0;
            if (!dead)
            {
                if (r.DeadAt >= 0f)
                {
                    r.DeadAt = -1f;
                    if (r.Visual != null && r.Opaque != null) r.Visual.sharedMaterials = r.Opaque;
                }
                if (!r.Body.activeSelf) r.Body.SetActive(true);
                if (r.Hitbox != null) r.Hitbox.enabled = true;
                return;
            }

            if (r.DeadAt < 0f)
            {
                r.DeadAt = _time;
                r.DeathVisibility = r.Visibility;
                if (r.Hitbox != null) r.Hitbox.enabled = false;
                if (r.SkinMaterial == null) // a skin fades out on its own material; the plain pill as before
                {
                    if (r.Visual != null && r.Fade == null) r.Fade = MakeFadeMaterial(r.Opaque);
                    if (r.Visual != null && r.Fade != null) r.Visual.sharedMaterial = r.Fade;
                }
            }
            float alpha = 1f - (_time - r.DeadAt) / FadeSeconds;
            if (alpha <= 0f)
            {
                if (r.Body.activeSelf) r.Body.SetActive(false);
                return;
            }
            if (!r.Body.activeSelf) r.Body.SetActive(true);
            if (r.SkinMaterial != null)
            {
                ApplyLook(r, r.DeathVisibility * alpha);
            }
            else if (r.Fade != null)
            {
                var c = r.Fade.color;
                c.a = alpha;
                r.Fade.color = c;
            }
            else
            {
                // No transparent shader available: shrink away instead.
                r.Body.transform.localScale *= alpha;
            }
        }

        static Material MakeFadeMaterial(Material[] opaque)
        {
            if (!_fadeShaderLooked)
            {
                _fadeShaderLooked = true;
                _fadeShader = Shader.Find("Legacy Shaders/Transparent/Diffuse")
                              ?? Shader.Find("Legacy Shaders/Transparent/VertexLit")
                              ?? Shader.Find("Sprites/Default");
                if (_fadeShader == null) Plugin.Log.LogWarning("No transparent shader found; dead players shrink away instead of fading.");
            }
            if (_fadeShader == null) return null;
            var source = opaque != null && opaque.Length > 0 ? opaque[0] : null;
            var m = new Material(_fadeShader);
            if (source != null)
            {
                if (source.HasProperty("_Color")) m.color = source.color;
                if (source.HasProperty("_MainTex")) m.mainTexture = source.mainTexture;
            }
            return m;
        }

        static void Prune(NetSession s)
        {
            _scratch.Clear();
            foreach (var id in _remotes.Keys)
                if (s == null || !s.Players.Exists(p => p.Id == id) || id == s.LocalId)
                    _scratch.Add(id);
            foreach (var id in _scratch) Remove(id);
        }

        public static void Remove(ushort id)
        {
            GearSync.RemoveView(id);
            if (!_remotes.TryGetValue(id, out var r)) return;
            DestroyOwned(r);
            _remotes.Remove(id);
        }

        public static void Reset()
        {
            foreach (var r in _remotes.Values) DestroyOwned(r);
            _remotes.Clear();
            _self.HasRaw = false;
            _self.Dirty = false;
            _sentSpawn = -1;
            _hiddenFor = null;
        }

        /// <summary>The body and the materials made for it (the game's own pill material is never destroyed).</summary>
        static void DestroyOwned(Remote r)
        {
            if (r.Body != null) Object.Destroy(r.Body);
            if (r.Fade != null) Object.Destroy(r.Fade);
            if (r.SkinMaterial != null) Object.Destroy(r.SkinMaterial);
            if (r.PlainMaterial != null) Object.Destroy(r.PlainMaterial);
            r.Body = null;
            r.Fade = r.SkinMaterial = r.PlainMaterial = null;
        }

        // ================= queries (enemies, tiles) =================

        /// <summary>Remote players that are present and alive, as (body transform, id).</summary>
        public static IEnumerable<RemotePlayerBody> LiveBodies()
        {
            foreach (var r in _remotes.Values)
            {
                if (r.Body == null || !r.Body.activeSelf) continue;
                if (_time - r.LastHeard > PresenceTimeout || (r.Flags & FlagDead) != 0) continue;
                var b = r.Body.GetComponent<RemotePlayerBody>();
                if (b != null) yield return b;
            }
        }

        /// <summary>Newest known positions of present remote players (dead or alive).</summary>
        public static IEnumerable<KeyValuePair<ushort, Vector3>> RemotePositions()
        {
            foreach (var kv in _remotes)
                if (kv.Value.Buffer.HasData && _time - kv.Value.LastHeard <= PresenceTimeout)
                    yield return new KeyValuePair<ushort, Vector3>(kv.Key, kv.Value.Buffer.Newest.Pos);
        }

        public static bool TryGetPosition(ushort id, out Vector3 pos)
        {
            pos = default;
            if (!_remotes.TryGetValue(id, out var r) || !r.Buffer.HasData) return false;
            pos = r.Buffer.Newest.Pos;
            return true;
        }

        public static Vector3 VelocityOf(ushort id) => _remotes.TryGetValue(id, out var r) ? r.Buffer.Velocity : Vector3.zero;

        // ================= bodies =================

        /// <summary>A copy of the game's own pill (the player's "Capsule" child) plus the player's hitbox.</summary>
        static GameObject CreateBody(ushort id, AimScript template)
        {
            var root = new GameObject("MP_Player_" + id) { layer = template.gameObject.layer };
            var rb = root.AddComponent<Rigidbody>();
            rb.isKinematic = true;
            rb.useGravity = false;
            var hitbox = root.AddComponent<CapsuleCollider>();
            hitbox.direction = 1;
            hitbox.height = 1.3f;
            hitbox.radius = 0.2f;
            hitbox.center = new Vector3(0f, 0.25f, 0f);
            var templateHitbox = template.GetComponent<CapsuleCollider>();
            if (templateHitbox != null)
            {
                hitbox.height = templateHitbox.height;
                hitbox.radius = templateHitbox.radius;
                hitbox.center = templateHitbox.center;
            }
            root.AddComponent<RemotePlayerBody>().PlayerId = id;

            var capsule = template.transform.Find("Capsule");
            var mf = capsule != null ? capsule.GetComponent<MeshFilter>() : null;
            var mr = capsule != null ? capsule.GetComponent<MeshRenderer>() : null;
            GameObject visual;
            if (mf != null && mr != null)
            {
                visual = new GameObject("Capsule", typeof(MeshFilter), typeof(MeshRenderer));
                visual.GetComponent<MeshFilter>().sharedMesh = mf.sharedMesh;
                visual.GetComponent<MeshRenderer>().sharedMaterials = mr.sharedMaterials;
                visual.transform.SetParent(root.transform, false);
                visual.transform.localPosition = capsule.localPosition;
                visual.transform.localRotation = capsule.localRotation;
                visual.transform.localScale = capsule.localScale;
                visual.layer = capsule.gameObject.layer;
            }
            else
            {
                visual = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                Object.Destroy(visual.GetComponent<Collider>());
                visual.transform.SetParent(root.transform, false);
                visual.transform.localPosition = new Vector3(0f, 0.225f, 0f);
                visual.transform.localScale = new Vector3(0.396f, 0.67f, 0.396f);
            }
            var renderer = visual.GetComponent<MeshRenderer>();
            renderer.enabled = true;
            renderer.shadowCastingMode = ShadowCastingMode.On;

            var r = _remotes[id];
            r.Hitbox = hitbox;
            r.IgnoringFor = null;
            r.Visual = renderer;
            r.Opaque = renderer.sharedMaterials;
            r.BaseOpaque = r.Opaque;
            r.SkinVersion = -1; // apply their skin (if any) on the next frame
            r.DeadAt = -1f;
            r.Belt = null; // the belt itself is invisible; only what hangs on it shows
            return root;
        }

        /// <summary>Players pass through each other (no shoving or getting stuck at the shared spawn).</summary>
        static void IgnoreLocalCollisions(Remote r, AimScript me)
        {
            r.IgnoringFor = me;
            if (r.Hitbox == null) return;
            foreach (var c in me.GetComponents<Collider>())
                Physics.IgnoreCollision(c, r.Hitbox, true);
        }

        /// <summary>The local pill is never drawn for its owner (the camera sits inside it anyway).</summary>
        static void HideOwnPill(AimScript me)
        {
            if (_hiddenFor == me) return;
            _hiddenFor = me;
            var capsule = me.transform.Find("Capsule");
            var renderer = capsule != null ? capsule.GetComponent<Renderer>() : null;
            if (renderer != null) renderer.enabled = false;
        }
    }
}
