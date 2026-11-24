# Run locally in a stopped-server maintenance window. Never pass the password as an argument.
# First setup writes Scada:BackupPassword. When a password already exists, the same
# script rotates it: verifies the current password, reencrypts recent full_*.zip.enc
# bundles when a backup directory and BackupTool are available, then writes the new
# password and keeps the previous one in Scada:BackupPasswordPrevious for restore only.
# There is no HTTP password-change endpoint.
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$SiteSettingsPath,
    [string]$BackupDirectory,
    [string]$BackupToolPath,
    [int]$RecentDays = 30
)
$ErrorActionPreference = 'Stop'
$path = (Resolve-Path -LiteralPath $SiteSettingsPath).Path
$temp = "$path.password-setup-$([Guid]::NewGuid().ToString('N')).tmp"
$currentSecure = $null; $first = $null; $second = $null
$bstrCurrent = [IntPtr]::Zero; $bstr = [IntPtr]::Zero; $bstr2 = [IntPtr]::Zero
function Read-Secure([string]$Prompt) {
    $secure = Read-Host $Prompt -AsSecureString
    $ptr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    try { return @{ Secure = $secure; Ptr = $ptr; Plain = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($ptr) } }
    catch { if ($ptr -ne [IntPtr]::Zero) { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($ptr) }; if ($secure) { $secure.Dispose() }; throw }
}
function Find-BackupTool([string]$hint) {
    if ($hint -and (Test-Path -LiteralPath $hint)) { return (Resolve-Path -LiteralPath $hint).Path }
    $candidates = @(
        (Join-Path $PSScriptRoot 'tools\Scada.BackupTool.exe'),
        (Join-Path $PSScriptRoot 'Scada.BackupTool.exe'),
        (Join-Path (Split-Path $PSScriptRoot -Parent) 'tools\Scada.BackupTool.exe')
    )
    foreach ($item in $candidates) { if (Test-Path -LiteralPath $item) { return (Resolve-Path -LiteralPath $item).Path } }
    return $null
}
try {
    $original = [IO.File]::ReadAllText($path)
    $settings = $original | ConvertFrom-Json
    if ($null -eq $settings.Scada) { throw 'Missing Scada section. Use the server-generated site settings file.' }
    $existing = [string]$settings.Scada.BackupPassword
    $rotating = -not [string]::IsNullOrWhiteSpace($existing)
    if ($rotating) {
        $current = Read-Secure 'Current backup password'
        $currentSecure = $current.Secure; $bstrCurrent = $current.Ptr
        if ($current.Plain -cne $existing) { throw 'Current password does not match Scada:BackupPassword. No settings were changed.' }
    }
    $firstInfo = Read-Secure $(if ($rotating) { 'New backup password (at least 12 characters; keep a separate recovery copy)' } else { 'New backup password (at least 12 characters; keep a separate recovery copy)' })
    $first = $firstInfo.Secure; $bstr = $firstInfo.Ptr; $plain = $firstInfo.Plain
    $secondInfo = Read-Secure 'Confirm backup password'
    $second = $secondInfo.Secure; $bstr2 = $secondInfo.Ptr
    if ($plain.Length -lt 12 -or [string]::IsNullOrWhiteSpace($plain)) { throw 'Use at least 12 non-blank characters.' }
    if ($plain -cne $secondInfo.Plain) { throw 'Passwords do not match. No settings were changed.' }
    if ($rotating -and $plain -ceq $existing) { throw 'New password is identical to the current password. No settings were changed.' }

    $pendingEnc = @()
    $skippedPlain = @()
    $tool = $null
    if ($rotating) {
        $backupRoot = $BackupDirectory
        if ([string]::IsNullOrWhiteSpace($backupRoot) -and $settings.Scada.BackupDirectory) { $backupRoot = [string]$settings.Scada.BackupDirectory }
        if ([string]::IsNullOrWhiteSpace($backupRoot) -and $settings.Scada.BackupRoot) { $backupRoot = [string]$settings.Scada.BackupRoot }
        if (-not [string]::IsNullOrWhiteSpace($backupRoot) -and (Test-Path -LiteralPath $backupRoot)) {
            $cutoff = (Get-Date).ToUniversalTime().AddDays(-[Math]::Max(1, $RecentDays))
            $pendingEnc = @(Get-ChildItem -LiteralPath $backupRoot -File -ErrorAction SilentlyContinue | Where-Object {
                $_.Name -like 'full_*.zip.enc' -and $_.Name -notlike '*.reenc.zip.enc' -and $_.LastWriteTimeUtc -ge $cutoff
            } | ForEach-Object { $_.FullName })
            $skippedPlain = @(Get-ChildItem -LiteralPath $backupRoot -File -ErrorAction SilentlyContinue | Where-Object {
                $_.Name -like 'full_*.zip' -and $_.Name -notlike '*.zip.enc' -and $_.LastWriteTimeUtc -ge $cutoff
            } | ForEach-Object { $_.Name })
            $tool = Find-BackupTool $BackupToolPath
        }
    }

    if ($rotating) {
        $settings.Scada | Add-Member -NotePropertyName BackupPasswordPrevious -NotePropertyValue $existing -Force
    }
    $settings.Scada | Add-Member -NotePropertyName BackupPassword -NotePropertyValue $plain -Force
    if ([IO.File]::ReadAllText($path) -cne $original) { throw 'Settings changed during setup. Retry after coordinating with the other editor.' }
    [IO.File]::WriteAllText($temp, '')
    Set-Acl -LiteralPath $temp -AclObject (Get-Acl -LiteralPath $path)
    [IO.File]::WriteAllText($temp, ($settings | ConvertTo-Json -Depth 100), (New-Object Text.UTF8Encoding($false)))
    [IO.File]::Replace($temp, $path, [NullString]::Value)
    if ($rotating) {
        Write-Host 'Password rotated (not displayed). Previous password kept as Scada:BackupPasswordPrevious for restore only.'
        if ($pendingEnc.Count -gt 0) {
            Write-Host 'Reencrypt recent encrypted bundles in this maintenance window (the tool prompts for the new password; do not pass it as an argument):'
            $exe = $(if ($tool) { $tool } else { 'Scada.BackupTool.exe' })
            foreach ($bundle in $pendingEnc) {
                Write-Host ("  {0} reencrypt --backup '{1}' --prompt-password --prompt-new-password" -f $exe, $bundle)
            }
        } else {
            Write-Host 'No recent full_*.zip.enc bundles were listed. Use Scada.BackupTool reencrypt for older encrypted packs if you still need them, then verify.'
        }
        if ($skippedPlain.Count -gt 0) { Write-Host ("Plaintext historical bundles were NOT rewritten and still need a new backup after restart: " + ($skippedPlain -join ', ')) }
        else { Write-Host 'Plaintext historical bundles (if any) were not rewritten; create a new encrypted bundle after restart.' }
        Write-Host 'Restart the server, then run a restore drill with a reencrypted or newly created bundle. There is no HTTP password-change API. Environment/command-line overrides still take precedence.'
    } else {
        Write-Host 'Password saved (not displayed). Restart the server, create an encrypted bundle and run a restore drill. Environment/command-line overrides still take precedence. Old plaintext bundles were NOT changed.'
    }
} finally {
    if ($bstrCurrent -ne [IntPtr]::Zero) { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstrCurrent) }
    if ($bstr -ne [IntPtr]::Zero) { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
    if ($bstr2 -ne [IntPtr]::Zero) { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr2) }
    if ($currentSecure) { $currentSecure.Dispose() }
    if ($first) { $first.Dispose() }; if ($second) { $second.Dispose() }
    $plain = $null; $settings = $null; $original = $null; $existing = $null
    Remove-Item Env:SCADA_BACKUP_PASSWORD -ErrorAction SilentlyContinue
    if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Force }
}
