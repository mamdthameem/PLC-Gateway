# Checks an installation code for the setup wizard, BEFORE anything is installed, so a mistyped or
# truncated code is caught on the page where it was pasted rather than halfway through installing.
#
# Reads the code from the SHOTSENSE_INSTALL_CODE environment variable (never the command line, which
# other programs can see) and writes plain key=value lines to -OutFile for the wizard to read:
#   ok=1, clientName=..., publicHostname=..., hasTunnel=1|0     or     ok=0, error=...
# Prints nothing secret and writes no keys to the file.
param([Parameter(Mandatory)][string]$OutFile)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Common.ps1')

$lines = @()
try {
    $c = ConvertFrom-InstallCode $env:SHOTSENSE_INSTALL_CODE
    $hasTunnel = -not [string]::IsNullOrWhiteSpace([string]$c.tunnelToken)
    $lines += 'ok=1'
    $lines += "clientName=$(([string]$c.clientName) -replace '[\r\n]', ' ')"
    $lines += "publicHostname=$(([string]$c.publicHostname) -replace '[\r\n]', ' ')"
    $lines += "hasTunnel=$(if ($hasTunnel) { '1' } else { '0' })"
    $code = 0
} catch {
    $lines += 'ok=0'
    $lines += "error=$($_.Exception.Message -replace '[\r\n]', ' ')"
    $code = 1
}

# With a BOM: the wizard's file reader takes a BOM-less file as the ANSI code page, which would
# garble a client name written in, say, Tamil.
[IO.File]::WriteAllLines($OutFile, $lines, (New-Object Text.UTF8Encoding($true)))
exit $code
