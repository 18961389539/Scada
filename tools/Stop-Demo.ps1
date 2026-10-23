$ErrorActionPreference = 'Stop'
$runtime = Join-Path (Split-Path $PSScriptRoot -Parent) '.local/HelloWorld'
$stateFile = Join-Path $runtime 'processes.json'
if (!(Test-Path $stateFile)) { Write-Host 'No demo processes recorded.'; return }
$entries = @(Get-Content $stateFile -Raw | ConvertFrom-Json)
[array]::Reverse($entries)
foreach ($entry in $entries) {
    $process = Get-Process -Id $entry.Id -ErrorAction SilentlyContinue
    if ($process -and $process.Path -eq $entry.Path -and $process.Path.StartsWith($runtime, [StringComparison]::OrdinalIgnoreCase)) {
        Stop-Process -Id $entry.Id
        Write-Host "Stopped $($entry.App)"
    }
}
Remove-Item -LiteralPath $stateFile
