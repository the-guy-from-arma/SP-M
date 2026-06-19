using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using BepInEx.Logging;
using Steamworks;

namespace SeapowerMultiplayer.Transport
{
    public class SteamTransport : ITransport
    {
        private HSteamListenSocket _listenSocket;
        private HSteamNetConnection _connectionToHost;
        private readonly List<HSteamNetConnection> _clientConnections = new();
        private readonly Dictionary<HSteamNetConnection, int> _peerIds = new();
        private readonly Dictionary<int, HSteamNetConnection> _connectionsByPeer = new();
        private readonly Dictionary<int, int> _peerRtt = new();
        private int _nextPeerId = 1;
        private bool _isHost;
        private bool _running;

        private Callback<SteamNetConnectionStatusChangedCallback_t>? _connectionStatusCallback;

        private static ManualLogSource Log => Plugin.Log;

        private const int MaxMessages = 64;
        private readonly IntPtr[] _messagePointers = new IntPtr[MaxMessages];
        private readonly byte[] _receiveBuffer = new byte[512 * 1024]; // 512KB

        // ── Fragmentation ────────────────────────────────────────────────────
        // SteamNetworkingSockets has a ~512KB per-message limit. Session sync
        // messages can exceed this after gameplay (save files grow to ~1MB+).
        // Fragment large reliable messages into chunks under the limit.

        private const int DirectReliableLimit = SteamFragmenter.ChunkPayloadBytes;
        private const int ChunksPerPollPerConnection = 2;
        private const int MaxQueuedBytesPerConnection = 32 * 1024 * 1024;

        private uint _nextFragmentId;

        private readonly Dictionary<ulong, FragmentBuffer> _pendingFragments = new();
        private readonly Dictionary<HSteamNetConnection, Queue<OutboundPacket>> _outbound = new();
        private readonly Dictionary<HSteamNetConnection, int> _outboundBytes = new();
        private long _lastCleanupTicks;
        private long _lastBackpressureLogTicks;

        private sealed class OutboundPacket
        {
            public byte[] Data = Array.Empty<byte>();
            public TransportDelivery Delivery;
        }

        private class FragmentBuffer
        {
            public byte[][] Chunks;
            public int[] ChunkLengths;
            public int ReceivedCount;
            public int TotalLength;
            public long CreatedTicks;

            public FragmentBuffer(int totalChunks)
            {
                Chunks = new byte[totalChunks][];
                ChunkLengths = new int[totalChunks];
                CreatedTicks = DateTime.UtcNow.Ticks;
            }
        }

        /// <summary>Host SteamID is read from SteamLobbyManager when connecting as client.</summary>

        public bool IsConnected => _isHost
            ? _clientConnections.Count > 0
            : _connectionToHost != HSteamNetConnection.Invalid;

        public int ConnectedPeerCount => _isHost ? _clientConnections.Count : (IsConnected ? 1 : 0);
        public IReadOnlyCollection<int> PeerIds => _connectionsByPeer.Keys;
        public int RttMs
        {
            get
            {
                if (_peerRtt.Count == 0) return 0;
                int total = 0;
                foreach (var value in _peerRtt.Values) total += value;
                return total / _peerRtt.Count;
            }
        }
        public bool LastSendFailed { get; private set; }
        public int PendingReliablePackets
        {
            get
            {
                int count = 0;
                foreach (var queue in _outbound.Values) count += queue.Count;
                return count;
            }
        }
        public int PendingReliableBytes
        {
            get
            {
                int count = 0;
                foreach (int bytes in _outboundBytes.Values) count += bytes;
                return count;
            }
        }

        public event Action<int, byte[], int>? OnDataReceived;
        public event Action<int>? OnPeerConnected;
        public event Action<int>? OnPeerDisconnected;

        public void Start(bool asHost)
        {
            _isHost = asHost;

            _connectionStatusCallback = Callback<SteamNetConnectionStatusChangedCallback_t>.Create(OnConnectionStatusChanged);

            if (asHost)
            {
                _listenSocket = SteamNetworkingSockets.CreateListenSocketP2P(0, 0, null);
                Log.LogInfo("[SteamTransport] Listening for P2P connections");
            }
            else
            {
                var hostId = SteamLobbyManager.HostSteamId;
                var identity = new SteamNetworkingIdentity();
                identity.SetSteamID(hostId);
                _connectionToHost = SteamNetworkingSockets.ConnectP2P(ref identity, 0, 0, null);
                Log.LogInfo($"[SteamTransport] Connecting to host {hostId}");
            }

            _running = true;
        }

        public void Stop()
        {
            if (!_running) return;

            if (_isHost)
            {
                foreach (var conn in _clientConnections)
                    SteamNetworkingSockets.CloseConnection(conn, 0, "Host shutting down", false);
                _clientConnections.Clear();

                if (_listenSocket != HSteamListenSocket.Invalid)
                {
                    SteamNetworkingSockets.CloseListenSocket(_listenSocket);
                    _listenSocket = HSteamListenSocket.Invalid;
                }
            }
            else
            {
                if (_connectionToHost != HSteamNetConnection.Invalid)
                {
                    SteamNetworkingSockets.CloseConnection(_connectionToHost, 0, "Client disconnecting", false);
                    _connectionToHost = HSteamNetConnection.Invalid;
                }
            }

            _connectionStatusCallback?.Dispose();
            _connectionStatusCallback = null;
            _peerIds.Clear();
            _connectionsByPeer.Clear();
            _peerRtt.Clear();
            _pendingFragments.Clear();
            _outbound.Clear();
            _outboundBytes.Clear();
            _running = false;
            Log.LogInfo("[SteamTransport] Stopped.");
        }

        public void DisconnectPeers()
        {
            if (!_running) return;

            if (_isHost)
            {
                foreach (var conn in _clientConnections)
                    SteamNetworkingSockets.CloseConnection(conn, 0, "Refused by host", false);
                _clientConnections.Clear();
                // Listen socket stays open - host remains joinable
            }
            else if (_connectionToHost != HSteamNetConnection.Invalid)
            {
                SteamNetworkingSockets.CloseConnection(_connectionToHost, 0, "Disconnecting", false);
                _connectionToHost = HSteamNetConnection.Invalid;
            }
            _peerIds.Clear();
            _connectionsByPeer.Clear();
            _peerRtt.Clear();
            _pendingFragments.Clear();
            _outbound.Clear();
            _outboundBytes.Clear();
            Log.LogInfo("[SteamTransport] Disconnected peers (transport stays up).");
        }

        public void DisconnectPeer(int peerId, string reason)
        {
            if (!_connectionsByPeer.TryGetValue(peerId, out var conn)) return;
            SteamNetworkingSockets.CloseConnection(conn, 0, reason, false);
        }

        public void Poll()
        {
            if (!_running) return;

            if (_isHost)
            {
                foreach (var conn in _clientConnections)
                    ReceiveMessages(conn);
            }
            else if (_connectionToHost != HSteamNetConnection.Invalid)
            {
                ReceiveMessages(_connectionToHost);
            }

            FlushOutbound();
            CleanupStaleFragments();
            UpdateRtt();
        }

        public void SendToServer(byte[] data, int length, TransportDelivery delivery)
        {
            if (_connectionToHost == HSteamNetConnection.Invalid) return;
            SendMessage(_connectionToHost, data, length, delivery);
        }

        public void SendToClient(int peerId, byte[] data, int length, TransportDelivery delivery)
        {
            if (_connectionsByPeer.TryGetValue(peerId, out var conn))
                SendMessage(conn, data, length, delivery);
        }

        public void BroadcastToClients(byte[] data, int length, TransportDelivery delivery)
        {
            foreach (var conn in _clientConnections)
                SendMessage(conn, data, length, delivery);
        }

        private void SendMessage(HSteamNetConnection conn, byte[] data, int length, TransportDelivery delivery)
        {
            LastSendFailed = false;

            bool reliable = delivery != TransportDelivery.Unreliable;
            if (reliable && HasOutboundBacklog(conn))
            {
                LastSendFailed = !EnqueueCopy(conn, data, length, delivery);
                return;
            }

            if (length <= DirectReliableLimit || !reliable)
            {
                EResult result = SendRaw(conn, data, length, delivery);
                if (result != EResult.k_EResultOK)
                {
                    if (reliable && result == EResult.k_EResultLimitExceeded)
                        LastSendFailed = !EnqueueCopy(conn, data, length, delivery);
                    else
                    {
                        Log.LogError($"[SteamTransport] Send failed: {result}, size={length}");
                        LastSendFailed = true;
                    }
                }
                return;
            }

            uint fragmentId = _nextFragmentId++;
            List<byte[]> chunks;
            try
            {
                chunks = SteamFragmenter.Split(data, length, fragmentId);
            }
            catch (Exception ex)
            {
                Log.LogError($"[SteamTransport] Could not fragment {length}-byte message: {ex.Message}");
                LastSendFailed = true;
                return;
            }
            int totalChunks = chunks.Count;

            Log.LogInfo($"[SteamTransport] Queued paced transfer: {length} bytes -> {totalChunks} chunks (id={fragmentId})");

            for (int i = 0; i < totalChunks; i++)
            {
                byte[] chunk = chunks[i];

                if (!EnqueueOwned(conn, chunk, delivery))
                {
                    Log.LogError($"[SteamTransport] Could not queue fragment {i + 1}/{totalChunks} (id={fragmentId}).");
                    LastSendFailed = true;
                    return;
                }
            }
        }

        private bool HasOutboundBacklog(HSteamNetConnection conn)
            => _outbound.TryGetValue(conn, out var queue) && queue.Count > 0;

        private bool EnqueueCopy(HSteamNetConnection conn, byte[] data, int length, TransportDelivery delivery)
        {
            var copy = new byte[length];
            Buffer.BlockCopy(data, 0, copy, 0, length);
            return EnqueueOwned(conn, copy, delivery);
        }

        private bool EnqueueOwned(HSteamNetConnection conn, byte[] data, TransportDelivery delivery)
        {
            int queued = _outboundBytes.TryGetValue(conn, out int bytes) ? bytes : 0;
            if (queued + data.Length > MaxQueuedBytesPerConnection)
            {
                Log.LogError($"[SteamTransport] Reliable queue exceeded {MaxQueuedBytesPerConnection / (1024 * 1024)} MB.");
                return false;
            }

            if (!_outbound.TryGetValue(conn, out var queue))
            {
                queue = new Queue<OutboundPacket>();
                _outbound[conn] = queue;
            }

            queue.Enqueue(new OutboundPacket { Data = data, Delivery = delivery });
            _outboundBytes[conn] = queued + data.Length;
            return true;
        }

        private void FlushOutbound()
        {
            if (_outbound.Count == 0) return;

            var connections = new List<HSteamNetConnection>(_outbound.Keys);
            foreach (var conn in connections)
            {
                if (!_outbound.TryGetValue(conn, out var queue)) continue;

                int sentThisPoll = 0;
                while (queue.Count > 0 && sentThisPoll < ChunksPerPollPerConnection)
                {
                    var packet = queue.Peek();
                    EResult result = SendRaw(conn, packet.Data, packet.Data.Length, packet.Delivery);
                    if (result == EResult.k_EResultOK)
                    {
                        queue.Dequeue();
                        _outboundBytes[conn] -= packet.Data.Length;
                        sentThisPoll++;
                        continue;
                    }

                    if (result == EResult.k_EResultLimitExceeded)
                    {
                        long now = DateTime.UtcNow.Ticks;
                        if (now - _lastBackpressureLogTicks > TimeSpan.TicksPerSecond * 2)
                        {
                            _lastBackpressureLogTicks = now;
                            Log.LogInfo($"[SteamTransport] Backpressure: {_outboundBytes[conn] / 1024} KB remains queued.");
                        }
                        break;
                    }

                    Log.LogError($"[SteamTransport] Queued send failed: {result}; dropping connection queue.");
                    LastSendFailed = true;
                    queue.Clear();
                    _outboundBytes[conn] = 0;
                    break;
                }

                if (queue.Count == 0)
                {
                    _outbound.Remove(conn);
                    _outboundBytes.Remove(conn);
                }
            }
        }

        private unsafe EResult SendRaw(HSteamNetConnection conn, byte[] data, int length, TransportDelivery delivery)
        {
            int flags = delivery switch
            {
                TransportDelivery.Unreliable => Constants.k_nSteamNetworkingSend_Unreliable,
                TransportDelivery.Reliable => Constants.k_nSteamNetworkingSend_Reliable
                                            | Constants.k_nSteamNetworkingSend_NoNagle,
                TransportDelivery.ReliableOrdered => Constants.k_nSteamNetworkingSend_Reliable,
                _ => Constants.k_nSteamNetworkingSend_Reliable,
            };

            fixed (byte* ptr = data)
            {
                return SteamNetworkingSockets.SendMessageToConnection(
                    conn, (IntPtr)ptr, (uint)length, flags, out _);
            }
        }

        private void ReceiveMessages(HSteamNetConnection conn)
        {
            if (!_peerIds.TryGetValue(conn, out int peerId))
                peerId = 0;
            int count = SteamNetworkingSockets.ReceiveMessagesOnConnection(conn, _messagePointers, MaxMessages);

            for (int i = 0; i < count; i++)
            {
                var msg = SteamNetworkingMessage_t.FromIntPtr(_messagePointers[i]);
                int length = msg.m_cbSize;
                byte[] data;
                if (length <= _receiveBuffer.Length)
                {
                    Marshal.Copy(msg.m_pData, _receiveBuffer, 0, length);
                    data = _receiveBuffer;
                }
                else
                {
                    data = new byte[length];
                    Marshal.Copy(msg.m_pData, data, 0, length);
                }

                SteamNetworkingMessage_t.Release(_messagePointers[i]);

                // Check for fragment marker
                if (SteamFragmenter.TryReadHeader(data, length, out _, out _, out _))
                {
                    HandleFragment(peerId, data, length);
                }
                else
                {
                    OnDataReceived?.Invoke(peerId, data, length);
                }
            }
        }

        private void HandleFragment(int peerId, byte[] data, int length)
        {
            if (!SteamFragmenter.TryReadHeader(data, length,
                    out uint fragmentId, out int chunkIndex, out int totalChunks))
            {
                Log.LogWarning("[SteamTransport] Invalid fragment header.");
                return;
            }

            ulong fragmentKey = ((ulong)(uint)peerId << 32) | fragmentId;

            if (totalChunks > 4096)
            {
                Log.LogWarning($"[SteamTransport] Fragment transfer is unreasonably large: {totalChunks} chunks.");
                return;
            }

            if (!_pendingFragments.TryGetValue(fragmentKey, out var buffer))
            {
                buffer = new FragmentBuffer(totalChunks);
                _pendingFragments[fragmentKey] = buffer;
            }
            else if (buffer.Chunks.Length != totalChunks)
            {
                Log.LogWarning($"[SteamTransport] Fragment id collision for id={fragmentId}; restarting transfer.");
                buffer = new FragmentBuffer(totalChunks);
                _pendingFragments[fragmentKey] = buffer;
            }

            int payloadLen = length - SteamFragmenter.HeaderSize;

            // Guard against duplicate chunks
            if (buffer.Chunks[chunkIndex] != null) return;

            buffer.Chunks[chunkIndex] = new byte[payloadLen];
            Buffer.BlockCopy(data, SteamFragmenter.HeaderSize, buffer.Chunks[chunkIndex], 0, payloadLen);
            buffer.ChunkLengths[chunkIndex] = payloadLen;
            buffer.TotalLength += payloadLen;
            buffer.ReceivedCount++;

            if (buffer.ReceivedCount == totalChunks)
            {
                // Reassemble
                byte[] reassembled = new byte[buffer.TotalLength];
                int offset = 0;
                for (int i = 0; i < totalChunks; i++)
                {
                    Buffer.BlockCopy(buffer.Chunks[i], 0, reassembled, offset, buffer.ChunkLengths[i]);
                    offset += buffer.ChunkLengths[i];
                }

                _pendingFragments.Remove(fragmentKey);
                Log.LogInfo($"[SteamTransport] Reassembled fragment id={fragmentId}: {totalChunks} chunks → {buffer.TotalLength} bytes");
                OnDataReceived?.Invoke(peerId, reassembled, buffer.TotalLength);
            }
        }

        private void CleanupStaleFragments()
        {
            long now = DateTime.UtcNow.Ticks;
            // Check every ~5 seconds
            if (now - _lastCleanupTicks < 50_000_000L) return;
            _lastCleanupTicks = now;

            long staleThreshold = TimeSpan.TicksPerMinute;
            List<ulong>? staleIds = null;

            foreach (var kvp in _pendingFragments)
            {
                if (now - kvp.Value.CreatedTicks > staleThreshold)
                {
                    staleIds ??= new List<ulong>();
                    staleIds.Add(kvp.Key);
                }
            }

            if (staleIds != null)
            {
                foreach (var id in staleIds)
                {
                    var buf = _pendingFragments[id];
                    Log.LogWarning($"[SteamTransport] Discarding stale fragment id={id}: {buf.ReceivedCount}/{buf.Chunks.Length} chunks received");
                    _pendingFragments.Remove(id);
                }
            }
        }

        private void UpdateRtt()
        {
            if (_isHost)
            {
                foreach (var conn in _clientConnections)
                    UpdateConnectionRtt(conn);
            }
            else if (_connectionToHost != HSteamNetConnection.Invalid)
                UpdateConnectionRtt(_connectionToHost);
        }

        private void UpdateConnectionRtt(HSteamNetConnection conn)
        {
            SteamNetConnectionRealTimeStatus_t status = default;
            SteamNetConnectionRealTimeLaneStatus_t laneStatus = default;
            var result = SteamNetworkingSockets.GetConnectionRealTimeStatus(conn, ref status, 0, ref laneStatus);
            if (result != EResult.k_EResultOK) return;
            int peerId = _peerIds.TryGetValue(conn, out int id) ? id : 0;
            _peerRtt[peerId] = status.m_nPing;
        }

        private void OnConnectionStatusChanged(SteamNetConnectionStatusChangedCallback_t callback)
        {
            var conn = callback.m_hConn;
            var info = callback.m_info;
            var oldState = callback.m_eOldState;

            Log.LogInfo($"[SteamTransport] Connection status: {oldState} -> {info.m_eState} (peer={info.m_identityRemote.GetSteamID()})");

            switch (info.m_eState)
            {
                case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connecting:
                    if (_isHost)
                    {
                        if (_clientConnections.Count >= 3)
                        {
                            SteamNetworkingSockets.CloseConnection(conn, 0, "Four-player lobby is full", false);
                            break;
                        }
                        var result = SteamNetworkingSockets.AcceptConnection(conn);
                        if (result != EResult.k_EResultOK)
                            Log.LogError($"[SteamTransport] AcceptConnection failed: {result}");
                    }
                    break;

                case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected:
                    if (_isHost)
                    {
                        _clientConnections.Add(conn);
                        int peerId = _nextPeerId++;
                        _peerIds[conn] = peerId;
                        _connectionsByPeer[peerId] = conn;
                        Log.LogInfo($"[SteamTransport] Client connected ({_clientConnections.Count} peers)");
                        OnPeerConnected?.Invoke(peerId);
                    }
                    else
                    {
                        _peerIds[conn] = 0;
                        _connectionsByPeer[0] = conn;
                        Log.LogInfo("[SteamTransport] Connected to host");
                        OnPeerConnected?.Invoke(0);
                    }
                    break;

                case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ClosedByPeer:
                case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ProblemDetectedLocally:
                    Log.LogInfo($"[SteamTransport] Connection closed: {info.m_szEndDebug}");

                    if (_isHost)
                    {
                        _clientConnections.Remove(conn);
                    }
                    else
                    {
                        _connectionToHost = HSteamNetConnection.Invalid;
                    }

                    int disconnectedPeer = _peerIds.TryGetValue(conn, out int existingPeer) ? existingPeer : 0;
                    _peerIds.Remove(conn);
                    _connectionsByPeer.Remove(disconnectedPeer);
                    _peerRtt.Remove(disconnectedPeer);
                    _outbound.Remove(conn);
                    _outboundBytes.Remove(conn);
                    SteamNetworkingSockets.CloseConnection(conn, 0, null, false);
                    OnPeerDisconnected?.Invoke(disconnectedPeer);
                    break;
            }
        }
    }
}
