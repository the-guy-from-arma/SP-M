using System;
using System.IO;

namespace SeapowerMultiplayer.Launcher.Services
{
    internal static class LauncherDiagnostics
    {
        private static readonly string LogPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SeaPowerFourPlayer",
            "launcher-startup.log");

        public static void Trace(string message)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                File.AppendAllText(
                    LogPath,
                    $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
            }
            catch
            {
                // Diagnostics must never prevent startup.
            }
        }
    }
}
