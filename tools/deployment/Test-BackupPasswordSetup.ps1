$ErrorActionPreference = 'Stop'
$root = Join-Path ([IO.Path]::GetTempPath()) ('scada-password-setup-test-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
$path = Join-Path $root 'appsettings.Site.json'
$setup = Join-Path $PSScriptRoot 'Set-BackupPassword.ps1'
$global:ScadaPasswordSetupTestAnswers = New-Object 'Collections.Generic.Queue[string]'
function Read-Host { param([string]$Prompt, [switch]$AsSecureString) ConvertTo-SecureString $global:ScadaPasswordSetupTestAnswers.Dequeue() -AsPlainText -Force }
function Invoke-SetupExpectingFailure {
    $failed = $false
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { & $setup -SiteSettingsPath $path } catch { $failed = $true }
    if (-not $failed -and -not $?) { $failed = $true }
    $ErrorActionPreference = $previous
    while ($global:ScadaPasswordSetupTestAnswers.Count -gt 0) { $null = $global:ScadaPasswordSetupTestAnswers.Dequeue() }
    return $failed
}
try {
    [IO.File]::WriteAllText($path, '{"Scada":{"SiteName":"test","Nested":{"Keep":true}},"Jwt":{"Key":"not-a-real-key"},"Unrelated":42}')
    $global:ScadaPasswordSetupTestAnswers.Enqueue('fixed-test-passphrase'); $global:ScadaPasswordSetupTestAnswers.Enqueue('fixed-test-passphrase')
    & $setup -SiteSettingsPath $path
    $json = [IO.File]::ReadAllText($path) | ConvertFrom-Json
    if ($json.Scada.BackupPassword -cne 'fixed-test-passphrase' -or $json.Unrelated -ne 42 -or -not $json.Scada.Nested.Keep) { throw 'Setup did not preserve unrelated settings.' }

    $before = [IO.File]::ReadAllText($path)
    $global:ScadaPasswordSetupTestAnswers.Enqueue('wrong-current-password')
    $refused = Invoke-SetupExpectingFailure
    if (-not $refused -or [IO.File]::ReadAllText($path) -cne $before) { throw 'Wrong current password should be refused without changing any file.' }

    $global:ScadaPasswordSetupTestAnswers.Enqueue('fixed-test-passphrase'); $global:ScadaPasswordSetupTestAnswers.Enqueue('new-test-passphrase'); $global:ScadaPasswordSetupTestAnswers.Enqueue('different-test-passphrase')
    $refused = Invoke-SetupExpectingFailure
    if (-not $refused -or [IO.File]::ReadAllText($path) -cne $before) { throw 'Rotation mismatch should not change settings.' }

    $global:ScadaPasswordSetupTestAnswers.Enqueue('fixed-test-passphrase'); $global:ScadaPasswordSetupTestAnswers.Enqueue('rotated-test-pass'); $global:ScadaPasswordSetupTestAnswers.Enqueue('rotated-test-pass')
    & $setup -SiteSettingsPath $path
    $json = [IO.File]::ReadAllText($path) | ConvertFrom-Json
    if ($json.Scada.BackupPassword -cne 'rotated-test-pass') { throw 'Rotation did not write the new password.' }
    if ($json.Scada.BackupPasswordPrevious -cne 'fixed-test-passphrase') { throw 'Rotation did not keep the previous password.' }
    if ($json.Unrelated -ne 42 -or -not $json.Scada.Nested.Keep) { throw 'Rotation did not preserve unrelated settings.' }

    [IO.File]::WriteAllText($path, '{"Scada":{"SiteName":"test"}}')
    $before = [IO.File]::ReadAllText($path)
    $global:ScadaPasswordSetupTestAnswers.Enqueue('fixed-test-passphrase'); $global:ScadaPasswordSetupTestAnswers.Enqueue('different-test-passphrase')
    $refused = Invoke-SetupExpectingFailure
    if (-not $refused -or [IO.File]::ReadAllText($path) -cne $before) { throw 'Mismatch should not change settings.' }
    if ((Get-ChildItem -LiteralPath $root -File).Count -ne 1) { throw 'Temporary files leaked.' }
    Write-Host 'PASS: setup, confirmation, unrelated settings, rotation with previous password, mismatch, temp cleanup (temporary test directory only).'
} finally { Remove-Item -LiteralPath $root -Recurse -Force; Remove-Variable ScadaPasswordSetupTestAnswers -Scope Global -ErrorAction SilentlyContinue }
