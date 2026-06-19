using LiteNetLib.Utils;

namespace SeapowerMultiplayer.Messages
{
    /// <summary>
    /// Sent client → host after the client has finished loading a session.
    /// Host tracks every connected client and waits until all have loaded.
    /// </summary>
    public class SessionReadyMessage : INetMessage
    {
        public MessageType Type => MessageType.SessionReady;

        public bool IsReady = true;

        public void Serialize(NetDataWriter w)
        {
            w.Put(IsReady);
        }

        public static SessionReadyMessage Deserialize(NetDataReader r) => new SessionReadyMessage
        {
            IsReady = r.GetBool(),
        };
    }
}
