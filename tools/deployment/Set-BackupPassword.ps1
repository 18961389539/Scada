# Run locally in a stopped-server maintenance window. Never pass the password as an argument.
[CmdletBinding()]
param([Parameter(Mandatory = $true)][string]$SiteSettingsPath)
$ErrorActionPreference = 'Stop'
$path = (Resolve-Path -LiteralPath $SiteSettingsPath).Path
$temp = "$path.password-setup-$([Guid]::NewGuid().ToString('N')).tmp"
$first = $null; $second = $null; $bstr = [IntPtr]::Zero; $bstr2 = [IntPtr]::Zero
try {
    $original = [IO.File]::ReadAllText($path)
    $settings = $original | ConvertFrom-Json
    if ($null -eq $settings.Scada) { throw 'Missing Scada section. Use the server-generated site settings file.' }
    if (-not [string]::IsNullOrWhiteSpace($settings.Scada.BackupPassword)) {
        throw 'A password is already configured. This first-setup tool does not rotate passwords; retain the existing password for older bundles.'
    }
    $first = Read-Host 'New backup password (at least 12 characters; keep a separate recovery copy)' -AsSecureString
    $second = Read-Host 'Confirm backup password' -AsSecureString
    $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($first)
    $bstr2 = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($second)
    $plain = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr)
    if ($plain.Length -lt 12 -or [string]::IsNullOrWhiteSpace($plain)) { throw 'Use at least 12 non-blank characters.' }
    if ($plain -cne [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr2)) { throw 'Passwords do not match. No settings were changed.' }
    $settings.Scada | Add-Member -NotePropertyName BackupPassword -NotePropertyValue $plain -Force
    # Refuse concurrent edits; do not silently replace another administrator's changes.
    if ([IO.File]::ReadAllText($path) -cne $original) { throw 'Settings changed during setup. Retry after coordinating with the other editor.' }
    [IO.File]::WriteAllText($temp, '')
    Set-Acl -LiteralPath $temp -AclObject (Get-Acl -LiteralPath $path)
    [IO.File]::WriteAllText($temp, ($settings | ConvertTo-Json -Depth 100), (New-Object Text.UTF8Encoding($false)))
    [IO.File]::Replace($temp, $path, [NullString]::Value)
    Write-Host 'Password saved (not displayed). Restart the server, create an encrypted bundle and run a restore drill. Environment/command-line overrides still take precedence. Old plaintext bundles were NOT changed.'
} finally {
    if ($bstr -ne [IntPtr]::Zero) { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
    if ($bstr2 -ne [IntPtr]::Zero) { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr2) }
    if ($first) { $first.Dispose() }; if ($second) { $second.Dispose() }
    $plain = $null; $settings = $null; $original = $null
    if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Force }
}
