# ShotSense Gateway - removes the services before Windows deletes the program files.
#
# Run by the uninstaller (elevated). Removes: the gateway service, the tunnel service (only if it is
# ShotSense's own), the database service registration and the firewall rule.
#
# KEEPS C:\ProgramData\ShotSense - settings, logs and the database with all recorded history.
# Recorded data is never deleted by software; installing again picks it all up ("Restore").
param([Parameter(Mandatory)][string]$AppDir)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Common.ps1')

$DataDir = Join-Path $env:ProgramData 'ShotSense'
Start-InstallLog (Join-Path $DataDir 'logs') 'uninstall'

function Remove-ServiceSafely([string]$Name) {
    $service = Get-Service $Name -ErrorAction SilentlyContinue
    if (-not $service) { return }
    if ($service.Status -ne 'Stopped') {
        Write-Log "Stopping $Name."
        Stop-Service $Name -Force -ErrorAction SilentlyContinue
        try { $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(60)) } catch { Write-Log "$Name did not stop within 60 s." }
    }
}

try {
    Write-Log "Uninstalling ShotSense Gateway from $AppDir"

    # 1. Gateway first, so nothing writes to the database while it is being stopped.
    Remove-ServiceSafely 'ShotSenseGateway'
    if (Get-Service 'ShotSenseGateway' -ErrorAction SilentlyContinue) {
        Invoke-Native -FilePath "$env:WINDIR\System32\sc.exe" -Arguments @('delete', 'ShotSenseGateway') | Out-Null
    }

    # 2. Tunnel - only when it is the one this product installed.
    $tunnel = Get-CimInstance Win32_Service -Filter "Name='Cloudflared'" -ErrorAction SilentlyContinue
    if ($tunnel -and $tunnel.PathName -like "*$AppDir*") {
        Remove-ServiceSafely 'Cloudflared'
        Invoke-Native -FilePath (Join-Path $AppDir 'cloudflared\cloudflared.exe') -Arguments @('service', 'uninstall') -SuccessCodes @(0, 1) | Out-Null
    } elseif ($tunnel) {
        Write-Log 'Leaving the Cloudflared service alone: it belongs to another program.'
    }

    # 3. Database service. The data folder stays exactly as it is.
    Remove-ServiceSafely 'ShotSensePostgres'
    if (Get-Service 'ShotSensePostgres' -ErrorAction SilentlyContinue) {
        Invoke-Native -FilePath (Join-Path $AppDir 'pgsql\bin\pg_ctl.exe') -Arguments @('unregister', '-N', 'ShotSensePostgres') | Out-Null
    }

    # 4. Firewall rule.
    Remove-NetFirewallRule -Name 'ShotSenseGateway-Dashboard' -ErrorAction SilentlyContinue

    Write-Log "Done. Settings, logs and the database were kept in $DataDir."
    exit 0
} catch {
    Write-Log "UNINSTALL STEP FAILED: $($_.Exception.Message)"
    exit 1
}
