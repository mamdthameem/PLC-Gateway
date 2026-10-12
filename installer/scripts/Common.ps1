# Shared helpers for the ShotSense Gateway installer scripts.
# Dot-sourced by Install-Gateway.ps1, Uninstall-Gateway.ps1 and Describe-InstallCode.ps1.
#
# Written for Windows PowerShell 5.1 (every Windows 10/11 PC has it): no ternaries, no ?? and
# ASCII only, because 5.1 reads a BOM-less script as the ANSI code page.

$script:LogFile = $null

# Well-known SIDs, so the scripts work on non-English Windows where the account NAMES are translated.
$script:SidAdministrators  = '*S-1-5-32-544'
$script:SidSystem          = '*S-1-5-18'
$script:SidNetworkService  = '*S-1-5-20'
$script:SidCreatorOwner    = '*S-1-3-0'

function Start-InstallLog([string]$LogsDir, [string]$Name) {
    New-Item -ItemType Directory -Force -Path $LogsDir | Out-Null
    $script:LogFile = Join-Path $LogsDir ("{0}-{1:yyyyMMdd-HHmmss}.log" -f $Name, (Get-Date))
}

# Never pass a secret to this. Passwords, keys and tokens must not reach the log file.
function Write-Log([string]$Message) {
    $line = "{0:yyyy-MM-dd HH:mm:ss}  {1}" -f (Get-Date), $Message
    Write-Host $line
    if ($script:LogFile) { Add-Content -Path $script:LogFile -Value $line -Encoding UTF8 }
}

# Runs a program and throws if it fails. -Mask hides a secret argument in the log.
function Invoke-Native {
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [string[]]$Arguments = @(),
        [string]$Mask,
        [int[]]$SuccessCodes = @(0)
    )
    $shown = ($Arguments | ForEach-Object { if ($Mask -and $_ -eq $Mask) { '***' } else { $_ } }) -join ' '
    Write-Log "> $(Split-Path $FilePath -Leaf) $shown"
    # Windows PowerShell 5.1 turns every stderr line of a native program into an error record, and
    # under ErrorActionPreference=Stop the first one aborts the script. initdb and pg_ctl write
    # ordinary progress to stderr, so success is judged by the exit code alone.
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    # Cleared first, so a program that reports no exit code can never borrow the previous program's
    # 0 and pass by accident (that is how the windowed vc_redist used to "succeed" on a fresh install).
    $global:LASTEXITCODE = $null
    try {
        $output = & $FilePath @Arguments 2>&1
        $code = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $previous
    }
    if ($null -eq $code) {
        throw "$(Split-Path $FilePath -Leaf) did not report an exit code. A windowed program must be run with Start-Process -Wait."
    }
    foreach ($line in $output) {
        $text = "$line"
        if ($Mask) { $text = $text.Replace($Mask, '***') }
        if ($text.Trim()) { Write-Log "    $text" }
    }
    if ($SuccessCodes -notcontains $code) {
        throw "$(Split-Path $FilePath -Leaf) failed with exit code $code."
    }
    return $code
}

# Random letters and digits from the OS's cryptographic generator. Safe inside a connection string,
# JSON and SQL without any escaping.
function New-Secret([int]$Length = 32) {
    $alphabet = [char[]]'ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789'
    $bytes = New-Object byte[] ($Length * 2)
    $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
    $chars = New-Object char[] $Length
    for ($i = 0; $i -lt $Length; $i++) {
        $value = [BitConverter]::ToUInt16($bytes, $i * 2)
        $chars[$i] = $alphabet[$value % $alphabet.Length]
    }
    return -join $chars
}

function New-Base64Secret([int]$Bytes = 48) {
    $buffer = New-Object byte[] $Bytes
    $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($buffer) } finally { $rng.Dispose() }
    return [Convert]::ToBase64String($buffer)
}

# Administrators and SYSTEM only (plus any extra grants), nothing inherited from above. For files and
# folders holding secrets. -Folder makes the grants flow down to everything inside.
function Set-PrivateAcl {
    param([Parameter(Mandatory)][string]$Path, [string[]]$ExtraGrants = @(), [switch]$Folder)
    $flags = ''
    if ($Folder) { $flags = '(OI)(CI)' }
    $grants = @("${script:SidAdministrators}:${flags}F", "${script:SidSystem}:${flags}F") + $ExtraGrants
    $arguments = @($Path, '/inheritance:r', '/grant:r') + $grants
    Invoke-Native -FilePath "$env:WINDIR\System32\icacls.exe" -Arguments $arguments | Out-Null
}

# Best-effort native call: output discarded, failures ignored. For settings that are nice to have
# (service descriptions, recovery options) where a failure must not stop the install.
function Invoke-Quiet([string]$FilePath, [string[]]$Arguments) {
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { & $FilePath @Arguments 2>&1 | Out-Null } finally { $ErrorActionPreference = $previous }
}

function Test-PortInUse([int]$Port) {
    $listener = Get-NetTCPConnection -State Listen -LocalPort $Port -ErrorAction SilentlyContinue
    return [bool]$listener
}

# 5432 when this PC has no PostgreSQL of its own; otherwise the first free port from 5433 up, so the
# gateway's private database never collides with one the client already runs.
function Get-FreePostgresPort {
    $otherPostgres = Get-Service -ErrorAction SilentlyContinue | Where-Object { $_.Name -like 'postgresql*' }
    if (-not $otherPostgres -and -not (Test-PortInUse 5432)) { return 5432 }
    for ($port = 5433; $port -le 5499; $port++) {
        if (-not (Test-PortInUse $port)) { return $port }
    }
    throw 'No free port between 5433 and 5499 for the database.'
}

function Wait-Until {
    param([Parameter(Mandatory)][scriptblock]$Condition, [int]$TimeoutSeconds = 60, [string]$What = 'condition')
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        if (& $Condition) { return }
        Start-Sleep -Seconds 2
    }
    throw "Timed out after $TimeoutSeconds s waiting for $What."
}

function Test-HttpOk([string]$Url) {
    try {
        $response = Invoke-WebRequest -Uri $Url -UseBasicParsing -TimeoutSec 5
        return $response.StatusCode -eq 200
    } catch { return $false }
}

# The installation code the cloud shows when a client is registered:
#   SSG1.<base64url of UTF-8 JSON>
# Full description: INSTALLATION-CODE.md at the repository root. Keep the two in step.
function ConvertFrom-InstallCode([string]$Code) {
    if ([string]::IsNullOrWhiteSpace($Code)) { throw 'Paste the installation code from the cloud.' }
    $Code = $Code -replace '\s', ''
    if (-not $Code.StartsWith('SSG1.')) { throw 'This is not a ShotSense installation code. It should start with SSG1.' }

    $b64 = $Code.Substring(5).Replace('-', '+').Replace('_', '/')
    switch ($b64.Length % 4) {
        2 { $b64 += '==' }
        3 { $b64 += '=' }
        1 { throw 'The installation code is incomplete. Copy it again from the cloud.' }
    }
    try {
        $json = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($b64))
        $c = $json | ConvertFrom-Json
    } catch {
        throw 'The installation code is damaged. Copy it again from the cloud.'
    }

    if ([string]$c.v -ne '1') { throw "This installation code needs a newer installer (code format $($c.v))." }
    foreach ($field in 'clientId', 'clientName', 'licenseCheckUrl', 'licenseKey', 'adminApiKey') {
        if ([string]::IsNullOrWhiteSpace([string]$c.$field)) {
            throw "The installation code has no '$field'. Generate a new code in the cloud."
        }
    }
    if (-not ([string]$c.licenseCheckUrl).StartsWith('https://')) {
        throw 'The licence check address in the code must start with https://.'
    }
    if (([string]$c.licenseKey).Length -lt 32 -or ([string]$c.adminApiKey).Length -lt 32) {
        throw 'The keys in the installation code are too short. Generate a new code in the cloud.'
    }
    $hasTunnel = -not [string]::IsNullOrWhiteSpace([string]$c.tunnelToken)
    if ($hasTunnel -and [string]::IsNullOrWhiteSpace([string]$c.publicHostname)) {
        throw 'The code has a tunnel token but no public address (publicHostname).'
    }
    return $c
}
