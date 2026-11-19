using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using LiteNetLib;
using LiteNetLib.Utils;
using ReceiverMultiplayer.Session;

namespace ReceiverMultiplayer.Net
{
    /// <summary>
    /// Every LiteNetLib NetManager builds a NatPunchModule, which registers its packets with LiteNetLib's
    /// reflection serializer, and that reads System.Runtime.Serialization.IgnoreDataMemberAttribute. Receiver's
    /// Mono doesn't ship System.Runtime.Serialization, so creating any NetManager threw "Could not load file or
    /// assembly 'System.Runtime.Serialization'" and Direct IP (host, join, LAN search) never started. NAT
    /// punch-through is never turned on here (NetManager only uses the module when NatPunchEnabled is set), so
    /// the module is left unbuilt.
    /// </summary>
    [HarmonyLib.HarmonyPatch]
    static class SkipNatPunchModule
    {
        static System.Reflection.MethodBase TargetMethod() =>
            HarmonyLib.AccessTools.Constructor(typeof(NatPunchModule), new[] { typeof(NetManager) });

        static bool Prefix() => false;
    }

    /// <summary>
    /// Direct IP host (LAN, Radmin, Tailscale, Hamachi, port-forwarded). Off unless the host turns it on.
    /// Connection requests must carry the mod key, the exact protocol version and the password (if set);
    /// anything else is refused before a peer exists. Kicked addresses stay refused for the session.
    /// </summary>
    sealed class DirectHostTransport : ITransport
    {
        readonly byte _index;
        readonly EventBasedNetListener _listener = new EventBasedNetListener();
        readonly NetManager _net;
        readonly Dictionary<int, NetPeer> _peers = new Dictionary<int, NetPeer>();
        readonly HashSet<IPAddress> _banned = new HashSet<IPAddress>();
        readonly Dictionary<IPAddress, int> _failures = new Dictionary<IPAddress, int>(); // wrong passwords, per address
        readonly string _password;
        readonly Func<byte[]> _discoveryReply;
        INetSink _sink;

        public string Kind => "Direct IP";
        public int Port { get; }

        public DirectHostTransport(byte index, int port, string password, Func<byte[]> discoveryReply)
        {
            _index = index;
            _password = password ?? "";
            _discoveryReply = discoveryReply;
            Port = port;

            _net = new NetManager(_listener)
            {
                AutoRecycle = true,
                UnconnectedMessagesEnabled = true,
                BroadcastReceiveEnabled = true,
                DisconnectTimeout = (int)(Protocol.PeerTimeout * 1000),
            };
            _listener.ConnectionRequestEvent += OnConnectionRequest;
            _listener.PeerConnectedEvent += p =>
            {
                _peers[p.Id] = p;
                _sink?.OnPeerConnected(Peer(p));
            };
            _listener.PeerDisconnectedEvent += (p, info) =>
            {
                if (_peers.Remove(p.Id))
                    _sink?.OnPeerDisconnected(Peer(p), DirectClientTransport.Describe(info));
            };
            _listener.NetworkReceiveEvent += (p, reader, channel, method) =>
            {
                if (!_peers.ContainsKey(p.Id)) return;
                var seg = reader.GetRemainingBytesSegment();
                _sink?.OnData(Peer(p), method == DeliveryMethod.ReliableOrdered ? Channel.Reliable : Channel.Unreliable,
                    seg.Array, seg.Offset, seg.Count);
            };
            _listener.NetworkReceiveUnconnectedEvent += OnUnconnected;

            if (!_net.Start(port))
                throw new InvalidOperationException($"Port {port} is already in use.");
        }

        PeerId Peer(NetPeer p) => new PeerId(_index, (ulong)p.Id);

        void OnConnectionRequest(ConnectionRequest request)
        {
            var address = request.RemoteEndPoint.Address;
            if (_banned.Contains(address))
            {
                request.Reject(DirectClientTransport.Reason("You were removed from this session."));
                return;
            }
            if (_failures.TryGetValue(address, out int failed) && failed >= Protocol.MaxPasswordFailures)
            {
                request.Reject(DirectClientTransport.Reason("Too many wrong passwords: this session no longer accepts your address."));
                return;
            }
            if (_peers.Count >= Protocol.MaxPlayers)
            {
                request.Reject(DirectClientTransport.Reason("The session is full."));
                return;
            }
            int fromAddress = 0;
            foreach (var p in _peers.Values)
                if (p.Address.Equals(address)) fromAddress++;
            if (fromAddress >= Protocol.MaxConnectionsPerAddress)
            {
                request.Reject(DirectClientTransport.Reason("Too many connections from your address."));
                return;
            }

            string key, password, release;
            ushort version;
            try
            {
                key = request.Data.GetString(32);
                version = request.Data.GetUShort();
            }
            catch
            {
                request.RejectForce();
                return;
            }

            if (key != Protocol.ConnectKey)
            {
                request.RejectForce(); // not our client: no reply
                return;
            }
            if (version != Protocol.Version)
            {
                // Checked before reading further: another version's request may be laid out differently.
                request.Reject(DirectClientTransport.Reason(Protocol.Mismatch(Protocol.ModVersion, "a different version")));
                return;
            }
            try
            {
                release = request.Data.GetString(32);
                password = request.Data.GetString(Protocol.MaxPasswordBytes);
            }
            catch
            {
                request.RejectForce();
                return;
            }
            if (release != Protocol.ModVersion)
            {
                request.Reject(DirectClientTransport.Reason(Protocol.Mismatch(Protocol.ModVersion, Protocol.SanitizeReason(release))));
                return;
            }
            if (_password.Length > 0 && !ConstantTimeEquals(password, _password))
            {
                _failures[address] = failed + 1;
                request.Reject(DirectClientTransport.Reason("Wrong password."));
                return;
            }
            request.Accept();
        }

        void OnUnconnected(IPEndPoint from, NetPacketReader reader, UnconnectedMessageType type)
        {
            // LAN discovery: reply only to an exact probe from this network, with a small fixed-size answer (no
            // amplification, and nothing told to the internet even when the port is forwarded).
            if (type != UnconnectedMessageType.Broadcast || reader.AvailableBytes != Protocol.DiscoveryProbe.Length)
                return;
            if (!LocalNetwork.Contains(from.Address)) return;
            var seg = reader.GetRemainingBytesSegment();
            for (int i = 0; i < Protocol.DiscoveryProbe.Length; i++)
                if (seg.Array[seg.Offset + i] != Protocol.DiscoveryProbe[i])
                    return;
            var reply = _discoveryReply?.Invoke();
            if (reply != null)
                _net.SendUnconnectedMessage(reply, from);
        }

        static bool ConstantTimeEquals(string a, string b)
        {
            var x = Encoding.UTF8.GetBytes(a ?? "");
            var y = Encoding.UTF8.GetBytes(b ?? "");
            int diff = x.Length ^ y.Length;
            for (int i = 0; i < Math.Max(x.Length, y.Length); i++)
                diff |= (i < x.Length ? x[i] : 0) ^ (i < y.Length ? y[i] : 0);
            return diff == 0;
        }

        public void Poll(INetSink sink)
        {
            _sink = sink;
            try { _net.PollEvents(); }
            finally { _sink = null; }
        }

        public void Send(PeerId peer, Channel channel, byte[] data, int length)
        {
            if (_peers.TryGetValue((int)peer.Value, out var p))
                p.Send(data, 0, length, channel == Channel.Reliable ? DeliveryMethod.ReliableOrdered : DeliveryMethod.Sequenced);
        }

        /// <summary>The address a peer connects from (its port changes on every connection), or "" if it's gone.</summary>
        public string AddressOf(PeerId peer) => _peers.TryGetValue((int)peer.Value, out var p) ? p.Address.ToString() : "";

        public long QueuedBytes(PeerId peer) =>
            _peers.TryGetValue((int)peer.Value, out var p) ? (long)p.GetPacketsCountInReliableQueue(0, true) * p.Mtu : 0;

        public void Disconnect(PeerId peer, string reason, bool ban)
        {
            if (!_peers.TryGetValue((int)peer.Value, out var p)) return;
            if (ban) _banned.Add(p.Address);
            _peers.Remove(p.Id);
            p.Disconnect(DirectClientTransport.Reason(reason));
        }

        public void Dispose()
        {
            _peers.Clear();
            _net.Stop(true);
        }
    }

    /// <summary>Direct IP client: one connection to the host.</summary>
    sealed class DirectClientTransport : ITransport
    {
        readonly byte _index;
        readonly EventBasedNetListener _listener = new EventBasedNetListener();
        readonly NetManager _net;
        NetPeer _host;
        INetSink _sink;
        readonly Queue<string> _failures = new Queue<string>();

        public string Kind => "Direct IP";

        public DirectClientTransport(byte index, string address, int port, string password)
        {
            _index = index;
            _net = new NetManager(_listener)
            {
                AutoRecycle = true,
                DisconnectTimeout = (int)(Protocol.PeerTimeout * 1000),
            };
            _listener.PeerConnectedEvent += p =>
            {
                if (p == _host) _sink?.OnPeerConnected(new PeerId(_index, 0));
            };
            _listener.PeerDisconnectedEvent += (p, info) =>
            {
                if (p == _host) _sink?.OnPeerDisconnected(new PeerId(_index, 0), Describe(info));
            };
            _listener.NetworkReceiveEvent += (p, reader, channel, method) =>
            {
                if (p != _host) return;
                var seg = reader.GetRemainingBytesSegment();
                _sink?.OnData(new PeerId(_index, 0), method == DeliveryMethod.ReliableOrdered ? Channel.Reliable : Channel.Unreliable,
                    seg.Array, seg.Offset, seg.Count);
            };

            _net.Start();
            var hello = new NetDataWriter();
            hello.Put(Protocol.ConnectKey);
            hello.Put(Protocol.Version);
            hello.Put(Protocol.ModVersion);
            hello.Put(password ?? "");
            try
            {
                _host = _net.Connect(address, port, hello);
            }
            catch (Exception e) when (e is SocketException || e is ArgumentException || e is FormatException)
            {
                _host = null;
            }
            if (_host == null)
                _failures.Enqueue($"Couldn't resolve '{address}'.");
        }

        public void Poll(INetSink sink)
        {
            while (_failures.Count > 0)
                sink.OnPeerDisconnected(default, _failures.Dequeue());
            _sink = sink;
            try { _net.PollEvents(); }
            finally { _sink = null; }
        }

        public void Send(PeerId peer, Channel channel, byte[] data, int length)
        {
            _host?.Send(data, 0, length, channel == Channel.Reliable ? DeliveryMethod.ReliableOrdered : DeliveryMethod.Sequenced);
        }

        public long QueuedBytes(PeerId peer) => _host != null ? (long)_host.GetPacketsCountInReliableQueue(0, true) * _host.Mtu : 0;

        public void Disconnect(PeerId peer, string reason, bool ban) => _host?.Disconnect();

        public void Dispose() => _net.Stop(true);

        internal static byte[] Reason(string reason)
        {
            var w = new NetDataWriter();
            w.Put(reason ?? "", Protocol.MaxReasonBytes);
            return w.CopyData();
        }

        internal static string Describe(DisconnectInfo info)
        {
            if (info.AdditionalData != null && info.AdditionalData.AvailableBytes > 0)
            {
                try
                {
                    var text = Protocol.SanitizeReason(info.AdditionalData.GetString(Protocol.MaxReasonBytes));
                    if (text.Length > 0) return text;
                }
                catch { /* fall through to the generic reason */ }
            }
            switch (info.Reason)
            {
                case DisconnectReason.ConnectionFailed: return "Couldn't reach the host.";
                case DisconnectReason.Timeout: return "Connection timed out.";
                case DisconnectReason.ConnectionRejected: return "The host refused the connection.";
                case DisconnectReason.RemoteConnectionClose: return "The host closed the connection.";
                case DisconnectReason.HostUnreachable:
                case DisconnectReason.NetworkUnreachable: return "The host is unreachable.";
                default: return "Disconnected (" + info.Reason + ").";
            }
        }
    }

    /// <summary>Finds Direct IP hosts on the local network (and LAN-like VPNs that pass broadcasts).</summary>
    sealed class LanDiscovery : IDisposable
    {
        public struct Entry
        {
            public IPEndPoint EndPoint;
            public string Name;
            public int Players;
            public LobbyPhase Phase;
            public bool HasPassword;
        }

        const int MaxResults = 64;
        readonly EventBasedNetListener _listener = new EventBasedNetListener();
        readonly NetManager _net;
        public readonly List<Entry> Results = new List<Entry>();
        public event Action Updated;

        public LanDiscovery()
        {
            _net = new NetManager(_listener) { UnconnectedMessagesEnabled = true, AutoRecycle = true };
            _listener.NetworkReceiveUnconnectedEvent += OnReply;
            _net.Start();
        }

        public void Probe(int port)
        {
            Results.Clear();
            var w = new NetDataWriter();
            w.Put(Protocol.DiscoveryProbe);
            _net.SendBroadcast(w, port);
            Updated?.Invoke();
        }

        public void Poll() => _net.PollEvents();

        void OnReply(IPEndPoint from, NetPacketReader reader, UnconnectedMessageType type)
        {
            if (type != UnconnectedMessageType.BasicMessage || reader.AvailableBytes > 512) return;
            try
            {
                var seg = reader.GetRemainingBytesSegment();
                var r = new NetReader(seg.Array, seg.Offset, seg.Count);
                foreach (byte b in Protocol.DiscoveryReply)
                    if (r.U8() != b) return;
                var entry = new Entry
                {
                    Name = Protocol.SanitizeName(r.Str(Protocol.MaxNameBytes)),
                    Players = r.U16(),
                    Phase = (LobbyPhase)Math.Min(r.U8(), (byte)LobbyPhase.InRun),
                    HasPassword = r.Bool(),
                };
                int port = r.U16();
                r.End();
                entry.EndPoint = new IPEndPoint(from.Address, port);
                int known = Results.FindIndex(e => e.EndPoint.Equals(entry.EndPoint));
                if (known >= 0) Results[known] = entry;
                else if (Results.Count < MaxResults) Results.Add(entry);
                else return;
                Updated?.Invoke();
            }
            catch (ProtocolException)
            {
                // ignore malformed replies
            }
        }

        public static byte[] BuildReply(string hostName, int players, LobbyPhase phase, bool hasPassword, int port)
        {
            var w = new NetWriter(64);
            foreach (byte b in Protocol.DiscoveryReply) w.U8(b);
            w.Str(hostName, Protocol.MaxNameBytes);
            w.U16((ushort)Math.Min(players, ushort.MaxValue));
            w.U8((byte)phase);
            w.Bool(hasPassword);
            w.U16((ushort)port);
            var bytes = new byte[w.Length];
            Buffer.BlockCopy(w.Buffer, 0, bytes, 0, w.Length);
            return bytes;
        }

        public void Dispose() => _net.Stop();
    }

    /// <summary>Is an address on this computer's own networks: private ranges, or the subnet of one of its adapters (Radmin, Hamachi)?</summary>
    static class LocalNetwork
    {
        static (uint net, uint mask)[] _subnets = new (uint, uint)[0];
        static DateTime _readAt = DateTime.MinValue;

        public static bool Contains(IPAddress a)
        {
            if (a == null) return false;
            if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
            if (IPAddress.IsLoopback(a)) return true;
            if (a.AddressFamily == AddressFamily.InterNetworkV6)
                return a.IsIPv6LinkLocal || a.IsIPv6SiteLocal || (a.GetAddressBytes()[0] & 0xFE) == 0xFC; // fc00::/7
            if (a.AddressFamily != AddressFamily.InterNetwork) return false;
            uint ip = ToUInt(a);
            if ((ip >> 24) == 10 || (ip >> 20) == 0xAC1 || (ip >> 16) == 0xC0A8 || (ip >> 16) == 0xA9FE || (ip >> 22) == 0x191)
                return true; // 10/8, 172.16/12, 192.168/16, 169.254/16, 100.64/10
            if ((DateTime.UtcNow - _readAt).TotalSeconds > 30) Refresh();
            foreach (var (net, mask) in _subnets)
                if ((ip & mask) == net) return true;
            return false;
        }

        static void Refresh()
        {
            _readAt = DateTime.UtcNow;
            var found = new List<(uint, uint)>();
            try
            {
                foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                    foreach (var u in nic.GetIPProperties().UnicastAddresses)
                    {
                        if (u.Address.AddressFamily != AddressFamily.InterNetwork || u.IPv4Mask == null) continue;
                        uint mask = ToUInt(u.IPv4Mask);
                        if ((mask >> 24) != 0xFF) continue; // a /8 or narrower: never "everything"
                        found.Add((ToUInt(u.Address) & mask, mask));
                    }
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning($"Couldn't read this computer's networks ({e.Message}); LAN search answers private addresses only."); }
            _subnets = found.ToArray();
        }

        static uint ToUInt(IPAddress a)
        {
            var b = a.GetAddressBytes();
            return (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]);
        }
    }
}
