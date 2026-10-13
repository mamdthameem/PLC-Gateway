<#
.SYNOPSIS
    Builds a ShotSense installation code by hand.

.DESCRIPTION
    The cloud is meant to generate this code when a client is registered (format: INSTALLATION-CODE.md).
    Until it does, or to test the installer, build one here from the values the cloud registration
    shows. The keys and the tunnel token are asked for without showing them on screen, and the code
    is checked with the same rules the installer uses before it is printed.

    The printed code contains every secret for that client. Paste it straight into the installer and
    do not save it in a file, an email or a chat.

.EXAMPLE
    .\tools\New-InstallCode.ps1 -ClientId 00837eb94ad026ed -ClientName "Sree Sakthi Foundry" `
        -PublicHostname clienttest.shotsense.com `
        -LicenseCheckUrl "https://asia-south1-shotsense-13b1f.cloudfunctions.net/licenseCheck?clientId=00837eb94ad026ed"
#>
param(
    [Parameter(Mandatory)][string]$ClientId,
    [Parameter(Mandatory)][string]$ClientName,
    [Parameter(Mandatory)][string]$LicenseCheckUrl,
    # Leave out for a plant-network-only install with no internet address.
    [string]$PublicHostname = ''
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\installer\scripts\Common.ps1')

function Read-Secret([string]$Prompt) {
    $secure = Read-Host -Prompt $Prompt -AsSecureString
    $ptr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    try { return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($ptr).Trim() }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($ptr) }
}

$licenseKey  = Read-Secret 'Licence key (License:Key)'
$adminApiKey = Read-Secret 'Admin API key (Admin:ApiKey)'
$tunnelToken = ''
if ($PublicHostname) {
    $tunnelToken = Read-Secret "Cloudflare tunnel token for $PublicHostname"
}

$payload = [ordered]@{
    v               = 1
    clientId        = [string]$ClientId
    clientName      = [string]$ClientName
    publicHostname  = [string]$PublicHostname
    licenseCheckUrl = [string]$LicenseCheckUrl
    licenseKey      = [string]$licenseKey
    adminApiKey     = [string]$adminApiKey
    tunnelToken     = [string]$tunnelToken
    siteProfileUrl  = [string](Get-SiteProfileUrl $LicenseCheckUrl)
}
$json = $payload | ConvertTo-Json -Compress
$b64url = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($json)).TrimEnd('=').Replace('+', '-').Replace('/', '_')
$code = "SSG1.$b64url"

ConvertFrom-InstallCode $code | Out-Null   # the installer's own checks; throws with the reason

Write-Host "`nInstallation code for $ClientName (paste it into the installer; do not save it):`n" -ForegroundColor Green
Write-Output $code
