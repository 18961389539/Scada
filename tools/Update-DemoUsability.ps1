# Deploy the usability changes without resetting runtime configuration or archived data.
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$runtime = Join-Path $repo '.local/HelloWorld'
$webDir = Join-Path $runtime 'ScadaWeb'
$webExe = [IO.Path]::GetFullPath((Join-Path $webDir 'ScadaWeb.exe'))
if (!(Test-Path -LiteralPath $webExe)) { throw 'Build the demo first with tools/Start-Demo.ps1 -Build.' }

foreach ($plugin in @('PlgMimic', 'PlgMimicEditor', 'PlgMimBasicComp')) {
    & dotnet build "$repo/ScadaWeb/Mimics/$plugin/$plugin.csproj" -c Release --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw "Build failed: $plugin" }
}

$backup = Join-Path $runtime ('backups/usability-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $backup -Force | Out-Null
Copy-Item -LiteralPath "$runtime/Views" -Destination "$backup/Views" -Recurse
foreach ($plugin in @('PlgMimic', 'PlgMimicEditor', 'PlgMimBasicComp')) {
    Copy-Item -LiteralPath "$webDir/wwwroot/plugins/$($plugin.Substring(3))" -Destination "$backup/$plugin-assets" -Recurse
    if (Test-Path -LiteralPath "$webDir/$plugin.dll") { Copy-Item -LiteralPath "$webDir/$plugin.dll" -Destination $backup }
}

# Match the resolved executable path before stopping a process.
$webProcess = Get-Process ScadaWeb -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $webExe }
$webProcess | Stop-Process
foreach ($stopped in $webProcess) {
    if (!$stopped.WaitForExit(5000)) { throw "Webstation process did not exit: $($stopped.Id)" }
}
function Copy-PluginAssembly([string]$source, [string]$destination) {
    for ($retry = 0; $retry -lt 20; $retry++) {
        try { Copy-Item -LiteralPath $source -Destination $destination -Force; return }
        catch {
            if ($retry -eq 19) { throw }
            Start-Sleep -Milliseconds 250
        }
    }
}
try {
    foreach ($plugin in @('PlgMimic', 'PlgMimicEditor', 'PlgMimBasicComp')) {
        Copy-PluginAssembly "$repo/ScadaWeb/Mimics/$plugin/bin/Release/net8.0/$plugin.dll" $webDir
    }
    foreach ($plugin in @('PlgMimic', 'PlgMimicEditor', 'PlgMimBasicComp')) {
        Copy-Item "$repo/ScadaWeb/Mimics/$plugin/wwwroot/*" "$webDir/wwwroot" -Recurse -Force
        Copy-Item "$repo/ScadaWeb/Mimics/$plugin/lang/*" "$webDir/lang" -Force
    }
    foreach ($view in @('Overview', 'Tank', 'HVAC', 'Energy', 'Simulator', 'Components')) {
        Copy-Item -LiteralPath "$repo/Projects/HelloWorld/Views/HelloWorld/$view.mim" -Destination "$runtime/Views/HelloWorld" -Force
    }
} finally {
    $process = Start-Process -FilePath $webExe -WorkingDirectory $webDir -WindowStyle Hidden -PassThru `
        -ArgumentList @('--urls', 'http://127.0.0.1:5000') `
        -RedirectStandardOutput "$webDir/Log/console.log" -RedirectStandardError "$webDir/Log/console-error.log"
    # Rebuild the process record from actual paths to preserve Unicode paths on Windows PowerShell.
    $entries = foreach ($app in @('ScadaServer', 'ScadaComm', 'ScadaWeb')) {
        $name = if ($app -eq 'ScadaWeb') { 'ScadaWeb' } else { "${app}Wkr" }
        Get-Process $name -ErrorAction SilentlyContinue | Where-Object { $_.Path.StartsWith($runtime + '\', [StringComparison]::OrdinalIgnoreCase) } |
            ForEach-Object { [pscustomobject]@{App = $app; Id = $_.Id; Path = $_.Path} }
    }
    $entries | ConvertTo-Json | Set-Content -LiteralPath "$runtime/processes.json" -Encoding UTF8
}
for ($attempt = 0; $attempt -lt 20; $attempt++) {
    try {
        $response = Invoke-WebRequest 'http://127.0.0.1:5000/Login' -UseBasicParsing -TimeoutSec 2
        if ($response.StatusCode -eq 200) { Write-Host "Demo updated. Backup: $backup"; return }
    } catch { Start-Sleep -Milliseconds 500 }
}
throw "Webstation did not become ready. See $webDir/Log/console-error.log. Backup: $backup"
