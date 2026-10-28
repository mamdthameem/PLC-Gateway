# Stops the ShotSense services before an upgrade replaces their program files (a running exe is
# locked). Run by the wizard, from its own temporary copy of the NEW version of this script, before
# any file is copied. Stops the gateway first so nothing is writing when the database stops.
# Exit 0 when everything is stopped, 1 otherwise.
param([Parameter(Mandatory)][string]$AppDir)

$ErrorActionPreference = 'Stop'

function Stop-One([string]$Name) {
    $service = Get-Service $Name -ErrorAction SilentlyContinue
    if (-not $service -or $service.Status -eq 'Stopped') { return $true }
    Stop-Service $Name -Force -ErrorAction SilentlyContinue
    try {
        $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(90))
        return $true
    } catch {
        Write-Host "$Name did not stop within 90 seconds."
        return $false
    }
}

$ok = Stop-One 'ShotSenseGateway'

# The tunnel only when it is ShotSense's own; another program's tunnel is not ours to stop.
$tunnel = Get-CimInstance Win32_Service -Filter "Name='Cloudflared'" -ErrorAction SilentlyContinue
if ($tunnel -and $tunnel.PathName -like "*$AppDir*") { $ok = (Stop-One 'Cloudflared') -and $ok }

$ok = (Stop-One 'ShotSensePostgres') -and $ok

if ($ok) { exit 0 } else { exit 1 }
