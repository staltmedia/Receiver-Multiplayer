using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ReceiverMultiplayer.Net;

namespace ReceiverMultiplayer.Session
{
    /// <summary>Host: the non-Workshop mods one connecting player was told it lacks, and what it has fetched.</summary>
    sealed class HostShares
    {
        public readonly HashSet<string> Offered = new HashSet<string>(); // keys of the host's mods this player may fetch
        public readonly List<ModFolderInfo> Slots = new List<ModFolderInfo>();
        public byte[] Buffer;
        // Pieces asked for while the connection still had plenty queued: sent as it drains, never piled up in memory.
        public readonly Queue<(int slot, int file, uint offset, int length)> Waiting = new Queue<(int, int, uint, int)>();
    }

    /// <summary>
    /// Joining with different add-ons. The host doesn't turn the player away: it says what differs and keeps the
    /// connection open (not in the lobby: no player entry, no world traffic) while the player sorts it out. Workshop
    /// add-ons go through Steam; the host's other mods are sent straight from the host, file by file, a piece at a
    /// time, only the ones it reported missing and only from its own loaded mod folders (the player names a mod by
    /// folder and hash, never a path). When nothing differs any more the player says Hello again.
    /// </summary>
    sealed partial class NetSession
    {
        // ---- client ----

        /// <summary>Client: the host reported different add-ons and is keeping the connection open meanwhile.</summary>
        public bool Resolving { get; private set; }
        /// <summary>Client: the host reported different add-ons (shown as one or two windows).</summary>
        public event Action AddonsDiffer;
        /// <summary>Client: joined after sorting out add-ons.</summary>
        public event Action JoinedAfterAddons;

        float _helloSentAt;
        bool _awaitingHelloReply;

        /// <summary>Client: add-ons now match what the host reported: ask again on the same connection.</summary>
        public void RetryJoin()
        {
            if (IsHost || Joined || !Resolving || !_hostConnected || _awaitingHelloReply) return;
            SendHello();
            Addons.LogLocal("joining again");
            Status = "Checking your add-ons with the host…";
            Raise();
        }

        /// <summary>Client: a request to the host while add-ons are sorted out (before the Welcome).</summary>
        internal void SendWhileResolving(NetWriter w)
        {
            if (!IsHost && _hostConnected && Resolving && !Joined)
                _transports[_host.Transport]?.Send(_host, Channel.Reliable, w.Buffer, w.Length);
        }

        // ---- host ----

        void HostHello(Remote r, ref NetReader rd)
        {
            ushort version = rd.U16();
            if (version != Protocol.Version)
            {
                // Checked before reading further: another version's Hello may be laid out differently.
                Drop(r, Protocol.Mismatch(Protocol.ModVersion, "a different version"), false);
                return;
            }
            string release = rd.Str(32);
            if (release != Protocol.ModVersion)
            {
                Drop(r, Protocol.Mismatch(Protocol.ModVersion, Protocol.SanitizeReason(release)), false);
                return;
            }
            string name = rd.Str(Protocol.MaxNameBytes);
            var theirAddons = Addons.Read(ref rd);
            rd.End();
            // Each Hello while sorting out add-ons gets the full list of differences back: asking over and over is abuse.
            if (r.Resolving && _time - r.LastHello < 2f && (r.Violations += 20) > Protocol.MaxViolations)
            {
                Drop(r, "Too much network traffic.", false);
                return;
            }
            r.LastHello = _time;
            if (_waitingForAddons)
            {
                Drop(r, "The host is still loading add-ons. Try again in a moment.", false);
                return;
            }
            r.LastRequest = _time;
            // With the host's mods off, nobody's add-ons are used (the host's mod settings apply to everyone),
            // so there's nothing to match. Otherwise every gun, tape and tile add-on must be the same.
            var missing = new List<Addon>();
            var extra = new List<Addon>();
            if (ModManager.IsModsEnabled())
                Addons.Diff(Addons.Local(), theirAddons, out missing, out extra);
            if (missing.Count > 0 || extra.Count > 0)
            {
                Plugin.Log.LogInfo($"A player's add-ons don't match (missing {missing.Count}, extra {extra.Count}); waiting while they sort it out.");
                var w = Begin(Msg.AddonMismatch);
                Addons.Write(w, missing);
                Addons.Write(w, extra);
                Send(r.Peer);
                if (!r.Resolving && _remotes.Values.Count(x => x.Resolving) >= Protocol.MaxResolvingPeers)
                {
                    Drop(r, "Too many players are fetching add-ons from the host right now. Try again in a minute.", false);
                    return;
                }
                r.Resolving = true;
                if (r.Shares == null) r.Shares = new HostShares();
                foreach (var a in missing)
                    if (!a.OnWorkshop && a.Shareable) r.Shares.Offered.Add(a.Key);
                return;
            }
            if (r.Peer.Transport == SteamSlot)
                name = SteamHostTransport.PersonaName(r.Peer); // Steam's name, not the client's claim
            Accept(r, Protocol.SanitizeName(name));
        }

        /// <summary>Host: the files of one of its non-Workshop mods the player lacks (or why it can't have them).</summary>
        void HostAddonFiles(Remote r, ref NetReader rd)
        {
            string folder = rd.Str(Addons.MaxTextBytes);
            string hash = rd.Str(LocalMods.HashChars);
            rd.End();
            r.LastRequest = _time;
            string key = "l" + folder.ToLowerInvariant() + ":" + hash;

            string refusal = null;
            ModFolderInfo info = null;
            var mine = r.Shares != null && r.Shares.Offered.Contains(key)
                ? Addons.Local().FirstOrDefault(a => !a.OnWorkshop && a.Key == key)
                : null;
            if (mine == null) refusal = "the host doesn't have that version loaded";
            else
            {
                info = LocalMods.Read(mine.Dir);
                if (info.Hash != hash) refusal = "the host's copy changed since you joined";
                else if (info.NotShareable != null) refusal = info.NotShareable;
                else if (r.Shares.Slots.Count >= 255 && !r.Shares.Slots.Contains(info)) refusal = "too many requests";
            }

            var w = Begin(Msg.AddonFiles);
            w.Str(folder, Addons.MaxTextBytes);
            w.Str(hash, LocalMods.HashChars);
            if (refusal != null)
            {
                w.Bool(false);
                w.Str(refusal, Protocol.MaxReasonBytes);
                Send(r.Peer);
                Plugin.Log.LogInfo($"A joining player asked for mod '{Protocol.SanitizeReason(folder)}': refused ({refusal}).");
                return;
            }
            int slot = r.Shares.Slots.IndexOf(info);
            if (slot < 0) { slot = r.Shares.Slots.Count; r.Shares.Slots.Add(info); }
            w.Bool(true);
            w.U8((byte)slot);
            w.U16((ushort)info.Files.Count);
            foreach (var f in info.Files)
            {
                w.Str(f.Name, LocalMods.MaxNameChars);
                w.U32((uint)(f.Size & 0xFFFFFFFF));
                w.U32((uint)(f.Size >> 32));
                w.Str(f.Sha256, LocalMods.HashChars);
            }
            Send(r.Peer);
            Plugin.Log.LogInfo($"Sending mod '{mine}' ({info.Files.Count} files, {LocalMods.FormatSize(info.Size)}) to a joining player.");
        }

        /// <summary>Host: one piece of one of those files.</summary>
        void HostAddonChunk(Remote r, ref NetReader rd)
        {
            int slot = rd.U8();
            int file = rd.U16();
            uint offset = rd.U32();
            int length = (int)rd.U32();
            rd.End();
            var shares = r.Shares;
            if (shares == null || slot >= shares.Slots.Count) throw new ProtocolException("unknown mod slot");
            var info = shares.Slots[slot];
            if (file >= info.Files.Count) throw new ProtocolException("unknown mod file");
            var f = info.Files[file];
            if (length <= 0 || length > Protocol.ChunkBytes || offset + (long)length > f.Size) throw new ProtocolException("bad mod piece");
            r.LastRequest = _time;
            if (shares.Waiting.Count > 0 || QueuedTo(r) > Protocol.MaxQueuedShareBytes)
            {
                if (shares.Waiting.Count >= 2 * Protocol.ChunkWindow + 2) throw new ProtocolException("too many mod pieces asked for at once");
                shares.Waiting.Enqueue((slot, file, offset, length));
                return;
            }
            SendChunk(r, slot, file, offset, length);
        }

        long QueuedTo(Remote r) => _transports[r.Peer.Transport]?.QueuedBytes(r.Peer) ?? 0;

        /// <summary>Host, every tick: send waiting pieces once the connection has room for them.</summary>
        void ServeWaitingChunks(Remote r)
        {
            var waiting = r.Shares?.Waiting;
            while (waiting != null && waiting.Count > 0 && QueuedTo(r) <= Protocol.MaxQueuedShareBytes && Current == this && _remotes.ContainsKey(r.Peer))
            {
                var (slot, file, offset, length) = waiting.Dequeue();
                SendChunk(r, slot, file, offset, length);
            }
        }

        /// <summary>Who this is for the share allowance: the Steam account, or the address (a Direct IP name is the player's own claim).</summary>
        string ShareKey(Remote r) =>
            _transports[r.Peer.Transport] is DirectHostTransport direct ? "ip:" + direct.AddressOf(r.Peer) : "steam:" + r.Peer.Value;

        readonly Dictionary<string, long> _sharedTo = new Dictionary<string, long>();

        void SendChunk(Remote r, int slot, int file, uint offset, int length)
        {
            var shares = r.Shares;
            var info = shares.Slots[slot];
            var f = info.Files[file];
            string who = ShareKey(r);
            _sharedTo.TryGetValue(who, out long sent);
            if (sent + length > Protocol.MaxShareBytesPerPeer)
            {
                Drop(r, "You've fetched more from the host than any set of mods needs.", false);
                return;
            }

            if (shares.Buffer == null) shares.Buffer = new byte[Protocol.ChunkBytes];
            int read = 0;
            try
            {
                using (var s = new FileStream(Path.Combine(info.Dir, f.Name), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096))
                {
                    if (s.Length != f.Size) throw new IOException("the file changed");
                    s.Seek(offset, SeekOrigin.Begin);
                    while (read < length)
                    {
                        int n = s.Read(shares.Buffer, read, length - read);
                        if (n <= 0) break;
                        read += n;
                    }
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Couldn't read '{f.Name}' to send it: {e.Message}");
                read = 0;
            }
            var w = Begin(Msg.AddonChunk);
            w.U8((byte)slot);
            w.U16((ushort)file);
            w.U32(offset);
            w.U32((uint)(read == length ? read : 0)); // 0: the host couldn't read it
            if (read == length) w.Raw(shares.Buffer, 0, read);
            Send(r.Peer);
            _sharedTo[who] = sent + read; // kept across reconnects for the whole session
        }
    }
}
