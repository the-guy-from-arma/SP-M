using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SeapowerMultiplayer.Net2;
using Steamworks;

namespace SeapowerMultiplayer.Transport
{
    /// <summary>
    /// Registers Steam lobbies in the Railway discovery directory. Gameplay never
    /// travels through this service; it only carries lobby metadata and telemetry.
    /// </summary>
    public static class LobbyDirectoryClient
    {
        private static readonly HttpClient Http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(10),
        };

        private static readonly object Gate = new object();
        private static string _heartbeatToken = "";
        private static string _steamLobbyId = "";
        private static DateTime _nextHeartbeatUtc;
        private static DateTime _nextScenarioCheckUtc;
        private static bool _requestInFlight;
        private static string _lastScenario = "Waiting for mission";
        private static string _serviceRoot =
            "https://spdash-production.up.railway.app";

        public static void Configure(string serviceUrl)
        {
            var environmentOverride =
                Environment.GetEnvironmentVariable("SP4P_SERVICE_URL");
            var root = string.IsNullOrWhiteSpace(environmentOverride)
                ? serviceUrl
                : environmentOverride;
            _serviceRoot = string.IsNullOrWhiteSpace(root)
                ? "https://spdash-production.up.railway.app"
                : root.TrimEnd('/');
        }

        public static bool IsRegistered
        {
            get
            {
                lock (Gate)
                    return !string.IsNullOrEmpty(_heartbeatToken);
            }
        }

        public static void RegisterPublicLobby(CSteamID lobbyId)
        {
            _lastScenario = ResolveScenarioName();
            var registration = new LobbyRegistration
            {
                SteamLobbyId = lobbyId.ToString(),
                HostSteamId = SteamUser.GetSteamID().ToString(),
                HostName = SteamFriends.GetPersonaName(),
                LobbyName = Plugin.Instance.CfgPublicLobbyName.Value,
                PlayerCount = Math.Max(1, SteamMatchmaking.GetNumLobbyMembers(lobbyId)),
                MaxPlayers = FourPlayerLobby.MaxPlayers,
                PvP = Plugin.Instance.CfgPvP.Value,
                HostTeam = Math.Max(0, Math.Min(1, Plugin.Instance.CfgPreferredTeam.Value)),
                Scenario = _lastScenario,
                Region = "Automatic",
                PluginVersion = PluginInfo.PLUGIN_VERSION,
                Protocol = ProtocolInfo.ProtocolVersion,
            };
            SteamMatchmaking.SetLobbyData(lobbyId, "scenario", _lastScenario);
            _ = RegisterAsync(registration);
        }

        public static void Tick(CSteamID lobbyId)
        {
            if (lobbyId == CSteamID.Nil)
                return;

            if (DateTime.UtcNow >= _nextScenarioCheckUtc)
            {
                _nextScenarioCheckUtc = DateTime.UtcNow.AddSeconds(2);
                var scenario = ResolveScenarioName();
                if (!string.Equals(scenario, _lastScenario, StringComparison.Ordinal))
                {
                    _lastScenario = scenario;
                    SteamMatchmaking.SetLobbyData(lobbyId, "scenario", scenario);
                    _nextHeartbeatUtc = DateTime.MinValue;
                    Plugin.Log.LogInfo($"[Directory] Mission changed to '{scenario}'.");
                }
            }

            if (lobbyId == CSteamID.Nil || DateTime.UtcNow < _nextHeartbeatUtc)
                return;

            string token;
            lock (Gate)
            {
                if (_requestInFlight || string.IsNullOrEmpty(_heartbeatToken))
                    return;
                _requestInFlight = true;
                token = _heartbeatToken;
            }

            _nextHeartbeatUtc = DateTime.UtcNow.AddSeconds(20);
            var heartbeat = new LobbyHeartbeat
            {
                PlayerCount = Math.Max(1, SteamMatchmaking.GetNumLobbyMembers(lobbyId)),
                Scenario = _lastScenario,
            };
            _ = HeartbeatAsync(lobbyId.ToString(), token, heartbeat);
        }

        public static void RemovePublicLobby()
        {
            string lobbyId;
            string token;
            lock (Gate)
            {
                lobbyId = _steamLobbyId;
                token = _heartbeatToken;
                _steamLobbyId = "";
                _heartbeatToken = "";
                _requestInFlight = false;
                _lastScenario = "Waiting for mission";
                _nextScenarioCheckUtc = DateTime.MinValue;
            }

            if (!string.IsNullOrEmpty(lobbyId) && !string.IsNullOrEmpty(token))
                _ = DeleteAsync(lobbyId, token);
        }

        public static void Track(string eventName, string lobbyId = "", string detail = "")
        {
            var payload = new TelemetryPayload
            {
                Event = eventName,
                LobbyId = lobbyId,
                Version = PluginInfo.PLUGIN_VERSION,
                Protocol = ProtocolInfo.ProtocolVersion,
                Detail = detail,
            };
            _ = SendTelemetryAsync(payload);
        }

        private static async Task SendTelemetryAsync(TelemetryPayload payload)
        {
            using (var response = await PostJsonAsync("/api/v1/telemetry", payload))
            {
            }
        }

        private static async Task RegisterAsync(LobbyRegistration registration)
        {
            lock (Gate)
            {
                if (_requestInFlight)
                    return;
                _requestInFlight = true;
            }

            try
            {
                using (var response = await PostJsonAsync("/api/v1/lobbies", registration))
                {
                if (response == null)
                    return;

                var json = JObject.Parse(await response.Content.ReadAsStringAsync());
                var token = json.Value<string>("heartbeatToken") ?? "";
                if (string.IsNullOrEmpty(token))
                    throw new InvalidOperationException("Directory returned no heartbeat token.");

                lock (Gate)
                {
                    _steamLobbyId = registration.SteamLobbyId;
                    _heartbeatToken = token;
                    _nextHeartbeatUtc = DateTime.UtcNow.AddSeconds(20);
                }
                Plugin.Log.LogInfo($"[Directory] Public lobby {registration.SteamLobbyId} registered.");
                Track("lobby_created", registration.SteamLobbyId);
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[Directory] Lobby registration failed: {ex.Message}");
            }
            finally
            {
                lock (Gate)
                    _requestInFlight = false;
            }
        }

        private static async Task HeartbeatAsync(
            string lobbyId,
            string token,
            LobbyHeartbeat heartbeat)
        {
            try
            {
                using (var request = new HttpRequestMessage(
                    HttpMethod.Put,
                    BuildUrl($"/api/v1/lobbies/{lobbyId}/heartbeat")))
                {
                    request.Headers.Authorization =
                        new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                    request.Content = JsonContent(heartbeat);
                    using (var response = await Http.SendAsync(request))
                        response.EnsureSuccessStatusCode();
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[Directory] Lobby heartbeat failed: {ex.Message}");
            }
            finally
            {
                lock (Gate)
                    _requestInFlight = false;
            }
        }

        private static async Task DeleteAsync(string lobbyId, string token)
        {
            try
            {
                using (var request = new HttpRequestMessage(
                    HttpMethod.Delete,
                    BuildUrl($"/api/v1/lobbies/{lobbyId}")))
                {
                    request.Headers.Authorization =
                        new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                    using (var response = await Http.SendAsync(request))
                    {
                        if (!response.IsSuccessStatusCode)
                            Plugin.Log.LogWarning(
                                $"[Directory] Lobby removal returned {(int)response.StatusCode}.");
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[Directory] Lobby removal failed: {ex.Message}");
            }
        }

        private static async Task<HttpResponseMessage?> PostJsonAsync(string path, object payload)
        {
            try
            {
                var response = await Http.PostAsync(BuildUrl(path), JsonContent(payload));
                response.EnsureSuccessStatusCode();
                return response;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[Directory] POST {path} failed: {ex.Message}");
                return null;
            }
        }

        private static StringContent JsonContent(object payload) =>
            new StringContent(
                JsonConvert.SerializeObject(payload),
                Encoding.UTF8,
                "application/json");

        private static string BuildUrl(string path)
        {
            return _serviceRoot + path;
        }

        private static string ResolveScenarioName()
        {
            try
            {
                var missionPath = SeaPower.Globals.currentMissionFilePath;
                if (string.IsNullOrWhiteSpace(missionPath))
                    return "Waiting for mission";

                if (Path.GetExtension(missionPath).Equals(
                        ".sav",
                        StringComparison.OrdinalIgnoreCase) &&
                    File.Exists(missionPath))
                {
                    var saveContent = File.ReadAllText(missionPath);
                    var baseFile = Regex.Match(
                        saveContent,
                        @"(?im)^\s*BaseFile\s*=\s*(.+?)\s*$");
                    if (baseFile.Success)
                        missionPath = baseFile.Groups[1].Value.Trim();
                }

                var name = Path.GetFileNameWithoutExtension(missionPath);
                if (string.IsNullOrWhiteSpace(name))
                    return "Mission loaded";

                name = Regex.Replace(name, @"[_-]+", " ");
                name = Regex.Replace(name, @"\s+", " ").Trim();
                return name.Length <= 80 ? name : name.Substring(0, 80);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning(
                    $"[Directory] Could not read current mission name: {ex.Message}");
                return "Mission loaded";
            }
        }

        private sealed class LobbyRegistration
        {
            public string SteamLobbyId = "";
            public string HostSteamId = "";
            public string HostName = "";
            public string LobbyName = "";
            public int PlayerCount;
            public int MaxPlayers;
            public bool PvP;
            public int HostTeam;
            public string Scenario = "";
            public string Region = "";
            public string PluginVersion = "";
            public int Protocol;
        }

        private sealed class LobbyHeartbeat
        {
            public int PlayerCount;
            public string Scenario = "";
        }

        private sealed class TelemetryPayload
        {
            public string Event = "";
            public string LobbyId = "";
            public string Version = "";
            public int Protocol;
            public string Detail = "";
        }
    }
}
