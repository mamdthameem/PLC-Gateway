# ShotSense Gateway installer

`ShotSense-Gateway-Setup-<version>.exe` installs the whole gateway on a client's Windows PC: the
app, a private PostgreSQL, the settings, the emergency local admin, Windows services that start on
boot, the firewall rule and the Cloudflare tunnel. Nothing has to be typed in a terminal.

## Building

Needs, on the build PC:

| What | Where it's expected |
| --- | --- |
| .NET 10 SDK | `dotnet` on PATH |
| Inno Setup 6 | `%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe` (`winget install JRSoftware.InnoSetup --scope user`) |
| PostgreSQL 18 | `C:\Program Files\PostgreSQL\18`: its runtime files are bundled |
| cloudflared | `C:\Program Files (x86)\cloudflared\cloudflared.exe` |

```powershell
# only if the dashboard changed:
cd d:\PLCGateway\dashboard; npm run build

powershell -ExecutionPolicy Bypass -File d:\PLCGateway\installer\build.ps1
```

Output: `installer\output\ShotSense-Gateway-Setup-<version>.exe`, about 95 MB. The version is
`<Version>` in `PLCGateway\PLCGateway.csproj`; bump it for every release, or Windows treats a new build
as the same version. Override any of the paths above with `-PgSource`, `-CloudflaredExe` or `-Iscc`.

Keep the bundled PostgreSQL on the same major version (18). An upgrade replaces the binaries and
keeps the data folder, and a different major version can't open it.

## What the setup does

Pages: **installation code** (a large box: click in it and press Ctrl+V; copy it in the cloud with
Clients → Edit → Copy installation code, see `INSTALLATION-CODE.md`) → **PLC** IP, rack and slot (only the
starting value: it can be changed later from the cloud, client page → PLC) → **emergency local admin**
(a password of at least 12 characters, typed twice; user name `localadmin`; works on the plant network
only; keep it in the office) → **plant network** checkbox → install folder.

There is **no shared dashboard login** any more (removed 2026-10-26). Everyone signs in with their own
email: add the client's people in the cloud (client page → Users) before handing the site over.

**Before installing, upload the client's tag file in the cloud** (Clients → Edit → Tag Settings File). The
setup ships no tags; the gateway fetches them from the cloud and setup stops with that message if there is
no file yet.

Then `scripts\Install-Gateway.ps1`:

1. Installs the Visual C++ runtime the database needs, if it's missing.
2. Creates a private PostgreSQL in `C:\ProgramData\ShotSense\pgdata` as the `ShotSensePostgres`
   service: port 5432, or 5433 and up if the PC already has a PostgreSQL. It accepts local
   connections only, and its superuser password is random.
3. Writes `C:\ProgramData\ShotSense\appsettings.Production.json` (keys, tag file address and email
   sign-in client id from the code, plus a random database password and JWT key). Only administrators
   and the gateway can read it.
4. Creates the database and schema, then sets the local admin password (only its hash is stored). Checks the cloud has this client's tag file.
5. Registers `ShotSenseGateway`: delayed automatic start, waits for the database, restarts itself
   after a failure. It runs as its own low-privilege account, not LocalSystem.
6. With the checkbox ticked, opens TCP 5200 for Private and Domain networks only, never Public.
7. Starts the gateway and waits until it answers.
8. Installs the `Cloudflared` tunnel service with the code's token.

Everything is logged to `C:\ProgramData\ShotSense\logs\install-*.log`. No secrets are written to it.

| Run setup when… | It… |
| --- | --- |
| nothing is installed | installs everything (**Fresh**) |
| a first install failed part-way | runs Fresh again, reuses what's already there, and finishes |
| it's already installed | asks nothing, stops the services, replaces the files, restarts (**Upgrade**). The gateway migrates its own database as it starts |
| it was uninstalled earlier | asks for a code, the PLC details and the local admin password, keeps the database (**Restore**) |

**Uninstall** removes the services, the firewall rule and the program files. It **keeps**
`C:\ProgramData\ShotSense`: settings, logs and the database with all recorded history.

## Testing a build (clean PC or VM)

1. Register a test client in the cloud, create a tunnel for a test hostname (steps in
   `INSTALLATION-CODE.md`) and paste its token into the client, upload a tag file
   (`tools\Export-SiteProfile.ps1` makes one), then **Copy installation code**.
2. Run the setup. Windows shows "Windows protected your PC" because the setup isn't code-signed yet:
   choose **More info → Run anyway**.
3. Check:
   - the dashboard opens at the end, and you can sign in with an email added on the client's Users page
     (and with an office admin email), and with the local admin on the plant network;
   - it works from another PC at `http://<pc-name>:5200`;
   - it works on the internet at the test hostname;
   - the cloud shows the client;
   - after a reboot, `ShotSensePostgres`, `ShotSenseGateway` and `Cloudflared` are running (services.msc) and the dashboard still works;
   - running the same setup again is an upgrade, and the data is still there;
   - uninstalling keeps `C:\ProgramData\ShotSense`, and installing again restores everything.

## Support commands (on the client PC)

From an administrator command prompt in `C:\Program Files\ShotSense Gateway\app`:

```
PLCGateway.exe --set-local-admin   set a new emergency local admin password, then restart the ShotSense Gateway service
PLCGateway.exe --migrate-only      bring the database schema up to date
```

Gateway logs: `C:\ProgramData\ShotSense\logs\gateway-YYYYMMDD.log`, kept for 30 days.

## Known limits

- **Not code-signed yet**, so expect the SmartScreen warning. Buy a code-signing certificate before
  shipping to clients, then sign both `PLCGateway.exe` and the setup.
- Keys can't be changed from the wizard on a running install: edit the settings file and restart
  the service.
