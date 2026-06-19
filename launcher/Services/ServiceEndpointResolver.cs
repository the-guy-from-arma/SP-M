using System;
using System.IO;
using System.Text.Json;

namespace SeapowerMultiplayer.Launcher.Services
{
    public static class ServiceEndpointResolver
    {
        public static string Resolve(string? configured)
        {
            var environment = Environment.GetEnvironmentVariable("SP4P_SERVICE_URL");
            if (IsHttpUrl(environment))
                return environment!.TrimEnd('/');

            foreach (var path in CandidateFiles())
            {
                try
                {
                    if (!File.Exists(path))
                        continue;
                    using var document = JsonDocument.Parse(File.ReadAllText(path));
                    if (document.RootElement.TryGetProperty("serviceUrl", out var value))
                    {
                        var url = value.GetString();
                        if (IsHttpUrl(url))
                            return url!.TrimEnd('/');
                    }
                }
                catch
                {
                    // A malformed optional override falls through to the next source.
                }
            }

            return IsHttpUrl(configured)
                ? configured!.TrimEnd('/')
                : LobbyServiceClient.DefaultServiceUrl;
        }

        private static string[] CandidateFiles() =>
        [
            Path.Combine(AppContext.BaseDirectory, "service-endpoint.json"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SeaPowerFourPlayer",
                "service-endpoint.json"),
        ];

        private static bool IsHttpUrl(string? value) =>
            Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttps ||
             uri.Scheme == Uri.UriSchemeHttp);
    }
}
