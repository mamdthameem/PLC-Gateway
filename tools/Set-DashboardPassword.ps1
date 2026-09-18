<#
.SYNOPSIS
    Sets a new password for one dashboard login.

.DESCRIPTION
    Asks for the new password twice without showing it on screen, then saves its hash in the
    gateway's users table. The password itself is never saved, printed or kept in your command
    history. Run it on the computer that has the gateway's PostgreSQL database.

    psql may first ask for the PostgreSQL password of the 'postgres' user.

.EXAMPLE
    .\tools\Set-DashboardPassword.ps1 -Username sreesakthi
#>
param(
    [Parameter(Mandatory = $true)]
    [string] $Username,

    [string] $Database = 'sreesakthi_gateway',
    [string] $DbUser   = 'postgres',
    [string] $Psql     = 'C:\Program Files\PostgreSQL\18\bin\psql.exe',

    # For scripted use only. Normally leave it out and the script asks for the password twice.
    [securestring] $NewPassword
)

$ErrorActionPreference = 'Stop'
$MinLength = 12

function ConvertTo-PlainText([securestring] $secure) {
    $ptr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    try     { [Runtime.InteropServices.Marshal]::PtrToStringBSTR($ptr) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($ptr) }
}

if ($Username -notmatch '^[A-Za-z0-9._@-]+$') { throw "'$Username' is not a valid login name. Nothing was changed." }
if (-not (Test-Path $Psql)) { throw "psql was not found at '$Psql'. Give its location with -Psql. Nothing was changed." }

if ($NewPassword) {
    $password = ConvertTo-PlainText $NewPassword
} else {
    $password = ConvertTo-PlainText (Read-Host -Prompt "New password for '$Username' (at least $MinLength characters)" -AsSecureString)
    $again    = ConvertTo-PlainText (Read-Host -Prompt 'Type it again' -AsSecureString)
    if ($password -cne $again) { throw 'The two passwords are different. Nothing was changed.' }
}
if ($password.Length -lt $MinLength) { throw "Too short: use at least $MinLength characters. Nothing was changed." }

# The same hash the gateway checks at login (PasswordHasher.Hash): SHA-256 of the UTF-8 bytes,
# written as lowercase hex. Only hex digits, so it is safe to put straight into the SQL below.
$sha  = [Security.Cryptography.SHA256]::Create()
$hash = -join ($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($password)) | ForEach-Object { $_.ToString('x2') })
$password = $null; $again = $null

$updated = & $Psql -U $DbUser -h localhost -d $Database -q -At -c "UPDATE users SET password_hash = '$hash' WHERE username = '$Username' RETURNING id;"
if ($LASTEXITCODE -ne 0) { throw "psql failed (exit code $LASTEXITCODE). Nothing was changed." }
if ("$updated".Trim()) {
    Write-Host "Password changed for '$Username'. Use the new password at the next sign-in." -ForegroundColor Green
} else {
    throw "No login called '$Username' was found. Nothing was changed."
}
