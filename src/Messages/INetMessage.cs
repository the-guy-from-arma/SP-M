using LiteNetLib.Utils;

namespace SeapowerMultiplayer.Messages
{
    public enum MessageType : byte
    {
        PlayerOrder = 1,
        GameEvent = 2,
        SessionSync = 3,
        SessionReady = 4,
        DamageState = 6,
        DamageDecal = 7,
        Hello = 15,
        Welcome = 16,
        EntityStateBatch = 17,
        EntitySpawn = 18,
        EntityDespawn = 19,
        ImpactEvent = 20,
        DestroyEvent = 21,
        GunBurstEvent = 22,
        AmmoStateEvent = 23,
        EntityCensus = 24,
        CensusDiffRequest = 25,
        DeckState = 26,
        FlightOpsAnim = 27,
        LobbyUpdate = 28,
        PlayerRoster = 29,
    }

    public interface INetMessage
    {
        MessageType Type { get; }
        void Serialize(NetDataWriter writer);
    }
}
