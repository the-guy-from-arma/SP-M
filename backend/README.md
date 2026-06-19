# Sea Power Lobby Service

One Railway service provides:

- Public Steam lobby directory
- Lobby heartbeat and expiry
- Anonymous operational telemetry
- Password-protected creator dashboard
- Announcements, maintenance mode, and version gates

## Railway deployment

1. Push this repository to GitHub.
2. In Railway, create a project from that GitHub repository.
3. Set the service root directory to `/backend`.
4. Add a PostgreSQL service to the same Railway project.
5. Add these variables to the web service:

   - `DATABASE_URL=${{Postgres.DATABASE_URL}}`
   - `ADMIN_PASSWORD=<a long unique password>`
   - `LAUNCHER_DOWNLOAD_URL=<GitHub Release EXE asset URL>`
   - `PACKAGE_DOWNLOAD_URL=<GitHub Release ZIP asset URL>`
   - `LAUNCHER_VERSION=0.6.0`
   - `LAUNCHER_SHA256=<the packaged EXE SHA-256>`

6. Generate a public Railway domain.
7. Open `/admin` on that domain and sign in.

The database schema is created automatically at startup.
The public root page is the download website. Public lobbies remain inside the
launcher; only the authenticated creator dashboard can inspect lobby traffic.

## Connect the launcher after deployment

When Railway generates the domain, run this once from the repository root:

```powershell
powershell -ExecutionPolicy Bypass -File scripts/set-service-url.ps1 `
  -ServiceUrl "https://your-service.up.railway.app"
```

Then rebuild the launcher package. `service-endpoint.json` is placed beside the
EXE automatically. Every friend using that package connects to Railway without
entering anything. The launcher also writes the same URL into the game mod config.

## Local development

Set `DATABASE_URL` to a PostgreSQL connection string and run:

```powershell
dotnet run --project backend/SeaPowerLobbyService.csproj
```

The launcher and mod default to:

```text
https://spdash-production.up.railway.app
```

For development, override that address with `SP4P_SERVICE_URL`.
