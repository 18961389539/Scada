$ErrorActionPreference = 'Stop'
$root = Join-Path ([IO.Path]::GetTempPath()) ('scada-password-setup-test-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
$path = Join-Path $root 'appsettings.Site.json'
$setup = Join-Path $PSScriptRoot 'Set-BackupPassword.ps1'
$global:ScadaPasswordSetupTestAnswers = New-Object 'Collections.Generic.Queue[string]'
function Read-Host { param([string]$Prompt, [switch]$AsSecureString) ConvertTo-SecureString $global:ScadaPasswordSetupTestAnswers.Dequeue() -AsPlainText -Force }
try {
    [IO.File]::WriteAllText($path, '{"Scada":{"SiteName":"test","Nested":{"Keep":true}},"Jwt":{"Key":"not-a-real-key"},"Unrelated":42}')
    $global:ScadaPasswordSetupTestAnswers.Enqueue('fixed-test-passphrase'); $global:ScadaPasswordSetupTestAnswers.Enqueue('fixed-test-passphrase')
    & $setup -SiteSettingsPath $path
    $json = [IO.File]::ReadAllText($path) | ConvertFrom-Json
    if ($json.Scada.BackupPassword -cne 'fixed-test-passphrase' -or $json.Unrelated -ne 42 -or -not $json.Scada.Nested.Keep) { throw 'Setup did not preserve unrelated settings.' }
    $before = [IO.File]::ReadAllText($path)
    $refused = $false
    try { & $setup -SiteSettingsPath $path } catch { $refused = $true }
    if (-not $refused -or [IO.File]::ReadAllText($path) -cne $before) { throw 'Rotation should be refused without changing any file.' }
    [IO.File]::WriteAllText($path, '{"Scada":{"SiteName":"test"}}')
    $before = [IO.File]::ReadAllText($path)
    $global:ScadaPasswordSetupTestAnswers.Enqueue('fixed-test-passphrase'); $global:ScadaPasswordSetupTestAnswers.Enqueue('different-test-passphrase')
    $refused = $false
    try { & $setup -SiteSettingsPath $path } catch { $refused = $true }
    if (-not $refused -or [IO.File]::ReadAllText($path) -cne $before) { throw 'Mismatch should not change settings.' }
    if ((Get-ChildItem -LiteralPath $root -File).Count -ne 1) { throw 'Temporary files leaked.' }
    Write-Host 'PASS: setup, confirmation, unrelated settings, no rotation, mismatch, temp cleanup (temporary test directory only).'
} finally { Remove-Item -LiteralPath $root -Recurse -Force; Remove-Variable ScadaPasswordSetupTestAnswers -Scope Global -ErrorAction SilentlyContinue }
