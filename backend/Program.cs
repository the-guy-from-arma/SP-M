using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "sp4p_admin";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = true;
        options.LoginPath = "/admin/login";
    });
builder.Services.AddAuthorization();
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders =
        ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("public-read", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 120,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));
    options.AddPolicy("public-write", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 30,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));
});

var connectionString = DatabaseUrl.Normalize(
    Environment.GetEnvironmentVariable("DATABASE_URL")
    ?? builder.Configuration.GetConnectionString("LobbyDatabase")
    ?? throw new InvalidOperationException(
        "DATABASE_URL or ConnectionStrings:LobbyDatabase must be configured."));
var dataSource = NpgsqlDataSource.Create(connectionString);
builder.Services.AddSingleton(dataSource);
builder.Services.AddHostedService<LobbyCleanupService>();

var app = builder.Build();
app.UseForwardedHeaders();
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

await Database.InitializeAsync(dataSource);

app.MapGet("/health", () => Results.Ok(new
{
    ok = true,
    service = "sea-power-lobby-directory",
    utc = DateTimeOffset.UtcNow,
}));

app.MapGet("/api/v1/release", (HttpRequest request) =>
{
    const string version = "0.6.3";
    const string pluginVersion = "0.1.6-alpha";
    var sha256 = Environment.GetEnvironmentVariable("LAUNCHER_SHA256") ?? "";
    return Results.Ok(new
    {
        version,
        pluginVersion,
        downloadUrl = "/download/launcher",
        packageUrl = "/download/package",
        sha256,
        releaseNotes =
            "Public lobby visibility, legacy-plugin conflict detection, and a working one-click full launcher package download.",
    });
}).RequireRateLimiting("public-read");

app.MapGet("/download/launcher", (IWebHostEnvironment environment) =>
    DownloadResult(
        Environment.GetEnvironmentVariable("LAUNCHER_DOWNLOAD_URL"),
        Path.Combine(
            environment.WebRootPath,
            "downloads",
            "SeaPowerFourPlayerLauncher.exe"),
        "SeaPowerFourPlayerLauncher.exe"));

app.MapGet("/download/package", (IWebHostEnvironment environment) =>
    DownloadResult(
        Environment.GetEnvironmentVariable("PACKAGE_DOWNLOAD_URL")
        ?? Environment.GetEnvironmentVariable("LAUNCHER_DOWNLOAD_URL"),
        Path.Combine(
            environment.WebRootPath,
            "downloads",
            "SeaPowerFourPlayer-Launcher.zip"),
        "SeaPowerFourPlayer-Launcher.zip"));

app.MapGet("/api/v1/config", async (NpgsqlDataSource db) =>
{
    await using var command = db.CreateCommand("""
        SELECT announcement, maintenance_mode, public_lobbies_enabled,
               required_launcher_version, required_plugin_version
        FROM service_config WHERE id = 1
        """);
    await using var reader = await command.ExecuteReaderAsync();
    if (!await reader.ReadAsync())
        return Results.NotFound();

    return Results.Ok(new
    {
        announcement = reader.GetString(0),
        maintenanceMode = reader.GetBoolean(1),
        publicLobbiesEnabled = reader.GetBoolean(2),
        requiredLauncherVersion = reader.GetString(3),
        requiredPluginVersion = reader.GetString(4),
    });
}).RequireRateLimiting("public-read");

app.MapGet("/api/v1/lobbies", async (
    HttpRequest request,
    NpgsqlDataSource db,
    string? pluginVersion,
    int? protocol) =>
{
    await Database.DeleteExpiredLobbiesAsync(db);
    var results = new List<object>();
    await using var command = db.CreateCommand("""
        SELECT steam_lobby_id, host_name, lobby_name, player_count, max_players,
               pvp, host_team, scenario, region, plugin_version, protocol,
               created_at, last_heartbeat
        FROM public_lobbies
        WHERE expires_at > NOW()
          AND blocked = FALSE
          AND (@plugin = '' OR plugin_version = @plugin)
          AND (@protocol = 0 OR protocol = @protocol)
        ORDER BY player_count DESC, created_at ASC
        LIMIT 100
        """);
    command.Parameters.AddWithValue("plugin", pluginVersion?.Trim() ?? "");
    command.Parameters.AddWithValue("protocol", protocol ?? 0);

    await using var reader = await command.ExecuteReaderAsync();
    while (await reader.ReadAsync())
    {
        results.Add(new
        {
            steamLobbyId = reader.GetString(0),
            hostName = reader.GetString(1),
            lobbyName = reader.GetString(2),
            playerCount = reader.GetInt32(3),
            maxPlayers = reader.GetInt32(4),
            pvp = reader.GetBoolean(5),
            hostTeam = reader.GetInt32(6),
            scenario = reader.GetString(7),
            region = reader.GetString(8),
            pluginVersion = reader.GetString(9),
            protocol = reader.GetInt32(10),
            createdAt = reader.GetFieldValue<DateTimeOffset>(11),
            lastHeartbeat = reader.GetFieldValue<DateTimeOffset>(12),
        });
    }

    return Results.Ok(new
    {
        lobbies = results,
        generatedAt = DateTimeOffset.UtcNow,
    });
}).RequireRateLimiting("public-read");

app.MapPost("/api/v1/lobbies", async (
    LobbyRegistration registration,
    HttpContext context,
    NpgsqlDataSource db) =>
{
    var validation = LobbyValidation.Validate(registration);
    if (validation is not null)
        return Results.BadRequest(new { error = validation });

    if (!await Database.PublicLobbiesEnabledAsync(db))
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);

    if (await Database.IsHostBlockedAsync(db, registration.HostSteamId))
        return Results.StatusCode(StatusCodes.Status403Forbidden);

    var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    var tokenHash = TokenHash.Compute(token);
    await using var command = db.CreateCommand("""
        INSERT INTO public_lobbies (
            steam_lobby_id, host_steam_id, host_name, lobby_name, player_count,
            max_players, pvp, host_team, scenario, region, plugin_version,
            protocol, heartbeat_token_hash, source_ip, created_at,
            last_heartbeat, expires_at, blocked)
        VALUES (
            @lobby, @steam, @host, @name, @players, @max, @pvp, @team,
            @scenario, @region, @version, @protocol, @token, @ip, NOW(),
            NOW(), NOW() + INTERVAL '75 seconds', FALSE)
        ON CONFLICT (steam_lobby_id) DO UPDATE SET
            host_steam_id = EXCLUDED.host_steam_id,
            host_name = EXCLUDED.host_name,
            lobby_name = EXCLUDED.lobby_name,
            player_count = EXCLUDED.player_count,
            max_players = EXCLUDED.max_players,
            pvp = EXCLUDED.pvp,
            host_team = EXCLUDED.host_team,
            scenario = EXCLUDED.scenario,
            region = EXCLUDED.region,
            plugin_version = EXCLUDED.plugin_version,
            protocol = EXCLUDED.protocol,
            heartbeat_token_hash = EXCLUDED.heartbeat_token_hash,
            source_ip = EXCLUDED.source_ip,
            last_heartbeat = NOW(),
            expires_at = NOW() + INTERVAL '75 seconds',
            blocked = FALSE
        """);
    LobbyValidation.AddParameters(command, registration);
    command.Parameters.AddWithValue("token", tokenHash);
    command.Parameters.AddWithValue(
        "ip",
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown");
    await command.ExecuteNonQueryAsync();

    await Database.RecordEventAsync(
        db,
        "lobby_registered",
        registration.SteamLobbyId,
        registration.PluginVersion,
        registration.Protocol,
        null);

    return Results.Ok(new
    {
        heartbeatToken = token,
        heartbeatSeconds = 20,
        expiresSeconds = 75,
    });
}).RequireRateLimiting("public-write");

app.MapPut("/api/v1/lobbies/{steamLobbyId}/heartbeat", async (
    string steamLobbyId,
    LobbyHeartbeat heartbeat,
    HttpRequest request,
    NpgsqlDataSource db) =>
{
    var token = BearerToken.Read(request);
    if (token is null)
        return Results.Unauthorized();

    await using var command = db.CreateCommand("""
        UPDATE public_lobbies
        SET player_count = @players,
            scenario = @scenario,
            last_heartbeat = NOW(),
            expires_at = NOW() + INTERVAL '75 seconds'
        WHERE steam_lobby_id = @lobby
          AND heartbeat_token_hash = @token
          AND blocked = FALSE
        """);
    command.Parameters.AddWithValue("players", Math.Clamp(heartbeat.PlayerCount, 1, 4));
    command.Parameters.AddWithValue(
        "scenario",
        InputText.Clean(heartbeat.Scenario, 80, "Waiting for mission"));
    command.Parameters.AddWithValue("lobby", steamLobbyId);
    command.Parameters.AddWithValue("token", TokenHash.Compute(token));
    var affected = await command.ExecuteNonQueryAsync();
    return affected == 1 ? Results.NoContent() : Results.Unauthorized();
}).RequireRateLimiting("public-write");

app.MapDelete("/api/v1/lobbies/{steamLobbyId}", async (
    string steamLobbyId,
    HttpRequest request,
    NpgsqlDataSource db) =>
{
    var token = BearerToken.Read(request);
    if (token is null)
        return Results.Unauthorized();

    await using var command = db.CreateCommand("""
        DELETE FROM public_lobbies
        WHERE steam_lobby_id = @lobby
          AND heartbeat_token_hash = @token
        """);
    command.Parameters.AddWithValue("lobby", steamLobbyId);
    command.Parameters.AddWithValue("token", TokenHash.Compute(token));
    var affected = await command.ExecuteNonQueryAsync();
    return affected == 1 ? Results.NoContent() : Results.Unauthorized();
}).RequireRateLimiting("public-write");

app.MapPost("/api/v1/telemetry", async (
    TelemetryEvent telemetry,
    HttpContext context,
    NpgsqlDataSource db) =>
{
    var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "launcher_started",
        "lobby_list_opened",
        "lobby_created",
        "join_attempted",
        "join_succeeded",
        "join_failed",
        "player_connected",
        "player_disconnected",
        "game_launched",
        "plugin_error",
    };
    if (!allowed.Contains(telemetry.Event))
        return Results.BadRequest(new { error = "Unknown telemetry event." });

    await Database.RecordEventAsync(
        db,
        telemetry.Event.ToLowerInvariant(),
        InputText.Clean(telemetry.LobbyId, 32, ""),
        InputText.Clean(telemetry.Version, 32, ""),
        telemetry.Protocol,
        InputText.Clean(telemetry.Detail, 240, ""),
        context.Connection.RemoteIpAddress?.ToString());
    return Results.Accepted();
}).RequireRateLimiting("public-write");

app.MapGet("/admin/login", () => Results.File(
    Path.Combine(app.Environment.WebRootPath, "login.html"),
    "text/html"));

app.MapPost("/admin/login", async (HttpContext context) =>
{
    var form = await context.Request.ReadFormAsync();
    var supplied = form["password"].ToString();
    var expected = Environment.GetEnvironmentVariable("ADMIN_PASSWORD") ?? "";
    if (string.IsNullOrWhiteSpace(expected) ||
        !CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(supplied)),
            SHA256.HashData(Encoding.UTF8.GetBytes(expected))))
    {
        return Results.Redirect("/admin/login?error=1");
    }

    var identity = new ClaimsIdentity(
        new[] { new Claim(ClaimTypes.Name, "creator") },
        CookieAuthenticationDefaults.AuthenticationScheme);
    await context.SignInAsync(
        CookieAuthenticationDefaults.AuthenticationScheme,
        new ClaimsPrincipal(identity));
    return Results.Redirect("/admin");
}).DisableAntiforgery().RequireRateLimiting("public-write");

app.MapPost("/admin/logout", async (HttpContext context) =>
{
    await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Redirect("/admin/login");
}).RequireAuthorization();

app.MapGet("/admin", () => Results.File(
    Path.Combine(app.Environment.WebRootPath, "admin.html"),
    "text/html")).RequireAuthorization();

app.MapGet("/api/admin/overview", async (NpgsqlDataSource db) =>
{
    await Database.DeleteExpiredLobbiesAsync(db);
    await using var command = db.CreateCommand("""
        SELECT
          (SELECT COUNT(*) FROM public_lobbies WHERE expires_at > NOW() AND blocked = FALSE),
          (SELECT COALESCE(SUM(player_count), 0) FROM public_lobbies WHERE expires_at > NOW() AND blocked = FALSE),
          (SELECT COUNT(*) FROM traffic_events WHERE created_at > NOW() - INTERVAL '24 hours'),
          (SELECT COUNT(*) FROM traffic_events WHERE event_name = 'join_attempted' AND created_at > NOW() - INTERVAL '24 hours'),
          (SELECT COUNT(*) FROM traffic_events WHERE event_name = 'join_succeeded' AND created_at > NOW() - INTERVAL '24 hours'),
          (SELECT COUNT(*) FROM blocked_hosts)
        """);
    await using var reader = await command.ExecuteReaderAsync();
    await reader.ReadAsync();
    return Results.Ok(new
    {
        activeLobbies = reader.GetInt64(0),
        playersOnline = reader.GetInt64(1),
        events24h = reader.GetInt64(2),
        joinAttempts24h = reader.GetInt64(3),
        successfulJoins24h = reader.GetInt64(4),
        blockedHosts = reader.GetInt64(5),
    });
}).RequireAuthorization();

app.MapGet("/api/admin/lobbies", async (NpgsqlDataSource db) =>
{
    var rows = new List<object>();
    await using var command = db.CreateCommand("""
        SELECT steam_lobby_id, host_steam_id, host_name, lobby_name,
               player_count, max_players, pvp, plugin_version, protocol,
               source_ip, created_at, last_heartbeat, expires_at, blocked
        FROM public_lobbies
        ORDER BY last_heartbeat DESC
        LIMIT 200
        """);
    await using var reader = await command.ExecuteReaderAsync();
    while (await reader.ReadAsync())
    {
        rows.Add(new
        {
            steamLobbyId = reader.GetString(0),
            hostSteamId = reader.GetString(1),
            hostName = reader.GetString(2),
            lobbyName = reader.GetString(3),
            playerCount = reader.GetInt32(4),
            maxPlayers = reader.GetInt32(5),
            pvp = reader.GetBoolean(6),
            pluginVersion = reader.GetString(7),
            protocol = reader.GetInt32(8),
            sourceIp = reader.GetString(9),
            createdAt = reader.GetFieldValue<DateTimeOffset>(10),
            lastHeartbeat = reader.GetFieldValue<DateTimeOffset>(11),
            expiresAt = reader.GetFieldValue<DateTimeOffset>(12),
            blocked = reader.GetBoolean(13),
        });
    }
    return Results.Ok(rows);
}).RequireAuthorization();

app.MapGet("/api/admin/events", async (NpgsqlDataSource db) =>
{
    var rows = new List<object>();
    await using var command = db.CreateCommand("""
        SELECT event_name, lobby_id, version, protocol, detail, source_ip, created_at
        FROM traffic_events
        ORDER BY created_at DESC
        LIMIT 250
        """);
    await using var reader = await command.ExecuteReaderAsync();
    while (await reader.ReadAsync())
    {
        rows.Add(new
        {
            eventName = reader.GetString(0),
            lobbyId = reader.GetString(1),
            version = reader.GetString(2),
            protocol = reader.GetInt32(3),
            detail = reader.GetString(4),
            sourceIp = reader.GetString(5),
            createdAt = reader.GetFieldValue<DateTimeOffset>(6),
        });
    }
    return Results.Ok(rows);
}).RequireAuthorization();

app.MapGet("/api/admin/config", async (NpgsqlDataSource db) =>
{
    await using var command = db.CreateCommand("""
        SELECT announcement, maintenance_mode, public_lobbies_enabled,
               required_launcher_version, required_plugin_version
        FROM service_config WHERE id = 1
        """);
    await using var reader = await command.ExecuteReaderAsync();
    await reader.ReadAsync();
    return Results.Ok(new
    {
        announcement = reader.GetString(0),
        maintenanceMode = reader.GetBoolean(1),
        publicLobbiesEnabled = reader.GetBoolean(2),
        requiredLauncherVersion = reader.GetString(3),
        requiredPluginVersion = reader.GetString(4),
    });
}).RequireAuthorization();

app.MapPut("/api/admin/config", async (
    ServiceConfigUpdate update,
    NpgsqlDataSource db) =>
{
    await using var command = db.CreateCommand("""
        UPDATE service_config SET
            announcement = @announcement,
            maintenance_mode = @maintenance,
            public_lobbies_enabled = @enabled,
            required_launcher_version = @launcher,
            required_plugin_version = @plugin,
            updated_at = NOW()
        WHERE id = 1
        """);
    command.Parameters.AddWithValue(
        "announcement",
        InputText.Clean(update.Announcement, 180, "JOIN THE SEAS"));
    command.Parameters.AddWithValue("maintenance", update.MaintenanceMode);
    command.Parameters.AddWithValue("enabled", update.PublicLobbiesEnabled);
    command.Parameters.AddWithValue(
        "launcher",
        InputText.Clean(update.RequiredLauncherVersion, 32, ""));
    command.Parameters.AddWithValue(
        "plugin",
        InputText.Clean(update.RequiredPluginVersion, 32, ""));
    await command.ExecuteNonQueryAsync();
    return Results.NoContent();
}).RequireAuthorization();

app.MapDelete("/api/admin/lobbies/{steamLobbyId}", async (
    string steamLobbyId,
    NpgsqlDataSource db) =>
{
    await using var command = db.CreateCommand(
        "DELETE FROM public_lobbies WHERE steam_lobby_id = @lobby");
    command.Parameters.AddWithValue("lobby", steamLobbyId);
    await command.ExecuteNonQueryAsync();
    return Results.NoContent();
}).RequireAuthorization();

app.MapPost("/api/admin/hosts/{hostSteamId}/block", async (
    string hostSteamId,
    NpgsqlDataSource db) =>
{
    if (!ulong.TryParse(hostSteamId, out _))
        return Results.BadRequest(new { error = "Invalid Steam ID." });

    await using var command = db.CreateCommand("""
        INSERT INTO blocked_hosts (host_steam_id, created_at)
        VALUES (@steam, NOW())
        ON CONFLICT (host_steam_id) DO NOTHING;
        DELETE FROM public_lobbies WHERE host_steam_id = @steam;
        """);
    command.Parameters.AddWithValue("steam", hostSteamId);
    await command.ExecuteNonQueryAsync();
    return Results.NoContent();
}).RequireAuthorization();

app.MapDelete("/api/admin/hosts/{hostSteamId}/block", async (
    string hostSteamId,
    NpgsqlDataSource db) =>
{
    await using var command = db.CreateCommand(
        "DELETE FROM blocked_hosts WHERE host_steam_id = @steam");
    command.Parameters.AddWithValue("steam", hostSteamId);
    await command.ExecuteNonQueryAsync();
    return Results.NoContent();
}).RequireAuthorization();

app.Run();

static IResult DownloadResult(
    string? externalUrl,
    string localPath,
    string downloadName)
{
    if (Uri.TryCreate(externalUrl, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp))
        return Results.Redirect(uri.ToString());

    if (File.Exists(localPath))
        return Results.File(
            localPath,
            "application/octet-stream",
            downloadName,
            enableRangeProcessing: true);

    return Results.Problem(
        "The full launcher package has not been published with this deployment.",
        statusCode: StatusCodes.Status503ServiceUnavailable);
}

public sealed record LobbyRegistration(
    string SteamLobbyId,
    string HostSteamId,
    string HostName,
    string LobbyName,
    int PlayerCount,
    int MaxPlayers,
    bool PvP,
    int HostTeam,
    string Scenario,
    string Region,
    string PluginVersion,
    int Protocol);

public sealed record LobbyHeartbeat(int PlayerCount, string Scenario);
public sealed record TelemetryEvent(
    string Event,
    string LobbyId,
    string Version,
    int Protocol,
    string Detail);
public sealed record ServiceConfigUpdate(
    string Announcement,
    bool MaintenanceMode,
    bool PublicLobbiesEnabled,
    string RequiredLauncherVersion,
    string RequiredPluginVersion);

internal static class LobbyValidation
{
    public static string? Validate(LobbyRegistration value)
    {
        if (!ulong.TryParse(value.SteamLobbyId, out _))
            return "Invalid Steam lobby ID.";
        if (!ulong.TryParse(value.HostSteamId, out _))
            return "Invalid host Steam ID.";
        if (value.PlayerCount is < 1 or > 4 || value.MaxPlayers != 4)
            return "Invalid player capacity.";
        if (value.HostTeam is < 0 or > 1)
            return "Invalid host team.";
        if (value.Protocol < 1)
            return "Invalid protocol.";
        if (string.IsNullOrWhiteSpace(value.PluginVersion))
            return "Plugin version is required.";
        return null;
    }

    public static void AddParameters(
        NpgsqlCommand command,
        LobbyRegistration value)
    {
        command.Parameters.AddWithValue("lobby", value.SteamLobbyId);
        command.Parameters.AddWithValue("steam", value.HostSteamId);
        command.Parameters.AddWithValue(
            "host",
            InputText.Clean(value.HostName, 40, "Commander"));
        command.Parameters.AddWithValue(
            "name",
            InputText.Clean(value.LobbyName, 64, "Sea Power Operation"));
        command.Parameters.AddWithValue("players", Math.Clamp(value.PlayerCount, 1, 4));
        command.Parameters.AddWithValue("max", 4);
        command.Parameters.AddWithValue("pvp", value.PvP);
        command.Parameters.AddWithValue("team", Math.Clamp(value.HostTeam, 0, 1));
        command.Parameters.AddWithValue(
            "scenario",
            InputText.Clean(value.Scenario, 80, "Waiting for mission"));
        command.Parameters.AddWithValue(
            "region",
            InputText.Clean(value.Region, 24, "Automatic"));
        command.Parameters.AddWithValue(
            "version",
            InputText.Clean(value.PluginVersion, 32, "unknown"));
        command.Parameters.AddWithValue("protocol", value.Protocol);
    }
}

internal static class InputText
{
    public static string Clean(string? value, int maxLength, string fallback)
    {
        var text = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        var clean = new string(text.Where(c => !char.IsControl(c)).ToArray());
        return clean.Length <= maxLength ? clean : clean[..maxLength];
    }
}

internal static class TokenHash
{
    public static string Compute(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)))
            .ToLowerInvariant();
}

internal static class BearerToken
{
    public static string? Read(HttpRequest request)
    {
        var header = request.Headers.Authorization.ToString();
        return header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? header[7..].Trim()
            : null;
    }
}

internal static class DatabaseUrl
{
    public static string Normalize(string value)
    {
        if (!value.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) &&
            !value.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
            return value;

        var uri = new Uri(value);
        var userInfo = uri.UserInfo.Split(':', 2);
        return new NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            Port = uri.Port > 0 ? uri.Port : 5432,
            Username = Uri.UnescapeDataString(userInfo[0]),
            Password = userInfo.Length > 1
                ? Uri.UnescapeDataString(userInfo[1])
                : "",
            Database = uri.AbsolutePath.TrimStart('/'),
            SslMode = SslMode.Prefer,
        }.ConnectionString;
    }
}

internal static class Database
{
    public static async Task InitializeAsync(NpgsqlDataSource db)
    {
        await using var command = db.CreateCommand("""
            CREATE TABLE IF NOT EXISTS public_lobbies (
                steam_lobby_id TEXT PRIMARY KEY,
                host_steam_id TEXT NOT NULL,
                host_name TEXT NOT NULL,
                lobby_name TEXT NOT NULL,
                player_count INTEGER NOT NULL,
                max_players INTEGER NOT NULL,
                pvp BOOLEAN NOT NULL,
                host_team INTEGER NOT NULL,
                scenario TEXT NOT NULL,
                region TEXT NOT NULL,
                plugin_version TEXT NOT NULL,
                protocol INTEGER NOT NULL,
                heartbeat_token_hash TEXT NOT NULL,
                source_ip TEXT NOT NULL,
                created_at TIMESTAMPTZ NOT NULL,
                last_heartbeat TIMESTAMPTZ NOT NULL,
                expires_at TIMESTAMPTZ NOT NULL,
                blocked BOOLEAN NOT NULL DEFAULT FALSE
            );

            CREATE INDEX IF NOT EXISTS idx_public_lobbies_expiry
                ON public_lobbies (expires_at);

            CREATE TABLE IF NOT EXISTS traffic_events (
                id BIGSERIAL PRIMARY KEY,
                event_name TEXT NOT NULL,
                lobby_id TEXT NOT NULL DEFAULT '',
                version TEXT NOT NULL DEFAULT '',
                protocol INTEGER NOT NULL DEFAULT 0,
                detail TEXT NOT NULL DEFAULT '',
                source_ip TEXT NOT NULL DEFAULT '',
                created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );

            CREATE INDEX IF NOT EXISTS idx_traffic_events_created
                ON traffic_events (created_at DESC);

            CREATE TABLE IF NOT EXISTS blocked_hosts (
                host_steam_id TEXT PRIMARY KEY,
                created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );

            CREATE TABLE IF NOT EXISTS service_config (
                id INTEGER PRIMARY KEY CHECK (id = 1),
                announcement TEXT NOT NULL,
                maintenance_mode BOOLEAN NOT NULL,
                public_lobbies_enabled BOOLEAN NOT NULL,
                required_launcher_version TEXT NOT NULL,
                required_plugin_version TEXT NOT NULL,
                updated_at TIMESTAMPTZ NOT NULL
            );

            INSERT INTO service_config (
                id, announcement, maintenance_mode, public_lobbies_enabled,
                required_launcher_version, required_plugin_version, updated_at)
            VALUES (1, 'JOIN THE SEAS // PUBLIC OPERATIONS ONLINE', FALSE, TRUE, '', '', NOW())
            ON CONFLICT (id) DO NOTHING;
            """);
        await command.ExecuteNonQueryAsync();
    }

    public static async Task DeleteExpiredLobbiesAsync(NpgsqlDataSource db)
    {
        await using var command = db.CreateCommand(
            "DELETE FROM public_lobbies WHERE expires_at <= NOW()");
        await command.ExecuteNonQueryAsync();
    }

    public static async Task<bool> PublicLobbiesEnabledAsync(NpgsqlDataSource db)
    {
        await using var command = db.CreateCommand(
            "SELECT public_lobbies_enabled AND NOT maintenance_mode FROM service_config WHERE id = 1");
        return (bool)(await command.ExecuteScalarAsync() ?? false);
    }

    public static async Task<bool> IsHostBlockedAsync(
        NpgsqlDataSource db,
        string hostSteamId)
    {
        await using var command = db.CreateCommand(
            "SELECT EXISTS(SELECT 1 FROM blocked_hosts WHERE host_steam_id = @steam)");
        command.Parameters.AddWithValue("steam", hostSteamId);
        return (bool)(await command.ExecuteScalarAsync() ?? false);
    }

    public static async Task RecordEventAsync(
        NpgsqlDataSource db,
        string eventName,
        string? lobbyId,
        string? version,
        int protocol,
        string? detail,
        string? sourceIp = null)
    {
        await using var command = db.CreateCommand("""
            INSERT INTO traffic_events (
                event_name, lobby_id, version, protocol, detail, source_ip, created_at)
            VALUES (@event, @lobby, @version, @protocol, @detail, @ip, NOW())
            """);
        command.Parameters.AddWithValue("event", eventName);
        command.Parameters.AddWithValue("lobby", lobbyId ?? "");
        command.Parameters.AddWithValue("version", version ?? "");
        command.Parameters.AddWithValue("protocol", protocol);
        command.Parameters.AddWithValue("detail", detail ?? "");
        command.Parameters.AddWithValue("ip", sourceIp ?? "");
        await command.ExecuteNonQueryAsync();
    }
}

internal sealed class LobbyCleanupService(
    NpgsqlDataSource dataSource,
    ILogger<LobbyCleanupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
                await Database.DeleteExpiredLobbiesAsync(dataSource);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Lobby cleanup failed.");
            }
        }
    }
}
