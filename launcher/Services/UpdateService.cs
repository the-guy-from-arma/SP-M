using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace SeapowerMultiplayer.Launcher.Services
{
    public class UpdateInfo
    {
        public string Version { get; set; } = "";
        public string DownloadUrl { get; set; } = "";
        public string ReleaseNotes { get; set; } = "";
        public string Sha256 { get; set; } = "";
    }

    public static class UpdateService
    {
        // Upload the generated latest.json and EXE under this public directory.
        // A missing manifest simply means the launcher stays on its bundled build.
        private const string ManifestUrl =
            "https://spdash-production.up.railway.app/api/v1/release";

        private static readonly HttpClient _http = new()
        {
            Timeout = TimeSpan.FromSeconds(15),
            DefaultRequestHeaders =
            {
                { "User-Agent", "SeaPowerFourPlayerLauncher" },
                { "Accept", "application/json" },
            },
        };

        /// <summary>
        /// Check the Thunder Buddies release manifest for a newer version.
        /// Returns null if up-to-date or on any error (fail silently).
        /// </summary>
        public static async Task<UpdateInfo?> CheckForUpdateAsync(string currentVersion)
        {
            try
            {
                var json = await _http.GetStringAsync(ManifestUrl);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                var versionText = root.TryGetProperty("version", out var versionElement)
                    ? versionElement.GetString() ?? ""
                    : "";
                var versionStr = ExtractVersion(versionText);
                if (versionStr == null || !Version.TryParse(versionStr, out var remoteVersion))
                    return null;
                if (!Version.TryParse(currentVersion, out var localVersion))
                    return null;
                if (remoteVersion <= localVersion)
                    return null;

                var downloadUrl = root.TryGetProperty("downloadUrl", out var downloadElement)
                    ? downloadElement.GetString()
                    : null;
                if (string.IsNullOrEmpty(downloadUrl))
                    return null;
                downloadUrl = new Uri(new Uri(ManifestUrl), downloadUrl).ToString();

                var releaseNotes = root.TryGetProperty("releaseNotes", out var notes)
                    ? notes.GetString() ?? ""
                    : "";
                var sha256 = root.TryGetProperty("sha256", out var hash)
                    ? hash.GetString() ?? ""
                    : "";

                return new UpdateInfo
                {
                    Version = versionStr,
                    DownloadUrl = downloadUrl,
                    ReleaseNotes = releaseNotes,
                    Sha256 = sha256,
                };
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Extract a version string (e.g. "0.1.0") from text like "v0.1.0", "v0.1.0 Beta Release", etc.
        /// </summary>
        private static string? ExtractVersion(string text)
        {
            var match = Regex.Match(text, @"(\d+\.\d+\.\d+)");
            return match.Success ? match.Groups[1].Value : null;
        }

        /// <summary>
        /// Download the update and replace the running exe via a batch script.
        /// This method shuts down the application.
        /// </summary>
        public static async Task ApplyUpdateAsync(UpdateInfo update, IProgress<string> progress)
        {
            var tempDir = Path.GetTempPath();
            var tempExe = Path.Combine(tempDir, "SeapowerMultiplayerLauncher_update.exe");
            var tempBat = Path.Combine(tempDir, "spm_update.bat");
            var currentExe = Environment.ProcessPath
                ?? throw new InvalidOperationException("Cannot determine current exe path");

            // Download
            progress.Report($"Downloading v{update.Version}...");
            using (var response = await _http.GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();
                var totalBytes = response.Content.Headers.ContentLength ?? -1;

                await using var contentStream = await response.Content.ReadAsStreamAsync();
                await using var fileStream = new FileStream(tempExe, FileMode.Create, FileAccess.Write, FileShare.None);

                var buffer = new byte[81920];
                long downloaded = 0;
                int bytesRead;

                while ((bytesRead = await contentStream.ReadAsync(buffer)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead));
                    downloaded += bytesRead;

                    if (totalBytes > 0)
                    {
                        var pct = (int)(downloaded * 100 / totalBytes);
                        progress.Report($"Downloading v{update.Version}... {pct}%");
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(update.Sha256))
            {
                progress.Report("Verifying download...");
                await using var stream = File.OpenRead(tempExe);
                var actualHash = Convert.ToHexString(await SHA256.HashDataAsync(stream));
                if (!actualHash.Equals(update.Sha256.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(tempExe);
                    throw new InvalidDataException("The downloaded launcher failed its SHA-256 integrity check.");
                }
            }

            progress.Report("Applying update...");

            // Write batch script that waits for this process to exit, then replaces the exe
            var script = $"""
                @echo off
                timeout /t 2 /nobreak >nul
                move /y "{tempExe}" "{currentExe}"
                start "" "{currentExe}" --post-update
                del "%~f0"
                """;
            await File.WriteAllTextAsync(tempBat, script);

            // Launch the batch script (hidden window)
            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"\"{tempBat}\"\"",
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            // Shut down the launcher so the batch script can replace the exe
            System.Windows.Application.Current.Shutdown();
        }
    }
}
