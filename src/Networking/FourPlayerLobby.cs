using System;
using SeapowerMultiplayer.Messages;

namespace SeapowerMultiplayer
{
    public sealed class LobbySlot
    {
        public byte Slot;
        public byte PlayerId;
        public int PeerId = -1;
        public string Name = "";
        public byte Team;
        public byte Role;
        public bool Connected;
        public bool Ready;
    }

    public static class FourPlayerLobby
    {
        public const byte MaxPlayers = 4;
        private static readonly LobbySlot[] _slots =
        {
            new LobbySlot { Slot = 1 },
            new LobbySlot { Slot = 2 },
            new LobbySlot { Slot = 3 },
            new LobbySlot { Slot = 4 },
        };

        public static LobbySlot[] Slots => _slots;
        public static byte LocalPlayerId { get; private set; } = 1;
        public static byte LocalSlot { get; private set; } = 1;
        public static byte LocalTeam { get; private set; }
        public static byte HostTeam { get; private set; }
        public static bool LocalReady { get; private set; }
        public static int ConnectedPlayerCount
        {
            get
            {
                int count = 0;
                foreach (var slot in _slots)
                    if (slot.Connected)
                        count++;
                return count;
            }
        }

        public static void InitializeHost(string name, byte requestedTeam)
        {
            HostTeam = requestedTeam <= 1 ? requestedTeam : (byte)0;
            ResetSlots();
            var host = _slots[0];
            host.PlayerId = 1;
            host.PeerId = 0;
            host.Name = NormalizeName(name, "Host");
            host.Team = HostTeam;
            host.Connected = true;
            host.Ready = true;
            LocalPlayerId = 1;
            LocalSlot = 1;
            LocalTeam = HostTeam;
            LocalReady = true;
        }

        public static LobbySlot? AssignPeer(int peerId, HelloMessage hello, bool pvp)
        {
            int requested = hello.RequestedSlot - 1;
            LobbySlot? slot = requested >= 1 && requested < MaxPlayers && !_slots[requested].Connected
                ? _slots[requested]
                : FirstOpenClientSlot();
            if (slot == null) return null;

            slot.PlayerId = NextPlayerId();
            slot.PeerId = peerId;
            slot.Name = NormalizeName(hello.PlayerName, $"Player {slot.PlayerId}");
            slot.Team = pvp
                ? NormalizeRequestedTeam(slot.Slot, hello.RequestedTeam)
                : HostTeam;
            slot.Role = hello.RequestedRole <= 4 ? hello.RequestedRole : (byte)0;
            slot.Connected = true;
            slot.Ready = false;
            return slot;
        }

        public static bool ApplyPeerUpdate(int peerId, LobbyUpdateMessage update, bool pvp)
        {
            var current = FindByPeer(peerId);
            if (current == null) return false;

            if (update.RequestedSlot >= 2 && update.RequestedSlot <= MaxPlayers
                && update.RequestedSlot != current.Slot)
            {
                var destination = _slots[update.RequestedSlot - 1];
                if (!destination.Connected)
                {
                    CopyPlayer(current, destination);
                    ClearSlot(current);
                    current = destination;
                }
            }

            current.Team = pvp
                ? NormalizeRequestedTeam(current.Slot, update.RequestedTeam)
                : HostTeam;
            current.Role = update.RequestedRole <= 4 ? update.RequestedRole : (byte)0;
            current.Ready = update.Ready;
            return true;
        }

        public static int RemovePeer(int peerId, bool pvp)
        {
            var slot = FindByPeer(peerId);
            if (slot != null) ClearSlot(slot);
            return RebalanceForPlayerCount(pvp);
        }

        /// <summary>
        /// With exactly three connected players, slot 3 navigates the neutral fleet.
        /// At four players, slot 3 rejoins combat on the opposing team and slot 4
        /// joins the host team, restoring a 2v2 while neutrals return to mission AI.
        /// Returns the peer that changed team and needs a targeted scene resync.
        /// </summary>
        public static int RebalanceForPlayerCount(bool pvp)
        {
            if (!pvp)
                return -1;

            var slot3 = _slots[2];
            if (!slot3.Connected)
                return -1;

            byte desired = ConnectedPlayerCount >= 4 ? OpponentTeam : (byte)2;
            if (slot3.Team == desired)
                return -1;

            slot3.Team = desired;
            slot3.Ready = false;
            return slot3.PeerId;
        }

        public static LobbySlot? FindByPeer(int peerId)
        {
            foreach (var slot in _slots)
                if (slot.Connected && slot.PeerId == peerId)
                    return slot;
            return null;
        }

        public static LobbySlot? FindByPlayer(byte playerId)
        {
            foreach (var slot in _slots)
                if (slot.Connected && slot.PlayerId == playerId)
                    return slot;
            return null;
        }

        public static bool AllConnectedReady
        {
            get
            {
                int connected = 0;
                foreach (var slot in _slots)
                {
                    if (!slot.Connected) continue;
                    connected++;
                    if (!slot.Ready) return false;
                }
                return connected >= 2;
            }
        }

        public static PlayerRosterMessage BuildRoster()
        {
            var message = new PlayerRosterMessage();
            for (int i = 0; i < _slots.Length; i++)
            {
                var slot = _slots[i];
                message.PlayerIds[i] = slot.PlayerId;
                message.Connected[i] = slot.Connected;
                message.Ready[i] = slot.Ready;
                message.Teams[i] = slot.Team;
                message.Roles[i] = slot.Role;
                message.Names[i] = slot.Name;
            }
            return message;
        }

        public static void ApplyWelcome(WelcomeMessage welcome)
        {
            LocalPlayerId = welcome.PlayerId;
            LocalSlot = welcome.AssignedSlot;
            LocalTeam = welcome.AssignedTeam;
            HostTeam = welcome.HostTeam <= 1 ? welcome.HostTeam : (byte)0;
            LocalReady = false;
        }

        public static void ApplyRoster(PlayerRosterMessage message)
        {
            for (int i = 0; i < _slots.Length; i++)
            {
                var slot = _slots[i];
                slot.PlayerId = message.PlayerIds[i];
                slot.Connected = message.Connected[i];
                slot.Ready = message.Ready[i];
                slot.Team = message.Teams[i];
                slot.Role = message.Roles[i];
                slot.Name = message.Names[i];
                if (slot.PlayerId == LocalPlayerId)
                {
                    LocalSlot = slot.Slot;
                    LocalTeam = slot.Team;
                    LocalReady = slot.Ready;
                }
            }
        }

        public static void SetLocalReady(bool ready)
        {
            LocalReady = ready;
        }

        public static void Reset()
        {
            HostTeam = 0;
            ResetSlots();
            LocalPlayerId = 1;
            LocalSlot = 1;
            LocalTeam = 0;
            LocalReady = false;
        }

        private static LobbySlot? FirstOpenClientSlot()
        {
            for (int i = 1; i < _slots.Length; i++)
                if (!_slots[i].Connected)
                    return _slots[i];
            return null;
        }

        private static byte NextPlayerId()
        {
            for (byte id = 2; id <= MaxPlayers; id++)
                if (FindByPlayer(id) == null)
                    return id;
            return 0;
        }

        private static byte DefaultTeam(byte slot) => slot switch
        {
            2 => OpponentTeam,
            3 => 2,
            4 => HostTeam,
            _ => HostTeam,
        };

        private static byte OpponentTeam => HostTeam == 0 ? (byte)1 : (byte)0;

        private static byte NormalizeRequestedTeam(byte slot, byte requestedTeam)
        {
            if (slot == 3 && requestedTeam == 2)
                return 2;
            if (requestedTeam <= 1)
                return requestedTeam;
            return DefaultTeam(slot);
        }

        private static string NormalizeName(string value, string fallback)
        {
            string name = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
            return name.Length <= 24 ? name : name.Substring(0, 24);
        }

        private static void CopyPlayer(LobbySlot source, LobbySlot destination)
        {
            destination.PlayerId = source.PlayerId;
            destination.PeerId = source.PeerId;
            destination.Name = source.Name;
            destination.Team = source.Team;
            destination.Role = source.Role;
            destination.Connected = source.Connected;
            destination.Ready = source.Ready;
        }

        private static void ClearSlot(LobbySlot slot)
        {
            byte number = slot.Slot;
            slot.PlayerId = 0;
            slot.PeerId = -1;
            slot.Name = "";
            slot.Team = DefaultTeam(number);
            slot.Role = 0;
            slot.Connected = false;
            slot.Ready = false;
        }

        private static void ResetSlots()
        {
            foreach (var slot in _slots) ClearSlot(slot);
        }
    }
}
