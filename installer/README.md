# ShotSense Gateway installer

`ShotSense-Gateway-Setup-<version>.exe` installs the whole gateway on a client's Windows PC: the
app, a private PostgreSQL, the settings, the first dashboard login, Windows services that start on
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

Pages: **installation code** (from the cloud, see `INSTALLATION-CODE.md`) → **PLC** IP, rack and slot →
**dashboard login** (username, and a password of at least 12 characters, typed twice) → **plant network**
checkbox → install folder.

Then `scripts\Install-Gateway.ps1`:

1. Installs the Visual C++ runtime the database needs, if it's missing.
2. Creates a private PostgreSQL in `C:\ProgramData\ShotSense\pgdata` as the `ShotSensePostgres`
   service: port 5432, or 5433 and up if the PC already has a PostgreSQL. It accepts local
   connections only, and its superuser password is random.
3. Writes `C:\ProgramData\ShotSense\appsettings.Production.json` (keys from the code, plus a random
   database password and JWT key). Only administrators and the gateway can read it.
4. Creates the database and schema, then the dashboard login.
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
| it was uninstalled earlier | asks for a code and the PLC details, keeps the database and its logins (**Restore**) |

**Uninstall** removes the services, the firewall rule and the program files. It **keeps**
`C:\ProgramData\ShotSense`: settings, logs and the database with all recorded history.

## Testing a build (clean PC or VM)

1. Make a code: register a test client in the cloud, create a tunnel for a test hostname (steps in
   `INSTALLATION-CODE.md`), then run `tools\New-InstallCode.ps1`.
2. Run the setup. Windows shows "Windows protected your PC" because the setup isn't code-signed yet:
   choose **More info → Run anyway**.
3. Check:
   - the dashboard opens at the end, and you can sign in;
   - it works from another PC at `http://<pc-name>:5200`;
   - it works on the internet at the test hostname;
   - the cloud shows the client;
   - after a reboot, `ShotSensePostgres`, `ShotSenseGateway` and `Cloudflared` are running (services.msc) and the dashboard still works;
   - running the same setup again is an upgrade, and the data is still there;
   - uninstalling keeps `C:\ProgramData\ShotSense`, and installing again restores everything.

## Support commands (on the client PC)

From an administrator command prompt in `C:\Program Files\ShotSense Gateway\app`:

```
PLCGateway.exe --set-password      change a dashboard password (asks for username and password)
PLCGateway.exe --create-admin      add another admin login
PLCGateway.exe --migrate-only      bring the database schema up to date
```

Gateway logs: `C:\ProgramData\ShotSense\logs\gateway-YYYYMMDD.log`, kept for 30 days.

## Known limits

- **Not code-signed yet**, so expect the SmartScreen warning. Buy a code-signing certificate before
  shipping to clients, then sign both `PLCGateway.exe` and the setup.
- Keys can't be changed from the wizard on a running install: edit the settings file and restart
  the service.
