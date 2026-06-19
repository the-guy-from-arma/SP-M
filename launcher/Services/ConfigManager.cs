using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace SeapowerMultiplayer.Launcher.Services
{
    public class LauncherSettings
    {
        public string? GameDirectory { get; set; }
        public string Transport { get; set; } = "Steam";
        public bool IsHost { get; set; } = true;
        public string HostIP { get; set; } = "0.0.0.0";
        public int Port { get; set; } = 7777;
        public bool AutoConnect { get; set; } = false;
        public bool TimeVote { get; set; } = false;
        public bool PvP { get; set; } = false;
        public int MissileStateHz { get; set; } = 20;
        public int UnitStateHz { get; set; } = 10;
        public string PlayerName { get; set; } = Environment.UserName;
        public int PreferredSlot { get; set; } = 0;
        public int PreferredTeam { get; set; } = 255;
        public int PreferredRole { get; set; } = 0;
        public string PublicLobbyName { get; set; } = "Open Fleet";
        public string DirectoryUrl { get; set; } = LobbyServiceClient.DefaultServiceUrl;
        public bool MusicEnabled { get; set; } = true;
        public int MusicTrackIndex { get; set; } = 0;
        public double MusicVolume { get; set; } = 0.22;
        public bool MusicMuted { get; set; } = false;
        public string? AcknowledgedVersion { get; set; }
    }

    public class ConfigManager
    {
        private static readonly string ConfigDir =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                         "SeaPowerFourPlayer");

        private static readonly string ConfigPath =
            Path.Combine(ConfigDir, "launcher.json");

        private static readonly string LegacyConfigPath =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                         "SeapowerMultiplayer", "launcher.json");

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
        };

        public LauncherSettings Settings { get; private set; } = new();

        public void Load()
        {
            try
            {
                var sourcePath = File.Exists(ConfigPath)
                    ? ConfigPath
                    : LegacyConfigPath;
                if (File.Exists(sourcePath))
                {
                    var json = File.ReadAllText(sourcePath);
                    Settings = JsonSerializer.Deserialize<LauncherSettings>(json, JsonOptions)
                               ?? new LauncherSettings();
                }
                EnsureDefaults();
            }
            catch
            {
                Settings = new LauncherSettings();
                EnsureDefaults();
            }
        }

        private void EnsureDefaults()
        {
            if (string.IsNullOrWhiteSpace(Settings.PlayerName))
                Settings.PlayerName = Environment.UserName;
            if (string.IsNullOrWhiteSpace(Settings.PublicLobbyName))
                Settings.PublicLobbyName = "Open Fleet";
            if (string.IsNullOrWhiteSpace(Settings.DirectoryUrl))
                Settings.DirectoryUrl = LobbyServiceClient.DefaultServiceUrl;
            Settings.PreferredSlot = Settings.PreferredSlot is >= 0 and <= 4
                ? Settings.PreferredSlot
                : 0;
            Settings.PreferredTeam = Settings.PreferredTeam is 0 or 1 or 255
                ? Settings.PreferredTeam
                : 255;
            Settings.PreferredRole = Math.Clamp(Settings.PreferredRole, 0, 4);
            Settings.MusicTrackIndex = Math.Clamp(Settings.MusicTrackIndex, 0, 3);
            Settings.MusicVolume = Math.Clamp(Settings.MusicVolume, 0, 1);
        }

        public void Save()
        {
            Directory.CreateDirectory(ConfigDir);
            var json = JsonSerializer.Serialize(Settings, JsonOptions);
            File.WriteAllText(ConfigPath, json);
        }

        /// <summary>
        /// Write the BepInEx config file that the mod reads on startup.
        /// Uses the exact INI format BepInEx expects.
        /// </summary>
        public static void WriteBepInExConfig(string gameDir, LauncherSettings settings)
        {
            var configDir = Path.Combine(gameDir, "BepInEx", "config");
            Directory.CreateDirectory(configDir);

            var configPath = Path.Combine(configDir, "com.seapower.fourplayer.cfg");
            var content = $"""
                [Network]

                ## Network transport: LiteNetLib (direct IP) or Steam (P2P with invites)
                # Setting type: String
                # Default value: LiteNetLib
                Transport = {settings.Transport}

                ## Are you the host?
                # Setting type: Boolean
                # Default value: true
                IsHost = {settings.IsHost.ToString().ToLower()}

                ## Host IP address to connect to (client only)
                # Setting type: String
                # Default value: 127.0.0.1
                HostIP = {settings.HostIP}

                ## Network port
                # Setting type: Int32
                # Default value: 7777
                Port = {settings.Port}

                ## PvP mode: players control opposing sides
                # Setting type: Boolean
                # Default value: false
                PvP = {settings.PvP.ToString().ToLower()}

                ## Automatically connect on game start
                # Setting type: Boolean
                # Default value: false
                AutoConnect = {settings.AutoConnect.ToString().ToLower()}

                ## Time vote mode: both players must agree on time compression changes
                # Setting type: Boolean
                # Default value: false
                TimeVote = {settings.TimeVote.ToString().ToLower()}

                [Lobby]

                ## Name shown in the four-player roster
                # Setting type: String
                # Default value: {Environment.UserName}
                PlayerName = {settings.PlayerName}

                ## 0 = automatic, or request slot 2 through 4
                # Setting type: Int32
                # Default value: 0
                PreferredSlot = {settings.PreferredSlot}

                ## 0 = Blue, 1 = Red, 255 = automatic
                # Setting type: Int32
                # Default value: 255
                PreferredTeam = {settings.PreferredTeam}

                ## 0 = Any, 1 = Surface, 2 = Submarine, 3 = Air, 4 = Land
                # Setting type: Int32
                # Default value: 0
                PreferredRole = {settings.PreferredRole}

                ## Operation name shown in the public launcher lobby browser
                # Setting type: String
                # Default value: Open Fleet
                PublicLobbyName = {settings.PublicLobbyName}

                [Services]

                ## Railway lobby directory and telemetry service
                # Setting type: String
                # Default value: {LobbyServiceClient.DefaultServiceUrl}
                DirectoryUrl = {settings.DirectoryUrl}

                [Sync]

                ## Host missile state stream rate in Hz (1-60, default 20)
                # Setting type: Int32
                # Default value: 20
                MissileStateHz = {settings.MissileStateHz}

                ## Host unit/torpedo state stream rate in Hz (1-60, default 10)
                # Setting type: Int32
                # Default value: 10
                UnitStateHz = {settings.UnitStateHz}

                """;

            File.WriteAllText(configPath, content);
        }

    }
}
