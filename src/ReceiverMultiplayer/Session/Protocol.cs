using System.Text;

namespace ReceiverMultiplayer.Session
{
    enum Msg : byte
    {
        Hello = 1,      // C→H  version, name, mod hash
        Welcome = 2,    // H→C  your id, host gun list
        Disconnect = 3, // H→C  reason (rejection, kick, host closing)
        Snapshot = 4,   // H→C  full lobby state
        SetReady = 5,   // C→H  bool
        SetGunPick = 6, // C→H  index into host gun list, -1 = Default
        StartRun = 7,   // H→C  run id, seed, settings, your gun
        Ping = 8,       // both
        Pong = 9,       // both

        // ---- in-run world sync (ignored outside a run; every one carries the low 16 bits of the run id) ----
        PlayerState = 10,  // C→H  unreliable: own body pose
        PlayerStates = 11, // H→C  unreliable: batch of body poses (host relays everyone's)
        Shot = 12,         // C→H own shot; H→C relayed with the shooter id. Reliable, visual only
        RobotStates = 13,  // H→C  unreliable: batch of enemy poses and states
        RobotHit = 14,     // C→H  reliable: your bullet hit an enemy part; the host applies the damage
        RobotDamage = 15,  // H→C  reliable: an enemy part was destroyed
        RobotShot = 16,    // H→C  reliable: a turret fired (the bullet can hurt the player it reaches)
        Shocked = 17,      // H→C  reliable: a drone's shock prod reached you
        Died = 18,         // C→H  reliable: I died (one life per run), with how many revives I had received
        LifeStates = 19,   // H→C  reliable: who is dead right now
        TapeTaken = 20,    // C→H  reliable: I picked up a tape here, at this crouch height (revives the dead)
        Revive = 21,       // H→C  reliable: dead players come back inside the player who found it
        Gear = 22,         // C→H own hands, belt and moving gun parts; H→C relayed with the owner id. Unreliable, on change
        ItemTaken = 25,    // C→H I picked up this world item; H→C it's gone
        ItemSpawned = 26,  // C→H I dropped/ejected this item (magazine, round, casing, flashlight); H→C relayed
        Sound = 27,        // C→H a sound my gun, magazine or body made; H→C relayed with the owner id. Reliable

        // ---- session-level (any phase) ----
        Skin = 28,         // C→H my skin image (or none); H→C a player's skin. Reliable, ≤ 128 KB, checked before decoding
        AddonMismatch = 29, // H→C (before Welcome) the add-ons you're missing and have extra; a Disconnect follows
        Won = 30,          // C→H I've heard every tape; H→C the team won (the run ends a few seconds later). Reliable
        Cheat = 31,        // C→H I used a cheat (slo-mo: please toggle it for everyone); H→C who used which cheat, and slo-mo's state
        DemonSpawn = 32,   // H→C a Green Demon for this player: its warning light here (or the live demon, for late joiners)
        DemonGone = 33,    // H→C that Green Demon is gone (its player died or left)
        Modifiers = 34,    // H→C (session) the host changed a modifier in the pause menu: all values. Reliable
        AddonFiles = 35,   // C→H (before Welcome, after a mismatch) list a non-Workshop mod of yours I lack; H→C its files
        AddonChunk = 36,   // C→H a piece of one of those files; H→C that piece

        // ---- in-run world sync, continued ----
        Projectile = 37,     // C→H my gun fired physics projectiles (ids, poses); H→C relayed with the shooter id. Reliable
        ProjectileRest = 38, // C→H where one of my projectiles came to rest (stuck to the level, an enemy or a player, or settled); H→C relayed
        Clock = 39,          // H→C the run clock (game ms since the host's level began). Unreliable, every second
        JoinState = 40,      // H→C (joining mid-run) 0: start inside a teammate who's alive; 1: you were down when you left, so you're down again
    }

    enum LobbyPhase : byte { Waiting = 0, Countdown = 1, InRun = 2 }

    enum LobbyVisibility : byte { FriendsOnly = 0, Public = 1, InviteOnly = 2 }

    /// <summary>Wire constants and input limits. Every inbound field is bounded by one of these.</summary>
    static class Protocol
    {
        public const ushort Version = 19;

        /// <summary>This release of the mod. Players connect only to the very same release (and protocol).</summary>
        public const string ModVersion = MyPluginInfo.PLUGIN_VERSION;

        /// <summary>What every connection path compares: protocol and release, e.g. "19/0.7.0".</summary>
        public static readonly string Build = Version + "/" + ModVersion;

        /// <summary>The release part of another player's <see cref="Build"/>, for messages.</summary>
        public static string ReleaseOf(string build)
        {
            int slash = build != null ? build.IndexOf('/') : -1;
            return slash >= 0 && slash + 1 < build.Length ? SanitizeReason(build.Substring(slash + 1)) : "an older version";
        }

        /// <summary>What a player whose version differs from the host's is told (by their own game or by the host).</summary>
        public static string Mismatch(string hostRelease, string yourRelease) =>
            $"Version mismatch: the host runs Receiver Multiplayer {hostRelease}, you have {yourRelease}. Everyone needs the same version: run ReceiverMultiplayer-Updater.exe (in BepInEx\\plugins\\ReceiverMultiplayer).";

        /// <summary>Direct IP connection key; requests without it are dropped silently.</summary>
        public const string ConnectKey = "ReceiverMP";
        public const int DefaultPort = 27777;

        // Steam lobby data keys (public metadata only: no IPs, no Steam IDs beyond what Steam itself shows).
        public const string LobbyProtocolKey = "rmp_protocol";
        public const string LobbyNameKey = "rmp_name";
        public const string LobbyStateKey = "rmp_state";
        public const int SteamLobbyMaxMembers = 250; // Steam's own ceiling; the mod sets no lower limit

        public const int MaxReliableBytes = 256 * 1024;
        public const int MaxUnreliableBytes = 1200;
        public const int MaxNameChars = 24;
        public const int MaxNameBytes = 96;
        public const int MaxReasonBytes = 256;
        public const int MaxPasswordBytes = 64;
        public const int MaxGuns = 1024;
        public const int MaxGunNameBytes = 128;
        public const int MaxPlayersInSnapshot = 4096;

        // World sync
        public const float WorldTick = 0.05f;          // 20 Hz send tick for poses
        public const int MaxPellets = 32;              // per shot (shotguns)
        public const float MaxBulletSpeed = 2000f;     // m/s; anything faster is rejected
        public const float MaxWorldCoord = 100000f;    // positions outside ±this are rejected
        public const float MaxShotOffset = 6f;         // a shot must start this close to the shooter's body
        public const float MaxHitOffset = 4f;          // a reported enemy hit must be this close to the part
        public const float MaxDamageMultiplier = 20f;

        public const float HandshakeTimeout = 10f;
        public const float ConnectTimeout = 20f;
        public const float PingInterval = 1f;
        public const float PeerTimeout = 30f; // generous: a stalled loading screen or Steam overlay shouldn't kick anyone
        public const float SnapshotMinInterval = 0.1f;
        public const float CountdownSeconds = 3f;

        // Fetching the host's non-Workshop mods (a joining player whose add-ons differ stays connected for it)
        public const int ChunkBytes = 128 * 1024;
        public const int ChunkWindow = 4;                 // pieces asked for ahead of the one arriving
        public const float ChunkRequestsPerSecond = 40f;  // well under the per-peer rate limit
        public const float ResolveIdleSeconds = 600f;     // the host lets a player sort out add-ons this long between requests
        public const float ResolveMaxSeconds = 3600f;     // and this long in all
        public const int MaxResolvingPeers = 8;
        public const long MaxShareBytesPerPeer = 4L << 30; // per Steam account or address, for the whole session
        public const long MaxQueuedShareBytes = 1L << 20;  // further pieces wait until the connection has sent this much

        // Per-peer inbound rate limit (token bucket). Sustained abuse disconnects the peer.
        public const float RatePerSecond = 120f;
        public const float RateBurst = 240f;
        public const int MaxViolations = 200;

        /// <summary>
        /// Host: on top of the overall limit, how many of each costly world event one player may send (a second,
        /// and in a burst). Each of these makes the host build objects and relay them to everyone, so a flood of
        /// them under the overall limit would still stall every game; beyond the budget they're ignored. Far above
        /// anything real play produces.
        /// </summary>
        public static readonly (Msg type, float rate, float burst)[] EventBudgets =
        {
            (Msg.Shot, 25f, 60f),
            (Msg.Projectile, 25f, 60f),
            (Msg.ProjectileRest, 25f, 60f),
            (Msg.RobotHit, 40f, 100f),
            (Msg.ItemSpawned, 20f, 60f),
            (Msg.ItemTaken, 20f, 60f),
            (Msg.Sound, 30f, 80f),
            (Msg.Cheat, 2f, 10f),
            (Msg.TapeTaken, 2f, 10f),
            (Msg.Died, 1f, 5f),
            (Msg.Won, 1f, 5f),
        };

        // Direct IP: connections and players the host accepts, and wrong passwords before an address is shut out.
        public const int MaxPlayers = 16;
        public const int MaxConnectionsPerAddress = 4;
        public const int MaxPasswordFailures = 5;

        public static readonly byte[] DiscoveryProbe = Encoding.ASCII.GetBytes("RMP?" + Build);
        public static readonly byte[] DiscoveryReply = Encoding.ASCII.GetBytes("RMP!" + Build);

        /// <summary>Names are shown in UI: strip control characters and rich-text markup, cap the length.</summary>
        public static string SanitizeName(string name)
        {
            var sb = new StringBuilder();
            foreach (char c in name ?? "")
            {
                if (char.IsControl(c) || c == '<' || c == '>' || char.IsSurrogate(c)) continue;
                sb.Append(c);
                if (sb.Length >= MaxNameChars) break;
            }
            var s = sb.ToString().Trim();
            return s.Length == 0 ? "Player" : s;
        }

        public static string SanitizeReason(string reason)
        {
            if (string.IsNullOrEmpty(reason)) return "";
            return StripMarkup(reason.Length <= 120 ? reason : reason.Substring(0, 120));
        }

        static string StripMarkup(string s)
        {
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
                if (!char.IsControl(c) && c != '<' && c != '>') sb.Append(c);
            return sb.ToString();
        }
    }
}
