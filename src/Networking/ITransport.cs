using System;
using System.Collections.Generic;

namespace SeapowerMultiplayer.Transport
{
    public enum TransportDelivery { Unreliable, Reliable, ReliableOrdered }

    public interface ITransport
    {
        bool IsConnected { get; }
        int ConnectedPeerCount { get; }
        int RttMs { get; }
        bool LastSendFailed { get; }
        IReadOnlyCollection<int> PeerIds { get; }

        void Start(bool asHost);
        void Stop();
        void Poll();

        /// <summary>Disconnect all connected peers but keep the transport alive
        /// (host keeps listening). Used to refuse incompatible peers.</summary>
        void DisconnectPeers();
        void DisconnectPeer(int peerId, string reason);

        void SendToServer(byte[] data, int length, TransportDelivery delivery);
        void SendToClient(int peerId, byte[] data, int length, TransportDelivery delivery);
        void BroadcastToClients(byte[] data, int length, TransportDelivery delivery);

        event Action<int, byte[], int> OnDataReceived;
        event Action<int> OnPeerConnected;
        event Action<int> OnPeerDisconnected;
    }
}
