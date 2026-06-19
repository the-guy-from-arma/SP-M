using System;

namespace SeapowerMultiplayer.Net2
{
    public static class HandshakeCompatibility
    {
        public static string? GetRefusalReason(
            ushort clientProtocol,
            string? clientPluginVersion,
            bool clientIsPvP,
            bool hostIsPvP)
        {
            if (clientProtocol != ProtocolInfo.ProtocolVersion)
            {
                return
                    $"Protocol mismatch: host {ProtocolInfo.ProtocolVersion}, " +
                    $"client {clientProtocol}. Update and repair the launcher on every PC.";
            }

            if (!string.Equals(
                    clientPluginVersion,
                    PluginInfo.PLUGIN_VERSION,
                    StringComparison.OrdinalIgnoreCase))
            {
                return
                    $"Plugin version mismatch: host {PluginInfo.PLUGIN_VERSION}, " +
                    $"client {clientPluginVersion ?? "unknown"}. " +
                    "Update and repair the launcher on every PC.";
            }

            if (clientIsPvP != hostIsPvP)
                return "PvP/co-op mode mismatch.";

            return null;
        }
    }
}
