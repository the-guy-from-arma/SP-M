using System;
using LiteNetLib.Utils;
using SeapowerMultiplayer;
using SeapowerMultiplayer.Messages;
using SeapowerMultiplayer.Net2;

static class Program
{
    static void Main()
    {
        Assert(ProtocolInfo.ProtocolVersion == 403, "four-player protocol version");
        Assert(ProtocolInfo.ClientUidBase(2) == 100_000_000, "player 2 UID band");
        Assert(ProtocolInfo.ClientUidBase(3) == 110_000_000, "player 3 UID band");
        Assert(ProtocolInfo.ClientUidBase(4) == 120_000_000, "player 4 UID band");
        Assert(
            HandshakeCompatibility.GetRefusalReason(
                ProtocolInfo.ProtocolVersion,
                PluginInfo.PLUGIN_VERSION,
                true,
                true) == null,
            "matching handshake is accepted");
        Assert(
            HandshakeCompatibility.GetRefusalReason(
                402,
                PluginInfo.PLUGIN_VERSION,
                true,
                true)?.Contains("Protocol mismatch") == true,
            "old protocol is refused");
        Assert(
            HandshakeCompatibility.GetRefusalReason(
                ProtocolInfo.ProtocolVersion,
                "0.1.5",
                true,
                true)?.Contains("Plugin version mismatch") == true,
            "mixed plugin versions are refused");

        var hello = RoundTrip(new HelloMessage
        {
            ProtocolVersion = ProtocolInfo.ProtocolVersion,
            PluginVersion = PluginInfo.PLUGIN_VERSION,
            IsPvP = true,
            PlayerName = "Admiral Three",
            RequestedSlot = 3,
            RequestedTeam = 0,
            RequestedRole = 3,
        }, HelloMessage.Deserialize);
        Assert(hello.PlayerName == "Admiral Three" && hello.RequestedSlot == 3
               && hello.RequestedTeam == 0 && hello.RequestedRole == 3,
            "Hello round trip");

        var welcome = RoundTrip(new WelcomeMessage
        {
            Accepted = true,
            IsPvP = true,
            PlayerId = 4,
            AssignedSlot = 4,
            AssignedTeam = 1,
            HostTeam = 0,
            AssignedRole = 2,
            MaxPlayers = 4,
            ClientUidBase = ProtocolInfo.ClientUidBase(4),
            StateRateHz = 10,
        }, WelcomeMessage.Deserialize);
        Assert(welcome.PlayerId == 4 && welcome.MaxPlayers == 4
               && welcome.ClientUidBase == 120_000_000, "Welcome round trip");

        var rosterSource = new PlayerRosterMessage();
        for (int i = 0; i < PlayerRosterMessage.SlotCount; i++)
        {
            rosterSource.PlayerIds[i] = (byte)(i + 1);
            rosterSource.Connected[i] = true;
            rosterSource.Ready[i] = i != 3;
            rosterSource.Teams[i] = (byte)(i % 2);
            rosterSource.Roles[i] = (byte)i;
            rosterSource.Names[i] = $"Player {i + 1}";
        }
        var roster = RoundTrip(rosterSource, PlayerRosterMessage.Deserialize);
        Assert(roster.Names[3] == "Player 4" && !roster.Ready[3] && roster.Teams[3] == 1,
            "Roster round trip");

        var update = RoundTrip(new LobbyUpdateMessage
        {
            RequestedSlot = 2,
            RequestedTeam = 1,
            RequestedRole = 2,
            Ready = true,
        }, LobbyUpdateMessage.Deserialize);
        Assert(update.RequestedSlot == 2 && update.RequestedTeam == 1
               && update.RequestedRole == 2 && update.Ready,
            "Lobby update round trip");

        var order = RoundTrip(new PlayerOrderMessage
        {
            SourceEntityId = 1234,
            Order = OrderType.LaunchAircraft,
            Speed = 2,
            Heading = 3,
            DestX = 4,
            DestY = 5,
            DestZ = 6,
            TargetEntityId = 1,
            TargetX = 7,
            TargetY = 8,
            TargetZ = 9,
            ShotsToFire = 10,
            AmmoId = "test_ammo",
        }, PlayerOrderMessage.Deserialize);
        Assert(order.SourceEntityId == 1234
               && order.Order == OrderType.LaunchAircraft
               && order.TargetEntityId == 1
               && order.ShotsToFire == 10
               && order.AmmoId == "test_ammo",
            "Player order round trip");

        var gameEvent = RoundTrip(new GameEventMessage
        {
            EventType = GameEventType.UnitSelected,
            SourceEntityId = 4,
            TargetEntityId = 777,
            Param = 12.5f,
        }, GameEventMessage.Deserialize);
        Assert(gameEvent.EventType == GameEventType.UnitSelected
               && gameEvent.SourceEntityId == 4
               && gameEvent.TargetEntityId == 777
               && Math.Abs(gameEvent.Param - 12.5f) < 0.001f,
            "Game event round trip");

        var largePayload = new byte[2_218_658];
        for (int i = 0; i < largePayload.Length; i++)
            largePayload[i] = (byte)(i * 31);
        var chunks = SeapowerMultiplayer.Transport.SteamFragmenter.Split(
            largePayload, largePayload.Length, 42);
        Assert(chunks.Count > 60, "large Steam transfer is split into paced chunks");
        var rebuilt = new byte[largePayload.Length];
        int rebuiltOffset = 0;
        for (int i = 0; i < chunks.Count; i++)
        {
            Assert(SeapowerMultiplayer.Transport.SteamFragmenter.TryReadHeader(
                    chunks[i], chunks[i].Length, out uint fragmentId,
                    out int chunkIndex, out int totalChunks)
                   && fragmentId == 42 && chunkIndex == i && totalChunks == chunks.Count,
                "Steam fragment header");
            int payloadLength = chunks[i].Length
                - SeapowerMultiplayer.Transport.SteamFragmenter.HeaderSize;
            Buffer.BlockCopy(chunks[i], SeapowerMultiplayer.Transport.SteamFragmenter.HeaderSize,
                rebuilt, rebuiltOffset, payloadLength);
            rebuiltOffset += payloadLength;
        }
        Assert(rebuiltOffset == largePayload.Length
               && System.Linq.Enumerable.SequenceEqual(largePayload, rebuilt),
            "large Steam transfer reassembles exactly");

        FourPlayerLobby.InitializeHost("Host", 1);
        Assert(FourPlayerLobby.LocalTeam == 1 && FourPlayerLobby.HostTeam == 1,
            "host can select red team");
        var p2 = FourPlayerLobby.AssignPeer(21, NewHello("Two", 2, 255), pvp: true);
        FourPlayerLobby.RebalanceForPlayerCount(pvp: true);
        var p3 = FourPlayerLobby.AssignPeer(22, NewHello("Three", 3, 255), pvp: true);
        FourPlayerLobby.RebalanceForPlayerCount(pvp: true);
        Assert(p2?.Team == 0 && p3?.Team == 2,
            "third player receives neutral navigation while teams remain red and blue");
        var p4 = FourPlayerLobby.AssignPeer(23, NewHello("Four", 4, 255), pvp: true);
        int fourPlayerRebalance = FourPlayerLobby.RebalanceForPlayerCount(pvp: true);
        Assert(p2?.Slot == 2 && p3?.Slot == 3 && p4?.Slot == 4, "three clients assigned");
        Assert(fourPlayerRebalance == 22
               && p3?.Team == 0
               && p4?.Team == 1,
            "four players rebalance to two red and two blue");
        Assert(FourPlayerLobby.AssignPeer(24, NewHello("Five", 0, 0), pvp: true) == null,
            "fifth player rejected");
        FourPlayerLobby.ApplyPeerUpdate(22, new LobbyUpdateMessage
        {
            RequestedSlot = 3,
            RequestedTeam = 1,
            RequestedRole = 3,
            Ready = true,
        }, pvp: true);
        FourPlayerLobby.RebalanceForPlayerCount(pvp: true);
        Assert(FourPlayerLobby.FindByPeer(22)?.Team == 0
               && FourPlayerLobby.FindByPeer(22)?.Role == 3
               && FourPlayerLobby.FindByPeer(22)?.Ready == false,
            "four-player rebalance overrides slot-three team and clears ready");
        FourPlayerLobby.ApplyPeerUpdate(22, new LobbyUpdateMessage
        {
            RequestedSlot = 3,
            RequestedTeam = 1,
            RequestedRole = 3,
            Ready = false,
        }, pvp: false);
        Assert(FourPlayerLobby.FindByPeer(22)?.Team == 1,
            "co-op forces clients onto host team");
        int threePlayerRebalance = FourPlayerLobby.RemovePeer(23, pvp: true);
        Assert(threePlayerRebalance == 22 && FourPlayerLobby.FindByPeer(22)?.Team == 2,
            "losing fourth player restores neutral navigation to third player");
        Assert(FourPlayerLobby.AssignPeer(25, NewHello("Reconnect", 4, 1), pvp: true)?.Slot == 4,
            "disconnected slot reusable");

        Assert(NeutralOrderPolicy.IsMovementOnlyOrder(OrderType.MoveTo)
               && NeutralOrderPolicy.IsMovementOnlyOrder(OrderType.Stop)
               && !NeutralOrderPolicy.IsMovementOnlyOrder(OrderType.FireWeapon)
               && !NeutralOrderPolicy.IsMovementOnlyOrder(OrderType.SetHeading)
               && !NeutralOrderPolicy.IsMovementOnlyOrder(OrderType.SensorToggle),
            "neutral authority is movement-only");

        Console.WriteLine("Protocol smoke tests passed.");
    }

    static HelloMessage NewHello(string name, byte slot, byte team) => new()
    {
        ProtocolVersion = ProtocolInfo.ProtocolVersion,
        PluginVersion = PluginInfo.PLUGIN_VERSION,
        IsPvP = true,
        PlayerName = name,
        RequestedSlot = slot,
        RequestedTeam = team,
        RequestedRole = 0,
    };

    static T RoundTrip<T>(INetMessage message, Func<NetDataReader, T> deserialize)
    {
        var writer = new NetDataWriter();
        message.Serialize(writer);
        return deserialize(new NetDataReader(writer.CopyData()));
    }

    static void Assert(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException($"Failed: {name}");
    }
}
