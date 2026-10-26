[CmdletBinding()]
param(
    [ValidateRange(1024, 65535)]
    [int]$Port = 18927,
    [string]$DataPath = '.local/customer-demo'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$data = if ([System.IO.Path]::IsPathRooted($DataPath)) {
    [System.IO.Path]::GetFullPath($DataPath)
} else {
    [System.IO.Path]::GetFullPath((Join-Path $repo $DataPath))
}
$listeners = [System.Net.NetworkInformation.IPGlobalProperties]::GetIPGlobalProperties().GetActiveTcpListeners()
foreach ($required in @(1502, $Port)) {
    if ($listeners | Where-Object { $_.Port -eq $required }) {
        throw "Port $required is already in use. Stop the conflicting process before starting this demo."
    }
}
if ($Port -eq 1502) { throw 'The web port must differ from the Modbus simulator port 1502.' }

Push-Location $repo
$simulator = $null
try {
    foreach ($project in @('src/Scada.Server/Scada.Server.csproj', 'tools/Scada.ModbusSimulator/Scada.ModbusSimulator.csproj')) {
        & dotnet build $project --nologo --verbosity minimal
        if ($LASTEXITCODE -ne 0) { throw "Build failed: $project" }
    }
    $webroot = Join-Path $repo 'web/dist'
    if (-not (Test-Path (Join-Path $webroot 'index.html'))) {
        & npm --prefix web ci --no-audit --no-fund
        if ($LASTEXITCODE -ne 0) { throw 'Frontend dependency installation failed.' }
        & npm --prefix web run build
        if ($LASTEXITCODE -ne 0) { throw 'Frontend build failed.' }
    }
    [System.IO.Directory]::CreateDirectory($data) | Out-Null
    $simulatorDll = Join-Path $repo 'tools/Scada.ModbusSimulator/bin/Debug/net10.0/Scada.ModbusSimulator.dll'
    $simulator = Start-Process -FilePath (Get-Command dotnet -CommandType Application).Source `
        -ArgumentList @('"' + $simulatorDll + '"', '1502', '1') -PassThru -NoNewWindow `
        -RedirectStandardOutput (Join-Path $data 'modbus.stdout.log') `
        -RedirectStandardError (Join-Path $data 'modbus.stderr.log')
    $server = Join-Path $repo 'src/Scada.Server/bin/Debug/net10.0/Scada.Server.dll'
    $serverArgs = @(
        '--environment=Development',
        "--contentRoot=$(Join-Path $repo 'src/Scada.Server')",
        "--webroot=$webroot",
        "--Kestrel:Endpoints:Http:Url=http://127.0.0.1:$Port",
        '--Scada:SeedDemoData=true',
        "--Scada:ConfigDbPath=$(Join-Path $data 'config.db')",
        "--Scada:HistoryRoot=$(Join-Path $data 'history')",
        "--Scada:BackupRoot=$(Join-Path $data 'backups')",
        "--Scada:ReportsRoot=$(Join-Path $data 'reports')",
        "--Scada:DiagnosticsRoot=$(Join-Path $data 'diagnostics')",
        "--Scada:LogRoot=$(Join-Path $data 'logs')",
        "--Scada:OpcPkiRoot=$(Join-Path $data 'opc-pki')"
    )
    Write-Host "Customer demo: http://127.0.0.1:$Port"
    Write-Host "Isolated data: $data"
    Write-Host 'Press Ctrl+C to stop the demo and its simulator.'
    & dotnet $server @serverArgs
    if ($LASTEXITCODE -ne 0) { throw "Demo server exited with code $LASTEXITCODE." }
} finally {
    if ($null -ne $simulator -and -not $simulator.HasExited) {
        Stop-Process -Id $simulator.Id -ErrorAction SilentlyContinue
    }
    Pop-Location
}
