using System;
using System.Collections.Generic;
using ReceiverMultiplayer.Session;
using Steamworks;

namespace ReceiverMultiplayer.Net
{
    /// <summary>
    /// Shared Steam P2P plumbing (the game's Steamworks.NET 13 / SDK 1.46: ISteamNetworking with relay fallback).
    /// Packets are only accepted from peers the subclass approves: lobby members for the host, the lobby owner
    /// for a client. Everything else is dropped unread.
    /// </summary>
    abstract class SteamP2PTransport : ITransport
    {
        const uint MaxPacket = Protocol.MaxReliableBytes;
        const int ReadBudgetPerPoll = 4096;

        static byte[] _recv; // shared: transports are polled on the main thread only

        protected readonly byte Index;
        protected readonly HashSet<ulong> Accepted = new HashSet<ulong>();
        readonly Queue<Action<INetSink>> _pending = new Queue<Action<INetSink>>();
        Callback<P2PSessionRequest_t> _sessionRequest;
        Callback<P2PSessionConnectFail_t> _connectFail;

        public string Kind => "Steam";
        public static bool Available => SteamManager.Initialized;

        protected SteamP2PTransport(byte index)
        {
            Index = index;
            if (_recv == null) _recv = new byte[MaxPacket];
            _sessionRequest = Callback<P2PSessionRequest_t>.Create(OnSessionRequest);
            _connectFail = Callback<P2PSessionConnectFail_t>.Create(OnConnectFail);
            SteamNetworking.AllowP2PPacketRelay(true);
        }

        protected abstract bool MayTalkTo(CSteamID id);

        protected PeerId Peer(ulong steamId) => new PeerId(Index, steamId);
        protected void Enqueue(Action<INetSink> evt) => _pending.Enqueue(evt);

        protected virtual void OnSessionRequest(P2PSessionRequest_t req)
        {
            if (MayTalkTo(req.m_steamIDRemote))
                SteamNetworking.AcceptP2PSessionWithUser(req.m_steamIDRemote);
        }

        void OnConnectFail(P2PSessionConnectFail_t fail)
        {
            ulong id = fail.m_steamIDRemote.m_SteamID;
            if (Accepted.Remove(id))
                Enqueue(s => s.OnPeerDisconnected(Peer(id), "Steam connection failed"));
        }

        public void Poll(INetSink sink)
        {
            while (_pending.Count > 0)
                _pending.Dequeue()(sink);
            Read(sink, Channel.Reliable);
            Read(sink, Channel.Unreliable);
        }

        void Read(INetSink sink, Channel channel)
        {
            for (int budget = ReadBudgetPerPoll; budget > 0; budget--)
            {
                if (!SteamNetworking.IsP2PPacketAvailable(out uint size, (int)channel))
                    return;
                if (!SteamNetworking.ReadP2PPacket(_recv, MaxPacket, out uint read, out CSteamID from, (int)channel))
                    return;
                if (size > MaxPacket || read == 0)
                    continue; // oversized: truncated by Steam, discard

                ulong id = from.m_SteamID;
                if (!Accepted.Contains(id))
                {
                    if (!MayTalkTo(from))
                        continue;
                    Accepted.Add(id);
                    sink.OnPeerConnected(Peer(id));
                }
                sink.OnData(Peer(id), channel, _recv, 0, (int)read);
            }
        }

        public void Send(PeerId peer, Channel channel, byte[] data, int length)
        {
            // Plain unreliable, not "no delay": NoDelay throws a packet away whenever Steam is still setting up or
            // adjusting the connection (a relay route change mid-session), while reliable messages wait it out.
            // Poses stopped for seconds that way and players vanished from each other's screens; plain unreliable
            // holds packets through the adjustment and sends them when the connection is back.
            var mode = channel == Channel.Reliable ? EP2PSend.k_EP2PSendReliable : EP2PSend.k_EP2PSendUnreliable;
            SteamNetworking.SendP2PPacket(new CSteamID(peer.Value), data, (uint)length, mode, (int)channel);
        }

        public long QueuedBytes(PeerId peer) =>
            SteamNetworking.GetP2PSessionState(new CSteamID(peer.Value), out P2PSessionState_t state) ? Math.Max(0, state.m_nBytesQueuedForSend) : 0;

        public virtual void Disconnect(PeerId peer, string reason, bool ban)
        {
            Accepted.Remove(peer.Value);
            SteamNetworking.CloseP2PSessionWithUser(new CSteamID(peer.Value));
        }

        public virtual void Dispose()
        {
            foreach (var id in Accepted)
                SteamNetworking.CloseP2PSessionWithUser(new CSteamID(id));
            Accepted.Clear();
            _sessionRequest?.Dispose();
            _connectFail?.Dispose();
            _sessionRequest = null;
            _connectFail = null;
        }

        protected static bool Left(uint stateChange) =>
            (stateChange & (uint)(EChatMemberStateChange.k_EChatMemberStateChangeLeft
                                | EChatMemberStateChange.k_EChatMemberStateChangeDisconnected
                                | EChatMemberStateChange.k_EChatMemberStateChangeKicked
                                | EChatMemberStateChange.k_EChatMemberStateChangeBanned)) != 0;
    }

    /// <summary>
    /// Host side: owns a Steam lobby. Only current lobby members may open P2P sessions; "Friends only" also
    /// requires them to be the host's Steam friend; kicked players stay refused for the session.
    /// </summary>
    sealed class SteamHostTransport : SteamP2PTransport
    {
        readonly HashSet<ulong> _banned = new HashSet<ulong>();
        CallResult<LobbyCreated_t> _created;
        Callback<LobbyChatUpdate_t> _chatUpdate;
        LobbyVisibility _visibility;
        readonly Dictionary<string, string> _data = new Dictionary<string, string>();

        public CSteamID Lobby { get; private set; }
        public bool Ready { get; private set; }
        public string Error { get; private set; }

        public SteamHostTransport(byte index, LobbyVisibility visibility) : base(index)
        {
            _visibility = visibility;
            _chatUpdate = Callback<LobbyChatUpdate_t>.Create(OnChatUpdate);
            _created = CallResult<LobbyCreated_t>.Create(OnCreated);
            _created.Set(SteamMatchmaking.CreateLobby(ToSteam(visibility), Protocol.SteamLobbyMaxMembers));
        }

        static ELobbyType ToSteam(LobbyVisibility v)
        {
            switch (v)
            {
                case LobbyVisibility.Public: return ELobbyType.k_ELobbyTypePublic;
                case LobbyVisibility.InviteOnly: return ELobbyType.k_ELobbyTypePrivate;
                default: return ELobbyType.k_ELobbyTypeFriendsOnly;
            }
        }

        void OnCreated(LobbyCreated_t result, bool ioFailure)
        {
            if (ioFailure || result.m_eResult != EResult.k_EResultOK)
            {
                Error = "Couldn't create the Steam lobby (" + (ioFailure ? "network error" : result.m_eResult.ToString()) + ")";
                Plugin.Log.LogWarning(Error);
                return;
            }
            Lobby = new CSteamID(result.m_ulSteamIDLobby);
            Ready = true;
            SteamMatchmaking.SetLobbyData(Lobby, Protocol.LobbyProtocolKey, Protocol.Build);
            foreach (var kv in _data)
                SteamMatchmaking.SetLobbyData(Lobby, kv.Key, kv.Value);
            Plugin.Log.LogInfo($"Steam lobby created ({_visibility}).");
        }

        public void SetData(string key, string value)
        {
            if (_data.TryGetValue(key, out var old) && old == value) return;
            _data[key] = value;
            if (Ready) SteamMatchmaking.SetLobbyData(Lobby, key, value);
        }

        public void SetVisibility(LobbyVisibility visibility)
        {
            _visibility = visibility;
            if (Ready) SteamMatchmaking.SetLobbyType(Lobby, ToSteam(visibility));
        }

        public void InviteFriends()
        {
            if (Ready) SteamFriends.ActivateGameOverlayInviteDialog(Lobby);
        }

        // A joiner's first packet can beat the lobby membership update; hold its session request until it's a member.
        readonly HashSet<ulong> _pendingRequests = new HashSet<ulong>();

        protected override void OnSessionRequest(P2PSessionRequest_t req)
        {
            if (MayTalkTo(req.m_steamIDRemote))
                SteamNetworking.AcceptP2PSessionWithUser(req.m_steamIDRemote);
            else if (Ready && !_banned.Contains(req.m_steamIDRemote.m_SteamID) && _pendingRequests.Count < 64)
                _pendingRequests.Add(req.m_steamIDRemote.m_SteamID);
        }

        void OnChatUpdate(LobbyChatUpdate_t update)
        {
            if (!Ready || update.m_ulSteamIDLobby != Lobby.m_SteamID)
                return;
            ulong id = update.m_ulSteamIDUserChanged;
            if (!Left(update.m_rgfChatMemberStateChange))
            {
                if (_pendingRequests.Remove(id) && MayTalkTo(new CSteamID(id)))
                    SteamNetworking.AcceptP2PSessionWithUser(new CSteamID(id));
                return;
            }
            _pendingRequests.Remove(id);
            SteamNetworking.CloseP2PSessionWithUser(new CSteamID(id));
            if (Accepted.Remove(id))
                Enqueue(s => s.OnPeerDisconnected(Peer(id), "Left the lobby"));
        }

        protected override bool MayTalkTo(CSteamID id)
        {
            if (!Ready || _banned.Contains(id.m_SteamID) || id == SteamUser.GetSteamID())
                return false;
            if (_visibility == LobbyVisibility.FriendsOnly && !SteamFriends.HasFriend(id, EFriendFlags.k_EFriendFlagImmediate))
                return false;
            int n = SteamMatchmaking.GetNumLobbyMembers(Lobby);
            for (int i = 0; i < n; i++)
                if (SteamMatchmaking.GetLobbyMemberByIndex(Lobby, i) == id)
                    return true;
            return false;
        }

        public override void Disconnect(PeerId peer, string reason, bool ban)
        {
            if (ban) _banned.Add(peer.Value);
            base.Disconnect(peer, reason, ban);
        }

        public static string PersonaName(PeerId peer) => SteamFriends.GetFriendPersonaName(new CSteamID(peer.Value));

        public override void Dispose()
        {
            base.Dispose();
            _chatUpdate?.Dispose();
            _created?.Dispose();
            if (Ready) SteamMatchmaking.LeaveLobby(Lobby);
            Ready = false;
        }
    }

    /// <summary>Client side: joins a lobby and talks only to its owner (the host).</summary>
    sealed class SteamClientTransport : SteamP2PTransport
    {
        readonly CSteamID _lobby;
        CSteamID _host;
        bool _entered;
        CallResult<LobbyEnter_t> _enter;
        Callback<LobbyChatUpdate_t> _chatUpdate;

        public SteamClientTransport(byte index, CSteamID lobby) : base(index)
        {
            _lobby = lobby;
            _chatUpdate = Callback<LobbyChatUpdate_t>.Create(OnChatUpdate);
            _enter = CallResult<LobbyEnter_t>.Create(OnEnter);
            _enter.Set(SteamMatchmaking.JoinLobby(lobby));
        }

        void OnEnter(LobbyEnter_t result, bool ioFailure)
        {
            if (ioFailure || result.m_EChatRoomEnterResponse != (uint)EChatRoomEnterResponse.k_EChatRoomEnterResponseSuccess)
            {
                Enqueue(s => s.OnPeerDisconnected(default, "Couldn't join the Steam lobby"));
                return;
            }
            string build = SteamMatchmaking.GetLobbyData(_lobby, Protocol.LobbyProtocolKey);
            if (build != Protocol.Build)
            {
                SteamMatchmaking.LeaveLobby(_lobby);
                string why = Protocol.Mismatch(Protocol.ReleaseOf(build), Protocol.ModVersion);
                Enqueue(s => s.OnPeerDisconnected(default, why));
                return;
            }
            _entered = true;
            _host = SteamMatchmaking.GetLobbyOwner(_lobby);
            Accepted.Add(_host.m_SteamID);
            ulong host = _host.m_SteamID;
            Enqueue(s => s.OnPeerConnected(Peer(host)));
        }

        void OnChatUpdate(LobbyChatUpdate_t update)
        {
            if (!_entered || update.m_ulSteamIDLobby != _lobby.m_SteamID || update.m_ulSteamIDUserChanged != _host.m_SteamID)
                return;
            if (Left(update.m_rgfChatMemberStateChange) && Accepted.Remove(_host.m_SteamID))
            {
                ulong host = _host.m_SteamID;
                Enqueue(s => s.OnPeerDisconnected(Peer(host), "The host left"));
            }
        }

        protected override bool MayTalkTo(CSteamID id) => _entered && id == _host;

        public override void Dispose()
        {
            base.Dispose();
            _chatUpdate?.Dispose();
            _enter?.Dispose();
            SteamMatchmaking.LeaveLobby(_lobby);
        }
    }

    /// <summary>Finds joinable mod lobbies: public ones via the lobby list, friends-only ones via friends' game info.</summary>
    sealed class SteamLobbyBrowser : IDisposable
    {
        public struct Entry
        {
            public CSteamID Lobby;
            public string Name;
            public int Members;
            public string State;
            public bool Friend;
        }

        public readonly List<Entry> Results = new List<Entry>();
        public bool Busy { get; private set; }
        public event Action Updated;

        readonly HashSet<ulong> _pendingFriendLobbies = new HashSet<ulong>();
        CallResult<LobbyMatchList_t> _list;
        Callback<LobbyDataUpdate_t> _data;

        public SteamLobbyBrowser()
        {
            _list = CallResult<LobbyMatchList_t>.Create(OnList);
            _data = Callback<LobbyDataUpdate_t>.Create(OnData);
        }

        public void Refresh()
        {
            Results.Clear();
            _pendingFriendLobbies.Clear();
            Busy = true;
            SteamMatchmaking.AddRequestLobbyListStringFilter(Protocol.LobbyProtocolKey, Protocol.Build, ELobbyComparison.k_ELobbyComparisonEqual);
            SteamMatchmaking.AddRequestLobbyListDistanceFilter(ELobbyDistanceFilter.k_ELobbyDistanceFilterWorldwide);
            SteamMatchmaking.AddRequestLobbyListResultCountFilter(100);
            _list.Set(SteamMatchmaking.RequestLobbyList());

            int friends = SteamFriends.GetFriendCount(EFriendFlags.k_EFriendFlagImmediate);
            for (int i = 0; i < friends; i++)
            {
                var friend = SteamFriends.GetFriendByIndex(i, EFriendFlags.k_EFriendFlagImmediate);
                if (!SteamFriends.GetFriendGamePlayed(friend, out FriendGameInfo_t game)) continue;
                if (game.m_gameID.AppID() != SteamScript.RECEIVER1_APP_ID || !game.m_steamIDLobby.IsValid()) continue;
                if (_pendingFriendLobbies.Add(game.m_steamIDLobby.m_SteamID))
                    SteamMatchmaking.RequestLobbyData(game.m_steamIDLobby);
            }
            Updated?.Invoke();
        }

        void OnList(LobbyMatchList_t result, bool ioFailure)
        {
            Busy = false;
            if (!ioFailure)
                for (int i = 0; i < result.m_nLobbiesMatching; i++)
                    Add(SteamMatchmaking.GetLobbyByIndex(i), false);
            Updated?.Invoke();
        }

        void OnData(LobbyDataUpdate_t update)
        {
            if (update.m_ulSteamIDLobby != update.m_ulSteamIDMember || !_pendingFriendLobbies.Remove(update.m_ulSteamIDLobby))
                return;
            if (update.m_bSuccess != 0)
                Add(new CSteamID(update.m_ulSteamIDLobby), true);
            Updated?.Invoke();
        }

        void Add(CSteamID lobby, bool friend)
        {
            if (SteamMatchmaking.GetLobbyData(lobby, Protocol.LobbyProtocolKey) != Protocol.Build)
                return;
            for (int i = 0; i < Results.Count; i++)
                if (Results[i].Lobby == lobby)
                {
                    var e = Results[i];
                    e.Friend |= friend;
                    Results[i] = e;
                    return;
                }
            Results.Add(new Entry
            {
                Lobby = lobby,
                Name = Protocol.SanitizeName(SteamMatchmaking.GetLobbyData(lobby, Protocol.LobbyNameKey)),
                Members = SteamMatchmaking.GetNumLobbyMembers(lobby),
                State = Protocol.SanitizeReason(SteamMatchmaking.GetLobbyData(lobby, Protocol.LobbyStateKey)),
                Friend = friend,
            });
        }

        public void Dispose()
        {
            _list?.Dispose();
            _data?.Dispose();
        }
    }

    /// <summary>Steam overlay invites, friends-list "Join Game" and +connect_lobby launch arguments.</summary>
    static class SteamInvites
    {
        static Callback<GameLobbyJoinRequested_t> _joinRequested;
        static bool _checkedCommandLine;

        public static event Action<CSteamID> JoinRequested;

        public static void Tick()
        {
            if (!SteamP2PTransport.Available) return;
            if (_joinRequested == null)
                _joinRequested = Callback<GameLobbyJoinRequested_t>.Create(r => JoinRequested?.Invoke(r.m_steamIDLobby));
            if (_checkedCommandLine) return;
            _checkedCommandLine = true;
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i + 1 < args.Length; i++)
                if (args[i] == "+connect_lobby" && ulong.TryParse(args[i + 1], out ulong lobby))
                    JoinRequested?.Invoke(new CSteamID(lobby));
        }
    }
}
