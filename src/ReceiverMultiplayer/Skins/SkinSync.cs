using System.Collections.Generic;
using ReceiverMultiplayer.Net;
using ReceiverMultiplayer.Session;
using UnityEngine;

namespace ReceiverMultiplayer.Skins
{
    /// <summary>
    /// Sharing skins. Your skin goes to the host when you join and whenever you change it; the host keeps every
    /// player's skin, passes each one on, and gives a joining player all of them. Images are checked (PNG or JPEG,
    /// ≤ 128 KB, ≤ 512×512) before anything decodes them, a player can change skin at most every few seconds, and
    /// "Other players' skins: hidden" shows everyone as the plain pill.
    /// </summary>
    static class SkinSync
    {
        const float MinSecondsBetweenUpdates = 3f;

        sealed class Entry
        {
            public byte[] Bytes;
            public Texture2D Texture;
            public int Version;
            public float LastUpdate = -100f;
        }

        static readonly Dictionary<ushort, Entry> _skins = new Dictionary<ushort, Entry>();
        static int _versionCounter;

        // ---------------- queries for the pills ----------------

        /// <summary>Changes whenever this player's displayed skin changes (including the show/hide switch).</summary>
        public static int VersionOf(ushort id) =>
            Plugin.ShowOtherSkins.Value && _skins.TryGetValue(id, out var e) ? e.Version : 0;

        public static Texture2D TextureFor(ushort id)
        {
            if (!Plugin.ShowOtherSkins.Value || !_skins.TryGetValue(id, out var e) || e.Bytes == null) return null;
            if (e.Texture == null) e.Texture = SkinStore.Decode(e.Bytes);
            return e.Texture;
        }

        public static void OnShowOthersChanged() => _versionCounter++; // VersionOf flips via the setting itself

        // ---------------- local changes ----------------

        public static void OnLocalChanged()
        {
            var s = NetSession.Current;
            if (s == null || !s.Joined) return;
            if (s.IsHost) Store(s.LocalId, SkinStore.Encoded, broadcast: true, s);
            else SendOwn(s);
        }

        /// <summary>Client: after the host welcomed us.</summary>
        public static void OnJoined(NetSession s)
        {
            if (SkinStore.Encoded != null) SendOwn(s);
        }

        static void SendOwn(NetSession s)
        {
            var bytes = SkinStore.Encoded;
            var w = new NetWriter((bytes?.Length ?? 0) + 8);
            w.U8((byte)Msg.Skin);
            w.U32((uint)(bytes?.Length ?? 0));
            if (bytes != null) w.Raw(bytes, 0, bytes.Length);
            s.SendToHost(w, Channel.Reliable);
        }

        // ---------------- host ----------------

        public static void HostReceive(NetSession s, ushort from, ref NetReader rd)
        {
            int len = (int)rd.U32();
            if (len > SkinImage.MaxBytes) throw new ProtocolException("skin too large");
            var bytes = len == 0 ? null : rd.Bytes(len);
            rd.End();
            if (bytes != null && !SkinImage.Validate(bytes, out string why))
            {
                Plugin.Log.LogWarning($"Ignored a skin that isn't a usable image ({why}).");
                return;
            }
            if (_skins.TryGetValue(from, out var e) && Time.unscaledTime - e.LastUpdate < MinSecondsBetweenUpdates)
                return; // too soon after the last change
            Store(from, bytes, broadcast: true, s);
        }

        /// <summary>Host: a player joined; send them every skin we have (the host's own included).</summary>
        public static void HostSendAllTo(NetSession s, ushort playerId)
        {
            if (SkinStore.Encoded != null) Send(s, s.LocalId, SkinStore.Encoded, playerId);
            foreach (var kv in _skins)
                if (kv.Key != playerId && kv.Key != s.LocalId && kv.Value.Bytes != null)
                    Send(s, kv.Key, kv.Value.Bytes, playerId);
        }

        static void Store(ushort owner, byte[] bytes, bool broadcast, NetSession s)
        {
            if (owner != s.LocalId) Apply(owner, bytes);
            else if (_skins.TryGetValue(owner, out var own)) own.LastUpdate = Time.unscaledTime;
            if (!broadcast) return;
            var w = Build(owner, bytes);
            s.SendToClients(w, Channel.Reliable, owner);
        }

        static void Send(NetSession s, ushort owner, byte[] bytes, ushort to) => s.SendToPlayer(to, Build(owner, bytes), Channel.Reliable);

        static NetWriter Build(ushort owner, byte[] bytes)
        {
            var w = new NetWriter((bytes?.Length ?? 0) + 8);
            w.U8((byte)Msg.Skin);
            w.U16(owner);
            w.U32((uint)(bytes?.Length ?? 0));
            if (bytes != null) w.Raw(bytes, 0, bytes.Length);
            return w;
        }

        // ---------------- client ----------------

        public static void ClientReceive(NetSession s, ref NetReader rd)
        {
            ushort owner = rd.U16();
            int len = (int)rd.U32();
            if (len > SkinImage.MaxBytes) throw new ProtocolException("skin too large");
            var bytes = len == 0 ? null : rd.Bytes(len);
            rd.End();
            if (owner == s.LocalId) return;
            if (bytes != null && !SkinImage.Validate(bytes, out _)) return;
            Apply(owner, bytes);
        }

        // ---------------- shared ----------------

        const int MaxSkins = 64; // a host naming endless made-up players can't fill memory with skins

        static void Apply(ushort owner, byte[] bytes)
        {
            if (!_skins.TryGetValue(owner, out var e))
            {
                if (_skins.Count >= MaxSkins) return;
                _skins[owner] = e = new Entry();
            }
            if (e.Texture != null) Object.Destroy(e.Texture);
            e.Texture = null;
            e.Bytes = bytes;
            e.Version = ++_versionCounter;
            e.LastUpdate = Time.unscaledTime;
        }

        public static void Forget(ushort id)
        {
            if (!_skins.TryGetValue(id, out var e)) return;
            if (e.Texture != null) Object.Destroy(e.Texture);
            _skins.Remove(id);
        }

        public static void Reset()
        {
            foreach (var e in _skins.Values) if (e.Texture != null) Object.Destroy(e.Texture);
            _skins.Clear();
        }
    }
}
