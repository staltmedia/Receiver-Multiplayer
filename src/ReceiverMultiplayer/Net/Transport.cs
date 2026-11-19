using System;

namespace ReceiverMultiplayer.Net
{
    enum Channel : byte
    {
        /// <summary>Reliable, ordered: events (lobby, spawns, pickups, mag in/out...).</summary>
        Reliable = 0,
        /// <summary>Unreliable, sequenced: high-rate transforms.</summary>
        Unreliable = 1,
    }

    /// <summary>A remote endpoint, unique across all transports of a session. Opaque to game logic.</summary>
    readonly struct PeerId : IEquatable<PeerId>
    {
        public readonly byte Transport;
        public readonly ulong Value;

        public PeerId(byte transport, ulong value)
        {
            Transport = transport;
            Value = value;
        }

        public bool Equals(PeerId other) => Transport == other.Transport && Value == other.Value;
        public override bool Equals(object obj) => obj is PeerId p && Equals(p);
        public override int GetHashCode() => Value.GetHashCode() ^ (Transport << 24);
        public static bool operator ==(PeerId a, PeerId b) => a.Equals(b);
        public static bool operator !=(PeerId a, PeerId b) => !a.Equals(b);
    }

    /// <summary>Receives transport events. Called only from <see cref="ITransport.Poll"/>, on the main thread.</summary>
    interface INetSink
    {
        void OnPeerConnected(PeerId peer);
        /// <summary>Also raised (with a default peer) when a client's connection attempt fails.</summary>
        void OnPeerDisconnected(PeerId peer, string reason);
        void OnData(PeerId peer, Channel channel, byte[] buffer, int offset, int count);
    }

    /// <summary>
    /// One network backend (Steam P2P, Direct IP). Game logic never sees which one is active.
    /// Topology is a star: clients talk only to the host, never to each other.
    /// </summary>
    interface ITransport : IDisposable
    {
        string Kind { get; }
        void Poll(INetSink sink);
        void Send(PeerId peer, Channel channel, byte[] data, int length);
        /// <summary>Roughly how much sent to this peer hasn't left yet (0 if unknown).</summary>
        long QueuedBytes(PeerId peer);
        /// <summary>Drop a peer. <paramref name="ban"/> refuses it for the rest of the session.</summary>
        void Disconnect(PeerId peer, string reason, bool ban);
    }
}
