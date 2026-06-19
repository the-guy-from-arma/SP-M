using LiteNetLib.Utils;

namespace SeapowerMultiplayer.Messages
{
    public sealed class LobbyUpdateMessage : INetMessage
    {
        public byte RequestedSlot;
        public byte RequestedTeam;
        public byte RequestedRole;
        public bool Ready;

        public MessageType Type => MessageType.LobbyUpdate;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(RequestedSlot);
            writer.Put(RequestedTeam);
            writer.Put(RequestedRole);
            writer.Put(Ready);
        }

        public static LobbyUpdateMessage Deserialize(NetDataReader reader) => new()
        {
            RequestedSlot = reader.GetByte(),
            RequestedTeam = reader.GetByte(),
            RequestedRole = reader.GetByte(),
            Ready = reader.GetBool(),
        };
    }

    public sealed class PlayerRosterMessage : INetMessage
    {
        public const int SlotCount = 4;

        public readonly byte[] PlayerIds = new byte[SlotCount];
        public readonly bool[] Connected = new bool[SlotCount];
        public readonly bool[] Ready = new bool[SlotCount];
        public readonly byte[] Teams = new byte[SlotCount];
        public readonly byte[] Roles = new byte[SlotCount];
        public readonly string[] Names = new string[SlotCount];

        public MessageType Type => MessageType.PlayerRoster;

        public void Serialize(NetDataWriter writer)
        {
            for (int i = 0; i < SlotCount; i++)
            {
                writer.Put(PlayerIds[i]);
                writer.Put(Connected[i]);
                writer.Put(Ready[i]);
                writer.Put(Teams[i]);
                writer.Put(Roles[i]);
                writer.Put(Names[i] ?? "");
            }
        }

        public static PlayerRosterMessage Deserialize(NetDataReader reader)
        {
            var message = new PlayerRosterMessage();
            for (int i = 0; i < SlotCount; i++)
            {
                message.PlayerIds[i] = reader.GetByte();
                message.Connected[i] = reader.GetBool();
                message.Ready[i] = reader.GetBool();
                message.Teams[i] = reader.GetByte();
                message.Roles[i] = reader.GetByte();
                message.Names[i] = reader.GetString();
            }
            return message;
        }
    }
}
