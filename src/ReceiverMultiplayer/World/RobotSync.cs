using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using ReceiverMultiplayer.Net;
using ReceiverMultiplayer.Session;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ReceiverMultiplayer.World
{
    /// <summary>
    /// Host-authoritative enemies. Every machine builds the same turrets and drones from the seed (per tile),
    /// and they get the same id: tile index and spawn order. The host runs their AI against the nearest
    /// player and streams poses and states; clients turn theirs into puppets. A client's bullet that hits a
    /// puppet is reported to the host, which applies the damage and broadcasts the destroyed part.
    /// Turret bullets are replayed on clients and hurt whoever they reach there; drone shocks are sent to the
    /// player they reach on the host.
    /// </summary>
    static class RobotSync
    {
        const byte AllAlive = 0x3F;
        const int EntryBytes = 35;          // id 4, mask 1, ai 1, pos 12, three rotations 12, rotor 1, light 4
        const int MaxEntriesPerPacket = 32; // 6 + 32 * 35 = 1126 bytes
        const float KeyframeSeconds = 1f;
        const float ReconcileSeconds = 0.6f;
        const float RotorMax = 30f, LightMax = 8f;
        const RobotType PuppetType = (RobotType)99; // falls through every branch of RobotScript.Update

        sealed class Entry
        {
            public uint Id;
            public RobotScript Robot;
            public Transform[] Parts;
            public bool Puppet;

            // host: last sent
            public Vector3 SentPos;
            public uint SentRot, SentGun, SentCam;
            public byte SentMask, SentAi, SentRotor, SentR, SentG, SentB, SentI;
            public float SentAt = -100f;

            // client: received
            public readonly PoseBuffer Buffer = new PoseBuffer();
            public bool HasState;
            public byte Mask = AllAlive, Ai, LastAi;
            public float Rotor, LightIntensity, MismatchSince = -1f, NextSpark, TopRotor, BottomRotor;
            public Color LightColor;
            public Quaternion LastGunRot = Quaternion.identity;
        }

        static readonly Dictionary<uint, Entry> _byId = new Dictionary<uint, Entry>();
        static readonly Dictionary<RobotScript, Entry> _byRobot = new Dictionary<RobotScript, Entry>();
        static readonly Dictionary<uint, byte> _knownMask = new Dictionary<uint, byte>(); // alive masks of robots not loaded here
        const int MaxKnownMasks = 50000; // far more robots than any level has; a host making up ids can't fill memory

        static void RememberMask(uint id, byte mask)
        {
            if (_knownMask.Count < MaxKnownMasks || _knownMask.ContainsKey(id)) _knownMask[id] = mask;
        }
        static readonly Dictionary<ushort, float> _lastShock = new Dictionary<ushort, float>();
        static readonly List<uint> _scratch = new List<uint>();
        static readonly NetWriter _w = new NetWriter(1200);
        static readonly NetWriter _ev = new NetWriter(128);
        static float _time, _nextKeyframe, _nextHousekeeping;
        static bool _applying;   // our own Damage()/WasShot() calls pass the puppet guards
        static RobotScript _updating;

        // ---- private game fields ----
        static readonly AccessTools.FieldRef<RobotScript, bool> BatteryAlive = AccessTools.FieldRefAccess<RobotScript, bool>("battery_alive");
        static readonly AccessTools.FieldRef<RobotScript, bool> MotorAlive = AccessTools.FieldRefAccess<RobotScript, bool>("motor_alive");
        static readonly AccessTools.FieldRef<RobotScript, bool> CameraAlive = AccessTools.FieldRefAccess<RobotScript, bool>("camera_alive");
        static readonly AccessTools.FieldRef<RobotScript, bool> TriggerAlive = AccessTools.FieldRefAccess<RobotScript, bool>("trigger_alive");
        static readonly AccessTools.FieldRef<RobotScript, bool> BarrelAlive = AccessTools.FieldRefAccess<RobotScript, bool>("barrel_alive");
        static readonly AccessTools.FieldRef<RobotScript, bool> AmmoAlive = AccessTools.FieldRefAccess<RobotScript, bool>("ammo_alive");
        static readonly AccessTools.FieldRef<RobotScript, AIState> AiState = AccessTools.FieldRefAccess<RobotScript, AIState>("ai_state");
        static readonly AccessTools.FieldRef<RobotScript, float> RotorSpeed = AccessTools.FieldRefAccess<RobotScript, float>("rotor_speed");
        static readonly AccessTools.FieldRef<RobotScript, float> TopRotorRotation = AccessTools.FieldRefAccess<RobotScript, float>("top_rotor_rotation");
        static readonly AccessTools.FieldRef<RobotScript, float> BottomRotorRotation = AccessTools.FieldRefAccess<RobotScript, float>("bottom_rotor_rotation");
        static readonly AccessTools.FieldRef<RobotScript, Light> LightObject = AccessTools.FieldRefAccess<RobotScript, Light>("lightObject");
        static readonly AccessTools.FieldRef<RobotScript, AudioSource> TaserSource = AccessTools.FieldRefAccess<RobotScript, AudioSource>("audiosource_taser");
        static readonly AccessTools.FieldRef<RobotScript, AudioSource> EffectSource = AccessTools.FieldRefAccess<RobotScript, AudioSource>("audiosource_effect");
        static readonly AccessTools.FieldRef<BulletScript, Vector3> BulletVelocity = AccessTools.FieldRefAccess<BulletScript, Vector3>("velocity");
        static readonly AccessTools.FieldRef<RobotScript, AudioSource> MotorSource = AccessTools.FieldRefAccess<RobotScript, AudioSource>("audiosource_motor");
        static readonly AccessTools.FieldRef<RobotScript, GameObject> MotorObject = AccessTools.FieldRefAccess<RobotScript, GameObject>("object_audiosource_motor");
        static readonly AccessTools.FieldRef<RobotScript, float> LineOfSight = AccessTools.FieldRefAccess<RobotScript, float>("sound_line_of_sight");
        static readonly AccessTools.FieldRef<RobotScript, float> SleepDistance = AccessTools.FieldRefAccess<RobotScript, float>("kSleepDistance");
        static readonly AccessTools.FieldRef<RobotScript, LensFlare> Flare = AccessTools.FieldRefAccess<RobotScript, LensFlare>("lensFlareObject");

        static byte ReadMask(RobotScript r) => (byte)(
            (BatteryAlive(r) ? 1 : 0) | (MotorAlive(r) ? 2 : 0) | (CameraAlive(r) ? 4 : 0) |
            (TriggerAlive(r) ? 8 : 0) | (BarrelAlive(r) ? 16 : 0) | (AmmoAlive(r) ? 32 : 0));

        /// <summary>Mark parts dead without effects (catching up with the host, or a robot rebuilt from a deleted tile).</summary>
        static void KillSilently(RobotScript r, byte aliveMask)
        {
            if ((aliveMask & 1) == 0) BatteryAlive(r) = false;
            if ((aliveMask & 2) == 0) MotorAlive(r) = false;
            if ((aliveMask & 4) == 0) CameraAlive(r) = false;
            if ((aliveMask & 8) == 0) TriggerAlive(r) = false;
            if ((aliveMask & 16) == 0) BarrelAlive(r) = false;
            if ((aliveMask & 32) == 0) AmmoAlive(r) = false;
        }

        static bool IsPuppet(RobotScript r) => r != null && _byRobot.TryGetValue(r, out var e) && e.Puppet;

        /// <summary>The shared id of this enemy (the same on every machine), if it's one the session knows.</summary>
        public static bool TryGetId(RobotScript r, out uint id)
        {
            id = 0;
            if (r == null || !_byRobot.TryGetValue(r, out var e)) return false;
            id = e.Id;
            return true;
        }

        /// <summary>The enemy with this id, if it's loaded here.</summary>
        public static RobotScript Find(uint id) => _byId.TryGetValue(id, out var e) && e.Robot != null ? e.Robot : null;

        // ================= registry =================

        /// <summary>Id = tile index (16 bits) and spawn order within the tile's enemy group (8 bits).</summary>
        static bool TryMakeId(RobotScript robot, out uint id)
        {
            id = 0;
            var enemies = robot.transform.parent;
            var level = enemies != null ? enemies.parent : null;
            if (enemies == null || level == null || enemies.name != "enemies") return false;
            string n = level.name;
            int cut = n.IndexOf('_');
            if (cut <= 0 || !int.TryParse(n.Substring(0, cut), out int tile)) return false;
            int index = robot.transform.GetSiblingIndex();
            if (index > 255 || tile < short.MinValue || tile > short.MaxValue) return false;
            id = ((uint)(ushort)(short)tile << 8) | (uint)index;
            return true;
        }

        static void Register(RobotScript robot)
        {
            // Level enemies are named by their tile; Green Demons (one per player) by the session.
            if (!TryMakeId(robot, out uint id) && !DemonSync.TryClaim(robot, out id)) return;
            if (_byId.TryGetValue(id, out var old) && old.Robot != null && old.Robot != robot)
            {
                Plugin.Log.LogWarning($"Enemy id {id:X6} is already in use; this enemy stays local.");
                return;
            }
            var e = new Entry { Id = id, Robot = robot, Puppet = !WorldSync.IsHost };
            _byId[id] = e;
            _byRobot[robot] = e;
        }

        static void OnStarted(RobotScript robot)
        {
            if (!_byRobot.TryGetValue(robot, out var e)) return;
            e.Parts = robot.GetComponentsInChildren<Transform>(true);
            if (e.Puppet)
            {
                var rb = robot.GetComponent<Rigidbody>();
                if (rb != null) rb.isKinematic = true;
            }
            if (_knownMask.TryGetValue(e.Id, out byte mask))
            {
                KillSilently(robot, mask);
                e.Mask = mask;
            }
        }

        public static void Housekeeping()
        {
            if (_time < _nextHousekeeping) return;
            _nextHousekeeping = _time + 2f;
            _scratch.Clear();
            foreach (var kv in _byId)
                if (kv.Value.Robot == null)
                    _scratch.Add(kv.Key);
            foreach (var id in _scratch)
            {
                var e = _byId[id];
                _byId.Remove(id);
                // Destroyed with its tile: remember its damage so a rebuilt copy comes back damaged.
                // (The host already records every change in the Damage postfix.)
                if (!WorldSync.IsHost && e.Mask != AllAlive) _knownMask[id] = e.Mask;
            }
            if (_scratch.Count > 0)
            {
                var dead = _byRobot.Keys.Where(k => k == null).ToList();
                foreach (var k in dead) _byRobot.Remove(k);
            }
        }

        public static void Reset()
        {
            _byId.Clear();
            _byRobot.Clear();
            _knownMask.Clear();
            _lastShock.Clear();
            _applying = false;
            _updating = null;
        }

        // ================= host: stream =================

        public static void HostNetTick(NetSession s)
        {
            _time += Protocol.WorldTick;
            bool keyframe = _time >= _nextKeyframe;
            if (keyframe) _nextKeyframe = _time + KeyframeSeconds;

            var w = _w;
            int count = 0, countAt = 0;
            foreach (var e in _byId.Values)
            {
                var r = e.Robot;
                if (r == null || !r.gameObject.activeInHierarchy) continue;
                var t = r.transform;
                Vector3 pos = t.position;
                uint rot = Codec.Quat(t.rotation);
                uint gun = r.gun_pivot != null ? Codec.Quat(r.gun_pivot.localRotation) : 0u;
                uint cam = r.camera_pivot != null ? Codec.Quat(r.camera_pivot.localRotation) : 0u;
                byte mask = ReadMask(r);
                byte ai = (byte)AiState(r);
                byte rotor = Codec.UnitByte(RotorSpeed(r), RotorMax);
                var light = LightObject(r);
                Color c = light != null ? light.color : Color.black;
                byte lr = Codec.UnitByte(c.r, 1f), lg = Codec.UnitByte(c.g, 1f), lb = Codec.UnitByte(c.b, 1f);
                byte li = light != null ? Codec.UnitByte(light.intensity, LightMax) : (byte)0;

                bool changed = (pos - e.SentPos).sqrMagnitude > 1e-6f || rot != e.SentRot || gun != e.SentGun || cam != e.SentCam
                               || mask != e.SentMask || ai != e.SentAi || rotor != e.SentRotor
                               || lr != e.SentR || lg != e.SentG || lb != e.SentB || li != e.SentI;
                if (!changed && !keyframe) continue;
                e.SentPos = pos; e.SentRot = rot; e.SentGun = gun; e.SentCam = cam; e.SentMask = mask; e.SentAi = ai;
                e.SentRotor = rotor; e.SentR = lr; e.SentG = lg; e.SentB = lb; e.SentI = li; e.SentAt = _time;

                if (count == 0)
                {
                    WorldSync.Begin(w, Msg.RobotStates);
                    w.U16(WorldSync.Tick16);
                    countAt = w.Length;
                    w.U8(0);
                }
                w.U32(e.Id);
                w.U8(mask);
                w.U8(ai);
                Codec.Pos(w, pos);
                w.U32(rot);
                w.U32(gun);
                w.U32(cam);
                w.U8(rotor);
                w.U8(lr); w.U8(lg); w.U8(lb); w.U8(li);
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

        // ================= client: receive =================

        public static void ClientReceiveStates(ref NetReader rd)
        {
            ushort tick = rd.U16();
            int n = rd.U8();
            if (n > MaxEntriesPerPacket || rd.Remaining != n * EntryBytes) throw new ProtocolException("bad enemy batch");
            for (int i = 0; i < n; i++)
            {
                uint id = rd.U32();
                byte mask = rd.U8();
                byte ai = rd.U8();
                Vector3 pos = Codec.Pos(ref rd);
                uint rot = rd.U32(), gun = rd.U32(), cam = rd.U32();
                byte rotor = rd.U8(), lr = rd.U8(), lg = rd.U8(), lb = rd.U8(), li = rd.U8();
                if (mask > AllAlive || ai > (byte)AIState.DEAD) throw new ProtocolException("bad enemy state");

                if (!_byId.TryGetValue(id, out var e) || e.Robot == null)
                {
                    RememberMask(id, mask);
                    continue;
                }
                long t = e.Buffer.Unwrap.Next(tick);
                if (t < 0) continue;
                e.Buffer.Add(new PoseSample { Tick = t, Pos = pos, R0 = Codec.Quat(rot), R1 = Codec.Quat(gun), R2 = Codec.Quat(cam) });
                e.HasState = true;
                e.Mask = mask;
                e.Ai = ai;
                e.Rotor = Codec.UnitByte(rotor, RotorMax);
                e.LightColor = new Color(lr / 255f, lg / 255f, lb / 255f, 1f);
                e.LightIntensity = Codec.UnitByte(li, LightMax);
            }
            rd.End();
        }

        public static void ClientReceiveDamage(ref NetReader rd)
        {
            uint id = rd.U32();
            ushort part = rd.U16();
            byte mask = rd.U8();
            rd.End();
            if (mask > AllAlive) throw new ProtocolException("bad enemy mask");
            if (!_byId.TryGetValue(id, out var e) || e.Robot == null)
            {
                RememberMask(id, mask);
                return;
            }
            var r = e.Robot;
            if (e.Parts != null && part < e.Parts.Length && e.Parts[part] != null)
            {
                _applying = true;
                try { r.Damage(e.Parts[part].gameObject); }
                catch (Exception ex) { Plugin.Log.LogWarning($"Enemy damage replay failed: {ex.Message}"); }
                finally { _applying = false; }
            }
            KillSilently(r, mask);
            e.Mask = mask;
        }

        public static void ClientReceiveShot(ref NetReader rd)
        {
            uint id = rd.U32();
            Vector3 pos = Codec.Pos(ref rd);
            Vector3 vel = Codec.Velocity(ref rd);
            rd.End();
            if (LocalPlayer.Get() == null) return;

            GameObject bulletPrefab = null;
            if (_byId.TryGetValue(id, out var e) && e.Robot != null)
            {
                var r = e.Robot;
                bulletPrefab = r.bullet_obj;
                if (r.muzzle_flash != null && r.point_muzzle_flash != null)
                    Object.Instantiate(r.muzzle_flash, r.point_muzzle_flash.position, r.point_muzzle_flash.rotation);
                try { r.PlaySoundFromGroup(r.sound_gunshot, 1f); }
                catch (Exception) { /* audio source not ready */ }
            }
            else
            {
                var level = Object.FindObjectOfType<LevelCreatorScript>();
                var turret = level != null && level.turret != null ? level.turret.GetComponent<RobotScript>() : null;
                bulletPrefab = turret != null ? turret.bullet_obj : null;
            }
            ShotSync.SpawnEnemyBullet(bulletPrefab, pos, vel);
        }

        public static void ClientReceiveShock(ref NetReader rd)
        {
            rd.End();
            var me = LocalPlayer.Get();
            if (me != null) me.Shock();
        }

        // ================= client: puppets =================

        public static void ClientFrame(float dt)
        {
            _time += dt;
            foreach (var e in _byId.Values)
            {
                var r = e.Robot;
                if (!e.Puppet || r == null || !e.HasState || !r.gameObject.activeInHierarchy) continue;

                if (e.Buffer.Sample(dt, out var pose))
                {
                    r.transform.SetPositionAndRotation(pose.Pos, pose.R0);
                    if (r.gun_pivot != null) r.gun_pivot.localRotation = pose.R1;
                    if (r.camera_pivot != null) r.camera_pivot.localRotation = pose.R2;
                }

                if (Flies(r)) SpinRotors(e, r, dt);

                bool awake = UpdateMotorAudio(e, r, dt);
                var light = LightObject(r);
                if (light != null)
                {
                    light.color = e.LightColor;
                    light.intensity = e.LightIntensity;
                    light.shadows = awake && e.LightIntensity > 0f ? LightShadows.Hard : LightShadows.None;
                    var flare = Flare(r);
                    if (flare != null)
                    {
                        flare.color = light.color;
                        flare.brightness = light.intensity;
                    }
                }

                if (e.Ai != e.LastAi) OnAiChanged(r, (AIState)e.LastAi, (AIState)e.Ai);
                e.LastAi = e.Ai;
                UpdateShockProd(e, r);
                Reconcile(e, r);
            }
        }

        /// <summary>
        /// The motor hum, as the game's AI update does it (which puppets skip): sleeps beyond the robot's sleep
        /// distance from the camera; drones scale volume with rotor speed and distance and are muffled when out
        /// of sight (with the game's own integer-clamp quirk); turrets pitch with how fast they swing.
        /// </summary>
        static bool UpdateMotorAudio(Entry e, RobotScript r, float dt)
        {
            var cam = Camera.main;
            var motor = MotorSource(r);
            if (cam == null || motor == null || dt <= 0f) return true;
            float sv = Preferences.sound_volume;
            var camPos = cam.transform.position;
            float dist = Vector3.Distance(camPos, r.transform.position);
            if (dist > SleepDistance(r))
            {
                if (motor.isPlaying) motor.Stop();
                return false;
            }
            if (!motor.isPlaying)
            {
                motor.volume = sv;
                motor.Play();
            }
            float follow = Mathf.Pow(0.0001f, dt);
            if (Flies(r))
            {
                motor.pitch = Mathf.Lerp(motor.pitch, Mathf.Clamp(e.Rotor * 0.2f, 0.2f, 3f), follow);
                float volume = e.Rotor * 0.1f * sv - dist * 0.0125f * sv;
                bool seen = !Physics.Linecast(r.transform.position, camPos, 1);
                float los = LineOfSight(r) + (seen ? dt * 3f : -dt * 3f);
                los = Mathf.Clamp((int)los, 0, 1);
                LineOfSight(r) = los;
                motor.volume = volume * (0.5f + los * 0.5f);
                var lowPass = MotorObject(r) != null ? MotorObject(r).GetComponent<AudioLowPassFilter>() : null;
                if (lowPass != null) lowPass.cutoffFrequency = Mathf.Lerp(5000f, 44000f, los);
            }
            else
            {
                motor.volume = 0.4f * sv;
                var gun = r.gun_pivot != null ? r.gun_pivot.localRotation : Quaternion.identity;
                float swing = Quaternion.Angle(e.LastGunRot, gun) / dt;
                e.LastGunRot = gun;
                motor.pitch = Mathf.Lerp(motor.pitch, Mathf.Clamp(swing * 0.01f, 0.2f, 2f), follow);
            }
            return true;
        }

        static void SpinRotors(Entry e, RobotScript r, float dt)
        {
            e.TopRotor += e.Rotor * dt * 1000f;
            e.BottomRotor -= e.Rotor * dt * 1000f;
            TopRotorRotation(r) = e.TopRotor;
            BottomRotorRotation(r) = e.BottomRotor;
            bool blur = e.Rotor > 7f;
            foreach (var (rotor, angle) in new[] { (r.top_rotor, e.TopRotor), (r.bottom_rotor, e.BottomRotor) })
            {
                if (rotor == null) continue;
                var euler = rotor.localEulerAngles;
                euler.y = angle;
                rotor.localEulerAngles = euler;
                var rend = rotor.GetComponent<Renderer>();
                if (rend != null) rend.enabled = !blur;
            }
        }

        static void OnAiChanged(RobotScript r, AIState from, AIState to)
        {
            var src = EffectSource(r);
            if (src == null) return;
            bool wasCalm = from == AIState.IDLE || from == AIState.ALERT_COOLDOWN;
            bool isAlert = to == AIState.ALERT || to == AIState.AIMING || to == AIState.FIRING;
            if (wasCalm && isAlert && r.sound_alert != null) src.PlayOneShot(r.sound_alert, Preferences.sound_volume);
            else if (to == AIState.IDLE && from != AIState.IDLE && r.sound_unalert != null) src.PlayOneShot(r.sound_unalert, Preferences.sound_volume);
        }

        /// <summary>Drone shock prod: sparks and the taser hum while it's attacking.</summary>
        /// <summary>Shock drones and Green Demons (same body: rotors, shock prod).</summary>
        static bool Flies(RobotScript r) => r.robot_type == RobotType.SHOCK_DRONE || r.robot_type == RobotType.GREEN_DEMON;

        static void UpdateShockProd(Entry e, RobotScript r)
        {
            if (!Flies(r)) return;
            var taser = TaserSource(r);
            bool firing = e.Ai == (byte)AIState.FIRING && (e.Mask & 16) != 0;
            if (taser != null)
            {
                if (firing && !taser.isPlaying) { taser.volume = Preferences.sound_volume; taser.Play(); }
                else if (!firing && taser.isPlaying) taser.Stop();
            }
            if (firing && _time >= e.NextSpark && r.muzzle_flash != null && r.point_spark != null)
            {
                e.NextSpark = _time + 0.1f;
                Object.Instantiate(r.muzzle_flash, r.point_spark.position, r.RandomOrientation());
            }
        }

        /// <summary>If a missed damage event leaves a part alive here but dead on the host, catch up quietly.</summary>
        static void Reconcile(Entry e, RobotScript r)
        {
            byte local = ReadMask(r);
            if ((local & ~e.Mask & AllAlive) == 0) { e.MismatchSince = -1f; return; }
            if (e.MismatchSince < 0f) { e.MismatchSince = _time; return; }
            if (_time - e.MismatchSince < ReconcileSeconds) return;
            KillSilently(r, e.Mask);
            e.MismatchSince = -1f;
        }

        // ================= client: report hits =================

        static void SendHit(Entry e, byte kind, GameObject part, Vector3 pos, Vector3 vel, float damage)
        {
            var s = NetSession.Current;
            if (s == null || e.Parts == null || part == null) return;
            int index = Array.IndexOf(e.Parts, part.transform);
            if (index < 0) return;
            SyncStats.Count("enemy hits sent");
            var w = WorldSync.Begin(_ev, Msg.RobotHit);
            w.U32(e.Id);
            w.U16((ushort)index);
            w.U8(kind);
            Codec.Pos(w, pos);
            Codec.Vel(w, vel);
            w.F32(float.IsNaN(damage) || float.IsInfinity(damage) ? 0f : Mathf.Clamp(damage, 0f, Protocol.MaxDamageMultiplier));
            s.SendToHost(w, Channel.Reliable);
        }

        // ================= host: apply hits, send events =================

        public static void HostReceiveHit(ushort from, ref NetReader rd)
        {
            uint id = rd.U32();
            ushort part = rd.U16();
            byte kind = rd.U8();
            Vector3 pos = Codec.Pos(ref rd);
            Vector3 vel = Codec.Velocity(ref rd);
            float damage = Mathf.Clamp(Codec.Number(ref rd, 0f), 0f, Protocol.MaxDamageMultiplier); // odd values are clamped, not a kick
            rd.End();
            if (kind > 1) throw new ProtocolException("bad hit");

            if (!_byId.TryGetValue(id, out var e) || e.Robot == null || e.Parts == null || part >= e.Parts.Length)
            {
                SyncStats.Note("enemy hit: unknown enemy", $"a hit on enemy {id:X6} arrived, but that enemy isn't loaded on the host");
                return;
            }
            var r = e.Robot;
            var partTransform = e.Parts[part];
            if (partTransform == null || !r.gameObject.activeInHierarchy) { SyncStats.Note("enemy hit: inactive", "a hit arrived for an enemy that's asleep on the host"); return; }
            if ((partTransform.position - pos).sqrMagnitude > Protocol.MaxHitOffset * Protocol.MaxHitOffset)
            {
                SyncStats.Note("enemy hit: too far", $"a hit was {(partTransform.position - pos).magnitude:0.0} m from the part on the host and was ignored");
                return;
            }
            SyncStats.Count("enemy hits applied");

            _applying = true;
            try
            {
                if (kind == 0)
                {
                    r.WasShot(partTransform.gameObject, pos, vel, damage);
                    var rb = r.GetComponent<Rigidbody>();
                    if (rb != null && !rb.isKinematic) rb.AddForceAtPosition(vel * 0.01f, pos, ForceMode.Impulse);
                }
                else
                {
                    r.WasShotInternal(partTransform.gameObject);
                }
            }
            finally
            {
                _applying = false;
            }
        }

        static void HostBroadcastDamage(Entry e, GameObject part, byte mask)
        {
            var s = NetSession.Current;
            if (s == null) return;
            int index = e.Parts != null && part != null ? Array.IndexOf(e.Parts, part.transform) : -1;
            var w = WorldSync.Begin(_ev, Msg.RobotDamage);
            w.U32(e.Id);
            w.U16(index < 0 ? ushort.MaxValue : (ushort)index);
            w.U8(mask);
            s.SendToClients(w, Channel.Reliable);
        }

        static void HostBroadcastShot(Entry e, Vector3 pos, Vector3 vel)
        {
            var s = NetSession.Current;
            if (s == null) return;
            var w = WorldSync.Begin(_ev, Msg.RobotShot);
            w.U32(e.Id);
            Codec.Pos(w, pos);
            Codec.Vel(w, vel);
            s.SendToClients(w, Channel.Reliable);
        }

        static void HostSendShock(ushort playerId)
        {
            var s = NetSession.Current;
            if (s == null) return;
            if (_lastShock.TryGetValue(playerId, out float at) && _time - at < 0.25f) return;
            _lastShock[playerId] = _time;
            var w = WorldSync.Begin(_ev, Msg.Shocked);
            s.SendToPlayer(playerId, w, Channel.Reliable);
        }

        // ================= host: targeting =================

        static int _targetsFrame = -1;
        static readonly List<Transform> _targets = new List<Transform>();

        /// <summary>Host enemies hunt the nearest living player, local or remote.</summary>
        static Transform NearestTarget(Vector3 from)
        {
            if (_targetsFrame != Time.frameCount)
            {
                _targetsFrame = Time.frameCount;
                _targets.Clear();
                var me = LocalPlayer.Get();
                if (LocalPlayer.IsAlive(me)) _targets.Add(me.transform);
                foreach (var b in PlayerSync.LiveBodies()) _targets.Add(b.transform);
                if (_targets.Count == 0 && me != null) _targets.Add(me.transform); // everyone down: behave like single-player
            }
            Transform best = null;
            float bestDist = float.MaxValue;
            foreach (var t in _targets)
            {
                if (t == null) continue;
                float d = (t.position - from).sqrMagnitude;
                if (d < bestDist) { bestDist = d; best = t; }
            }
            return best;
        }

        // ---- shims the enemy AI calls instead of reaching into the player directly ----

        public static void ShimShock(Component target)
        {
            if (target == null) return;
            var aim = target.GetComponent<AimScript>();
            if (aim != null) { aim.Shock(); return; }
            var body = target.GetComponent<RemotePlayerBody>();
            if (body != null) HostSendShock(body.PlayerId);
        }

        public static Vector3 ShimVelocity(Component target)
        {
            if (target == null) return Vector3.zero;
            var aim = target.GetComponent<AimScript>();
            if (aim != null) return aim.GetVelocity();
            var body = target.GetComponent<RemotePlayerBody>();
            return body != null ? PlayerSync.VelocityOf(body.PlayerId) : Vector3.zero;
        }

        public static void ShimDanger(MusicScript music, float level)
        {
            if (music != null) music.AddDangerLevel(level);
        }

        // ================= patches =================

        [HarmonyPatch(typeof(RobotScript), nameof(RobotScript.Awake))]
        static class AwakePatch
        {
            static void Postfix(RobotScript __instance)
            {
                if (WorldSync.InRun) Register(__instance);
            }
        }

        [HarmonyPatch(typeof(RobotScript), nameof(RobotScript.Start))]
        static class StartPatch
        {
            static void Postfix(RobotScript __instance)
            {
                if (WorldSync.InRun) OnStarted(__instance);
            }
        }

        [HarmonyPatch(typeof(RobotScript), nameof(RobotScript.Update))]
        static class UpdatePatch
        {
            static void Prefix(RobotScript __instance, out RobotType __state)
            {
                __state = __instance.robot_type;
                if (!WorldSync.InRun) return;
                if (IsPuppet(__instance)) __instance.robot_type = PuppetType; // keep only the bullet-hole follow code
                else _updating = __instance;
            }

            static Exception Finalizer(Exception __exception, RobotScript __instance, RobotType __state)
            {
                __instance.robot_type = __state;
                _updating = null;
                return __exception;
            }
        }

        [HarmonyPatch(typeof(RobotScript), nameof(RobotScript.FixedUpdate))]
        static class FixedUpdatePatch
        {
            static bool Prefix(RobotScript __instance) => !WorldSync.InRun || !IsPuppet(__instance);
        }

        [HarmonyPatch(typeof(RobotScript), nameof(RobotScript.OnCollisionEnter))]
        static class CollisionPatch
        {
            static bool Prefix(RobotScript __instance) => !WorldSync.InRun || !IsPuppet(__instance);
        }

        [HarmonyPatch(typeof(RobotScript), nameof(RobotScript.GetTarget))]
        static class GetTargetPatch
        {
            static bool Prefix(RobotScript __instance, ref Transform __result)
            {
                if (!WorldSync.IsHost) return true;
                __result = DemonSync.TryGetQuarry(__instance, out var quarry) ? quarry : NearestTarget(__instance.transform.position);
                return false;
            }
        }

        [HarmonyPatch(typeof(RobotScript), nameof(RobotScript.WasShot))]
        static class WasShotPatch
        {
            static bool Prefix(RobotScript __instance, GameObject obj, Vector3 pos, Vector3 vel, float damage)
            {
                if (!WorldSync.InRun || _applying) return true;
                bool replay = ShotSync.HitIsReplay;
                if (WorldSync.IsHost || !_byRobot.TryGetValue(__instance, out var e))
                    return !replay;
                if (!replay && ShotSync.InShotHit) SendHit(e, 0, obj, pos, vel, damage);
                return false;
            }
        }

        [HarmonyPatch(typeof(RobotScript), nameof(RobotScript.WasShotInternal))]
        static class WasShotInternalPatch
        {
            static bool Prefix(RobotScript __instance, GameObject obj)
            {
                if (!WorldSync.InRun || _applying) return true;
                bool replay = ShotSync.HitIsReplay;
                if (WorldSync.IsHost || !_byRobot.TryGetValue(__instance, out var e))
                    return !replay;
                if (!replay && ShotSync.InShotHit && obj != null)
                    SendHit(e, 1, obj, obj.transform.position, Vector3.zero, 1f);
                return false;
            }
        }

        [HarmonyPatch(typeof(RobotScript), nameof(RobotScript.Damage))]
        static class DamagePatch
        {
            /// <summary>
            /// RobotScript.WasShotInternal (a bullet passing through into an internal part) is 9 bytes, so the
            /// runtime inlines it into BulletScript.Update and it can't be hooked: its Damage call is handled here.
            /// A replayed bullet never damages; on a client a local bullet's internal hit is reported to the host.
            /// </summary>
            static bool Prefix(RobotScript __instance, GameObject obj, out byte __state)
            {
                __state = 0;
                if (!WorldSync.InRun || !_byRobot.TryGetValue(__instance, out var e)) return true;
                if (!_applying && ShotSync.HitIsReplay) return false;
                if (e.Puppet && !_applying)
                {
                    if (ShotSync.InShotHit && obj != null) SendHit(e, 1, obj, obj.transform.position, Vector3.zero, 1f);
                    return false; // puppets only take damage the host reports
                }
                __state = ReadMask(__instance);
                return true;
            }

            static void Postfix(RobotScript __instance, GameObject obj, byte __state)
            {
                if (!WorldSync.IsHost || !_byRobot.TryGetValue(__instance, out var e)) return;
                byte after = ReadMask(__instance);
                if (after == __state) return;
                _knownMask[e.Id] = after;
                HostBroadcastDamage(e, obj, after);
            }
        }

        /// <summary>Host: a turret just fired (SetHostile follows SetVelocity on its bullet).</summary>
        [HarmonyPatch(typeof(BulletScript), nameof(BulletScript.SetHostile))]
        static class EnemyShotPatch
        {
            static void Postfix(BulletScript __instance)
            {
                if (!WorldSync.IsHost || _updating == null || ShotSync.IsReplayed(__instance)) return;
                if (!_byRobot.TryGetValue(_updating, out var e)) return;
                HostBroadcastShot(e, __instance.transform.position, BulletVelocity(__instance));
            }
        }

        /// <summary>
        /// The AI reaches into its target with GetComponent&lt;AimScript&gt;() (shock, aim lead) and
        /// GetComponent&lt;MusicScript&gt;() (danger music). A remote player's pill has neither, so those calls
        /// are routed through shims that understand both kinds of target.
        /// </summary>
        [HarmonyPatch]
        static class TargetShimPatch
        {
            static IEnumerable<MethodBase> TargetMethods()
            {
                yield return AccessTools.Method(typeof(RobotScript), nameof(RobotScript.UpdateDrone));
                yield return AccessTools.Method(typeof(RobotScript), nameof(RobotScript.UpdateStationaryTurret));
            }

            static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
            {
                var shock = AccessTools.Method(typeof(AimScript), nameof(AimScript.Shock));
                var velocity = AccessTools.Method(typeof(AimScript), nameof(AimScript.GetVelocity));
                var danger = AccessTools.Method(typeof(MusicScript), nameof(MusicScript.AddDangerLevel));
                var list = instructions.ToList();
                int shocks = 0, velocities = 0, dangers = 0;
                for (int i = 0; i < list.Count; i++)
                {
                    var ins = list[i];
                    if (IsGetAimScript(ins) && i + 1 < list.Count)
                    {
                        var next = list[i + 1];
                        if (next.Calls(shock))
                        {
                            ins.opcode = OpCodes.Call;
                            ins.operand = AccessTools.Method(typeof(RobotSync), nameof(ShimShock));
                            next.opcode = OpCodes.Nop;
                            next.operand = null;
                            shocks++;
                            continue;
                        }
                        if (next.Calls(velocity))
                        {
                            ins.opcode = OpCodes.Call;
                            ins.operand = AccessTools.Method(typeof(RobotSync), nameof(ShimVelocity));
                            next.opcode = OpCodes.Nop;
                            next.operand = null;
                            velocities++;
                            continue;
                        }
                    }
                    if (ins.Calls(danger))
                    {
                        ins.opcode = OpCodes.Call;
                        ins.operand = AccessTools.Method(typeof(RobotSync), nameof(ShimDanger));
                        dangers++;
                    }
                }
                Plugin.Log.LogInfo($"Enemy AI shims in {__originalMethod.Name}: shock {shocks}, velocity {velocities}, danger {dangers}.");
                return list;
            }

            static bool IsGetAimScript(CodeInstruction ins) =>
                (ins.opcode == OpCodes.Callvirt || ins.opcode == OpCodes.Call) && ins.operand is MethodInfo m &&
                m.Name == "GetComponent" && m.IsGenericMethod && m.GetGenericArguments()[0] == typeof(AimScript);
        }
    }
}
