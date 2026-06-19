using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using BepInEx.Logging;
using LiteNetLib;
using LiteNetLib.Utils;
using SeapowerMultiplayer.Messages;
using SeapowerMultiplayer.Net2;
using SeapowerMultiplayer.Transport;
using UnityEngine;

namespace SeapowerMultiplayer
{
    public sealed class NetworkManager
    {
        private sealed class HostPeerState
        {
            public HandshakeState Handshake = HandshakeState.AwaitingHello;
            public float Deadline;
            public float DisconnectAt = -1f;
        }

        public static readonly NetworkManager Instance = new NetworkManager();
        private NetworkManager() { }

        private ITransport? _transport;
        private bool _isHost;
        private bool _running;
        private readonly ConcurrentQueue<Action> _mainThreadQueue = new();
        private readonly Dictionary<int, HostPeerState> _hostPeers = new();
        private HandshakeState _clientHandshake = HandshakeState.Disconnected;
        private float _clientHandshakeDeadline = -1f;
        private bool _lastSendFailed;
        private const float HandshakeTimeoutSec = 5f;

        private static ManualLogSource Log => Plugin.Log;

        public WelcomeMessage? SessionParams { get; private set; }
        public bool LastSendFailed => _lastSendFailed || (_transport?.LastSendFailed ?? false);
        public int LastRttMs => _transport?.RttMs ?? 0;
        public int PendingReliableBytes => (_transport as SteamTransport)?.PendingReliableBytes ?? 0;
        public int PendingReliablePackets => (_transport as SteamTransport)?.PendingReliablePackets ?? 0;
        public bool IsConnected => _transport?.IsConnected ?? false;
        public bool IsHost => _isHost;
        public bool IsConnectedClient => !_isHost && IsConnected;
        public bool IsHostRunning => _running && _isHost;
        public int ConnectedClientCount => _isHost ? (_transport?.ConnectedPeerCount ?? 0) : 0;

        public int EstablishedClientCount
        {
            get
            {
                if (!_isHost) return _clientHandshake == HandshakeState.Established ? 1 : 0;
                int count = 0;
                foreach (var peer in _hostPeers.Values)
                    if (peer.Handshake == HandshakeState.Established)
                        count++;
                return count;
            }
        }

        public IReadOnlyCollection<int> EstablishedPeerIds
        {
            get
            {
                var ids = new List<int>();
                if (!_isHost) return ids;
                foreach (var pair in _hostPeers)
                    if (pair.Value.Handshake == HandshakeState.Established)
                        ids.Add(pair.Key);
                return ids;
            }
        }

        public bool IsEstablished => _running && (_isHost
            ? EstablishedClientCount > 0
            : _clientHandshake == HandshakeState.Established);

        public HandshakeState Handshake
        {
            get
            {
                if (!_isHost) return _clientHandshake;
                if (EstablishedClientCount > 0) return HandshakeState.Established;
                return _hostPeers.Count > 0 ? HandshakeState.AwaitingHello : HandshakeState.Disconnected;
            }
        }

        public void StartHost(int port)
        {
            if (_running) Stop();
            _isHost = true;
            _transport = CreateTransport();
            WireTransportEvents();
            FourPlayerLobby.InitializeHost(
                Plugin.Instance.CfgPlayerName.Value,
                (byte)Mathf.Clamp(Plugin.Instance.CfgPreferredTeam.Value, 0, 1));
            _transport.Start(asHost: true);
            _running = true;
            Log.LogInfo($"[Net] Hosting four-player session (transport={Plugin.Instance.CfgTransport.Value}, max=4)");
        }

        public void StartClient(string ip, int port)
        {
            if (_running) Stop();
            _isHost = false;
            _transport = CreateTransport();
            WireTransportEvents();
            FourPlayerLobby.Reset();
            _clientHandshake = HandshakeState.Disconnected;
            _transport.Start(asHost: false);
            _running = true;
            Log.LogInfo($"[Net] Connecting as client (transport={Plugin.Instance.CfgTransport.Value})");
        }

        public void StartTransport(bool asHost)
        {
            if (asHost) StartHost(0);
            else StartClient("", 0);
        }

        public void Stop()
        {
            if (!_running) return;
            Patch_Vehicle_UpdateAllData_PvP.ClearCache();
            Patch_ObjectBase_HandleEngageTasks.Reset();
            _transport?.Stop();
            _transport = null;
            _running = false;
            _hostPeers.Clear();
            _clientHandshake = HandshakeState.Disconnected;
            _clientHandshakeDeadline = -1f;
            _lastSendFailed = false;
            SessionParams = null;
            FourPlayerLobby.Reset();
            NeutralNavigationManager.Reset();
            SimSyncManager.Reset();
            Log.LogInfo("[Net] Stopped.");
        }

        public void Tick()
        {
            if (!_running) return;
            _transport?.Poll();

            while (_mainThreadQueue.TryDequeue(out var action))
                action();

            float now = Time.realtimeSinceStartup;
            if (_isHost)
            {
                var disconnect = new List<int>();
                foreach (var pair in _hostPeers)
                {
                    var state = pair.Value;
                    if (state.Handshake == HandshakeState.AwaitingHello && now > state.Deadline)
                    {
                        Log.LogError($"[Handshake] Peer {pair.Key} did not send Hello within {HandshakeTimeoutSec:0}s.");
                        state.Handshake = HandshakeState.Refused;
                        disconnect.Add(pair.Key);
                    }
                    else if (state.DisconnectAt > 0f && now > state.DisconnectAt)
                    {
                        disconnect.Add(pair.Key);
                    }
                }
                foreach (int peerId in disconnect)
                    _transport?.DisconnectPeer(peerId, "Handshake refused or timed out");
            }
            else if (_clientHandshake == HandshakeState.AwaitingWelcome
                     && _clientHandshakeDeadline > 0f
                     && now > _clientHandshakeDeadline)
            {
                Log.LogError("[Handshake] Host did not send Welcome within timeout.");
                _clientHandshake = HandshakeState.Refused;
                _transport?.DisconnectPeers();
            }
        }

        public void SendToServer(INetMessage message, DeliveryMethod delivery = DeliveryMethod.ReliableOrdered)
        {
            if (_transport == null) return;
            if (message.Type != MessageType.Hello && _clientHandshake != HandshakeState.Established) return;
            _lastSendFailed = false;
            var writer = Serialize(message);
            _transport.SendToServer(writer.Data, writer.Length, MapDelivery(delivery));
            _lastSendFailed |= _transport.LastSendFailed;
            Telemetry.OnSend((byte)message.Type, writer.Length);
        }

        public void SendToClient(int peerId, INetMessage message,
            DeliveryMethod delivery = DeliveryMethod.ReliableOrdered)
        {
            if (_transport == null || !_isHost) return;
            if (message.Type != MessageType.Welcome
                && (!_hostPeers.TryGetValue(peerId, out var peer)
                    || peer.Handshake != HandshakeState.Established))
                return;
            _lastSendFailed = false;
            var writer = Serialize(message);
            _transport.SendToClient(peerId, writer.Data, writer.Length, MapDelivery(delivery));
            _lastSendFailed |= _transport.LastSendFailed;
            Telemetry.OnSend((byte)message.Type, writer.Length);
        }

        public void BroadcastToClients(INetMessage message,
            DeliveryMethod delivery = DeliveryMethod.ReliableOrdered)
        {
            if (_transport == null || !_isHost) return;
            _lastSendFailed = false;
            var writer = Serialize(message);
            foreach (var pair in _hostPeers)
            {
                if (pair.Value.Handshake != HandshakeState.Established) continue;
                _transport.SendToClient(pair.Key, writer.Data, writer.Length, MapDelivery(delivery));
                _lastSendFailed |= _transport.LastSendFailed;
                Telemetry.OnSend((byte)message.Type, writer.Length);
            }
        }

        public void BroadcastToClientsExcept(int excludedPeerId, INetMessage message,
            DeliveryMethod delivery = DeliveryMethod.ReliableOrdered)
        {
            if (_transport == null || !_isHost) return;
            _lastSendFailed = false;
            var writer = Serialize(message);
            foreach (var pair in _hostPeers)
            {
                if (pair.Key == excludedPeerId || pair.Value.Handshake != HandshakeState.Established)
                    continue;
                _transport.SendToClient(pair.Key, writer.Data, writer.Length, MapDelivery(delivery));
                _lastSendFailed |= _transport.LastSendFailed;
                Telemetry.OnSend((byte)message.Type, writer.Length);
            }
        }

        public void SendToOther(INetMessage message,
            DeliveryMethod delivery = DeliveryMethod.ReliableOrdered)
        {
            if (_isHost) BroadcastToClients(message, delivery);
            else SendToServer(message, delivery);
        }

        public void SendLobbyUpdate(byte requestedSlot, byte requestedTeam, byte requestedRole, bool ready)
        {
            FourPlayerLobby.SetLocalReady(ready);
            SendToServer(new LobbyUpdateMessage
            {
                RequestedSlot = requestedSlot,
                RequestedTeam = requestedTeam,
                RequestedRole = requestedRole,
                Ready = ready,
            });
        }

        public void BroadcastRoster()
        {
            if (!_isHost) return;
            BroadcastToClients(FourPlayerLobby.BuildRoster());
        }

        private static NetDataWriter Serialize(INetMessage message)
        {
            var writer = new NetDataWriter();
            writer.Put((byte)message.Type);
            message.Serialize(writer);
            return writer;
        }

        private ITransport CreateTransport()
            => Plugin.Instance.CfgTransport.Value.Equals("Steam", StringComparison.OrdinalIgnoreCase)
                ? new SteamTransport()
                : new LiteNetTransport();

        private void WireTransportEvents()
        {
            if (_transport == null) return;
            _transport.OnDataReceived += OnDataReceived;
            _transport.OnPeerConnected += OnPeerConnected;
            _transport.OnPeerDisconnected += OnPeerDisconnected;
        }

        private static TransportDelivery MapDelivery(DeliveryMethod delivery) => delivery switch
        {
            DeliveryMethod.Unreliable => TransportDelivery.Unreliable,
            DeliveryMethod.ReliableSequenced => TransportDelivery.Reliable,
            DeliveryMethod.ReliableOrdered => TransportDelivery.ReliableOrdered,
            DeliveryMethod.ReliableUnordered => TransportDelivery.Reliable,
            _ => TransportDelivery.ReliableOrdered,
        };

        private void OnPeerConnected(int peerId)
        {
            _mainThreadQueue.Enqueue(() =>
            {
                if (_isHost)
                {
                    if (!_hostPeers.TryGetValue(peerId, out var existing))
                    {
                        _hostPeers[peerId] = new HostPeerState
                        {
                            Handshake = HandshakeState.AwaitingHello,
                            Deadline = Time.realtimeSinceStartup + HandshakeTimeoutSec,
                        };
                    }
                    else if (existing.Handshake == HandshakeState.AwaitingHello)
                    {
                        existing.Deadline = Time.realtimeSinceStartup + HandshakeTimeoutSec;
                    }
                    Log.LogInfo($"[Handshake] Peer {peerId} connected; awaiting Hello.");
                }
                else
                {
                    var hello = new HelloMessage
                    {
                        ProtocolVersion = ProtocolInfo.ProtocolVersion,
                        PluginVersion = PluginInfo.PLUGIN_VERSION,
                        IsPvP = Plugin.Instance.CfgPvP.Value,
                        PlayerName = Plugin.Instance.CfgPlayerName.Value,
                        RequestedSlot = (byte)Mathf.Clamp(Plugin.Instance.CfgPreferredSlot.Value, 0, 4),
                        RequestedTeam = (byte)Mathf.Clamp(Plugin.Instance.CfgPreferredTeam.Value, 0, 255),
                        RequestedRole = (byte)Mathf.Clamp(Plugin.Instance.CfgPreferredRole.Value, 0, 4),
                    };
                    _clientHandshake = HandshakeState.AwaitingWelcome;
                    _clientHandshakeDeadline = Time.realtimeSinceStartup + HandshakeTimeoutSec;
                    SendHello(hello);
                }
            });
        }

        private void SendHello(HelloMessage hello)
        {
            if (_transport == null) return;
            var writer = Serialize(hello);
            _transport.SendToServer(writer.Data, writer.Length, TransportDelivery.ReliableOrdered);
            Telemetry.OnSend((byte)hello.Type, writer.Length);
            Log.LogInfo($"[Handshake] Hello sent for '{hello.PlayerName}' (slot={hello.RequestedSlot}, team={hello.RequestedTeam}).");
        }

        private void OnPeerDisconnected(int peerId)
        {
            _mainThreadQueue.Enqueue(() =>
            {
                Log.LogInfo($"[Net] Peer {peerId} disconnected.");
                if (_isHost)
                {
                    var disconnectedSlot = FourPlayerLobby.FindByPeer(peerId);
                    if (disconnectedSlot != null)
                    {
                        UnitLockManager.OnRemoteDeselected(disconnectedSlot.PlayerId);
                        BroadcastToClientsExcept(peerId, new GameEventMessage
                        {
                            EventType = GameEventType.UnitDeselected,
                            SourceEntityId = disconnectedSlot.PlayerId,
                        });
                    }
                    _hostPeers.Remove(peerId);
                    int rebalancedPeer = FourPlayerLobby.RemovePeer(
                        peerId, Plugin.Instance.CfgPvP.Value);
                    SimSyncManager.OnClientDisconnected(peerId);
                    NeutralNavigationManager.RefreshHostPolicy();
                    BroadcastRoster();
                    ResyncRebalancedPeer(rebalancedPeer, peerId);
                    return;
                }

                _clientHandshake = HandshakeState.Disconnected;
                _clientHandshakeDeadline = -1f;
                SessionParams = null;
                ResetClientReplication();
                FourPlayerLobby.Reset();
            });
        }

        private static void ResetClientReplication()
        {
            UnitReplicaDriver.Reset();
            AircraftReplicaDriver.Reset();
            DeckPuppetDriver.Reset();
            CarrierOpsHandler.Reset();
            SpawnReplicator.Reset();
            WeaponReplicaDriver.Reset();
            EntityCensusManager.Reset();
            Patch_V2_MissionEnd_Capture.Reset();
            CaptureState.Clear();
            ReplicaRegistry.Clear();
            Suppression.EnforceDefenseFlag();
            TaskforceAssignmentManager.Reset();
            UnitLockManager.Reset();
            StateApplier.ResetOrphanTracking();
            Patch_Vehicle_UpdateAllData_PvP.ClearCache();
            Patch_ObjectBase_HandleEngageTasks.Reset();
            Patch_Compartments_CalculateWantedVelocityInKnots.ClearLogCache();
            Patch_Vessel_ApplyRudderThrust.ClearLogCache();
            Patch_VesselPropulsionSystem_OnUpdate.ClearLogCache();
        }

        private void OnDataReceived(int peerId, byte[] data, int length)
        {
            if (length < 1)
            {
                Log.LogWarning($"[Net] Ignored empty packet from peer {peerId}.");
                return;
            }

            try
            {
                var reader = new NetDataReader(data, 0, length);
                var type = (MessageType)reader.GetByte();
                Telemetry.OnReceive((byte)type, length);

                if (_isHost)
                {
                    if (!_hostPeers.TryGetValue(peerId, out var peer))
                    {
                        // A fast localhost peer can deliver Hello in the same transport
                        // poll as the connect event, before the queued connect handler
                        // has created its state. Preserve FIFO by queueing both.
                        if (type == MessageType.Hello)
                        {
                            var earlyHello = HelloMessage.Deserialize(reader);
                            _mainThreadQueue.Enqueue(() =>
                            {
                                if (!_hostPeers.ContainsKey(peerId))
                                {
                                    _hostPeers[peerId] = new HostPeerState
                                    {
                                        Handshake = HandshakeState.AwaitingHello,
                                        Deadline = Time.realtimeSinceStartup + HandshakeTimeoutSec,
                                    };
                                }
                                HandleHello(peerId, earlyHello);
                            });
                        }
                        return;
                    }
                    if (peer.Handshake != HandshakeState.Established)
                    {
                        if (type == MessageType.Hello)
                        {
                            var hello = HelloMessage.Deserialize(reader);
                            _mainThreadQueue.Enqueue(() => HandleHello(peerId, hello));
                        }
                        return;
                    }
                }
                else if (_clientHandshake != HandshakeState.Established)
                {
                    if (type == MessageType.Welcome)
                    {
                        var welcome = WelcomeMessage.Deserialize(reader);

                        // Poll() runs on Unity's main thread. Apply Welcome now so a
                        // PlayerRoster delivered immediately afterward in the same
                        // poll is accepted instead of being dropped.
                        HandleWelcome(welcome);
                    }
                    return;
                }

                DispatchEstablished(peerId, type, reader);
            }
            catch (Exception ex)
            {
                Log.LogWarning($"[Net] Ignored malformed {length}-byte packet from peer {peerId}: {ex.Message}");
                Telemetry.Count("net.malformedPacket");
            }
        }

        private void DispatchEstablished(int peerId, MessageType type, NetDataReader reader)
        {
            switch (type)
            {
                case MessageType.EntityStateBatch:
                {
                    var message = EntityStateBatchMessage.Deserialize(reader);
                    _mainThreadQueue.Enqueue(() => UnitReplicaDriver.Apply(message));
                    break;
                }
                case MessageType.EntitySpawn:
                {
                    var message = EntitySpawnMessage.Deserialize(reader);
                    _mainThreadQueue.Enqueue(() => SpawnReplicator.HandleSpawn(message));
                    break;
                }
                case MessageType.EntityDespawn:
                {
                    var message = EntityDespawnMessage.Deserialize(reader);
                    _mainThreadQueue.Enqueue(() => SpawnReplicator.HandleDespawn(message));
                    break;
                }
                case MessageType.DeckState:
                {
                    var message = DeckStateMessage.Deserialize(reader);
                    _mainThreadQueue.Enqueue(() => DeckPuppetDriver.OnDeckState(message));
                    break;
                }
                case MessageType.FlightOpsAnim:
                {
                    var message = FlightOpsAnimMessage.Deserialize(reader);
                    _mainThreadQueue.Enqueue(() => CarrierOpsHandler.HandleAnim(message));
                    break;
                }
                case MessageType.ImpactEvent:
                {
                    var message = ImpactEventMessage.Deserialize(reader);
                    _mainThreadQueue.Enqueue(() => SpawnReplicator.HandleImpact(message));
                    break;
                }
                case MessageType.DestroyEvent:
                {
                    var message = DestroyEventMessage.Deserialize(reader);
                    _mainThreadQueue.Enqueue(() => SpawnReplicator.HandleDestroyEvent(message));
                    break;
                }
                case MessageType.GunBurstEvent:
                {
                    var message = GunBurstEventMessage.Deserialize(reader);
                    _mainThreadQueue.Enqueue(() => CosmeticEventHandler.HandleGunBurst(message));
                    break;
                }
                case MessageType.AmmoStateEvent:
                {
                    var message = AmmoStateEventMessage.Deserialize(reader);
                    _mainThreadQueue.Enqueue(() => CosmeticEventHandler.HandleAmmoState(message));
                    break;
                }
                case MessageType.EntityCensus:
                {
                    var message = EntityCensusMessage.Deserialize(reader);
                    _mainThreadQueue.Enqueue(() => EntityCensusManager.HandleCensus(message));
                    break;
                }
                case MessageType.CensusDiffRequest:
                {
                    var message = CensusDiffRequestMessage.Deserialize(reader);
                    _mainThreadQueue.Enqueue(() => EntityCensusManager.HandleDiffRequest(peerId, message));
                    break;
                }
                case MessageType.PlayerOrder:
                {
                    var message = PlayerOrderMessage.Deserialize(reader);
                    _mainThreadQueue.Enqueue(() => ApplyPlayerOrder(peerId, message));
                    break;
                }
                case MessageType.GameEvent:
                {
                    var message = GameEventMessage.Deserialize(reader);
                    _mainThreadQueue.Enqueue(() => GameEventHandler.Apply(message, peerId));
                    break;
                }
                case MessageType.SessionSync:
                {
                    var message = SessionSyncMessage.Deserialize(reader);
                    _mainThreadQueue.Enqueue(() => SessionManager.ApplyReceivedSession(message));
                    break;
                }
                case MessageType.SessionReady:
                {
                    var message = SessionReadyMessage.Deserialize(reader);
                    _mainThreadQueue.Enqueue(() => SimSyncManager.OnClientReady(peerId, message.IsReady));
                    break;
                }
                case MessageType.DamageState:
                {
                    var message = DamageStateMessage.Deserialize(reader);
                    _mainThreadQueue.Enqueue(() => DamageStateSerializer.Apply(message));
                    break;
                }
                case MessageType.DamageDecal:
                {
                    var message = DamageDecalMessage.Deserialize(reader);
                    _mainThreadQueue.Enqueue(() => CombatEventHandler.RunAsNetworkEvent(
                        () => DamageStateSerializer.ApplyDecal(message)));
                    break;
                }
                case MessageType.LobbyUpdate:
                {
                    var message = LobbyUpdateMessage.Deserialize(reader);
                    _mainThreadQueue.Enqueue(() => HandleLobbyUpdate(peerId, message));
                    break;
                }
                case MessageType.PlayerRoster:
                {
                    var message = PlayerRosterMessage.Deserialize(reader);
                    _mainThreadQueue.Enqueue(() => FourPlayerLobby.ApplyRoster(message));
                    break;
                }
                default:
                    Log.LogWarning($"[Net] Unknown message type: {type}");
                    break;
            }
        }

        private void ApplyPlayerOrder(int peerId, PlayerOrderMessage message)
        {
            if (SessionManager.SceneLoading || SimSyncManager.CurrentState != SimState.Synchronized)
                return;

            if (_isHost)
            {
                var unit = StateSerializer.FindById(message.SourceEntityId);
                if (!TaskforceAssignmentManager.HostPeerMayControl(peerId, unit, message.Order))
                {
                    Log.LogWarning($"[Order] Rejected peer {peerId} order for unit {message.SourceEntityId}: ownership mismatch.");
                    Telemetry.Count("order.rejectedOwnership");
                    return;
                }
            }
            OrderHandler.Apply(message);

            // The sender already applied its order optimistically and the host has
            // now validated/applied it authoritatively. Relay it to every other
            // established client so players 3 and 4 update immediately.
            if (_isHost)
                BroadcastToClientsExcept(peerId, message);
        }

        private void HandleHello(int peerId, HelloMessage hello)
        {
            if (!_hostPeers.TryGetValue(peerId, out var peer)
                || peer.Handshake != HandshakeState.AwaitingHello)
                return;

            string? refusal = null;
            if (hello.ProtocolVersion != ProtocolInfo.ProtocolVersion)
                refusal = $"Protocol mismatch: host {ProtocolInfo.ProtocolVersion}, client {hello.ProtocolVersion}.";
            else if (hello.IsPvP != Plugin.Instance.CfgPvP.Value)
                refusal = "PvP/co-op mode mismatch.";

            LobbySlot? slot = null;
            if (refusal == null)
            {
                slot = FourPlayerLobby.AssignPeer(peerId, hello, Plugin.Instance.CfgPvP.Value);
                if (slot == null) refusal = "The four-player lobby is full.";
            }

            if (refusal != null)
            {
                SendWelcomeRaw(peerId, new WelcomeMessage { Accepted = false, RefusalReason = refusal });
                peer.Handshake = HandshakeState.Refused;
                peer.DisconnectAt = Time.realtimeSinceStartup + 0.75f;
                return;
            }

            peer.Handshake = HandshakeState.Established;
            peer.Deadline = -1f;
            int rebalancedPeer = FourPlayerLobby.RebalanceForPlayerCount(
                Plugin.Instance.CfgPvP.Value);
            var welcome = new WelcomeMessage
            {
                Accepted = true,
                IsPvP = Plugin.Instance.CfgPvP.Value,
                PlayerId = slot!.PlayerId,
                AssignedSlot = slot.Slot,
                AssignedTeam = slot.Team,
                HostTeam = FourPlayerLobby.HostTeam,
                AssignedRole = slot.Role,
                MaxPlayers = FourPlayerLobby.MaxPlayers,
                ClientUidBase = ProtocolInfo.ClientUidBase(slot.PlayerId),
                StateRateHz = (byte)Mathf.Clamp(Plugin.Instance.CfgUnitStateHz.Value, 1, 60),
            };
            SendWelcomeRaw(peerId, welcome);
            NeutralNavigationManager.RefreshHostPolicy();
            BroadcastRoster();
            ResyncRebalancedPeer(rebalancedPeer, peerId);
            Log.LogInfo($"[Handshake] Accepted peer {peerId} as player {slot.PlayerId}, slot {slot.Slot}, team {slot.Team}, role {(ControlRole)slot.Role}.");
        }

        private void SendWelcomeRaw(int peerId, WelcomeMessage welcome)
        {
            if (_transport == null) return;
            var writer = Serialize(welcome);
            _transport.SendToClient(peerId, writer.Data, writer.Length, TransportDelivery.ReliableOrdered);
            Telemetry.OnSend((byte)welcome.Type, writer.Length);
        }

        private void HandleWelcome(WelcomeMessage welcome)
        {
            if (_clientHandshake != HandshakeState.AwaitingWelcome) return;
            _clientHandshakeDeadline = -1f;
            if (!welcome.Accepted)
            {
                Log.LogError($"[Handshake] Host refused connection: {welcome.RefusalReason}");
                _clientHandshake = HandshakeState.Refused;
                _transport?.DisconnectPeers();
                return;
            }

            SessionParams = welcome;
            FourPlayerLobby.ApplyWelcome(welcome);
            TaskforceAssignmentManager.OnAssignmentReceived(welcome.AssignedRole);
            _clientHandshake = HandshakeState.Established;
            Log.LogInfo($"[Handshake] Established as player {welcome.PlayerId}, slot {welcome.AssignedSlot}, team {welcome.AssignedTeam}, role {(ControlRole)welcome.AssignedRole}.");
        }

        private void HandleLobbyUpdate(int peerId, LobbyUpdateMessage update)
        {
            if (!_isHost) return;
            if (FourPlayerLobby.ApplyPeerUpdate(peerId, update, Plugin.Instance.CfgPvP.Value))
            {
                int rebalancedPeer = FourPlayerLobby.RebalanceForPlayerCount(
                    Plugin.Instance.CfgPvP.Value);
                NeutralNavigationManager.RefreshHostPolicy();
                BroadcastRoster();
                ResyncRebalancedPeer(rebalancedPeer, peerId);
            }
        }

        private static void ResyncRebalancedPeer(int rebalancedPeer, int triggeringPeer)
        {
            if (rebalancedPeer < 0 || rebalancedPeer == triggeringPeer)
                return;
            if (!SessionManager.HasLiveScene)
                return;

            Log.LogInfo($"[Lobby] Team rebalance requires targeted resync for peer {rebalancedPeer}.");
            SessionManager.CaptureAndSend(rebalancedPeer);
        }
    }
}
