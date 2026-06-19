using SeapowerMultiplayer.Launcher.Services;

var testRoot = Path.Combine(Path.GetTempPath(), $"sp4p-launcher-smoke-{Guid.NewGuid():N}");
Directory.CreateDirectory(testRoot);

try
{
    var legacyPluginPath = Path.Combine(
        testRoot,
        "BepInEx",
        "plugins",
        "nested",
        "renamed-legacy-network.dll");
    Directory.CreateDirectory(Path.GetDirectoryName(legacyPluginPath)!);
    File.WriteAllText(
        legacyPluginPath,
        "fixture com.seapowermultiplayer.plugin fixture");
    if (!Installer.HasActiveConflictingPlugin(testRoot))
        throw new InvalidOperationException("Conflict detector missed the legacy plugin.");

    var progress = new Progress<string>(_ => { });
    await Installer.InstallAsync(testRoot, progress, skipGameRunningCheck: true);

    Require("BepInEx core", Path.Combine(testRoot, "BepInEx", "core", "BepInEx.dll"));
    Require("stored winhttp proxy", Path.Combine(testRoot, "BepInEx", "proxy", "winhttp.dll"));
    Require("stored Doorstop config", Path.Combine(testRoot, "BepInEx", "proxy", "doorstop_config.ini"));
    Require("four-player plugin", Path.Combine(testRoot, "BepInEx", "plugins", "SeaPowerFourPlayer.dll"));
    Require("LiteNetLib", Path.Combine(testRoot, "BepInEx", "plugins", "LiteNetLib.dll"));
    if (File.Exists(legacyPluginPath))
        throw new InvalidOperationException("Installer left a renamed legacy plugin active.");
    if (Installer.HasActiveConflictingPlugin(testRoot))
        throw new InvalidOperationException("Installer left an active conflicting plugin.");
    var backupDir = Path.Combine(
        testRoot,
        "BepInEx",
        "plugins",
        "SeaPowerFourPlayer-backup");
    if (!Directory.Exists(backupDir) ||
        !Directory.EnumerateFiles(backupDir, "*.disabled").Any())
        throw new InvalidOperationException("Installer did not quarantine the legacy plugin.");

    if (File.Exists(Path.Combine(testRoot, "winhttp.dll")) ||
        File.Exists(Path.Combine(testRoot, "doorstop_config.ini")))
        throw new InvalidOperationException("Installer left active bootstrap files in the fake game root.");

    var settings = new LauncherSettings
    {
        PlayerName = "Smoke Tester",
        PreferredSlot = 3,
        PreferredTeam = 1,
        PreferredRole = 2,
        Transport = "LiteNetLib",
        IsHost = false,
        HostIP = "127.0.0.1",
        Port = 7777,
        PublicLobbyName = "Smoke Fleet",
        DirectoryUrl = "https://example.invalid",
    };
    ConfigManager.WriteBepInExConfig(testRoot, settings);

    var configPath = Path.Combine(testRoot, "BepInEx", "config", "com.seapower.fourplayer.cfg");
    Require("four-player config", configPath);
    var config = File.ReadAllText(configPath);
    foreach (var expected in new[]
             {
                 "PlayerName = Smoke Tester",
                 "PreferredSlot = 3",
                 "PreferredTeam = 1",
                 "PreferredRole = 2",
                 "PublicLobbyName = Smoke Fleet",
                 "DirectoryUrl = https://example.invalid",
             })
    {
        if (!config.Contains(expected, StringComparison.Ordinal))
            throw new InvalidOperationException($"Config did not contain: {expected}");
    }

    await Installer.RepairAsync(testRoot, progress, skipGameRunningCheck: true);
    Require("repaired four-player plugin",
        Path.Combine(testRoot, "BepInEx", "plugins", "SeaPowerFourPlayer.dll"));

    Console.WriteLine("Launcher install/repair smoke tests passed.");
}
finally
{
    if (Directory.Exists(testRoot))
        Directory.Delete(testRoot, recursive: true);
}

static void Require(string label, string path)
{
    if (!File.Exists(path))
        throw new FileNotFoundException($"Missing {label}", path);
}
