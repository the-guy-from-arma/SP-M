using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;

namespace SeapowerMultiplayer.Launcher.Services
{
    public sealed class PublicLobby
    {
        public string SteamLobbyId { get; set; } = "";
        public string HostName { get; set; } = "";
        public string LobbyName { get; set; } = "";
        public int PlayerCount { get; set; }
        public int MaxPlayers { get; set; } = 4;
        public bool PvP { get; set; }
        public int HostTeam { get; set; }
        public string Scenario { get; set; } = "";
        public string Region { get; set; } = "";
        public string PluginVersion { get; set; } = "";
        public int Protocol { get; set; }
        public DateTimeOffset LastHeartbeat { get; set; }

        public string Crew => $"{PlayerCount}/{MaxPlayers}";
        public string Mode => PvP ? "PVP" : "CO-OP";
        public string Side => HostTeam == 1 ? "RED HOST" : "BLUE HOST";
    }

    public sealed class DirectoryServiceConfig
    {
        public string Announcement { get; set; } =
            "JOIN THE SEAS // PUBLIC OPERATIONS ONLINE";
        public bool MaintenanceMode { get; set; }
        public bool PublicLobbiesEnabled { get; set; } = true;
        public string RequiredLauncherVersion { get; set; } = "";
        public string RequiredPluginVersion { get; set; } = "";
    }

    public sealed class LobbyDirectoryResult
    {
        public List<PublicLobby> Lobbies { get; set; } = new();
        public DateTimeOffset GeneratedAt { get; set; }
    }

    public static class LobbyServiceClient
    {
        public const string DefaultServiceUrl =
            "https://spdash-production.up.railway.app";

        private static readonly HttpClient Http = new()
        {
            Timeout = TimeSpan.FromSeconds(8),
        };

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
        };

        static LobbyServiceClient()
        {
            Http.DefaultRequestHeaders.UserAgent.ParseAdd(
                "SeaPowerFourPlayerLauncher/" + LauncherVersions.LauncherVersion);
            Http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        }

        public static async Task<LobbyDirectoryResult> GetLobbiesAsync(string serviceUrl)
        {
            var url = BuildUrl(
                serviceUrl,
                $"/api/v1/lobbies?pluginVersion={Uri.EscapeDataString(LauncherVersions.PluginNumericVersion)}&protocol={LauncherVersions.ProtocolVersion}");
            using var response = await Http.GetAsync(url);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<LobbyDirectoryResult>(JsonOptions)
                   ?? new LobbyDirectoryResult();
        }

        public static async Task<DirectoryServiceConfig?> GetConfigAsync(string serviceUrl)
        {
            try
            {
                return await Http.GetFromJsonAsync<DirectoryServiceConfig>(
                    BuildUrl(serviceUrl, "/api/v1/config"),
                    JsonOptions);
            }
            catch
            {
                return null;
            }
        }

        public static async Task TrackAsync(
            string serviceUrl,
            string eventName,
            string lobbyId = "",
            string detail = "")
        {
            try
            {
                using var response = await Http.PostAsJsonAsync(
                    BuildUrl(serviceUrl, "/api/v1/telemetry"),
                    new
                    {
                        @event = eventName,
                        lobbyId,
                        version = LauncherVersions.LauncherVersion,
                        protocol = LauncherVersions.ProtocolVersion,
                        detail,
                    });
            }
            catch
            {
                // Telemetry is optional and must never block the launcher.
            }
        }

        private static string BuildUrl(string configured, string path)
        {
            var root = ServiceEndpointResolver.Resolve(configured);
            return root.TrimEnd('/') + path;
        }
    }
}
