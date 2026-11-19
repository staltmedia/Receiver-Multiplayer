using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ReceiverMultiplayer.Net;
using Steamworks;
using UnityEngine;

namespace ReceiverMultiplayer.Session
{
    sealed class LobbyPlayer
    {
        public ushort Id;
        public string Name;
        public bool Ready;
        public short GunPick = -1; // index into the host's gun list; -1 = Default (random)
        public bool IsHost;
    }

    /// <summary>Settings every client receives; part of the run's determinism contract.</summary>
    sealed class SharedSettings
    {
        public bool WeaponChoice;
        public bool FriendlyFire;          // off: other players' bullets never hurt you
        public SharedSettings Clone() => (SharedSettings)MemberwiseClone();
    }

    /// <summary>Host-local settings; never sent to clients.</summary>
    sealed class HostOptions
    {
        public LobbyVisibility Visibility = LobbyVisibility.FriendsOnly;
        public bool DirectIp;
        public int Port = Protocol.DefaultPort;
        public string Password = "";
        public string SeedText = "";
    }

    /// <summary>
    /// One multiplayer session, host or client. The host owns all lobby state (players, ready flags, picks,
    /// settings, countdown) and broadcasts snapshots; a client may only ask to change its own entry.
    /// Survives scene loads: it is plain C# ticked by <see cref="Runner"/>.
    /// </summary>
    sealed partial class NetSession : INetSink
    {
        public static NetSession Current { get; private set; }
        public static event Action<NetSession> Started;
        public static event Action<string> Ended;

        const byte SteamSlot = 0;
        const byte DirectSlot = 1;

        public bool IsHost { get; }
        public bool Joined { get; private set; }
        public LobbyPhase Phase { get; private set; }
        public int CountdownSeconds { get; private set; }
        public ushort LocalId { get; private set; }
        public string Status { get; private set; } = "";
        public readonly List<LobbyPlayer> Players = new List<LobbyPlayer>();
        public readonly SharedSettings Settings = new SharedSettings();
        public readonly HostOptions Options = new HostOptions();
        public IReadOnlyList<string> GunNames => _gunNames;
        public string ConnectionKind { get; private set; } = "";

        /// <summary>Lobby state changed; UI should refresh (coalesce: may fire several times a frame).</summary>
        public event Action Changed;
        /// <summary>The host started a new run setup; clients' UI should bring the waiting room back.</summary>
        public event Action ReturnedToWaitingRoom;

        readonly ITransport[] _transports = new ITransport[2];
        readonly NetWriter _w = new NetWriter(512);
        readonly System.Random _rng = new System.Random();
        List<string> _gunNames = new List<string>();
        float _time;

        // ---- host state ----
        sealed class Remote
        {
            public PeerId Peer;
            public LobbyPlayer Player; // null until the handshake completes
            public float ConnectedAt, LastHeard, NextPing;
            public float Tokens = Protocol.RateBurst;
            public int Violations;
            public float ViolationFade;
            public int Dropped;          // messages ignored over the rate limit since the last report
            public float NextDropReport;
            // Refused over add-ons but kept connected while the player sorts them out (see NetSession.Addons.cs)
            public bool Resolving;
            public float LastRequest, LastHello = -100f;
            public HostShares Shares;
            public string Identity;     // who this is across reconnects (Steam ID, or address and name), for rejoining mid-run
            public readonly float[] Events = NewEventBudget(); // per costly event type, see Protocol.EventBudgets
        }

        static readonly int[] _budgetSlot = BudgetSlots();

        static int[] BudgetSlots()
        {
            var slots = new int[256];
            for (int i = 0; i < slots.Length; i++) slots[i] = -1;
            for (int i = 0; i < Protocol.EventBudgets.Length; i++) slots[(byte)Protocol.EventBudgets[i].type] = i;
            return slots;
        }

        static float[] NewEventBudget()
        {
            var b = new float[Protocol.EventBudgets.Length];
            for (int i = 0; i < b.Length; i++) b[i] = Protocol.EventBudgets[i].burst;
            return b;
        }

        /// <summary>Host: may this player send one more of this event now? (Everything not budgeted: yes.)</summary>
        static bool SpendEvent(Remote r, Msg type)
        {
            int slot = _budgetSlot[(byte)type];
            if (slot < 0) return true;
            if (r.Events[slot] < 1f) return false;
            r.Events[slot] -= 1f;
            return true;
        }

        readonly Dictionary<PeerId, Remote> _remotes = new Dictionary<PeerId, Remote>();
        readonly Dictionary<ushort, Remote> _byPlayer = new Dictionary<ushort, Remote>();
        readonly List<Remote> _scratch = new List<Remote>();
        readonly List<(PeerId peer, float at, bool ban)> _closing = new List<(PeerId, float, bool)>();
        ushort _nextId = 1;
        float _countdownLeft, _nextSnapshot;
        bool _dirty;
        uint _runId;
        int _runSeed;

        // ---- client state ----
        PeerId _host;
        bool _hostConnected;
        float _connectedAt, _lastHeard, _nextPing;

        NetSession(bool isHost) { IsHost = isHost; }

        public LobbyPlayer Me => Players.FirstOrDefault(p => p.Id == LocalId);
        /// <summary>Current run (host: the one it started; client: from the last StartRun). World messages carry its low 16 bits.</summary>
        public uint RunId => _runId;
        public bool InRun => Phase == LobbyPhase.InRun && Joined;
        /// <summary>Client: smoothed round trip to the host in seconds (-1 until the first answer).</summary>
        public float RoundTrip { get; private set; } = -1f;
        public bool HasSteamLobby => _transports[SteamSlot] is SteamHostTransport;
        public bool SteamLobbyReady => _transports[SteamSlot] is SteamHostTransport s && s.Ready;

        // ================= lifecycle =================

        public static NetSession Host()
        {
            Current?.Close("Started a new session.");
            var s = new NetSession(true) { LocalId = 0, Joined = true, ConnectionKind = "Host" };
            s.Players.Add(new LobbyPlayer { Id = 0, Name = LocalName(), IsHost = true });
            Current = s;
            Plugin.Log.LogInfo("Hosting a session.");
            // The gun list and add-on list are what everyone is checked against: wait until the game has finished
            // loading add-ons (it doesn't wait before building the first level).
            if (Addons.StillLoading(out var what))
            {
                s._waitingForAddons = true;
                s.Status = $"Loading add-ons before the session opens ({what})…";
                Plugin.Log.LogInfo("Waiting for add-ons to finish loading before opening the session.");
            }
            else s.OpenHost();
            Started?.Invoke(s);
            s.MarkDirty();
            return s;
        }

        bool _waitingForAddons;

        /// <summary>Host: add-ons are loaded; settle the gun list and open the Steam lobby.</summary>
        void OpenHost()
        {
            _waitingForAddons = false;
            Addons.RefreshGunList();
            _gunNames = RunStarter.LocalGunNames();
            if (SteamP2PTransport.Available)
            {
                _transports[SteamSlot] = new SteamHostTransport(SteamSlot, Options.Visibility);
                Status = "";
            }
            else Status = "Steam isn't running: only Direct IP is available.";
            Addons.LogLocal("hosting");
            MarkDirty();
            Raise();
        }

        public static NetSession JoinSteam(CSteamID lobby)
        {
            if (!SteamP2PTransport.Available) return null;
            var s = BeginClient("Steam");
            s.ConnectWhenReady(() => new SteamClientTransport(0, lobby), "Joining the Steam lobby…");
            Started?.Invoke(s);
            return s;
        }

        public static NetSession JoinDirect(string address, int port, string password)
        {
            var s = BeginClient("Direct IP");
            s.ConnectWhenReady(() => new DirectClientTransport(0, address, port, password), $"Connecting to {address}:{port}…");
            Started?.Invoke(s);
            return s;
        }

        Func<ITransport> _connect;
        string _connectStatus;
        float _connectStartedAt;

        /// <summary>Client: connect once the game has finished loading add-ons (they're sent to the host to compare).</summary>
        void ConnectWhenReady(Func<ITransport> make, string status)
        {
            _connect = make;
            _connectStatus = status;
            TryConnect();
        }

        void TryConnect()
        {
            if (_connect == null) return;
            if (Addons.StillLoading(out var what) && _time < Addons.MaxWaitSeconds)
            {
                string text = $"Loading your add-ons before joining ({what})…";
                if (Status != text) { Status = text; Raise(); }
                return;
            }
            var make = _connect;
            _connect = null;
            _connectStartedAt = _time;
            Status = _connectStatus;
            _transports[0] = make();
            Raise();
        }

        static NetSession BeginClient(string kind)
        {
            Current?.Close("Joining another session.");
            var s = new NetSession(false) { ConnectionKind = kind };
            Current = s;
            return s;
        }

        public void Leave() => Close(IsHost ? "You closed the session." : "You left the session.");

        bool _polling;
        string _pendingClose;

        void Close(string reason)
        {
            if (Current != this) return;
            if (_polling)
            {
                // Never tear a transport down from inside its own event callback.
                if (_pendingClose == null) _pendingClose = reason;
                return;
            }
            if (IsHost)
            {
                foreach (var r in _remotes.Values)
                {
                    if (r.Player != null || r.Resolving) { Begin(Msg.Disconnect).Str("The host closed the session.", Protocol.MaxReasonBytes); Send(r.Peer); }
                    _transports[r.Peer.Transport]?.Disconnect(r.Peer, "The host closed the session.", false);
                }
            }
            for (int i = 0; i < _transports.Length; i++)
            {
                try { _transports[i]?.Dispose(); }
                catch (Exception e) { Plugin.Log.LogWarning($"Transport shutdown: {e.Message}"); }
                _transports[i] = null;
            }
            Current = null;
            World.WorldSync.Reset();
            Skins.SkinSync.Reset();
            ModifierSync.Restore();
            Plugin.Log.LogInfo("Session ended: " + reason);
            Ended?.Invoke(reason);
        }

        public static void TickCurrent(float dt) => Current?.Tick(dt);

        void Tick(float dt)
        {
            _time += dt;
            if (IsHost) RefillAllowance(dt);
            _polling = true;
            try
            {
                for (int i = 0; i < _transports.Length; i++)
                {
                    var t = _transports[i];
                    if (t == null) continue;
                    try { t.Poll(this); }
                    catch (Exception e) { Plugin.Log.LogError($"{t.Kind} transport error: {e}"); }
                    if (_pendingClose != null) break;
                }
            }
            finally
            {
                _polling = false;
            }
            if (_pendingClose != null)
            {
                Close(_pendingClose);
                return;
            }
            if (IsHost) HostTick(dt);
            else ClientTick();
            if (Current == this)
                World.WorldSync.Tick(this, dt);
        }

        // ================= transport events =================

        public void OnPeerConnected(PeerId peer)
        {
            if (IsHost)
            {
                _remotes[peer] = new Remote { Peer = peer, ConnectedAt = _time, LastHeard = _time };
                return;
            }
            if (_hostConnected) return;
            _host = peer;
            _hostConnected = true;
            _connectedAt = _lastHeard = _time;
            SendHello();
            Addons.LogLocal("joining");
            Status = "Connected. Waiting for the host…";
            Raise();
        }

        /// <summary>Client: introduce this game (again, after fixing add-ons): protocol, name, add-ons.</summary>
        void SendHello()
        {
            var w = Begin(Msg.Hello);
            w.U16(Protocol.Version);
            w.Str(Protocol.ModVersion, 32); // players connect only to the very same release
            w.Str(LocalName(), Protocol.MaxNameBytes);
            Addons.Write(w, Addons.Local()); // the host checks these match its own
            Send(_host);
            _helloSentAt = _time;
            _awaitingHelloReply = true;
        }

        public void OnPeerDisconnected(PeerId peer, string reason)
        {
            if (IsHost)
            {
                if (_remotes.TryGetValue(peer, out var r))
                    RemoveRemote(r, reason);
                return;
            }
            if (!_hostConnected || peer == _host)
                Close(string.IsNullOrEmpty(reason) ? "Disconnected." : reason);
        }

        public void OnData(PeerId peer, Channel channel, byte[] buffer, int offset, int count)
        {
            if (count <= 0) return;
            int max = channel == Channel.Reliable ? Protocol.MaxReliableBytes : Protocol.MaxUnreliableBytes;

            if (IsHost)
            {
                if (!_remotes.TryGetValue(peer, out var r)) return;
                r.LastHeard = _time;
                if (count > max || r.Tokens < 1f)
                {
                    r.Dropped++;
                    if (++r.Violations > Protocol.MaxViolations) Drop(r, "Too much network traffic.", false);
                    return;
                }
                r.Tokens -= 1f;
                var type = (Msg)buffer[offset];
                try
                {
                    var rd = new NetReader(buffer, offset, count);
                    rd.U8();
                    FromClient(r, type, ref rd);
                }
                catch (ProtocolException e)
                {
                    Plugin.Log.LogWarning($"Dropping {Who(r)}: {e.Message}");
                    Drop(r, "Protocol error.", false);
                }
                catch (Exception e)
                {
                    // Well-formed, but handling it failed in game code: skip this one message and carry on. A player
                    // whose messages keep failing is still dropped.
                    HandlerFailed(type, e);
                    r.Violations += 10;
                    if (r.Violations > Protocol.MaxViolations) Drop(r, "Protocol error.", false);
                }
                return;
            }

            if (peer != _host || count > max) return;
            _lastHeard = _time;
            var hostType = (Msg)buffer[offset];
            try
            {
                var rd = new NetReader(buffer, offset, count);
                rd.U8();
                FromHost(hostType, ref rd);
            }
            catch (ProtocolException e)
            {
                Close($"The host sent invalid data ({e.Message}).");
            }
            catch (Exception e)
            {
                HandlerFailed(hostType, e);
            }
        }

        static readonly Dictionary<Msg, int> _handlerFailures = new Dictionary<Msg, int>();

        /// <summary>The first failure per message type is logged in full; after that, every hundredth is counted.</summary>
        static void HandlerFailed(Msg type, Exception e)
        {
            _handlerFailures.TryGetValue(type, out int n);
            _handlerFailures[type] = ++n;
            if (n == 1) Plugin.Log.LogError($"Handling a {type} message failed (skipped): {e}");
            else if (n % 100 == 0) Plugin.Log.LogWarning($"Handling {type} messages has failed {n} times (skipped).");
        }

        // ================= host =================

        void FromClient(Remote r, Msg type, ref NetReader rd)
        {
            if (r.Player == null)
            {
                switch (type)
                {
                    case Msg.Hello:
                        HostHello(r, ref rd);
                        return;
                    case Msg.Ping when r.Resolving:
                    {
                        uint stamp = rd.U32();
                        rd.End();
                        Begin(Msg.Pong).U32(stamp); // keeps the connection alive while the player reads the add-on windows
                        Send(r.Peer);
                        return;
                    }
                    case Msg.Pong when r.Resolving:
                        rd.U32();
                        rd.End();
                        return;
                    case Msg.AddonFiles when r.Resolving:
                        HostAddonFiles(r, ref rd);
                        return;
                    case Msg.AddonChunk when r.Resolving:
                        HostAddonChunk(r, ref rd);
                        return;
                    default:
                        throw new ProtocolException("expected hello");
                }
            }

            switch (type)
            {
                case Msg.SetReady:
                {
                    bool ready = rd.Bool();
                    rd.End();
                    if (Phase != LobbyPhase.InRun && r.Player.Ready != ready)
                    {
                        r.Player.Ready = ready;
                        MarkDirty();
                        EvaluateReady();
                    }
                    break;
                }
                case Msg.SetGunPick:
                {
                    short pick = rd.I16();
                    rd.End();
                    if (pick < -1 || pick >= _gunNames.Count) throw new ProtocolException("bad gun pick");
                    if (Phase != LobbyPhase.InRun && r.Player.GunPick != pick)
                    {
                        r.Player.GunPick = pick;
                        MarkDirty();
                    }
                    break;
                }
                case Msg.Ping:
                {
                    uint stamp = rd.U32();
                    rd.End();
                    Begin(Msg.Pong).U32(stamp);
                    Send(r.Peer);
                    break;
                }
                case Msg.Pong:
                    rd.U32();
                    rd.End();
                    break;
                case Msg.Skin:
                    Skins.SkinSync.HostReceive(this, r.Player.Id, ref rd);
                    break;
                case Msg.PlayerState:
                case Msg.Shot:
                case Msg.RobotHit:
                case Msg.Died:
                case Msg.TapeTaken:
                case Msg.Gear:
                case Msg.ItemTaken:
                case Msg.ItemSpawned:
                case Msg.Sound:
                case Msg.Won:
                case Msg.Cheat:
                case Msg.Projectile:
                case Msg.ProjectileRest:
                    if (Phase != LobbyPhase.InRun) break;
                    if (!SpendEvent(r, type)) { r.Dropped++; break; } // over this event's budget: ignored
                    World.WorldSync.HostReceive(this, r.Player.Id, type, ref rd);
                    break;
                default:
                    throw new ProtocolException("unexpected message " + (byte)type);
            }
        }

        void Accept(Remote r, string name)
        {
            var p = new LobbyPlayer { Id = NextId(), Name = name };
            r.Player = p;
            r.NextPing = _time + Protocol.PingInterval;
            r.Identity = _transports[r.Peer.Transport] is DirectHostTransport direct
                ? "ip:" + direct.AddressOf(r.Peer) + "/" + name
                : "steam:" + r.Peer.Value;
            Players.Add(p);
            _byPlayer[p.Id] = r;

            var w = Begin(Msg.Welcome);
            w.U16(p.Id);
            w.U16((ushort)Math.Min(_gunNames.Count, Protocol.MaxGuns));
            for (int i = 0; i < _gunNames.Count && i < Protocol.MaxGuns; i++)
                w.Str(_gunNames[i], Protocol.MaxGunNameBytes);
            foreach (int m in ModifierSync.Read()) w.I32(m); // mod settings decide the gun list; modifiers ride along
            Addons.WriteOrder(w, Addons.LoadOrder());          // the order guns and level tiles are listed in
            Send(r.Peer);
            r.Resolving = false;
            r.Shares = null;

            Plugin.Log.LogInfo($"{p.Name} joined via {_transports[r.Peer.Transport]?.Kind}.");
            MarkDirty();
            EvaluateReady();
            SendSnapshot();
            Skins.SkinSync.HostSendAllTo(this, p.Id);
            if (Phase == LobbyPhase.InRun)
            {
                SendStartRun(r); // late join: straight into the current run at the start spawn
                World.WorldSync.OnPlayerJoined(this, p.Id, r.Identity);
            }
        }

        ushort NextId()
        {
            while (_nextId == 0 || Players.Any(p => p.Id == _nextId)) _nextId++;
            return _nextId++;
        }

        /// <summary>
        /// Host, before reading what arrived: top up each player's message allowance. After a long frame (a level
        /// loading, a hitch) everything a player sent meanwhile arrives at once; the allowance grows with the frame,
        /// so ordinary traffic is never thrown away (it was, and a dropped "I picked this up" left the item for
        /// others to take again), while a sustained flood still is. Violations fade one a second, so a long
        /// session's occasional bursts never add up to a disconnect.
        /// </summary>
        void RefillAllowance(float dt)
        {
            float add = dt * Protocol.RatePerSecond;
            foreach (var r in _remotes.Values)
            {
                r.Tokens = Math.Min(Protocol.RateBurst + add, r.Tokens + add);
                for (int i = 0; i < r.Events.Length; i++)
                {
                    var (_, rate, burst) = Protocol.EventBudgets[i];
                    r.Events[i] = Math.Min(burst + dt * rate, r.Events[i] + dt * rate);
                }
                if (r.Violations > 0)
                {
                    r.ViolationFade += dt;
                    while (r.ViolationFade >= 1f && r.Violations > 0) { r.ViolationFade -= 1f; r.Violations--; }
                }
                else r.ViolationFade = 0f;
                if (r.Dropped > 0 && _time >= r.NextDropReport)
                {
                    Plugin.Log.LogWarning($"Ignored {r.Dropped} message(s) from {Who(r)}: more than {Protocol.RatePerSecond:0} a second.");
                    r.Dropped = 0;
                    r.NextDropReport = _time + 10f;
                }
            }
        }

        void HostTick(float dt)
        {
            if (_waitingForAddons)
            {
                if (!Addons.StillLoading(out var what) || _time >= Addons.MaxWaitSeconds)
                {
                    if (_time >= Addons.MaxWaitSeconds) Plugin.Log.LogWarning("Add-ons still loading after a minute; opening the session anyway.");
                    OpenHost();
                }
                else
                {
                    string text = $"Loading add-ons before the session opens ({what})…";
                    if (Status != text) { Status = text; Raise(); }
                }
            }

            if (_transports[SteamSlot] is SteamHostTransport steam)
            {
                if (steam.Error != null && Status != steam.Error) { Status = steam.Error; Raise(); }
                steam.SetData(Protocol.LobbyNameKey, Players[0].Name);
                steam.SetData(Protocol.LobbyStateKey, Phase == LobbyPhase.InRun ? "In a run" : "Waiting room");
            }

            _scratch.Clear();
            _scratch.AddRange(_remotes.Values);
            foreach (var r in _scratch)
            {
                if (r.Player == null && r.Resolving && (_time - r.LastRequest > Protocol.ResolveIdleSeconds || _time - r.ConnectedAt > Protocol.ResolveMaxSeconds))
                { Drop(r, "You took too long to sort out add-ons. Join again when you're ready.", false); continue; }
                if (r.Player == null && r.Resolving) ServeWaitingChunks(r);
                if (r.Player == null && !r.Resolving && _time - r.ConnectedAt > Protocol.HandshakeTimeout) { Drop(r, "Handshake timed out.", false); continue; }
                if (_time - r.LastHeard > Protocol.PeerTimeout) { Drop(r, "Connection timed out.", false); continue; }
                if (r.Player != null && _time >= r.NextPing)
                {
                    r.NextPing = _time + Protocol.PingInterval;
                    Begin(Msg.Ping).U32((uint)(_time * 1000f));
                    Send(r.Peer);
                }
            }

            for (int i = _closing.Count - 1; i >= 0; i--)
            {
                var (peer, at, ban) = _closing[i];
                if (_time < at) continue;
                _closing.RemoveAt(i);
                _transports[peer.Transport]?.Disconnect(peer, "", ban);
            }

            if (Phase == LobbyPhase.Countdown)
            {
                _countdownLeft -= dt;
                int secs = Math.Max(0, Mathf.CeilToInt(_countdownLeft));
                if (secs != CountdownSeconds) { CountdownSeconds = secs; MarkDirty(); }
                if (_countdownLeft <= 0f) StartGame();
            }

            if (_dirty && _time >= _nextSnapshot)
                SendSnapshot();
        }

        /// <summary>Tell the peer why, then close it shortly after so the reason has time to arrive.</summary>
        void Drop(Remote r, string reason, bool ban)
        {
            if (r.Player != null || reason != null)
            {
                Begin(Msg.Disconnect).Str(reason ?? "", Protocol.MaxReasonBytes);
                Send(r.Peer);
            }
            _closing.Add((r.Peer, _time + 0.5f, ban));
            RemoveRemote(r, reason);
        }

        void RemoveRemote(Remote r, string reason)
        {
            _remotes.Remove(r.Peer);
            if (r.Player == null) return;
            Players.Remove(r.Player);
            _byPlayer.Remove(r.Player.Id);
            if (Phase == LobbyPhase.InRun) World.LifeSync.RememberLeaver(r.Identity, r.Player.Id);
            World.WorldSync.OnPlayerLeft(r.Player.Id);
            Skins.SkinSync.Forget(r.Player.Id);
            Plugin.Log.LogInfo($"{r.Player.Name} left ({reason}).");
            MarkDirty();
            EvaluateReady();
        }

        void EvaluateReady()
        {
            if (!IsHost || Phase == LobbyPhase.InRun) return;
            bool all = Players.Count > 0 && Players.All(p => p.Ready);
            if (all && Phase == LobbyPhase.Waiting)
            {
                Phase = LobbyPhase.Countdown;
                _countdownLeft = Protocol.CountdownSeconds;
                CountdownSeconds = Mathf.CeilToInt(_countdownLeft);
                MarkDirty();
            }
            else if (!all && Phase == LobbyPhase.Countdown)
            {
                Phase = LobbyPhase.Waiting;
                MarkDirty();
            }
        }

        void MarkDirty()
        {
            _dirty = true;
            Raise();
        }

        void SendSnapshot()
        {
            _dirty = false;
            _nextSnapshot = _time + Protocol.SnapshotMinInterval;
            var w = Begin(Msg.Snapshot);
            w.U8((byte)Phase);
            w.U8((byte)CountdownSeconds);
            WriteSettings(w, Settings);
            w.U16((ushort)Players.Count);
            foreach (var p in Players)
            {
                w.U16(p.Id);
                w.Str(p.Name, Protocol.MaxNameBytes);
                w.Bool(p.Ready);
                w.I16(p.GunPick);
                w.Bool(p.IsHost);
            }
            foreach (var r in _remotes.Values)
                if (r.Player != null) Send(r.Peer);
            Raise();
        }

        /// <summary>The scene of the current run (the main level unless the host took everyone elsewhere).</summary>
        public string RunScene => _runScene;
        string _runScene = SceneCatalog.GameScene;

        /// <summary>
        /// Host: start now (Start Game button, when the countdown ends, or a scene picked in the pause menu).
        /// Everyone loads a fresh, seeded match of <paramref name="scene"/> (default: the main level).
        /// </summary>
        public void StartGame(string scene = null)
        {
            if (!IsHost || Current != this) return;
            _runId++;
            _runSeed = ResolveSeed();
            _runScene = string.IsNullOrEmpty(scene) ? SceneCatalog.GameScene : scene;
            Phase = LobbyPhase.InRun;
            Status = "";
            CountdownSeconds = 0;
            foreach (var p in Players) p.Ready = false;

            // Each player's gun, and the fixed set of guns level ammo is drawn from for the whole run.
            _runGunOf.Clear();
            foreach (var p in Players) _runGunOf[p.Id] = ResolveGun(p);
            _runGunSet = _runGunOf.Values.Select(n => _gunNames.IndexOf(n)).Where(i => i >= 0).Distinct().OrderBy(i => i).ToList();
            _runModifiers = ModifierSync.Read();

            SendSnapshot();
            foreach (var r in _remotes.Values)
                if (r.Player != null) SendStartRun(r);
            Plugin.Log.LogInfo($"Starting run {_runId} in {SceneCatalog.DisplayName(_runScene)} (seed {_runSeed}) with {Players.Count} player(s).");
            RunStarter.Begin(_runSeed, _runGunOf[Players[0].Id], Settings.Clone(), _runGunSet, _runScene);
        }

        readonly Dictionary<ushort, string> _runGunOf = new Dictionary<ushort, string>();
        List<int> _runGunSet = new List<int>();
        int[] _runModifiers = ModifierSync.Read();

        /// <summary>Host: end the current run setup and bring everyone back to the waiting room.</summary>
        public void NewRun()
        {
            if (!IsHost || Phase != LobbyPhase.InRun) return;
            Phase = LobbyPhase.Waiting;
            foreach (var p in Players) p.Ready = false;
            MarkDirty();
            SendSnapshot();
        }

        /// <summary>Host: the run is over (everyone died, or it was won): back to the waiting room.</summary>
        public void EndRun(string reason)
        {
            if (!IsHost || Phase != LobbyPhase.InRun) return;
            Plugin.Log.LogInfo("Run ended: " + reason);
            Status = reason;
            NewRun();
            ReturnedToWaitingRoom?.Invoke();
        }

        void SendStartRun(Remote r)
        {
            var w = Begin(Msg.StartRun);
            w.U32(_runId);
            w.I32(_runSeed);
            WriteSettings(w, Settings);
            if (!_runGunOf.TryGetValue(r.Player.Id, out var gun))
                _runGunOf[r.Player.Id] = gun = ResolveGun(r.Player); // late joiner
            w.Str(gun, Protocol.MaxGunNameBytes);
            w.U16((ushort)_runGunSet.Count);
            foreach (int g in _runGunSet) w.U16((ushort)g);
            foreach (int m in _runModifiers) w.I32(m);
            w.Str(_runScene, SceneCatalog.MaxNameBytes);
            Send(r.Peer);
        }

        /// <summary>Host: a modifier changed in the pause menu; everyone uses the new values (fog at once).</summary>
        public void BroadcastModifiers()
        {
            if (!IsHost) return;
            var values = ModifierSync.Read();
            foreach (var r in _remotes.Values)
            {
                if (r.Player == null) continue;
                var w = Begin(Msg.Modifiers);
                foreach (int m in values) w.I32(m);
                Send(r.Peer);
            }
            Plugin.Log.LogInfo("Modifiers changed: sent to everyone.");
        }

        int ResolveSeed()
        {
            var text = (Options.SeedText ?? "").Trim();
            if (text.Length == 0) return _rng.Next(0, int.MaxValue);
            if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int seed)) return seed;
            return Math.Abs(text.GetHashCode()); // same rule the game uses for a non-numeric seed
        }

        /// <summary>Host decides every gun, so no client ever rolls one (keeps SeededRNG in step on all clients).</summary>
        string ResolveGun(LobbyPlayer p)
        {
            if (_gunNames.Count == 0) return "";
            if (Settings.WeaponChoice && p.GunPick >= 0 && p.GunPick < _gunNames.Count) return _gunNames[p.GunPick];
            return _gunNames[_rng.Next(_gunNames.Count)];
        }

        // ---- host settings (host UI only) ----

        public void SetWeaponChoice(bool on)
        {
            if (!IsHost || Settings.WeaponChoice == on) return;
            Settings.WeaponChoice = on;
            MarkDirty();
        }

        public void SetFriendlyFire(bool on)
        {
            if (!IsHost || Settings.FriendlyFire == on) return;
            Settings.FriendlyFire = on;
            MarkDirty();
        }

        public void SetSeedText(string text)
        {
            if (IsHost) Options.SeedText = text ?? "";
        }

        public void SetVisibility(LobbyVisibility visibility)
        {
            if (!IsHost) return;
            Options.Visibility = visibility;
            (_transports[SteamSlot] as SteamHostTransport)?.SetVisibility(visibility);
            Raise();
        }

        public void InviteSteamFriends() => (_transports[SteamSlot] as SteamHostTransport)?.InviteFriends();

        /// <summary>Turn the Direct IP listener on/off (restarts it when the port or password changes).</summary>
        public bool SetDirectIp(bool on, int port, string password, out string error)
        {
            error = null;
            if (!IsHost) return false;
            if (_transports[DirectSlot] != null)
            {
                _scratch.Clear();
                _scratch.AddRange(_remotes.Values.Where(r => r.Peer.Transport == DirectSlot));
                foreach (var r in _scratch) Drop(r, "The host changed Direct IP settings.", false);
                _transports[DirectSlot].Dispose();
                _transports[DirectSlot] = null;
            }
            Options.DirectIp = false;
            Options.Port = port;
            Options.Password = password ?? "";
            if (on)
            {
                if (port < 1024 || port > 65535)
                    error = "Port must be between 1024 and 65535.";
                else
                    try
                    {
                        _transports[DirectSlot] = new DirectHostTransport(DirectSlot, port, Options.Password, DiscoveryReply);
                        Options.DirectIp = true;
                        Plugin.Log.LogInfo($"Direct IP listening on port {port}{(Options.Password.Length > 0 ? " (password set)" : "")}.");
                    }
                    catch (Exception e)
                    {
                        error = e.Message;
                    }
            }
            Status = error ?? "";
            Raise();
            return error == null;
        }

        byte[] DiscoveryReply() =>
            LanDiscovery.BuildReply(Players[0].Name, Players.Count, Phase, Options.Password.Length > 0, Options.Port);

        public void Kick(ushort playerId)
        {
            if (!IsHost) return;
            var r = _remotes.Values.FirstOrDefault(x => x.Player != null && x.Player.Id == playerId);
            if (r != null) Drop(r, "You were removed by the host.", true);
        }

        // ================= client =================

        void FromHost(Msg type, ref NetReader rd)
        {
            if (!Joined && type != Msg.Welcome && type != Msg.Disconnect && type != Msg.Ping && type != Msg.AddonMismatch
                && !(Resolving && (type == Msg.Pong || type == Msg.AddonFiles || type == Msg.AddonChunk)))
                throw new ProtocolException("message before welcome");

            switch (type)
            {
                case Msg.Welcome:
                {
                    if (Joined) throw new ProtocolException("duplicate welcome");
                    LocalId = rd.U16();
                    int n = rd.U16();
                    if (n > Protocol.MaxGuns) throw new ProtocolException("too many guns");
                    var guns = new List<string>(n);
                    for (int i = 0; i < n; i++)
                        guns.Add(Protocol.SanitizeReason(rd.Str(Protocol.MaxGunNameBytes)));
                    var hostSettings = new int[ModifierSync.Keys.Length];
                    for (int i = 0; i < hostSettings.Length; i++) hostSettings[i] = rd.I32();
                    var hostOrder = Addons.ReadOrder(ref rd);
                    rd.End();
                    _awaitingHelloReply = false;
                    // Use the host's mod settings from now on (put back when the session ends), and rebuild the gun
                    // list with them: the level built at launch may predate the add-on imports or other settings.
                    // The same add-ons may have loaded in another order here (Workshop items arrive after local
                    // mods, fetched mods last): take the host's, which decides the order of guns and level tiles.
                    ModifierSync.Apply(hostSettings);
                    ModifierSync.ApplyLive();
                    Addons.MatchOrder(hostOrder);
                    Addons.RefreshGunList();
                    // Same add-ons, but every gun must also be at the same place in the list (shots, pickups and
                    // gear refer to guns by position): refuse rather than desync if the load order differs.
                    var mine = RunStarter.LocalGunNames().Select(Protocol.SanitizeReason).ToList();
                    if (mine.Count > 0 && !mine.SequenceEqual(guns))
                    {
                        Plugin.Log.LogWarning($"Gun lists differ: host has {guns.Count}, this game has {mine.Count} (or a different order).");
                        Close("Your guns don't match the host's (different add-ons or load order). Restart Receiver and try again.");
                        break;
                    }
                    _gunNames = guns;
                    Joined = true;
                    bool wasResolving = Resolving;
                    Resolving = false;
                    AddonGate.Clear();
                    Status = "";
                    Plugin.Log.LogInfo("Joined the session.");
                    Skins.SkinSync.OnJoined(this);
                    if (wasResolving) JoinedAfterAddons?.Invoke();
                    Raise();
                    break;
                }
                case Msg.Snapshot:
                    ReadSnapshot(ref rd);
                    break;
                case Msg.StartRun:
                {
                    _runId = rd.U32();
                    int seed = rd.I32();
                    var settings = ReadSettings(ref rd);
                    string gun = rd.Str(Protocol.MaxGunNameBytes);
                    int n = rd.U16();
                    if (n > Protocol.MaxGuns) throw new ProtocolException("too many run guns");
                    var set = new List<int>(n);
                    for (int i = 0; i < n; i++)
                    {
                        int g = rd.U16();
                        if (g >= _gunNames.Count) throw new ProtocolException("bad run gun");
                        set.Add(g);
                    }
                    var modifiers = new int[ModifierSync.Keys.Length];
                    for (int i = 0; i < modifiers.Length; i++) modifiers[i] = rd.I32();
                    string scene = rd.Str(SceneCatalog.MaxNameBytes);
                    rd.End();
                    // Only the main level or a scene the game's own menu offers, and only one this game has.
                    if (!SceneCatalog.IsAllowed(scene))
                    {
                        Plugin.Log.LogWarning($"The host started a run in scene '{Protocol.SanitizeReason(scene)}', which this game can't load.");
                        Close("The host went to a level this game doesn't have.");
                        break;
                    }
                    _runScene = scene;
                    ModifierSync.Apply(modifiers);
                    RunStarter.Begin(seed, gun, settings, set, scene);
                    break;
                }
                case Msg.Disconnect:
                {
                    string reason = Protocol.SanitizeReason(rd.Str(Protocol.MaxReasonBytes));
                    rd.End();
                    Close(reason.Length > 0 ? reason : "Disconnected by the host.");
                    break;
                }
                case Msg.Ping:
                {
                    uint stamp = rd.U32();
                    rd.End();
                    Begin(Msg.Pong).U32(stamp);
                    Send(_host);
                    break;
                }
                case Msg.Pong:
                {
                    uint stamp = rd.U32();
                    rd.End();
                    float rtt = _time - stamp / 1000f;
                    if (rtt >= 0f && rtt < 10f) RoundTrip = RoundTrip < 0f ? rtt : RoundTrip + (rtt - RoundTrip) * 0.25f;
                    break;
                }
                case Msg.Skin:
                    Skins.SkinSync.ClientReceive(this, ref rd);
                    break;
                case Msg.AddonMismatch:
                {
                    if (Joined) throw new ProtocolException("add-on mismatch after welcome");
                    var missing = Addons.Read(ref rd);
                    var extra = Addons.Read(ref rd);
                    rd.End();
                    _awaitingHelloReply = false;
                    AddonGate.Set(missing, extra);
                    Resolving = true; // the host keeps the connection open while this is sorted out
                    Status = "Your add-ons don't match the host's.";
                    Plugin.Log.LogInfo($"The host's add-ons differ: missing {missing.Count}, extra {extra.Count}"
                                       + $" ({missing.Count(a => !a.OnWorkshop) + extra.Count(a => !a.OnWorkshop)} not on the Workshop).");
                    AddonsDiffer?.Invoke();
                    Raise();
                    break;
                }
                case Msg.AddonFiles:
                    ModFetch.OnFiles(ref rd);
                    break;
                case Msg.AddonChunk:
                    ModFetch.OnChunk(ref rd);
                    break;
                case Msg.PlayerStates:
                case Msg.Shot:
                case Msg.RobotStates:
                case Msg.RobotDamage:
                case Msg.RobotShot:
                case Msg.Shocked:
                case Msg.LifeStates:
                case Msg.Revive:
                case Msg.Gear:
                case Msg.ItemTaken:
                case Msg.ItemSpawned:
                case Msg.Sound:
                case Msg.Won:
                case Msg.Cheat:
                case Msg.DemonSpawn:
                case Msg.DemonGone:
                case Msg.Projectile:
                case Msg.ProjectileRest:
                case Msg.Clock:
                case Msg.JoinState:
                    if (Phase == LobbyPhase.InRun)
                        World.WorldSync.ClientReceive(this, type, ref rd);
                    break;
                case Msg.Modifiers:
                {
                    var values = new int[ModifierSync.Keys.Length];
                    for (int i = 0; i < values.Length; i++) values[i] = rd.I32();
                    rd.End();
                    ModifierSync.Apply(values);
                    ModifierSync.ApplyLive();
                    break;
                }
                default:
                    throw new ProtocolException("unexpected message " + (byte)type);
            }
        }

        void ReadSnapshot(ref NetReader rd)
        {
            byte phase = rd.U8();
            if (phase > (byte)LobbyPhase.InRun) throw new ProtocolException("bad phase");
            byte countdown = rd.U8();
            if (countdown > 10) throw new ProtocolException("bad countdown");
            var settings = ReadSettings(ref rd);
            int n = rd.U16();
            if (n > Protocol.MaxPlayersInSnapshot) throw new ProtocolException("too many players");
            var list = new List<LobbyPlayer>(n);
            for (int i = 0; i < n; i++)
            {
                var p = new LobbyPlayer();
                p.Id = rd.U16();
                p.Name = Protocol.SanitizeName(rd.Str(Protocol.MaxNameBytes));
                p.Ready = rd.Bool();
                p.GunPick = rd.I16();
                p.IsHost = rd.Bool();
                if (p.GunPick < -1 || p.GunPick >= _gunNames.Count) p.GunPick = -1;
                list.Add(p);
            }
            rd.End();

            var previous = Phase;
            Phase = (LobbyPhase)phase;
            CountdownSeconds = countdown;
            Settings.WeaponChoice = settings.WeaponChoice;
            Settings.FriendlyFire = settings.FriendlyFire;
            Players.Clear();
            Players.AddRange(list);
            if (previous == LobbyPhase.InRun && Phase == LobbyPhase.Waiting)
                ReturnedToWaitingRoom?.Invoke();
            Raise();
        }

        void ClientTick()
        {
            if (_connect != null)
            {
                TryConnect(); // still waiting for this game's add-ons to load
                return;
            }
            if (!_hostConnected)
            {
                if (_time - _connectStartedAt > Protocol.ConnectTimeout) Close("Couldn't reach the host.");
                return;
            }
            // While add-ons are being sorted out there's no deadline (the host has its own); a Hello must be answered.
            if (!Joined && _awaitingHelloReply && _time - _helloSentAt > Protocol.HandshakeTimeout) { Close("The host didn't answer."); return; }
            if (_time - _lastHeard > Protocol.PeerTimeout) { Close("Lost connection to the host."); return; }
            if (_time >= _nextPing)
            {
                _nextPing = _time + Protocol.PingInterval;
                Begin(Msg.Ping).U32((uint)(_time * 1000f));
                Send(_host);
            }
        }

        // ================= local player actions =================

        public void SetReady(bool ready)
        {
            if (Phase == LobbyPhase.InRun || !Joined) return;
            if (IsHost)
            {
                var me = Players[0];
                if (me.Ready == ready) return;
                me.Ready = ready;
                MarkDirty();
                EvaluateReady();
                return;
            }
            Begin(Msg.SetReady).Bool(ready);
            Send(_host);
        }

        public void SetGunPick(int index)
        {
            if (Phase == LobbyPhase.InRun || !Joined) return;
            short pick = (short)Mathf.Clamp(index, -1, _gunNames.Count - 1);
            if (IsHost)
            {
                Players[0].GunPick = pick;
                MarkDirty();
                return;
            }
            Begin(Msg.SetGunPick).I16(pick);
            Send(_host);
        }

        // ================= helpers =================

        NetWriter Begin(Msg type)
        {
            _w.Reset();
            _w.U8((byte)type);
            return _w;
        }

        void Send(PeerId peer, Channel channel = Channel.Reliable) =>
            _transports[peer.Transport]?.Send(peer, channel, _w.Buffer, _w.Length);

        // ---- world sync sends (the world code owns its own writers) ----

        /// <summary>Client: send to the host.</summary>
        internal void SendToHost(NetWriter w, Channel channel)
        {
            if (!IsHost && _hostConnected && Joined)
                _transports[_host.Transport]?.Send(_host, channel, w.Buffer, w.Length);
        }

        /// <summary>Host: send to every joined client except <paramref name="except"/>.</summary>
        internal void SendToClients(NetWriter w, Channel channel, int except = -1)
        {
            if (!IsHost) return;
            foreach (var r in _remotes.Values)
                if (r.Player != null && r.Player.Id != except)
                    _transports[r.Peer.Transport]?.Send(r.Peer, channel, w.Buffer, w.Length);
        }

        /// <summary>Host: send to one client by player id.</summary>
        internal void SendToPlayer(ushort playerId, NetWriter w, Channel channel)
        {
            if (IsHost && _byPlayer.TryGetValue(playerId, out var r))
                _transports[r.Peer.Transport]?.Send(r.Peer, channel, w.Buffer, w.Length);
        }

        static void WriteSettings(NetWriter w, SharedSettings s)
        {
            w.Bool(s.WeaponChoice);
            w.Bool(s.FriendlyFire);
        }

        static SharedSettings ReadSettings(ref NetReader rd) =>
            new SharedSettings { WeaponChoice = rd.Bool(), FriendlyFire = rd.Bool() };

        void Raise() => Changed?.Invoke();

        static string Who(Remote r) => r.Player != null ? r.Player.Name : "a connecting player";

        public static string LocalName()
        {
            string name = null;
            if (SteamP2PTransport.Available)
                name = SteamFriends.GetPersonaName();
            if (string.IsNullOrEmpty(name))
                name = Plugin.PlayerName.Value;
            return Protocol.SanitizeName(name);
        }
    }
}
