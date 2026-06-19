using LiteNetLib.Utils;

namespace SeapowerMultiplayer.Messages
{
    /// <summary>
    /// Client → host, first message after transport connect.
    /// Carries protocol identity + session mode for the host's accept/refuse verdict.
    /// </summary>
    public class HelloMessage : INetMessage
    {
        public ushort ProtocolVersion;
        public string PluginVersion = "";
        public bool   IsPvP;
        public string PlayerName = "";
        public byte   RequestedSlot;
        public byte   RequestedTeam;
        public byte   RequestedRole;

        public MessageType Type => MessageType.Hello;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(ProtocolVersion);
            writer.Put(PluginVersion);
            writer.Put(IsPvP);
            writer.Put(PlayerName);
            writer.Put(RequestedSlot);
            writer.Put(RequestedTeam);
            writer.Put(RequestedRole);
        }

        public static HelloMessage Deserialize(NetDataReader reader) => new()
        {
            ProtocolVersion = reader.GetUShort(),
            PluginVersion   = reader.GetString(),
            IsPvP           = reader.GetBool(),
            PlayerName      = reader.GetString(),
            RequestedSlot   = reader.GetByte(),
            RequestedTeam   = reader.GetByte(),
            RequestedRole   = reader.GetByte(),
        };
    }
}
