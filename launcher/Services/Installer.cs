using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace SeapowerMultiplayer.Launcher.Services
{
    public static class Installer
    {
        private const string ProxyDir = "BepInEx\\proxy";
        private const string PluginsDir = "BepInEx\\plugins";
        private const string LogFile = "BepInEx\\LogOutput.log";
        private const int InitTimeoutMs = 90_000;

        public static bool HasActiveConflictingPlugin(string gameDir)
        {
            var pluginsPath = Path.Combine(gameDir, PluginsDir);
            if (!Directory.Exists(pluginsPath))
                return false;

            var backupDir = Path.Combine(pluginsPath, "SeaPowerFourPlayer-backup");
            var backupRoot = Path.GetFullPath(backupDir) + Path.DirectorySeparatorChar;
            return Directory.EnumerateFiles(
                    pluginsPath,
                    "*.dll",
                    SearchOption.AllDirectories)
                .Where(path => !Path.GetFullPath(path).StartsWith(
                    backupRoot,
                    StringComparison.OrdinalIgnoreCase))
                .Any(path =>
                    ContainsAscii(path, "com.seapowermultiplayer.plugin") &&
                    !ContainsAscii(path, "com.seapower.fourplayer"));
        }

        public static async Task InstallAsync(string gameDir, IProgress<string> progress)
        {
            EnsureGameClosed();

            // Step 1: Extract BepInEx
            progress.Report("Extracting BepInEx...");
            ExtractBepInEx(gameDir);

            // Step 2: Stash proxy files
            progress.Report("Configuring proxy...");
            StashProxy(gameDir);

            // Step 3: Install the embedded four-player payload
            progress.Report("Installing four-player multiplayer...");
            InstallModDlls(gameDir);

            progress.Report("Installation complete — ready to launch.");
            await Task.CompletedTask;
        }

        public static async Task RepairAsync(string gameDir, IProgress<string> progress)
        {
            EnsureGameClosed();

            // Re-stash proxy if needed
            progress.Report("Checking proxy configuration...");
            var proxyPath = Path.Combine(gameDir, ProxyDir);
            if (!File.Exists(Path.Combine(proxyPath, "winhttp.dll")) ||
                !File.Exists(Path.Combine(proxyPath, "doorstop_config.ini")))
            {
                // If proxy DLL is in game root, stash it
                if (File.Exists(Path.Combine(gameDir, "winhttp.dll")))
                    StashProxy(gameDir);
                else
                {
                    // Need to re-extract BepInEx to get proxy files
                    progress.Report("Re-extracting BepInEx...");
                    ExtractBepInEx(gameDir);
                    StashProxy(gameDir);
                }
            }

            // Re-install mod DLLs
            progress.Report("Updating four-player multiplayer...");
            InstallModDlls(gameDir);

            progress.Report("Repair complete — ready to launch.");
            await Task.CompletedTask;
        }

        private static void ExtractBepInEx(string gameDir)
        {
            using var stream = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream("BepInEx.zip")
                ?? throw new InvalidOperationException(
                    "BepInEx.zip not found as embedded resource. " +
                    "Place BepInEx_x64_5.4.23.2.zip in the Resources/ folder and rebuild.");

            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
            foreach (var entry in archive.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name)) continue; // directory entry

                var destPath = Path.Combine(gameDir, entry.FullName);
                var destDir = Path.GetDirectoryName(destPath)!;
                Directory.CreateDirectory(destDir);

                entry.ExtractToFile(destPath, overwrite: true);
            }
        }

        private static void StashProxy(string gameDir)
        {
            var proxyPath = Path.Combine(gameDir, ProxyDir);
            Directory.CreateDirectory(proxyPath);

            MoveIfExists(
                Path.Combine(gameDir, "winhttp.dll"),
                Path.Combine(proxyPath, "winhttp.dll"));
            MoveIfExists(
                Path.Combine(gameDir, "doorstop_config.ini"),
                Path.Combine(proxyPath, "doorstop_config.ini"));
        }

        private static async Task InitializeBepInEx(string gameDir, IProgress<string> progress)
        {
            var proxyDir = Path.Combine(gameDir, ProxyDir);

            // Temporarily place proxy in game root
            File.Copy(Path.Combine(proxyDir, "winhttp.dll"),
                       Path.Combine(gameDir, "winhttp.dll"), overwrite: true);
            File.Copy(Path.Combine(proxyDir, "doorstop_config.ini"),
                       Path.Combine(gameDir, "doorstop_config.ini"), overwrite: true);

            // Delete old log so we can detect fresh init
            var logPath = Path.Combine(gameDir, LogFile);
            if (File.Exists(logPath))
                File.Delete(logPath);

            Process? proc = null;
            try
            {
                proc = Process.Start(new ProcessStartInfo
                {
                    FileName = Path.Combine(gameDir, "Sea Power.exe"),
                    WorkingDirectory = gameDir,
                    UseShellExecute = true,
                });

                if (proc == null)
                    throw new InvalidOperationException("Failed to start Sea Power.exe");

                // Wait for BepInEx to initialize.
                // Steam may restart the game (original process exits, new one spawns),
                // so we monitor the log file rather than relying on the process handle.
                var sw = Stopwatch.StartNew();
                bool initialized = false;
                while (sw.ElapsedMilliseconds < InitTimeoutMs)
                {
                    await Task.Delay(1000);

                    if (File.Exists(logPath))
                    {
                        try
                        {
                            using var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                            using var sr = new StreamReader(fs);
                            var logContent = sr.ReadToEnd();
                            if (logContent.Contains("Chainloader startup complete"))
                            {
                                progress.Report("BepInEx initialized successfully. Closing game...");
                                await Task.Delay(2000);
                                initialized = true;
                                break;
                            }
                        }
                        catch (IOException)
                        {
                            // File might be locked - retry next iteration
                        }
                    }
                }

                if (!initialized)
                    progress.Report("BepInEx initialization timed out. Closing game...");
            }
            finally
            {
                proc?.Dispose();

                // Kill all Sea Power processes (original + any Steam-relaunched ones)
                await KillAllGameProcesses();

                // Remove proxy from game root
                TryDelete(Path.Combine(gameDir, "winhttp.dll"));
                TryDelete(Path.Combine(gameDir, "doorstop_config.ini"));
            }
        }

        private static void InstallModDlls(string gameDir)
        {
            var pluginsPath = Path.Combine(gameDir, PluginsDir);
            Directory.CreateDirectory(pluginsPath);

            BackupAllConflictingPlugins(pluginsPath);
            BackupConflictingPlugin(pluginsPath, "SeaPowerFourPlayer.dll");

            ExtractEmbeddedResource("SeaPowerFourPlayer.dll",
                Path.Combine(pluginsPath, "SeaPowerFourPlayer.dll"));
            ExtractEmbeddedResource("LiteNetLib.dll",
                Path.Combine(pluginsPath, "LiteNetLib.dll"));

            VerifyInstalledPlugins(pluginsPath);

            var chainloaderCache = Path.Combine(gameDir, "BepInEx", "cache", "chainloader_typeloader.dat");
            TryDelete(chainloaderCache);
        }

        private static void BackupAllConflictingPlugins(string pluginsPath)
        {
            var backupDir = Path.Combine(pluginsPath, "SeaPowerFourPlayer-backup");
            Directory.CreateDirectory(backupDir);

            foreach (var file in Directory.EnumerateFiles(
                         pluginsPath,
                         "*.dll",
                         SearchOption.AllDirectories))
            {
                var fullPath = Path.GetFullPath(file);
                if (fullPath.StartsWith(
                        Path.GetFullPath(backupDir) + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!ContainsAscii(file, "com.seapowermultiplayer.plugin") ||
                    ContainsAscii(file, "com.seapower.fourplayer"))
                    continue;

                BackupPluginFile(file, backupDir);
            }
        }

        private static void BackupConflictingPlugin(string pluginsPath, string fileName)
        {
            var source = Path.Combine(pluginsPath, fileName);
            if (!File.Exists(source))
                return;

            var backupDir = Path.Combine(pluginsPath, "SeaPowerFourPlayer-backup");
            Directory.CreateDirectory(backupDir);
            BackupPluginFile(source, backupDir);
        }

        private static void BackupPluginFile(string source, string backupDir)
        {
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmssfff");
            var fileName = Path.GetFileName(source);
            var destination = Path.Combine(backupDir, $"{fileName}.{stamp}.disabled");
            File.Move(source, destination);
        }

        private static void VerifyInstalledPlugins(string pluginsPath)
        {
            var fourPlayerPath = Path.Combine(pluginsPath, "SeaPowerFourPlayer.dll");
            if (!File.Exists(fourPlayerPath) ||
                !ContainsAscii(fourPlayerPath, "com.seapower.fourplayer"))
                throw new InvalidDataException(
                    "The four-player plugin failed installation verification.");

            if (HasActiveConflictingPlugin(
                    Directory.GetParent(
                        Directory.GetParent(pluginsPath)!.FullName)!.FullName))
                throw new InvalidDataException(
                    "A conflicting two-player multiplayer plugin remains active.");
        }

        private static bool ContainsAscii(string path, string value)
        {
            try
            {
                var bytes = File.ReadAllBytes(path);
                var needle = Encoding.ASCII.GetBytes(value);
                for (int i = 0; i <= bytes.Length - needle.Length; i++)
                {
                    int j = 0;
                    for (; j < needle.Length; j++)
                    {
                        if (bytes[i + j] != needle[j])
                            break;
                    }
                    if (j == needle.Length)
                        return true;
                }
            }
            catch
            {
            }
            return false;
        }

        private static void EnsureGameClosed()
        {
            string[] names = { "Sea Power", "SeaPower", "Sea_Power" };
            foreach (var name in names)
            {
                var processes = Process.GetProcessesByName(name);
                try
                {
                    if (processes.Length > 0)
                        throw new InvalidOperationException(
                            "Sea Power is running. Close the game before installing or repairing the mod.");
                }
                finally
                {
                    foreach (var process in processes)
                        process.Dispose();
                }
            }
        }

        private static void ExtractEmbeddedResource(string resourceName, string destPath)
        {
            using var stream = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException(
                    $"Embedded resource '{resourceName}' not found. Build the mod project first.");

            using var fs = File.Create(destPath);
            stream.CopyTo(fs);
        }

        private static void MoveIfExists(string source, string dest)
        {
            if (!File.Exists(source)) return;
            if (File.Exists(dest)) File.Delete(dest);
            File.Move(source, dest);
        }

        private static async Task KillAllGameProcesses()
        {
            // Try multiple possible process names
            string[] names = { "Sea Power", "SeaPower", "Sea_Power" };
            foreach (var name in names)
            {
                foreach (var p in Process.GetProcessesByName(name))
                {
                    try { p.Kill(); p.WaitForExit(5000); } catch { }
                    p.Dispose();
                }
            }

            // Also find by window title as a fallback
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (p.MainWindowTitle.Contains("Sea Power", StringComparison.OrdinalIgnoreCase))
                    {
                        p.Kill();
                        p.WaitForExit(5000);
                    }
                }
                catch { }
                finally { p.Dispose(); }
            }

            // Give OS time to fully release
            await Task.Delay(1000);
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }
}
