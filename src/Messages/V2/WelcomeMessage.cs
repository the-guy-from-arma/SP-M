using LiteNetLib.Utils;

namespace SeapowerMultiplayer.Messages
{
    /// <summary>
    /// Host → client handshake verdict. On acceptance carries the session parameters
    /// the client needs before any gameplay traffic (mode, UID band, stream rate).
    /// </summary>
    public class WelcomeMessage : INetMessage
    {
        public bool   Accepted;
        public string RefusalReason = "";
        public bool   IsPvP;
        public byte   PlayerId;
        public byte   AssignedSlot;
        public byte   AssignedTeam;
        public byte   HostTeam;
        public byte   AssignedRole;
        public byte   MaxPlayers;
        public int    ClientUidBase;
        public byte   StateRateHz;

        public MessageType Type => MessageType.Welcome;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(Accepted);
            writer.Put(RefusalReason);
            writer.Put(IsPvP);
            writer.Put(PlayerId);
            writer.Put(AssignedSlot);
            writer.Put(AssignedTeam);
            writer.Put(HostTeam);
            writer.Put(AssignedRole);
            writer.Put(MaxPlayers);
            writer.Put(ClientUidBase);
            writer.Put(StateRateHz);
        }

        public static WelcomeMessage Deserialize(NetDataReader reader) => new()
        {
            Accepted          = reader.GetBool(),
            RefusalReason     = reader.GetString(),
            IsPvP             = reader.GetBool(),
            PlayerId          = reader.GetByte(),
            AssignedSlot      = reader.GetByte(),
            AssignedTeam      = reader.GetByte(),
            HostTeam          = reader.GetByte(),
            AssignedRole      = reader.GetByte(),
            MaxPlayers        = reader.GetByte(),
            ClientUidBase     = reader.GetInt(),
            StateRateHz       = reader.GetByte(),
        };
    }
}
