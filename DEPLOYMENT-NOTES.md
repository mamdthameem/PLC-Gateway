# PLCGateway — Deployment Notes (IIS)

The gateway is now a single ASP.NET Core (.NET 10) application that runs the PLC pipeline,
the dashboard API, and the dashboard static files (React build in `wwwroot/`) in one process,
hosted under IIS on the client's server. The cloud reaches this app only over HTTPS via the
secured `/api/admin/*` endpoints — never the database directly.

---

## 1. Prerequisites on the server

1. **Windows features (IIS)** — enable via *Server Manager → Roles* or PowerShell:
   - Web Server (IIS) with: *Web Server → Common HTTP Features* (Static Content, Default Document,
     HTTP Errors), *Health and Diagnostics* (HTTP Logging), *Security* (Request Filtering,
     **IP and Domain Restrictions**), *Performance* (Static/Dynamic Compression).
   - `Enable-WindowsOptionalFeature -Online -FeatureName IIS-WebServer, IIS-IPSecurity` (or the GUI).
2. **.NET Hosting Bundle** — install the **.NET 10 Hosting Bundle** (installs the ASP.NET Core
   Module V2 / `AspNetCoreModuleV2` for IIS). Restart IIS afterwards: `net stop was /y && net start w3svc`.
3. **PostgreSQL** — already installed locally. **Bind it to localhost only** (`listen_addresses = 'localhost'`
   in `postgresql.conf`) so the database is never reachable from the network. The cloud uses the
   admin API, not the DB.

## 2. Database migration

Run once (idempotent, safe to re-run):

```powershell
psql -U postgres -d sreesakthi_gateway -f PLCGateway\migration.sql
```

## 3. Publish

The dashboard source lives in this repo under `dashboard/`, and its built output is vendored
into `PLCGateway/wwwroot`. Publishing this project alone produces a complete, self-contained
site — no separate frontend server and no external folder are involved.

```powershell
dotnet publish PLCGateway\PLCGateway.csproj -c Release /p:PublishProfile=FolderProfile
# output: PLCGateway\bin\Release\net10.0\publish\  (includes wwwroot, DLLs, and web.config)
```

Copy the `publish\` folder to the site root, e.g. `C:\inetpub\PLCGateway`. That folder is the
entire deployable — nothing else is needed on the client's machine.

*To change the dashboard*, edit `dashboard/` then rebuild it straight into `wwwroot`:

```powershell
cd dashboard
npm install      # first time only
npm run build    # emits into ..\PLCGateway\wwwroot (same-origin)
```

then re-publish. No Node runtime is needed on the server — only the built `wwwroot` is deployed.

## 4. IIS site + application pool

Create an **application pool** (e.g. `PLCGatewayPool`):

- **.NET CLR version = No Managed Code** (the ASP.NET Core Module hosts the runtime).
- **Start Mode = AlwaysRunning**  ← the PLC scan loop must run continuously.
- **Idle Time-out (minutes) = 0**  ← never idle the worker process.
- Advanced → *Recycling*: **disable regular time interval / specific-time recycles**, and set
  **Disable Overlapped Recycle = True**. Overlapped recycle would briefly run two worker
  processes → two PLC pollers writing duplicate data. (The app also takes a PostgreSQL advisory
  hold at startup; keep overlapped recycle off regardless.)

Create the **site**:

- Physical path → the `publish\` folder.
- Application pool → `PLCGatewayPool`.
- **Preload Enabled = True** (Site → Advanced Settings) so the app starts with IIS, not on first
  request.
- `web.config` (shipped in publish) already sets `hostingModel="InProcess"` for ANCM.

## 5. HTTPS binding (port 443)

- Install/obtain the TLS certificate for the site's hostname into the local machine store.
- Add an **https binding on port 443** to the site and select the certificate.
- Optionally add an http→https redirect. The dashboard and API are same-origin, so no CORS
  config is needed in production (the dev origins in `appsettings.json:Cors` are harmless).

## 6. Lock down `/api/admin/*` at the IIS layer (defense in depth)

The app already guards `/api/admin/*` with an IP allowlist **and** an `X-Api-Key`
(`AdminGuardMiddleware`). Mirror the IP allowlist at the IIS level as a second layer:

- Site → **IP Address and Domain Restrictions** → *Add Allow Entry* for the cloud egress IP(s);
  set the feature's default to **Deny**. Apply this rule scoped to the `api/admin` path (add it on
  a virtual path or use URL Rewrite/Request Filtering to restrict), so only the cloud can reach it.

## 7. Router / firewall

- Forward **TCP 443** on the client's static public IP to this server's LAN address.
- Do **not** forward 5432 (PostgreSQL) or any other port. Only 443 is exposed.
- The PLC (192.168.0.180) and the server must be on the same reachable network segment.

## 8. Configuration placeholders to set before go-live (`appsettings.json`)

| Key | Replace with |
| --- | --- |
| `Jwt:Key` | a random secret of at least 32 characters |
| `Admin:ApiKey` | a strong random API key (shared with the cloud) |
| `Admin:AllowedIps` | the cloud's egress IP(s); keep `127.0.0.1`/`::1` only if local admin access is wanted |
| `License:CheckUrl` | the cloud license-validation URL (leave empty to disable licensing) |
| `License:Key` | the client's license key |
| `Seed:AdminPassword` | change before first run, **or** rotate the seeded `admin` password after first login |
| `PostgreSQL:ConnectionString` / `ConnectionStrings:PostgresDb` | the local DB password |

**Rotate the seeded admin account.** On first run the app seeds `admin` with `Seed:AdminPassword`
(default `admin123`) and logs a warning. Change it immediately.

## 9. Licensing behaviour

`LicenseCheckService` polls `License:CheckUrl` every `License:CheckIntervalMinutes` (default 60).
On success it refreshes the grace anchor. If checks keep failing beyond `License:GraceHours`
(default 72) the **dashboard and data API return HTTP 402** (the SPA shows a lock screen), but the
**PLC pipeline keeps recording to the local database** and `/api/admin/*` stays reachable — no data
is ever lost to a licensing outage. Last-success is persisted in `gateway_license_state`, so a
restart does not reset the grace clock.

## 10. Operations

- **Health check:** `GET /api/health` → `{"status":"ok"}` (anonymous).
- **Rebuild Section 1 accumulators** (after a restore or if the incremental state is suspect):
  run the app once with `--rebuild-aggregation` (resets `plc_aggregation_state`; the next
  aggregation pass replays the full Tier 2 history). Under IIS, set this as a one-off ANCM argument
  or run the published DLL from a console once, then remove the flag.
- **Never delete `plc_historical_data`.** The typed backfill keeps the original TEXT `value`
  column frozen; do not drop it.
- **Logs:** ANCM stdout logging is off by default in `web.config`; enable `stdoutLogEnabled="true"`
  temporarily to debug startup, then turn it back off.
