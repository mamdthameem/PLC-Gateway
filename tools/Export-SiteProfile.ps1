<#
.SYNOPSIS
    Writes a client's tag file (site profile) from an appsettings.json, ready to upload in the cloud.

.DESCRIPTION
    Each installed gateway fetches its tag list and recording rules from the cloud
    (PLCGateway/Hosting/SiteProfile.cs); the installer ships none. This picks exactly the sections a tag
    file holds — Tags, ScanIntervalMs, CovRules, DataCollection, MaintenanceThresholds — out of an
    appsettings.json and writes them to one file. Nothing else (keys, connection strings, licence
    settings) is copied.

    Upload the file in the cloud: Clients, Edit on the client, Tag Settings File. Keep the file in the
    office; it is the client's private PLC configuration.

.EXAMPLE
    .\tools\Export-SiteProfile.ps1 -OutFile "$env:USERPROFILE\Documents\sreesakthi.siteprofile.json"
#>
param(
    # Default: the gateway's own appsettings.json. Worked out below, not here: Windows PowerShell 5.1
    # leaves $PSScriptRoot empty in parameter defaults when started with -File.
    [string]$Source = '',
    [Parameter(Mandatory)][string]$OutFile
)

$ErrorActionPreference = 'Stop'
if (-not $Source) { $Source = Join-Path $PSScriptRoot '..\PLCGateway\appsettings.json' }

# Must match SiteProfile.Sections in the gateway and SECTIONS in the cloud's siteProfile function.
$sections = 'Tags', 'ScanIntervalMs', 'CovRules', 'DataCollection', 'MaintenanceThresholds'

$settings = [IO.File]::ReadAllText((Resolve-Path $Source)) | ConvertFrom-Json
$profile = [ordered]@{}
foreach ($section in $sections) {
    if ($null -ne $settings.$section) { $profile[$section] = $settings.$section }
}
if (-not $profile.Contains('Tags') -or @($profile['Tags']).Count -eq 0) {
    throw "$Source has no Tags, so there is nothing to export."
}

[IO.File]::WriteAllText($OutFile, ($profile | ConvertTo-Json -Depth 20), (New-Object Text.UTF8Encoding($false)))
Write-Host ("Wrote {0} tags and {1} other section(s) to {2}" -f @($profile['Tags']).Count, ($profile.Count - 1), $OutFile)
Write-Host 'Upload it in the cloud: Clients, Edit on the client, Tag Settings File.'
