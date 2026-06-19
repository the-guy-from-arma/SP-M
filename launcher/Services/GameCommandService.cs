using System;
using System.IO;
using System.Text.Json;

namespace SeapowerMultiplayer.Launcher.Services
{
    public static class GameCommandService
    {
        private static readonly string CommandPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SeaPowerFourPlayer",
            "pending-game-command.json");

        public static void WriteCreatePublicLobby(string lobbyName, bool openInviteOverlay)
            => Write(new
            {
                action = "create-public",
                lobbyId = "",
                lobbyName,
                openInviteOverlay,
                createdUtc = DateTimeOffset.UtcNow,
            });

        public static void WriteJoinLobby(string lobbyId)
            => Write(new
            {
                action = "join",
                lobbyId,
                lobbyName = "",
                openInviteOverlay = false,
                createdUtc = DateTimeOffset.UtcNow,
            });

        private static void Write(object command)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CommandPath)!);
            var tempPath = CommandPath + ".tmp";
            File.WriteAllText(
                tempPath,
                JsonSerializer.Serialize(command, new JsonSerializerOptions
                {
                    WriteIndented = true,
                }));
            File.Move(tempPath, CommandPath, overwrite: true);
        }
    }
}
