# PLCGateway — Deployment Notes (IIS)

The gateway is a single ASP.NET Core (.NET 10) application that runs the PLC pipeline, the
dashboard API, and the dashboard static files (React build in `wwwroot/`) in one process, hosted
under IIS on the client's server. The cloud reaches this app only over HTTPS via the secured
`/api/admin/*` endpoints — never the database directly.

Follow the sections in order. Sections 1–5 get the site running; 6–8 secure and expose it.

---

## 1. Install prerequisites on the server

Install in this order — the Hosting Bundle must come **after** IIS, or it won't register the
ASP.NET Core Module with IIS and you'll get a `500.19` / `HTTP Error 500.21` later.

### 1.1 Enable IIS

Run in **PowerShell as Administrator**:

```powershell
Enable-WindowsOptionalFeature -Online -FeatureName IIS-WebServer, IIS-IPSecurity -All
```

Or via the GUI: *Control Panel → Programs → Turn Windows features on or off → Internet
Information Services*. Make sure these are ticked:

| Group | Features needed |
| ----- | --------------- |
| Common HTTP Features | Static Content, Default Document, HTTP Errors |
| Health and Diagnostics | HTTP Logging |
| Security | Request Filtering, **IP and Domain Restrictions** |
| Performance | Static Content Compression, Dynamic Content Compression |
| Web Management Tools | IIS Management Console |

Confirm IIS is up by browsing `http://localhost` — you should see the IIS welcome page.

### 1.2 .NET 10 Hosting Bundle

Download the **ASP.NET Core 10 Hosting Bundle** from
<https://dotnet.microsoft.com/download/dotnet/10.0> (under "Run server apps" → *Hosting Bundle*).
This installs the runtime **and** the `AspNetCoreModuleV2` IIS handler.

After installing, restart IIS so it picks up the module:

```powershell
net stop was /y
net start w3svc
```

Verify: IIS Manager → server node → **Modules** should list `AspNetCoreModuleV2`.

### 1.3 .NET 10 SDK — *only if you publish on this machine*

The SDK is needed for `dotnet publish` (section 3). If you build on a dev machine and copy the
output across, the server needs the Hosting Bundle only, **not** the SDK.

Download the **.NET 10 SDK** from the same page. Verify with `dotnet --list-sdks`.

### 1.4 PostgreSQL

Install PostgreSQL (v14+) and set the `postgres` password to match the connection string in
`appsettings.json`.

**Bind it to localhost only** — edit `postgresql.conf`:

```
listen_addresses = 'localhost'
```

and restart the PostgreSQL service. The database must never be reachable from the network; the
cloud reads through the admin API instead.

> Node.js is **not** required on the server. The dashboard is shipped pre-built inside `wwwroot`.

---

## 2. Create the database and run the migration

```powershell
psql -U postgres -c "CREATE DATABASE sreesakthi_gateway;"
psql -U postgres -d sreesakthi_gateway -f PLCGateway\migration.sql
```

The migration is idempotent — safe to re-run on an existing database. It creates every table and
index, and runs the one-time typed-column and per-cycle backfills.

---

## 3. Build the dashboard and publish the app

### 3.1 Decide the URL path first — this affects the build

The dashboard build is **pinned to the URL path it will be served from**. Vite bakes that path
into `index.html`, the asset URLs, the React Router basename and the API prefix at build time, so
it cannot be changed by moving files afterwards.

The path lives in one place, [`dashboard/.env.production`](dashboard/.env.production). It ships
**commented out**, i.e. root hosting, because that is what a local `dotnet run` and a root-bound
IIS site both need:

```
# VITE_BASE_PATH=/shotsense/
```

> Uncomment it only for sub-path hosting. A build made with it set renders a **white screen**
> anywhere not serving under that exact prefix: `index.html` requests
> `/shotsense/assets/index-*.js`, the server has the file at `/assets/index-*.js`, nothing loads
> and the page is blank with no visible error. Always confirm the emitted path (below) before
> publishing.

| Hosting layout | `VITE_BASE_PATH` | IIS application name |
| -------------- | ---------------- | -------------------- |
| Under Default Web Site as `/shotsense` | `/shotsense/` (leading **and** trailing slash) | `shotsense` |
| As its own site at the root, or a local `dotnet run` | leave the line commented out (**the default**) | n/a |

**The value must match the IIS application alias in section 5 exactly.** If they disagree you get
a blank page with `404`s on `/assets/*`, or a console warning that `<Router basename=...> is not
able to match the URL`.

### 3.2 Rebuild the dashboard (only if you changed `dashboard/` or `VITE_BASE_PATH`)

```powershell
cd dashboard
npm install      # first time only
npm run build    # emits into ..\PLCGateway\wwwroot
```

`emptyOutDir` clears the previous bundles from `wwwroot` automatically. Sanity check the result —
`PLCGateway\wwwroot\index.html` must reference the intended path:

```html
<!-- root hosting (the default) -->
<script type="module" crossorigin src="/assets/index-XXXXXXXX.js"></script>

<!-- only if VITE_BASE_PATH=/shotsense/ is uncommented -->
<script type="module" crossorigin src="/shotsense/assets/index-XXXXXXXX.js"></script>
```

The built output in `PLCGateway/wwwroot` is committed to the repo, so a plain publish (below)
already includes a working dashboard — you only rebuild when the frontend or the path changes.

### 3.3 Publish

```powershell
cd d:\PLCGateway
dotnet publish PLCGateway\PLCGateway.csproj -c Release /p:PublishProfile=FolderProfile
# output: PLCGateway\bin\Release\net10.0\publish\   (wwwroot + DLLs + web.config)
```

The profile ([`FolderProfile.pubxml`](PLCGateway/Properties/PublishProfiles/FolderProfile.pubxml))
pins `win-x64`, framework-dependent. That folder is the entire deployable.

> `dotnet publish` does **not** clear the output folder first, so old dashboard bundles pile up in
> `publish\wwwroot\assets`. They are harmless (filenames are content-hashed and `index.html` names
> only the current one), but delete `publish\wwwroot\assets` before publishing to keep it clean.

---

## 4. Copy the publish output to the server

Copy the **contents** of `publish\` into the folder that will hold the application:

```
C:\inetpub\wwwroot\shotsense\
```

Use the application name you picked in section 3.1. The folder should end up containing
`PLCGateway.dll`, `web.config`, `appsettings.json` and `wwwroot\`.

If the app is already running when you copy, IIS holds a lock on the DLLs. Either stop the
application pool first, or drop a file named `app_offline.htm` into the folder — the ASP.NET Core
Module shuts the app down gracefully and releases the locks; delete the file to bring it back.

**Edit `appsettings.json` in this folder now** — see the checklist in section 9.

---

## 5. Create the application in IIS Manager

Open IIS Manager (`inetmgr`). We create one **application pool**, then attach the folder from
section 4 to *Default Web Site* as an **Application**.

### 5.1 Create the application pool

*Application Pools → Add Application Pool…*

| Setting | Value |
| ------- | ----- |
| Name | `ShotSensePool` |
| .NET CLR version | **No Managed Code** |
| Managed pipeline mode | Integrated |

The ASP.NET Core Module hosts the .NET runtime itself, which is why the pool is set to *No Managed
Code* — this is not a .NET Framework app.

Then select the pool → **Advanced Settings** and set:

| Setting | Value | Why |
| ------- | ----- | --- |
| Start Mode | **AlwaysRunning** | the PLC scan loop must run without a request to trigger it |
| Idle Time-out (minutes) | **0** | never shut the worker process down |
| Regular Time Interval (minutes) *(Recycling)* | **0** | no scheduled recycles |
| Disable Overlapped Recycle | **True** | overlapped recycle briefly runs two worker processes → two PLC pollers writing duplicate rows |

### 5.2 Turn the folder into an Application

Right-click **Default Web Site** → **Add Application…**

| Field | Value |
| ----- | ----- |
| Alias | `shotsense` — must match `VITE_BASE_PATH` from section 3.1 |
| Application pool | `ShotSensePool` (click *Select…*) |
| Physical path | `C:\inetpub\wwwroot\shotsense` |

> This is the step people miss: a plain folder under `wwwroot` is served as **static files** and
> the app never starts. It must be an *Application* (the icon changes from a folder to a globe) so
> IIS loads the ASP.NET Core Module for it. A *Virtual Directory* is not the same thing and will
> not work.

Because it is an application under a parent site, ASP.NET Core automatically sets its `PathBase`
to `/shotsense` — every route the app declares (`/api/health`) is served at `/shotsense/api/health`
with no code change. The shipped `web.config` already sets `hostingModel="InProcess"` and wraps its
settings in `<location path="." inheritInChildApplications="false">` so the parent site's config
does not leak in.

### 5.3 Enable preload

Select the `shotsense` application → **Advanced Settings** → **Preload Enabled = True**.

Combined with *AlwaysRunning* on the pool, this starts the app when IIS starts rather than on the
first browser request — so PLC recording begins immediately after a server reboot.

### 5.4 Verify

| Check | Expected |
| ----- | -------- |
| `http://localhost/shotsense/api/health` | `{"status":"ok"}` |
| `http://localhost/shotsense` | dashboard login page |
| Browser DevTools → Network | assets load from `/shotsense/assets/…`, no 404s |
| Browser DevTools → Console | no `<Router basename=…>` warning |

If the page is blank, check the Console first — a basename/path mismatch between section 3.1 and
5.2 is the usual cause. Hard-refresh (Ctrl+F5); `index.html` caches aggressively.

---

## 6. HTTPS binding (port 443)

- Install the TLS certificate for the site's hostname into the local machine store.
- *Default Web Site* → **Bindings…** → *Add* → type `https`, port `443`, select the certificate.
- Optionally add an http→https redirect.

The bindings live on the parent site, so the `/shotsense` application inherits them. The dashboard
and API are same-origin, so no CORS configuration is needed in production.

---

## 7. Lock down `/api/admin/*` (defense in depth)

The app guards `/api/admin/*` with a single `X-Api-Key` check
([`AdminGuardMiddleware`](PLCGateway/Api/Middleware/AdminGuardMiddleware.cs)). There is no IP
allowlist — the cloud caller runs on Firebase Cloud Functions, which has no fixed egress IP on the
current plan, so an IP-based gate can never pass and would only lock the real caller out.

- Treat `Admin:ApiKey` as the entire perimeter for this path: generate it with a real secret
  generator (not a short/guessable string), store it only in the deployed `appsettings.json` and
  the cloud's own secret store, and rotate it if it's ever suspected leaked.
- HTTPS (section 6) is what keeps the key confidential in transit — never expose `/api/admin/*`
  over plain HTTP.

---

## 8. Static IP, router and firewall

- Give the server a **static LAN IP** (or a DHCP reservation). The PLC connection and the port
  forward both assume the address does not move.
- The PLC (default `192.168.0.180`) and the server must be on the same reachable network segment.
- On the client's router, forward **TCP 443** from the static public IP to this server's LAN
  address.
- Do **not** forward 5432 (PostgreSQL) or any other port. Only 443 is exposed.
- Windows Firewall: allow inbound TCP 443. Outbound HTTPS must be open for the license check.

---

## 9. `appsettings.json` checklist before go-live

Edit these in the deployed copy (`C:\inetpub\wwwroot\shotsense\appsettings.json`), then recycle the
application pool:

| Key | Action |
| --- | ------ |
| `PLC:IpAddress`, `Rack`, `Slot` | set to this client's PLC |
| `PostgreSQL:ConnectionString`, `ConnectionStrings:PostgresDb` | set the real password (both keys) |
| `Jwt:Key` | replace with a 32+ character secret, unique per install |
| `Seed:AdminUsername` / `Seed:AdminPassword` | change from `admin` / `admin123` **before first start** — the default admin is seeded on first run only |
| `Admin:ApiKey` | replace with a generated key |
| `License:CheckUrl`, `License:Key` | set for this install |
| `Tags` | the ~443 tag addresses; see the note below |

> **`Tags` is per-client.** The tag names are a contract the calculation engine depends on — do not
> rename them. Only the `Address` values (and the DB number) should change between clients. This is
> hand-editing a large JSON array today; making it configurable from the dashboard is a planned
> change, not yet implemented.

A malformed `appsettings.json` stops the app from starting. If the site returns 500 after an edit,
check Event Viewer → Windows Logs → Application, or temporarily set `stdoutLogEnabled="true"` in
`web.config` and read `logs\stdout*.log`.

---

## 10. Updating an existing deployment

```powershell
# 1. (only if the frontend changed)
cd d:\PLCGateway\dashboard; npm run build

# 2. republish
cd d:\PLCGateway
dotnet publish PLCGateway\PLCGateway.csproj -c Release /p:PublishProfile=FolderProfile

# 3. take the app offline, copy, bring it back
New-Item C:\inetpub\wwwroot\shotsense\app_offline.htm -ItemType File
Copy-Item PLCGateway\bin\Release\net10.0\publish\* C:\inetpub\wwwroot\shotsense\ -Recurse -Force
Remove-Item C:\inetpub\wwwroot\shotsense\app_offline.htm
```

`appsettings.json` is overwritten by the copy — back up the deployed one first, or exclude it and
re-apply the section 9 values afterwards.

Recording stops for the duration of the swap. The gateway backdates the gap as machine-OFF on
restart ([`GatewayWorker`](PLCGateway/GatewayWorker.cs) + startup gap handling in
[`Program.cs`](PLCGateway/Program.cs)), so the parameters stay correct — but keep the window short
and avoid mid-blast-cycle swaps.

---

## Appendix — common failures

| Symptom | Cause |
| ------- | ----- |
| `HTTP Error 500.19` / `500.21` | Hosting Bundle installed before IIS, or not installed. Re-run it, then `net stop was /y && net start w3svc` |
| Directory listing or 404 at `/shotsense` | The folder was never converted to an **Application** (section 5.2) |
| Blank page, `404` on `/assets/*` | `VITE_BASE_PATH` doesn't match the IIS alias — rebuild the dashboard |
| Blank page, Console: `<Router basename="/x/"> is not able to match the URL "/x"` | Dashboard built before the basename fix; rebuild from current source |
| Login returns 404 | API prefix mismatch — same root cause as above, rebuild |
| Data stops updating overnight | Pool idle time-out or scheduled recycle still enabled (section 5.1) |
| Duplicate rows in `plc_historical_data` | Overlapped recycle enabled, or two instances pointed at one database |
| `502.5` on start | `appsettings.json` malformed, or PostgreSQL unreachable — check Event Viewer |
