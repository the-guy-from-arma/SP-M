using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Windows;
using SeapowerMultiplayer.Launcher.Services;

namespace SeapowerMultiplayer.Launcher
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            LauncherDiagnostics.Trace("App.OnStartup entered.");

            if (TryRunInstallSmoke(e.Args))
                return;

            LauncherDiagnostics.Trace("Constructing MainWindow.");
            MainWindow = new MainWindow();
            LauncherDiagnostics.Trace("MainWindow constructed; calling Show.");
            MainWindow.Show();
            LauncherDiagnostics.Trace("MainWindow.Show returned.");
        }

        private bool TryRunInstallSmoke(string[] args)
        {
            if (args.Length < 2 ||
                !args[0].Equals("--smoke-install", StringComparison.OrdinalIgnoreCase))
                return false;

            var target = Path.GetFullPath(args[1]);
            try
            {
                Directory.CreateDirectory(target);
                File.WriteAllBytes(Path.Combine(target, "Sea Power.exe"), Array.Empty<byte>());

                var progress = new Progress<string>(_ => { });
                Installer.InstallAsync(target, progress).GetAwaiter().GetResult();

                var settings = new LauncherSettings
                {
                    PlayerName = "Packaged EXE Smoke Test",
                    Transport = "LiteNetLib",
                    IsHost = false,
                    HostIP = "127.0.0.1",
                    Port = 7777,
                    PreferredSlot = 0,
                    PreferredTeam = 255,
                    PreferredRole = 0,
                };
                ConfigManager.WriteBepInExConfig(target, settings);

                var required = new Dictionary<string, string>
                {
                    ["bepInExCore"] = Path.Combine(target, "BepInEx", "core", "BepInEx.dll"),
                    ["storedProxy"] = Path.Combine(target, "BepInEx", "proxy", "winhttp.dll"),
                    ["storedDoorstop"] = Path.Combine(target, "BepInEx", "proxy", "doorstop_config.ini"),
                    ["plugin"] = Path.Combine(target, "BepInEx", "plugins", "SeaPowerFourPlayer.dll"),
                    ["liteNetLib"] = Path.Combine(target, "BepInEx", "plugins", "LiteNetLib.dll"),
                    ["config"] = Path.Combine(target, "BepInEx", "config", "com.seapower.fourplayer.cfg"),
                };

                foreach (var item in required)
                {
                    if (!File.Exists(item.Value))
                        throw new FileNotFoundException($"Packaged EXE did not install {item.Key}.", item.Value);
                }

                var mediaResources = new Dictionary<string, string>
                {
                    ["steamTrailer"] =
                        "pack://application:,,,/Assets/Video/sea-power-launch-trailer.mp4",
                    ["crashingDown"] =
                        "pack://application:,,,/Assets/Music/01-crashing-down.mp3",
                    ["caliMan"] =
                        "pack://application:,,,/Assets/Music/02-cali-man.mp3",
                    ["gta6"] =
                        "pack://application:,,,/Assets/Music/03-gta-6.mp3",
                    ["imIll"] =
                        "pack://application:,,,/Assets/Music/04-im-ill.mp3",
                };
                var embeddedMediaBytes = new Dictionary<string, long>();
                foreach (var item in mediaResources)
                {
                    var resource = GetResourceStream(new Uri(item.Value));
                    if (resource == null)
                        throw new FileNotFoundException(
                            $"Packaged EXE did not contain media resource {item.Key}.");

                    using (resource.Stream)
                        embeddedMediaBytes[item.Key] = resource.Stream.Length;
                }

                var report = new
                {
                    ok = true,
                    launcherVersion = LauncherVersions.LauncherVersion,
                    pluginVersion = LauncherVersions.PluginVersion,
                    target,
                    installed = required,
                    embeddedMediaBytes,
                    activeBootstrapLeftInRoot =
                        File.Exists(Path.Combine(target, "winhttp.dll")) ||
                        File.Exists(Path.Combine(target, "doorstop_config.ini")),
                };
                File.WriteAllText(
                    Path.Combine(target, "launcher-smoke-result.json"),
                    JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));

                Environment.ExitCode = 0;
            }
            catch (Exception ex)
            {
                Directory.CreateDirectory(target);
                File.WriteAllText(Path.Combine(target, "launcher-smoke-failure.txt"), ex.ToString());
                Environment.ExitCode = 1;
            }

            Shutdown(Environment.ExitCode);
            return true;
        }
    }
}
