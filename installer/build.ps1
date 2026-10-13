<#
.SYNOPSIS
    Builds the ShotSense Gateway installer: installer\output\ShotSense-Gateway-Setup-<version>.exe

.DESCRIPTION
    1. Publishes the gateway as a self-contained win-x64 app (no .NET needed on the client PC).
    2. Stages PostgreSQL's runtime files from a local PostgreSQL install (server, tools, timezone
       and extension data; not pgAdmin, StackBuilder, docs, headers or compile-only libraries).
    3. Stages cloudflared.exe and the Visual C++ runtime installer PostgreSQL needs.
    4. Compiles installer\ShotSenseGateway.iss with Inno Setup.

    The version comes from <Version> in PLCGateway\PLCGateway.csproj - bump it there for every release.
    The dashboard is NOT rebuilt here: PLCGateway\wwwroot is published as it is, so run
    `npm run build` in dashboard\ first if it changed.

    The bundled PostgreSQL major version must stay the same across releases: an upgrade replaces the
    binaries but keeps the data folder, and a different major version cannot open it.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File installer\build.ps1
#>
param(
    [string]$PgSource       = 'C:\Program Files\PostgreSQL\18',
    [string]$CloudflaredExe = 'C:\Program Files (x86)\cloudflared\cloudflared.exe',
    [string]$Iscc           = "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
)

$ErrorActionPreference = 'Stop'
$here    = $PSScriptRoot
$root    = Split-Path $here -Parent
$staging = Join-Path $here 'staging'
$csproj  = Join-Path $root 'PLCGateway\PLCGateway.csproj'

function Step([string]$Text) { Write-Host "`n== $Text" -ForegroundColor Cyan }

# ---- Prerequisites ---------------------------------------------------------------------------
foreach ($required in @($csproj, $Iscc, $CloudflaredExe, (Join-Path $PgSource 'bin\postgres.exe'),
                        (Join-Path $PgSource 'installer\vcredist_x64.exe'), (Join-Path $here 'assets\shotsense.ico'))) {
    if (-not (Test-Path $required)) { throw "Not found: $required" }
}
$version = ([xml](Get-Content $csproj)).Project.PropertyGroup |
    ForEach-Object { $_.Version } | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw "No <Version> in $csproj." }
$pgVersion = (& (Join-Path $PgSource 'bin\postgres.exe') --version) -replace '^.*\) ', ''
Write-Host "ShotSense Gateway $version  |  PostgreSQL $pgVersion  |  $(& $CloudflaredExe --version)"

if (Test-Path $staging) { Remove-Item -Recurse -Force $staging }
New-Item -ItemType Directory -Force -Path $staging | Out-Null

# ---- 1. Gateway ------------------------------------------------------------------------------
Step 'Publishing the gateway (self-contained, win-x64)'
& dotnet publish $csproj -c Release -r win-x64 --self-contained true -o (Join-Path $staging 'app') `
    -p:DebugType=none -p:DebugSymbols=false --nologo
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

$app = Join-Path $staging 'app'
if (Test-Path (Join-Path $app 'appsettings.Production.json')) {
    throw 'appsettings.Production.json (this PC''s secrets) ended up in the publish output. Refusing to build.'
}
if (-not (Test-Path (Join-Path $app 'wwwroot\index.html'))) { throw 'The dashboard (wwwroot) is missing from the publish output.' }

# The shipped appsettings.json must not point at the development database. An installed gateway gets
# its connection string from C:\ProgramData\ShotSense; if that is ever missing it should stop with a
# clear error, not quietly connect to whatever the dev default happens to be.
$appsettings = Join-Path $app 'appsettings.json'
$text = [IO.File]::ReadAllText($appsettings)
$blanked = [regex]::Replace($text, '("(?:PostgresDb|ConnectionString)"\s*:\s*)"[^"]*"', '$1""')
if ($blanked -eq $text) { throw 'Could not find the connection strings in the published appsettings.json.' }
[IO.File]::WriteAllText($appsettings, $blanked, (New-Object Text.UTF8Encoding($false)))
Write-Host 'Development connection strings removed from the shipped appsettings.json.'

# No client's tags ship in the installer: every installed gateway fetches its own tag file from the
# cloud (PLCGateway/Hosting/SiteProfile.cs). These are SiteProfile.Sections — keep the two lists the
# same. Settings files merge lists by position, so a tag left here would also be read on every client.
$profileSections = 'Tags', 'ScanIntervalMs', 'CovRules', 'DataCollection', 'MaintenanceThresholds'
$settings = [IO.File]::ReadAllText($appsettings) | ConvertFrom-Json
foreach ($section in $profileSections) { $settings.PSObject.Properties.Remove($section) }
[IO.File]::WriteAllText($appsettings, ($settings | ConvertTo-Json -Depth 20), (New-Object Text.UTF8Encoding($false)))
$check = [IO.File]::ReadAllText($appsettings)
if ($check -match '"Tags"|DB\d+\.DB[BWDX]') { throw 'Tag settings are still in the shipped appsettings.json. Refusing to build.' }
Write-Host 'Tag settings removed from the shipped appsettings.json (each gateway fetches its own from the cloud).'

# ---- 2. PostgreSQL ---------------------------------------------------------------------------
Step "Staging PostgreSQL $pgVersion"
$pg = Join-Path $staging 'pgsql'
function Copy-Tree([string]$From, [string]$To, [string[]]$ExcludeFiles = @()) {
    $arguments = @($From, $To, '/E', '/NFL', '/NDL', '/NJH', '/NJS', '/NP')
    if ($ExcludeFiles.Count) { $arguments += '/XF'; $arguments += $ExcludeFiles }
    & robocopy.exe @arguments | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "Copying $From failed (robocopy exit $LASTEXITCODE)." }
}
Copy-Tree (Join-Path $PgSource 'bin')   (Join-Path $pg 'bin')   @('stackbuilder.exe', 'wx*.dll', '*.pdb')
Copy-Tree (Join-Path $PgSource 'lib')   (Join-Path $pg 'lib')   @('*.lib', '*.a', '*.pdb')
Copy-Tree (Join-Path $PgSource 'share') (Join-Path $pg 'share')
Set-Content -Path (Join-Path $pg 'VERSION.txt') -Value "PostgreSQL $pgVersion, staged from $PgSource" -Encoding ASCII

# ---- 3. Tunnel and runtime -------------------------------------------------------------------
Step 'Staging cloudflared and the Visual C++ runtime'
New-Item -ItemType Directory -Force -Path (Join-Path $staging 'cloudflared'), (Join-Path $staging 'redist') | Out-Null
Copy-Item $CloudflaredExe (Join-Path $staging 'cloudflared\cloudflared.exe')
Copy-Item (Join-Path $PgSource 'installer\vcredist_x64.exe') (Join-Path $staging 'redist\vc_redist.x64.exe')

$sizeMb = (Get-ChildItem $staging -Recurse -File | Measure-Object Length -Sum).Sum / 1MB
Write-Host ("Staged {0:N0} MB." -f $sizeMb)

# ---- 4. Installer ----------------------------------------------------------------------------
Step 'Compiling the installer'
& $Iscc "/DAppVersion=$version" "/DStaging=$staging" "/Q" (Join-Path $here 'ShotSenseGateway.iss')
if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed (exit $LASTEXITCODE)." }

$output = Join-Path $here "output\ShotSense-Gateway-Setup-$version.exe"
Write-Host ("`nBuilt {0}  ({1:N0} MB)" -f $output, ((Get-Item $output).Length / 1MB)) -ForegroundColor Green
