using HarmonyLib;
using ReceiverMultiplayer.Net;
using ReceiverMultiplayer.Session;
using UnityEngine;

namespace ReceiverMultiplayer.World
{
    /// <summary>
    /// In-run world sync: player bodies, shots, and host-authoritative enemies. Ticked every frame by the
    /// session; sends on a fixed 20 Hz network tick. Everything here is inert outside a shared run.
    /// </summary>
    static class WorldSync
    {
        static float _acc;
        static ushort _tick;

        /// <summary>This machine's 20 Hz send tick (stamps poses for interpolation).</summary>
        public static ushort Tick16 => _tick;

        public static bool InRun
        {
            get
            {
                var s = NetSession.Current;
                return s != null && s.InRun && RunStarter.Active != null;
            }
        }

        public static bool IsHost => InRun && NetSession.Current.IsHost;
        public static bool IsClient => InRun && !NetSession.Current.IsHost;
        public static ushort RunTag => (ushort)(NetSession.Current?.RunId ?? 0);
        public static bool FriendlyFire => NetSession.Current?.Settings.FriendlyFire ?? false;

        public static NetWriter Begin(NetWriter w, Msg type)
        {
            w.Reset();
            w.U8((byte)type);
            w.U16(RunTag);
            return w;
        }

        public static void Tick(NetSession s, float dt)
        {
            if (!InRun)
            {
                _acc = 0f;
                PlayerSync.Frame(dt, false);
                Spectator.Frame(dt);
                return;
            }
            _acc += dt;
            int steps = 0;
            while (_acc >= Protocol.WorldTick && steps < 3)
            {
                _acc -= Protocol.WorldTick;
                steps++;
                _tick++;
                Guard("pose", () => PlayerSync.NetTick(s));
                Guard("gear", () => GearSync.NetTick(s));
                if (s.IsHost)
                {
                    Guard("relay", () => PlayerSync.HostRelay(s));
                    Guard("enemies", () => RobotSync.HostNetTick(s));
                }
            }
            if (_acc > Protocol.WorldTick) _acc = 0f;

            Guard("players", () => PlayerSync.Frame(dt, true));
            if (!s.IsHost) Guard("enemy puppets", () => RobotSync.ClientFrame(dt));
            Guard("life", () => LifeSync.Frame(s, dt));
            if (s.IsHost) Guard("green demons", () => DemonSync.HostFrame(s, dt));
            else Guard("green demons", () => DemonSync.ClientFrame(dt));
            Guard("spectator", () => Spectator.Frame(dt));
            Guard("items", () => ItemSync.Frame(dt));
            Guard("projectiles", () => ProjectileSync.Frame(dt));
            Guard("clock", () => RunClock.Frame(s));
            Guard("level animations", AnimatorClock.Scan);
            Guard("carry", () => CarrySync.Frame(LocalPlayer.Get()));
            Guard("other guns' magazines", () => ForeignMags.Frame(LocalPlayer.Get()));
            RobotSync.Housekeeping();
            SyncStats.Frame(dt);
        }

        /// <summary>End of frame (after the game's camera update): camera-relative displays.</summary>
        public static void LateFrame()
        {
            if (!InRun) { CarrySync.HidePicker(); return; }
            Guard("spectator (late)", Spectator.LateFrame);
            Guard("gear", PlayerSync.LateFrame);
            Guard("ammo picker", () => CarrySync.LateFrame(LocalPlayer.Get()));
        }

        static readonly System.Collections.Generic.HashSet<string> _reported = new System.Collections.Generic.HashSet<string>();

        /// <summary>One failing subsystem must not stop the others; each error is logged once.</summary>
        static void Guard(string what, System.Action action)
        {
            try { action(); }
            catch (System.Exception e)
            {
                if (_reported.Add(what + e.GetType().Name + e.Message))
                    Plugin.Log.LogError($"World sync ({what}) failed: {e}");
            }
        }

        public static void HostReceive(NetSession s, ushort from, Msg type, ref NetReader rd)
        {
            if (rd.U16() != RunTag) return; // from a previous run: ignore
            switch (type)
            {
                case Msg.PlayerState: PlayerSync.HostReceive(s, from, ref rd); break;
                case Msg.Shot: ShotSync.HostReceive(s, from, ref rd); break;
                case Msg.RobotHit: RobotSync.HostReceiveHit(from, ref rd); break;
                case Msg.Died: LifeSync.HostReceiveDied(s, from, ref rd); break;
                case Msg.TapeTaken: LifeSync.HostReceiveTape(s, from, ref rd); break;
                case Msg.Gear: GearSync.HostReceive(s, from, ref rd); break;
                case Msg.ItemTaken: ItemSync.HostReceiveTaken(s, from, ref rd); break;
                case Msg.ItemSpawned: ItemSync.HostReceiveSpawned(s, from, ref rd); break;
                case Msg.Sound: SoundSync.HostReceive(s, from, ref rd); break;
                case Msg.Won: LifeSync.HostReceiveWon(s, from, ref rd); break;
                case Msg.Cheat: CheatSync.HostReceive(s, from, ref rd); break;
                case Msg.Projectile: ProjectileSync.HostReceiveShot(s, from, ref rd); break;
                case Msg.ProjectileRest: ProjectileSync.HostReceiveRest(s, from, ref rd); break;
            }
        }

        public static void ClientReceive(NetSession s, Msg type, ref NetReader rd)
        {
            if (rd.U16() != RunTag) return;
            switch (type)
            {
                case Msg.PlayerStates: PlayerSync.ClientReceive(s, ref rd); break;
                case Msg.Shot: ShotSync.ClientReceive(ref rd); break;
                case Msg.RobotStates: RobotSync.ClientReceiveStates(ref rd); break;
                case Msg.RobotDamage: RobotSync.ClientReceiveDamage(ref rd); break;
                case Msg.RobotShot: RobotSync.ClientReceiveShot(ref rd); break;
                case Msg.Shocked: RobotSync.ClientReceiveShock(ref rd); break;
                case Msg.LifeStates: LifeSync.ClientReceiveStates(ref rd); break;
                case Msg.Revive: LifeSync.ClientReceiveRevive(s, ref rd); break;
                case Msg.Gear: GearSync.ClientReceive(s, ref rd); break;
                case Msg.ItemTaken: ItemSync.ClientReceiveTaken(ref rd); break;
                case Msg.ItemSpawned: ItemSync.ClientReceiveSpawned(ref rd); break;
                case Msg.Sound: SoundSync.ClientReceive(s, ref rd); break;
                case Msg.Won: LifeSync.ClientReceiveWon(ref rd); break;
                case Msg.Cheat: CheatSync.ClientReceive(s, ref rd); break;
                case Msg.DemonSpawn: DemonSync.ClientReceiveSpawn(ref rd); break;
                case Msg.DemonGone: DemonSync.ClientReceiveGone(ref rd); break;
                case Msg.Projectile: ProjectileSync.ClientReceiveShot(ref rd); break;
                case Msg.ProjectileRest: ProjectileSync.ClientReceiveRest(ref rd); break;
                case Msg.Clock: RunClock.ClientReceive(s, ref rd); break;
                case Msg.JoinState: LifeSync.ClientReceiveJoinState(ref rd); break;
            }
        }

        public static void OnPlayerLeft(ushort id)
        {
            PlayerSync.Remove(id);
            LifeSync.OnPlayerLeft(id);
        }

        /// <summary>Host: someone joined mid-run; bring them up to date on the shared world.</summary>
        public static void OnPlayerJoined(NetSession s, ushort id, string identity)
        {
            if (!InRun) return;
            LifeSync.HostPlayerJoined(s, id, identity);
            ItemSync.HostSendTakenTo(s, id);
            CheatSync.HostSendStateTo(s, id);
            DemonSync.HostSendAllTo(s, id);
            ProjectileSync.HostSendRestsTo(s, id);
        }

        /// <summary>A new run is loading: forget everything tied to the old level.</summary>
        public static void OnRunStarting() => ResetWorld();

        public static void Reset()
        {
            _acc = 0f;
            ResetWorld();
        }

        static void ResetWorld()
        {
            LocalPlayer.Forget();
            Spectator.Reset();
            PlayerSync.Reset();
            GearSync.Reset();
            RobotSync.Reset();
            ItemSync.Reset();
            LifeSync.Reset();
            CarrySync.Reset();
            CheatSync.Reset();
            DemonSync.Reset();
            PrefabRegistry.Invalidate();
            GunCache.Invalidate();
            SoundSync.Reset();
            ProjectileSync.Reset();
            RunClock.Reset();
            AnimatorClock.Reset();
            SyncStats.Reset();
        }
    }

    /// <summary>The local player's AimScript (the one with main_client_control), set when it spawns.</summary>
    static class LocalPlayer
    {
        static AimScript _current;
        static int _spawnCount;

        public static readonly AccessTools.FieldRef<AimScript, bool> Dead = AccessTools.FieldRefAccess<AimScript, bool>("dead");

        public static AimScript Get() => _current != null ? _current : null;

        /// <summary>Increments on every spawn, so senders can flag a teleport.</summary>
        public static int SpawnCount => _spawnCount;

        public static void Set(AimScript player)
        {
            _current = player;
            _spawnCount++;
        }

        /// <summary>The local player was moved (revived elsewhere): others should snap, not glide.</summary>
        public static void MarkTeleported() => _spawnCount++;

        /// <summary>
        /// A new run is loading: the old player object still exists for a moment (often dead) and must not be
        /// reported as this run's player, or its death counts against the new run.
        /// </summary>
        public static void Forget() => _current = null;

        public static bool IsAlive(AimScript p) => p != null && !Dead(p);
    }
}
