using System;
using System.IO;
using Newtonsoft.Json;

namespace SeapowerMultiplayer.Transport
{
    internal sealed class LauncherGameCommand
    {
        public string Action { get; set; } = "";
        public string LobbyId { get; set; } = "";
        public string LobbyName { get; set; } = "";
        public bool OpenInviteOverlay { get; set; }
        public DateTimeOffset CreatedUtc { get; set; }

        public static LauncherGameCommand? Consume()
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SeaPowerFourPlayer",
                "pending-game-command.json");
            if (!File.Exists(path))
                return null;

            try
            {
                var command = JsonConvert.DeserializeObject<LauncherGameCommand>(
                    File.ReadAllText(path));
                File.Delete(path);
                if (command == null ||
                    DateTimeOffset.UtcNow - command.CreatedUtc > TimeSpan.FromMinutes(5))
                    return null;
                return command;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning(
                    $"[LauncherCommand] Could not consume pending command: {ex.Message}");
                try { File.Delete(path); } catch { }
                return null;
            }
        }
    }
}
