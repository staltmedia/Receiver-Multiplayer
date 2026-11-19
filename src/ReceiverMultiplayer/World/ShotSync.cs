using System;
using System.Collections.Generic;
using HarmonyLib;
using ReceiverMultiplayer.Net;
using ReceiverMultiplayer.Session;
using UnityEngine;
using Object = UnityEngine.Object;
using Random = UnityEngine.Random;

namespace ReceiverMultiplayer.World
{
    /// <summary>
    /// Marks a bullet this machine is only replaying (another player's shot, or a turret's shot sent by the host).
    /// Replayed bullets fly, ricochet, leave holes and sparks like real ones, but never damage enemies here:
    /// enemy damage comes only from the shooter's own report to the host. A replayed player bullet hurts the
    /// local player only with friendly fire on; a replayed turret bullet always can.
    /// </summary>
    sealed class RemoteBullet : MonoBehaviour
    {
        public bool FromEnemy;
    }

    /// <summary>Shots: each shooter replicates their own discharges; everyone else replays them.</summary>
    static class ShotSync
    {
        /// <summary>The bullet whose Update is running (so hit handlers know which bullet hit).</summary>
        public static BulletScript CurrentBullet;

        static FiringComponent _capturing;
        static Vector3 _flashPos;
        static Quaternion _flashRot;
        static readonly List<(Vector3 pos, Vector3 vel)> _pellets = new List<(Vector3, Vector3)>();
        static readonly NetWriter _w = new NetWriter(1024);
        static readonly List<(Vector3 pos, Vector3 vel)> _incoming = new List<(Vector3, Vector3)>();

        public static bool IsReplayed(BulletScript b) => b != null && b.GetComponent<RemoteBullet>() != null;

        /// <summary>A shot's hit is being handled right now: a bullet's step, or a projectile's collision.</summary>
        public static bool InShotHit => CurrentBullet != null || ProjectileSync.Current != null;

        /// <summary>...and it's a replay (another player's shot, or a turret's), which never damages enemies here.</summary>
        public static bool HitIsReplay => IsReplayed(CurrentBullet) || ProjectileSync.IsReplayed(ProjectileSync.Current);

        // ================= capture (the local gun) =================

        static readonly AccessTools.FieldRef<BulletScript, Vector3> BulletVelocity = AccessTools.FieldRefAccess<BulletScript, Vector3>("velocity");
        static readonly AccessTools.FieldRef<BulletScript, float> BulletLife = AccessTools.FieldRefAccess<BulletScript, float>("life_time");
        static readonly AccessTools.FieldRef<BulletScript, bool> BulletHit = AccessTools.FieldRefAccess<BulletScript, bool>("hit_something");
        static readonly AccessTools.FieldRef<BulletScript, bool> BulletHostile = AccessTools.FieldRefAccess<BulletScript, bool>("hostile");
        static readonly HashSet<BulletScript> _before = new HashSet<BulletScript>();
        static readonly HashSet<BulletScript> _localShots = new HashSet<BulletScript>();
        static readonly HashSet<ProjectileScript> _beforeProjectiles = new HashSet<ProjectileScript>();
        static readonly List<ProjectileScript> _projectiles = new List<ProjectileScript>();

        /// <summary>
        /// The game sets a bullet's velocity through BulletScript.SetVelocity, a 9-byte method the runtime inlines
        /// into the firing code, so hooking it never fires. Instead: note the bullets that exist before the gun
        /// discharges, and afterwards the new ones are this shot's pellets (position and velocity read directly).
        /// </summary>
        static void BeginCapture(FiringComponent fc)
        {
            _capturing = fc;
            _pellets.Clear();
            var flash = fc.point_muzzleflash != null ? fc.point_muzzleflash : fc.point_muzzle;
            _flashPos = flash != null ? flash.position : Vector3.zero;
            _flashRot = flash != null ? flash.rotation : Quaternion.identity;
            _before.Clear();
            foreach (var b in Object.FindObjectsOfType<BulletScript>()) _before.Add(b);
            _beforeProjectiles.Clear();
            _projectiles.Clear();
            foreach (var p in Object.FindObjectsOfType<ProjectileScript>()) _beforeProjectiles.Add(p);
        }

        static void CollectPellets()
        {
            _localShots.RemoveWhere(b => b == null);
            foreach (var b in Object.FindObjectsOfType<BulletScript>())
            {
                if (_before.Contains(b) || b.GetComponent<RemoteBullet>() != null) continue;
                _localShots.Add(b);
                if (_pellets.Count < Protocol.MaxPellets) _pellets.Add((b.transform.position, BulletVelocity(b)));
            }
            _before.Clear();
            // Projectile guns (the game's ProjectileScript) spawn a physics object instead of a bullet.
            foreach (var p in Object.FindObjectsOfType<ProjectileScript>())
                if (!_beforeProjectiles.Contains(p) && !ProjectileSync.IsReplayed(p) && p.GetComponent<ProjectileTag>() == null)
                    _projectiles.Add(p);
            _beforeProjectiles.Clear();
        }

        static void EndCapture()
        {
            var fc = _capturing;
            _capturing = null;
            if (fc != null) CollectPellets();
            if (fc == null || (_pellets.Count == 0 && _projectiles.Count == 0))
            {
                if (fc != null) SyncStats.Note("shot: no pellets", "a shot fired but neither a bullet nor a projectile was captured");
                return;
            }
            int gun = IdentifyGun(fc);
            var s = NetSession.Current;
            if (gun < 0) SyncStats.Note("shot: unknown gun", $"couldn't identify the gun that fired ({GunCache.Count} guns known)");
            if (gun < 0 || s == null) return;
            if (_projectiles.Count > 0)
            {
                ProjectileSync.OnLocalShot((ushort)gun, _flashPos, _flashRot, _projectiles);
                _projectiles.Clear();
                if (_pellets.Count == 0) return;
            }
            SyncStats.Count("shots sent");

            if (s.IsHost)
            {
                Broadcast(s, s.LocalId, (ushort)gun, _flashPos, _flashRot, _pellets, -1);
                return;
            }
            var w = WorldSync.Begin(_w, Msg.Shot);
            WritePayload(w, (ushort)gun, _flashPos, _flashRot, _pellets);
            s.SendToHost(w, Channel.Reliable);
        }

        /// <summary>
        /// Which entry of the gun list fired: by the gun object's own name first (it's an instance of
        /// WeaponHolder.gun_object), then by its firing data, then the local player's starting gun (only the
        /// local player's gun ever fires here).
        /// </summary>
        static int IdentifyGun(FiringComponent fc)
        {
            int own = GearSync.OwnWeapon();
            int reg = PrefabRegistry.IndexOf(fc.gameObject, own);
            if (reg >= 0 && PrefabRegistry.TryGet(reg, out var entry) && entry.Kind == PrefabKind.Gun && entry.Weapon >= 0)
                return entry.Weapon;
            int byData = GunCache.IndexOf(fc);
            if (byData >= 0) return byData;
            SyncStats.Note("shot: gun by fallback", "identified the firing gun as your starting gun (name and data didn't match)");
            return own;
        }

        static void WritePayload(NetWriter w, ushort gun, Vector3 flashPos, Quaternion flashRot, List<(Vector3 pos, Vector3 vel)> pellets)
        {
            w.U16(gun);
            Codec.Pos(w, flashPos);
            w.U32(Codec.Quat(flashRot));
            int n = Math.Min(pellets.Count, Protocol.MaxPellets);
            w.U8((byte)n);
            for (int i = 0; i < n; i++)
            {
                Codec.Pos(w, pellets[i].pos);
                Codec.Vel(w, pellets[i].vel);
            }
        }

        static void ReadPayload(ref NetReader rd, out ushort gun, out Vector3 flashPos, out Quaternion flashRot)
        {
            gun = rd.U16();
            flashPos = Codec.Pos(ref rd);
            flashRot = Codec.Quat(rd.U32());
            int n = rd.U8();
            if (n == 0 || n > Protocol.MaxPellets) throw new ProtocolException("bad pellet count");
            _incoming.Clear();
            for (int i = 0; i < n; i++)
                _incoming.Add((Codec.Pos(ref rd), Codec.Velocity(ref rd)));
            rd.End();
        }

        // ================= host =================

        public static void HostReceive(NetSession s, ushort from, ref NetReader rd)
        {
            ReadPayload(ref rd, out ushort gun, out var flashPos, out var flashRot);
            SyncStats.Count("shots received");
            if (gun >= GunCache.Count) { SyncStats.Note("shot: gun index", $"a shot named gun {gun}, but only {GunCache.Count} guns are known here"); return; }
            // Shots must start at the shooter's body; anything else is dropped (lag-tolerant, not a disconnect).
            if (!PlayerSync.TryGetPosition(from, out var body) || (flashPos - body).sqrMagnitude > Protocol.MaxShotOffset * Protocol.MaxShotOffset)
            {
                SyncStats.Note("shot: far from shooter", $"a shot started {(flashPos - body).magnitude:0.0} m from the shooter and was ignored");
                return;
            }
            foreach (var p in _incoming)
                if ((p.pos - flashPos).sqrMagnitude > 4f) { SyncStats.Note("shot: pellet far", "a shot's bullet started far from its muzzle and was ignored"); return; }

            Render(gun, flashPos, flashRot, _incoming);
            Broadcast(s, from, gun, flashPos, flashRot, _incoming, from);
        }

        static void Broadcast(NetSession s, ushort shooter, ushort gun, Vector3 flashPos, Quaternion flashRot,
            List<(Vector3 pos, Vector3 vel)> pellets, int except)
        {
            var w = WorldSync.Begin(_w, Msg.Shot);
            w.U16(shooter);
            WritePayload(w, gun, flashPos, flashRot, pellets);
            s.SendToClients(w, Channel.Reliable, except);
        }

        // ================= client =================

        public static void ClientReceive(ref NetReader rd)
        {
            ushort shooter = rd.U16();
            ReadPayload(ref rd, out ushort gun, out var flashPos, out var flashRot);
            if (shooter == NetSession.Current?.LocalId) return;
            SyncStats.Count("shots received");
            Render(gun, flashPos, flashRot, _incoming);
        }

        // ================= replay =================

        static void Render(ushort gun, Vector3 flashPos, Quaternion flashRot, List<(Vector3 pos, Vector3 vel)> pellets)
        {
            if (LocalPlayer.Get() == null) { SyncStats.Note("shot: no level", "a shot arrived before this player's level was ready"); return; }
            var fc = GunCache.Get(gun);
            if (fc == null) { SyncStats.Note("shot: no gun data", $"no firing data for gun {gun} here ({GunCache.Count} guns known)"); return; }
            SyncStats.Count("shots drawn");

            if (fc.bullet_obj != null && fc.bullet_obj.GetComponent<BulletScript>() == null)
            {
                SyncStats.Note("shot: not a bullet", "a shot named a gun whose bullet has no BulletScript (projectiles travel as their own message)");
                return;
            }
            PlayDischarge(fc, flashPos, flashRot);
            if (fc.bullet_obj != null)
            {
                foreach (var (pos, vel) in pellets)
                {
                    var go = Object.Instantiate(fc.bullet_obj, pos, vel.sqrMagnitude > 0f ? Quaternion.LookRotation(vel) : flashRot);
                    var bullet = go.GetComponent<BulletScript>();
                    go.AddComponent<RemoteBullet>();
                    bullet.SetVelocity(vel);
                }
            }
        }

        /// <summary>A teammate's gun went off here: its muzzle flash and gunshot.</summary>
        public static void PlayDischarge(FiringComponent fc, Vector3 flashPos, Quaternion flashRot)
        {
            if (fc.muzzle_flash != null)
                Object.Instantiate(fc.muzzle_flash, flashPos, flashRot);
            var clips = fc.sound_gunshot_smallroom;
            if (clips != null && clips.Length > 0)
                OneShotAudio.Play(clips[Random.Range(0, clips.Length)], flashPos, 1f);
        }

        public static void SpawnEnemyBullet(GameObject prefab, Vector3 pos, Vector3 vel)
        {
            if (prefab == null) return;
            var go = Object.Instantiate(prefab, pos, vel.sqrMagnitude > 0f ? Quaternion.LookRotation(vel) : Quaternion.identity);
            var bullet = go.GetComponent<BulletScript>();
            if (bullet == null) { Object.Destroy(go); return; }
            go.AddComponent<RemoteBullet>().FromEnemy = true;
            bullet.SetVelocity(vel);
            bullet.SetHostile();
        }

        // ================= patches =================

        [HarmonyPatch]
        static class DischargePatch
        {
            static readonly AccessTools.FieldRef<GunSystemsV1.FiringSystem, FiringComponent> Fc =
                AccessTools.FieldRefAccess<GunSystemsV1.FiringSystem, FiringComponent>("fc");

            static System.Reflection.MethodBase TargetMethod() =>
                AccessTools.Method(typeof(GunSystemsV1.FiringSystem), nameof(GunSystemsV1.FiringSystem.Discharge));

            static void Prefix(GunSystemsV1.FiringSystem __instance)
            {
                if (!WorldSync.InRun) return;
                var fc = Fc(__instance);
                if (fc != null) BeginCapture(fc);
            }

            static Exception Finalizer(Exception __exception)
            {
                if (_capturing == null) return __exception;
                try { EndCapture(); }
                catch (Exception e) { Plugin.Log.LogError($"Shot sync failed: {e}"); }
                return __exception;
            }
        }

        [HarmonyPatch(typeof(BulletScript), nameof(BulletScript.Update))]
        static class BulletUpdatePatch
        {
            static readonly AccessTools.FieldRef<BulletScript, LineRenderer> Line = AccessTools.FieldRefAccess<BulletScript, LineRenderer>("line_renderer");
            const float TrailBoost = 4f;

            static void Prefix(BulletScript __instance, out bool __state)
            {
                CurrentBullet = __instance;
                __state = BulletHit(__instance);
            }

            /// <summary>
            /// After a replayed bullet's step: if it just hit something it may have spawned a ricochet bullet, which
            /// must be a replay too (or it would damage enemies a second time). The game draws a trail at 5%
            /// opacity, plain to the shooter looking down it but faint from the side, so replayed player trails are
            /// drawn brighter.
            /// </summary>
            static Exception Finalizer(Exception __exception, BulletScript __instance, bool __state)
            {
                CurrentBullet = null;
                if (__exception != null || __instance == null) return __exception;
                var replay = __instance.GetComponent<RemoteBullet>();
                if (replay == null) return null;
                if (!__state && BulletHit(__instance)) TagRicochets(__instance, replay.FromEnemy);
                var line = !replay.FromEnemy ? Line(__instance) : null;
                if (line != null)
                {
                    var a = line.startColor; a.a = Mathf.Clamp01(a.a * TrailBoost); line.startColor = a;
                    var b = line.endColor; b.a = Mathf.Clamp01(b.a * TrailBoost); line.endColor = b;
                }
                return null;
            }

            static void TagRicochets(BulletScript parent, bool fromEnemy)
            {
                foreach (var b in Object.FindObjectsOfType<BulletScript>())
                {
                    if (b == parent || BulletLife(b) > 0f || _localShots.Contains(b)) continue;
                    if (b.GetComponent<RemoteBullet>() != null || (BulletHostile(b) && !fromEnemy)) continue;
                    b.gameObject.AddComponent<RemoteBullet>().FromEnemy = fromEnemy;
                }
            }
        }

        /// <summary>
        /// Friendly fire: another player's replayed bullet still hits you (puff, sound and the bullet hole on your
        /// screen, as in the base game) but only hurts you when the host has friendly fire on.
        /// </summary>
        [HarmonyPatch(typeof(AimScript), nameof(AimScript.WasShot))]
        static class FriendlyFirePatch
        {
            static bool Prefix()
            {
                if (!WorldSync.InRun || !InShotHit) return true;
                if (CurrentBullet != null)
                {
                    var replay = CurrentBullet.GetComponent<RemoteBullet>();
                    if (replay == null || replay.FromEnemy) return true;
                }
                else if (!ProjectileSync.IsReplayed(ProjectileSync.Current)) return true; // our own projectile
                SyncStats.Count(WorldSync.FriendlyFire ? "hit by teammate (hurt)" : "hit by teammate (no damage)");
                return WorldSync.FriendlyFire;
            }
        }
    }

    /// <summary>The installed guns' firing data, indexed like the host's gun list.</summary>
    static class GunCache
    {
        static FiringComponent[] _guns;

        public static int Count => Ensure().Length;

        public static FiringComponent Get(int index)
        {
            var guns = Ensure();
            return index >= 0 && index < guns.Length ? guns[index] : null;
        }

        public static int IndexOf(FiringComponent fc)
        {
            var guns = Ensure();
            for (int i = 0; i < guns.Length; i++)
            {
                var g = guns[i];
                if (g != null && g.bullet_obj == fc.bullet_obj && g.muzzle_flash == fc.muzzle_flash && g.sound_gunshot_smallroom == fc.sound_gunshot_smallroom)
                    return i;
            }
            for (int i = 0; i < guns.Length; i++)
                if (guns[i] != null && guns[i].bullet_obj == fc.bullet_obj)
                    return i;
            return -1;
        }

        /// <summary>A new level: build again from its (complete) gun list.</summary>
        public static void Invalidate() => _guns = null;

        static FiringComponent[] Ensure()
        {
            if (_guns != null && _guns.Length > 0) return _guns;
            var holder = Object.FindObjectOfType<GUISkinHolder>();
            if (holder == null || holder.weapons == null) return _guns = Array.Empty<FiringComponent>();
            Session.Guns.LoadAll(holder); // another player's add-on gun must be known here to replay its shots
            _guns = new FiringComponent[holder.weapons.Length];
            for (int i = 0; i < holder.weapons.Length; i++)
            {
                var wh = holder.weapons[i] != null ? holder.weapons[i].GetComponent<WeaponHolder>() : null;
                if (wh != null && wh.gun_object != null)
                    _guns[i] = wh.gun_object.GetComponentInChildren<FiringComponent>(true);
            }
            return _guns;
        }
    }

    /// <summary>A positional one-shot sound that carries like a gunshot.</summary>
    static class OneShotAudio
    {
        public static void Play(AudioClip clip, Vector3 pos, float volume, float minDistance = 4f, float maxDistance = 200f)
        {
            if (clip == null) return;
            var go = new GameObject("MP_Sound");
            go.transform.position = pos;
            var src = go.AddComponent<AudioSource>();
            src.clip = clip;
            src.spatialBlend = 1f;
            src.rolloffMode = AudioRolloffMode.Logarithmic;
            src.minDistance = minDistance;
            src.maxDistance = maxDistance;
            src.dopplerLevel = 0f;
            src.volume = volume * Preferences.sound_volume;
            src.Play();
            Object.Destroy(go, clip.length + 0.2f);
        }
    }
}
