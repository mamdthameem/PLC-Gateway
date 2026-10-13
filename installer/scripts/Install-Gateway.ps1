# ShotSense Gateway - the work the setup wizard does after it has copied the program files.
#
# Run by ShotSenseGateway.iss (elevated). Logs to C:\ProgramData\ShotSense\logs\install-*.log and
# exits 0 on success, 1 on failure. Secrets arrive in environment variables set by the wizard, never
# on the command line and never in the log:
#   SHOTSENSE_INSTALL_CODE       the code from the cloud (Fresh and Restore)
#   SHOTSENSE_ADMIN_USERNAME     first dashboard login (Fresh)
#   SHOTSENSE_ADMIN_PASSWORD
#
# Modes (the wizard decides):
#   Fresh    nothing installed before. Bundled PostgreSQL, database, settings, admin login,
#            services, firewall rule, tunnel.
#   Restore  ProgramData\ShotSense survives from an earlier install that was uninstalled. Keeps the
#            database and its data, rewrites settings from the new code, re-creates the services.
#   Upgrade  the services exist. Program files were just replaced; restart everything. The gateway
#            migrates its own schema on start.
#
# Nothing here ever drops, empties or deletes the database.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('Fresh', 'Restore', 'Upgrade')][string]$Mode,
    [Parameter(Mandatory)][string]$AppDir,
    [string]$PlcIp = '192.168.0.180',
    [int]$PlcRack = 0,
    [int]$PlcSlot = 1,
    [int]$OpenFirewall = 1,
    [int]$DashboardPort = 5200,
    [string]$VcRedist,
    [string]$SummaryFile
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Common.ps1')

$DataDir      = Join-Path $env:ProgramData 'ShotSense'
$LogsDir      = Join-Path $DataDir 'logs'
$SiteDir      = Join-Path $DataDir 'site'      # the gateway's encrypted copy of the client's tag file
$SettingsFile = Join-Path $DataDir 'appsettings.Production.json'
$DbAdminFile  = Join-Path $DataDir 'db-admin.json'
$PgData       = Join-Path $DataDir 'pgdata'

$GatewayExe   = Join-Path $AppDir 'app\PLCGateway.exe'
$PgBin        = Join-Path $AppDir 'pgsql\bin'
$Cloudflared  = Join-Path $AppDir 'cloudflared\cloudflared.exe'

$GatewayService     = 'ShotSenseGateway'
$PostgresService    = 'ShotSensePostgres'
$CloudflaredService = 'Cloudflared'
$GatewayAccount     = "NT SERVICE\$GatewayService"
$FirewallRuleName   = 'ShotSenseGateway-Dashboard'

$DbName = 'shotsense_gateway'
$DbUser = 'shotsense_gateway'

# ---------------------------------------------------------------------------------------------

function Get-InstalledVcRuntimeVersion {
    # Where the Visual C++ 2015-2022 x64 runtime records itself when installed.
    $key = Get-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\X64' -ErrorAction SilentlyContinue
    if (-not $key -or $key.Installed -ne 1) { return $null }
    return New-Object Version ([int]$key.Major), ([int]$key.Minor), ([int]$key.Bld), ([int]$key.Rbld)
}

function Install-VcRuntime {
    # The bundled PostgreSQL needs the Visual C++ 2015-2022 runtime, at least the version it was
    # built with, which is the one bundled.
    if (-not $VcRedist -or -not (Test-Path $VcRedist)) { Write-Log 'Visual C++ runtime installer not supplied; assuming it is present.'; return }

    $bundled   = New-Object Version ((Get-Item $VcRedist).VersionInfo.FileVersion)
    $installed = Get-InstalledVcRuntimeVersion
    if ($installed -and $installed -ge $bundled) {
        Write-Log "Visual C++ runtime $installed is already installed (bundled: $bundled); nothing to do."
        return
    }

    # Start-Process -Wait, not '& exe': vc_redist is a windowed program, and PowerShell neither waits
    # for a windowed program called with & nor gets its exit code. It used to return at once with no
    # code at all - a failed step on an upgrade, and on a clean PC a database started before its
    # runtime existed. Exit codes: 0 done, 1638 a newer one already present, 3010 done but a restart
    # is recommended (PostgreSQL does not need it).
    Write-Log "Installing the Visual C++ runtime $bundled (found: $(if ($installed) { $installed } else { 'none' }))."
    $process = Start-Process -FilePath $VcRedist -ArgumentList '/install', '/quiet', '/norestart' -Wait -PassThru -WindowStyle Hidden
    $code = $process.ExitCode
    if (@(0, 1638, 3010) -notcontains $code) {
        throw "The Visual C++ runtime installer failed (exit code $code). Install it from Microsoft (aka.ms/vs/17/release/vc_redist.x64.exe) and run setup again."
    }
    Write-Log "Visual C++ runtime installer finished (exit code $code)."
}

function Initialize-DataFolders {
    New-Item -ItemType Directory -Force -Path $DataDir, $LogsDir, $SiteDir | Out-Null
    # Settings and database files are not for ordinary users of this PC. Logs stay readable to
    # administrators; the gateway service is given its own rights further down.
    Set-PrivateAcl -Path $DataDir -Folder
}

function Initialize-PostgresCluster([string]$SuperuserPassword, [int]$Port) {
    Write-Log "Creating the database files in $PgData (port $Port)."
    New-Item -ItemType Directory -Force -Path $PgData | Out-Null

    # initdb drops administrator rights before it writes anything, so the account running setup
    # needs its own grant on the folder - exactly what PostgreSQL's official installer does.
    $me = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    Invoke-Native -FilePath "$env:WINDIR\System32\icacls.exe" -Arguments @(
        $PgData, '/grant', "*${me}:(OI)(CI)F", '/grant', "${script:SidNetworkService}:(OI)(CI)F",
        '/grant', "${script:SidCreatorOwner}:(OI)(CI)F") | Out-Null

    $pwFile = Join-Path $env:TEMP ("shotsense-pg-{0}.txt" -f [guid]::NewGuid())
    [IO.File]::WriteAllText($pwFile, $SuperuserPassword)
    try {
        Invoke-Native -FilePath (Join-Path $PgBin 'initdb.exe') -Arguments @(
            '-D', $PgData, '-U', 'postgres', '-E', 'UTF8', '--locale=C',
            '--auth=scram-sha-256', "--pwfile=$pwFile") | Out-Null
    } finally {
        Remove-Item -LiteralPath $pwFile -Force -ErrorAction SilentlyContinue
    }

    # Private to this PC: only local connections, on the port chosen for it.
    Add-Content -Path (Join-Path $PgData 'postgresql.conf') -Encoding ASCII -Value @(
        '', '# ShotSense Gateway', "port = $Port", "listen_addresses = 'localhost'")
}

function Register-PostgresService([int]$Port) {
    if (-not (Get-Service $PostgresService -ErrorAction SilentlyContinue)) {
        Write-Log "Registering the $PostgresService service."
        Invoke-Native -FilePath (Join-Path $PgBin 'pg_ctl.exe') -Arguments @(
            'register', '-N', $PostgresService, '-D', $PgData, '-S', 'auto',
            '-U', 'NT AUTHORITY\NetworkService', '-w') | Out-Null
        Invoke-Quiet sc.exe @('description', $PostgresService, 'Database for ShotSense Gateway. The gateway records into it; keep it running.')
        Invoke-Quiet sc.exe @('failure', $PostgresService, 'reset=', '86400', 'actions=', 'restart/60000/restart/60000/restart/60000')
    }
    Start-PostgresAndWait $Port
}

function Start-PostgresAndWait([int]$Port) {
    Start-Service $PostgresService
    $isReady = Join-Path $PgBin 'pg_isready.exe'
    Wait-Until -TimeoutSeconds 90 -What "the database to accept connections on port $Port" -Condition {
        $ErrorActionPreference = 'Continue'   # local to this block; see Invoke-Native
        & $isReady -h localhost -p $Port -q 2>&1 | Out-Null
        $LASTEXITCODE -eq 0
    }
    Write-Log "Database is running on port $Port."
}

function Get-ExistingSettings {
    if (Test-Path $SettingsFile) { return Get-Content -Raw -Path $SettingsFile | ConvertFrom-Json }
    return $null
}

function Write-Settings($Code, [int]$PgPort, [string]$DbPassword, [string]$JwtKey) {
    $connection = "Host=localhost;Port=$PgPort;Database=$DbName;Username=$DbUser;Password=$DbPassword"
    if ($OpenFirewall -eq 1) { $bind = '0.0.0.0' } else { $bind = 'localhost' }

    # Every value is cast to [string] or [int]: Windows PowerShell attaches hidden properties to some
    # strings, and ConvertTo-Json would then write an object where the gateway expects text - after
    # which the gateway silently falls back to the development connection string.
    $settings = [ordered]@{
        Urls              = [string]"http://${bind}:$DashboardPort"
        ConnectionStrings = [ordered]@{ PostgresDb = [string]$connection }
        PostgreSQL        = [ordered]@{ ConnectionString = [string]$connection }
        PLC               = [ordered]@{ IpAddress = [string]$PlcIp; Rack = [int]$PlcRack; Slot = [int]$PlcSlot }
        Jwt               = [ordered]@{ Key = [string]$JwtKey }
        Admin             = [ordered]@{ ApiKey = [string]$Code.adminApiKey }
        License           = [ordered]@{ CheckUrl = [string]$Code.licenseCheckUrl; Key = [string]$Code.licenseKey }
        # Email sign-in accepts only this client's people; the tag file comes from the cloud.
        Firebase          = [ordered]@{ ClientId = [string]$Code.clientId }
        SiteProfile       = [ordered]@{ Url = [string]$Code.siteProfileUrl }
        ShotSense         = [ordered]@{
            ClientId       = [string]$Code.clientId
            ClientName     = [string]$Code.clientName
            PublicHostname = [string]$Code.publicHostname
        }
    }
    $json = $settings | ConvertTo-Json -Depth 5
    [IO.File]::WriteAllText($SettingsFile, $json, (New-Object Text.UTF8Encoding($false)))
    Set-PrivateAcl -Path $SettingsFile
    Write-Log "Settings written to $SettingsFile (dashboard on $bind`:$DashboardPort, PLC $PlcIp rack $PlcRack slot $PlcSlot)."
}

function Invoke-Gateway([string[]]$Arguments, [int[]]$SuccessCodes = @(0)) {
    # Run from the program folder so the gateway finds its appsettings.json next to it.
    Push-Location (Split-Path $GatewayExe -Parent)
    try { return Invoke-Native -FilePath $GatewayExe -Arguments $Arguments -SuccessCodes $SuccessCodes }
    finally { Pop-Location }
}

function Register-GatewayService {
    if (-not (Get-Service $GatewayService -ErrorAction SilentlyContinue)) {
        Write-Log "Registering the $GatewayService service."
        New-Service -Name $GatewayService -BinaryPathName "`"$GatewayExe`"" -DisplayName 'ShotSense Gateway' `
            -Description 'Reads the PLC, records into the database and serves the dashboard.' `
            -StartupType Automatic -DependsOn $PostgresService | Out-Null
    }
    # Delayed start: Windows brings it up shortly after boot, once the database service is running.
    Invoke-Native -FilePath "$env:WINDIR\System32\sc.exe" -Arguments @('config', $GatewayService, 'start=', 'delayed-auto') | Out-Null
    # Its own low-privilege identity (a virtual account), not LocalSystem: the dashboard faces the
    # internet through the tunnel, so the process should not own the whole PC.
    Invoke-Native -FilePath "$env:WINDIR\System32\sc.exe" -Arguments @('config', $GatewayService, 'obj=', $GatewayAccount) | Out-Null
    # Restart after a minute on any failure, including the clean "exit 1" the gateway does when its
    # database stays unreachable.
    Invoke-Quiet sc.exe @('failure', $GatewayService, 'reset=', '86400', 'actions=', 'restart/60000/restart/60000/restart/60000')
    Invoke-Quiet sc.exe @('failureflag', $GatewayService, '1')

    # What the service account may touch: read its program folder and settings, write its logs and
    # its saved copy of the tag file.
    $icacls = "$env:WINDIR\System32\icacls.exe"
    New-Item -ItemType Directory -Force -Path $SiteDir | Out-Null
    Invoke-Native -FilePath $icacls -Arguments @((Split-Path $GatewayExe -Parent), '/grant', "${GatewayAccount}:(OI)(CI)RX") | Out-Null
    Invoke-Native -FilePath $icacls -Arguments @($DataDir, '/grant', "${GatewayAccount}:(RX)") | Out-Null
    Invoke-Native -FilePath $icacls -Arguments @($LogsDir, '/grant', "${GatewayAccount}:(OI)(CI)M") | Out-Null
    Invoke-Native -FilePath $icacls -Arguments @($SiteDir, '/grant', "${GatewayAccount}:(OI)(CI)M") | Out-Null
    Invoke-Native -FilePath $icacls -Arguments @($SettingsFile, '/grant', "${GatewayAccount}:R") | Out-Null
}

function Set-FirewallRule {
    Remove-NetFirewallRule -Name $FirewallRuleName -ErrorAction SilentlyContinue
    if ($OpenFirewall -eq 1) {
        New-NetFirewallRule -Name $FirewallRuleName -DisplayName "ShotSense Gateway dashboard (TCP $DashboardPort)" `
            -Direction Inbound -Action Allow -Protocol TCP -LocalPort $DashboardPort `
            -Profile Domain, Private -Program $GatewayExe | Out-Null
        Write-Log "Firewall: port $DashboardPort open to Private and Domain networks (not Public)."
    } else {
        Write-Log 'Firewall: dashboard reachable from this PC only.'
    }
}

# The gateway reads the PLC with the client's tag file, fetched from the cloud when it starts (it keeps
# an encrypted copy for starts without internet). With no file in the cloud it cannot start, so say
# so now, plainly, instead of after a three-minute wait for a gateway that never answers.
function Assert-SiteProfile([string]$Url, [string]$LicenseKey) {
    if ([string]::IsNullOrWhiteSpace($Url)) { return }
    try {
        $response = Invoke-WebRequest -Uri $Url -Headers @{ 'X-License-Key' = $LicenseKey } -UseBasicParsing -TimeoutSec 30
        $tags = @((($response.Content | ConvertFrom-Json).profile.Tags)).Count
        Write-Log "Tag file found in the cloud ($tags tags)."
    } catch {
        $status = 0
        if ($_.Exception.Response) { $status = [int]$_.Exception.Response.StatusCode }
        if ($status -eq 404) {
            throw 'There is no tag file for this client in the cloud yet. Upload it (cloud: Clients, Edit on this client, Tag Settings File), then run setup again.'
        }
        if ($status -eq 403) {
            throw 'The cloud refused this installation''s licence key when fetching the tag file. Copy a fresh installation code from the cloud and run setup again.'
        }
        throw "The tag file could not be fetched from the cloud ($($_.Exception.Message)). Check this PC's internet connection and run setup again."
    }
}

# Installs from before 2026-10-13 have no tag file address or email client id in their settings: add
# them, from the licence check address and the client id the install already has.
function Update-SettingsForUpgrade {
    $existing = Get-ExistingSettings
    if (-not $existing) { return $null }
    $changed = $false
    if (-not $existing.SiteProfile -or [string]::IsNullOrWhiteSpace([string]$existing.SiteProfile.Url)) {
        $url = Get-SiteProfileUrl ([string]$existing.License.CheckUrl)
        $existing | Add-Member -NotePropertyName SiteProfile -NotePropertyValue ([pscustomobject]@{ Url = [string]$url }) -Force
        $changed = $true
    }
    if (-not $existing.Firebase -and $existing.ShotSense -and $existing.ShotSense.ClientId) {
        $existing | Add-Member -NotePropertyName Firebase -NotePropertyValue ([pscustomobject]@{ ClientId = [string]$existing.ShotSense.ClientId }) -Force
        $changed = $true
    }
    if ($changed) {
        [IO.File]::WriteAllText($SettingsFile, ($existing | ConvertTo-Json -Depth 5), (New-Object Text.UTF8Encoding($false)))
        Set-PrivateAcl -Path $SettingsFile
        Write-Log 'Settings: added the tag file address and the email sign-in client id.'
    }
    return $existing
}

function Start-GatewayAndWait {
    Start-Service $GatewayService
    $url = "http://localhost:$DashboardPort/api/health"
    Wait-Until -TimeoutSeconds 180 -What "the gateway to answer at $url" -Condition { Test-HttpOk $url }
    Write-Log 'Gateway is running and answering.'
}

function Install-Tunnel($Code) {
    $token = [string]$Code.tunnelToken
    if ([string]::IsNullOrWhiteSpace($token)) {
        Write-Log 'No tunnel token in the code: the dashboard is not published to the internet.'
        return
    }

    $existing = Get-CimInstance Win32_Service -Filter "Name='$CloudflaredService'" -ErrorAction SilentlyContinue
    if ($existing) {
        if ($existing.PathName -like "*$AppDir*") {
            Write-Log 'Replacing the existing ShotSense tunnel service.'
            Invoke-Native -FilePath $Cloudflared -Arguments @('service', 'uninstall') -SuccessCodes @(0, 1) | Out-Null
        } else {
            # Another program's tunnel. Replacing it would break that program, so stop and say why.
            throw "This PC already has a Cloudflared service that belongs to another program ($($existing.PathName)). Remove it, or install without a tunnel token."
        }
    }

    Write-Log "Installing the tunnel service for $($Code.publicHostname)."
    Invoke-Native -FilePath $Cloudflared -Arguments @('service', 'install', $token) -Mask $token | Out-Null
    Wait-Until -TimeoutSeconds 60 -What 'the tunnel service to start' -Condition {
        $s = Get-Service $CloudflaredService -ErrorAction SilentlyContinue
        $s -and $s.Status -eq 'Running'
    }
    Write-Log 'Tunnel service is running.'
}

function Write-Summary($Code) {
    if (-not $SummaryFile) { return }
    $lines = @("On this PC: http://localhost:$DashboardPort")
    if ($OpenFirewall -eq 1) { $lines += "On the plant network: http://$($env:COMPUTERNAME):$DashboardPort" }
    if ($Code -and -not [string]::IsNullOrWhiteSpace([string]$Code.tunnelToken)) {
        $lines += "On the internet: https://$($Code.publicHostname)"
    }
    $lines += "Logs and settings: $DataDir"
    [IO.File]::WriteAllLines($SummaryFile, $lines, (New-Object Text.UTF8Encoding($false)))
}

# ---------------------------------------------------------------------------------------------

function Invoke-FreshOrRestore {
    $code = ConvertFrom-InstallCode $env:SHOTSENSE_INSTALL_CODE
    Write-Log "Client: $($code.clientName) ($($code.clientId)). Public address: $($code.publicHostname)"

    Initialize-DataFolders
    Install-VcRuntime

    # Database server: keep an existing one (Restore), otherwise create it.
    if (Test-Path (Join-Path $PgData 'PG_VERSION')) {
        if (-not (Test-Path $DbAdminFile)) {
            throw "Database files exist in $PgData but $DbAdminFile is missing, so they cannot be opened. Restore that file, or move the folder away to start fresh."
        }
        $dbAdmin = Get-Content -Raw $DbAdminFile | ConvertFrom-Json
        $pgPort = [int]$dbAdmin.port
        $superuserPassword = [string]$dbAdmin.superuserPassword
        Write-Log "Keeping the existing database files (port $pgPort)."
    } else {
        $pgPort = Get-FreePostgresPort
        $superuserPassword = New-Secret 32
        $dbAdmin = [ordered]@{
            note              = 'PostgreSQL superuser for the ShotSense Gateway database. Needed to repair or reinstall; keep it.'
            port              = [int]$pgPort
            superuser         = 'postgres'
            superuserPassword = [string]$superuserPassword
        }
        [IO.File]::WriteAllText($DbAdminFile, ($dbAdmin | ConvertTo-Json), (New-Object Text.UTF8Encoding($false)))
        Set-PrivateAcl -Path $DbAdminFile
        Initialize-PostgresCluster -SuperuserPassword $superuserPassword -Port $pgPort
    }
    Register-PostgresService $pgPort

    # Settings: keep the database password and JWT key from an earlier install so existing data
    # stays reachable and signed-in users stay signed in; everything else comes from the new code.
    $existing = Get-ExistingSettings
    $dbPassword = $null
    $jwtKey = $null
    # (In Fresh mode this only happens after a failed attempt: reuse what it already created.)
    if ($existing) {
        $existingConnection = [string]$existing.ConnectionStrings.PostgresDb
        if ($existingConnection -match 'Password=([^;]+)') { $dbPassword = $Matches[1] }
        $jwtKey = [string]$existing.Jwt.Key
    }
    if ([string]::IsNullOrWhiteSpace($dbPassword)) { $dbPassword = New-Secret 32 }
    if ([string]::IsNullOrWhiteSpace($jwtKey) -or $jwtKey.Length -lt 32) { $jwtKey = New-Base64Secret 48 }
    Write-Settings -Code $code -PgPort $pgPort -DbPassword $dbPassword -JwtKey $jwtKey

    # Database and schema, as the gateway's own role.
    $env:SHOTSENSE_PG_SUPERUSER_PASSWORD = $superuserPassword
    try { Invoke-Gateway -Arguments @('--init-database') | Out-Null }
    finally { $env:SHOTSENSE_PG_SUPERUSER_PASSWORD = $null }

    if ($Mode -eq 'Fresh') {
        # Both must be present: without them --create-admin would wait for typing on a hidden window.
        if ([string]::IsNullOrWhiteSpace($env:SHOTSENSE_ADMIN_USERNAME) -or [string]::IsNullOrEmpty($env:SHOTSENSE_ADMIN_PASSWORD)) {
            throw 'No dashboard username and password were given to setup.'
        }
        Write-Log "Creating the dashboard login '$($env:SHOTSENSE_ADMIN_USERNAME)'."
        $created = Invoke-Gateway -Arguments @('--create-admin') -SuccessCodes @(0, 3)
        if ($created -eq 3) {
            # A re-run after a failed install: the login was made last time. Give it the password
            # typed this time, so the account always ends up with what was entered most recently.
            Write-Log 'That login already exists from an earlier attempt; setting the password just entered.'
            Invoke-Gateway -Arguments @('--set-password', '--username', $env:SHOTSENSE_ADMIN_USERNAME) | Out-Null
        }
    } else {
        Write-Log 'Restore: keeping the dashboard logins already in the database.'
    }

    Assert-SiteProfile -Url ([string]$code.siteProfileUrl) -LicenseKey ([string]$code.licenseKey)
    Register-GatewayService
    Set-FirewallRule
    Start-GatewayAndWait
    Install-Tunnel $code
    Write-Summary $code

    # Marks a COMPLETED install. The wizard offers "Restore" (keep data, skip the login page) only when
    # this exists; after a failed first attempt it runs as Fresh again and finishes the job.
    [IO.File]::WriteAllText((Join-Path $DataDir 'install-complete.txt'),
        ("Installed {0:yyyy-MM-dd HH:mm} by setup ({1})." -f (Get-Date), $Mode))
}

function Invoke-Upgrade {
    Write-Log 'Upgrade: program files were replaced. Restarting the services.'
    Install-VcRuntime

    $dbAdmin = Get-Content -Raw $DbAdminFile | ConvertFrom-Json
    Start-PostgresAndWait ([int]$dbAdmin.port)

    # Settings stay as they are, apart from the two added for the cloud tag file and email sign-in;
    # the service registration is refreshed.
    $upgraded = Update-SettingsForUpgrade
    if ($upgraded) { Assert-SiteProfile -Url ([string]$upgraded.SiteProfile.Url) -LicenseKey ([string]$upgraded.License.Key) }
    Register-GatewayService
    Start-GatewayAndWait   # the gateway brings its schema up to date as it starts

    if (Get-Service $CloudflaredService -ErrorAction SilentlyContinue) {
        Start-Service $CloudflaredService
        Write-Log 'Tunnel service restarted.'
    }

    $existing = Get-ExistingSettings
    $summaryCode = $null
    if ($existing) {
        $summaryCode = [pscustomobject]@{ publicHostname = [string]$existing.ShotSense.PublicHostname; tunnelToken = $null }
        if ((Get-Service $CloudflaredService -ErrorAction SilentlyContinue) -and $summaryCode.publicHostname) {
            $summaryCode.tunnelToken = 'present'
        }
        if ([string]$existing.Urls -notlike '*0.0.0.0*') { $script:OpenFirewall = 0 }
    }
    Write-Summary $summaryCode
}

# ---------------------------------------------------------------------------------------------

Start-InstallLog $LogsDir ("install-{0}" -f $Mode.ToLower())
try {
    Write-Log "ShotSense Gateway setup ($Mode) into $AppDir"
    if ($Mode -eq 'Upgrade') { Invoke-Upgrade } else { Invoke-FreshOrRestore }
    Write-Log 'Setup finished successfully.'
    exit 0
} catch {
    Write-Log "SETUP FAILED: $($_.Exception.Message)"
    Write-Log "At: $($_.InvocationInfo.PositionMessage)"
    exit 1
}
