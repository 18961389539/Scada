#requires -Version 7.2
<# .SYNOPSIS 切回上一版本；新版本已迁移配置库时加 -RestoreDatabase 用升级前快照覆盖（快照之后的配置修改会丢失）。 #>
[CmdletBinding()]
param([Parameter(Mandatory)][string]$InstallRoot, [switch]$RestoreDatabase)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ScadaService.Common.ps1')
Assert-Admin
$meta = Get-Content (Join-Path $InstallRoot 'service.json') | ConvertFrom-Json
$previous = Get-Content (Join-Path $InstallRoot 'previous.json') | ConvertFrom-Json
if (-not $previous.Release -or -not (Test-Path $previous.Release)) { throw '没有可回滚的上一版本' }
Stop-Service $meta.ServiceName
if ($RestoreDatabase) {
  if (-not $previous.Snapshot -or -not (Test-Path $previous.Snapshot)) { throw '没有升级前快照' }
  $db = Join-Path $meta.DataPath 'scada.db'
  Copy-Item $db (Join-Path $meta.DataPath "upgrade-snapshots\failed-$(Get-Date -Format 'yyyyMMdd-HHmmss').db")
  Remove-Item "$db-wal", "$db-shm" -ErrorAction SilentlyContinue
  Copy-Item $previous.Snapshot $db -Force
}
Set-CurrentRelease -InstallRoot $InstallRoot -Release $previous.Release
Start-Service $meta.ServiceName
Wait-Healthy -Port $meta.Port
Write-Host "已回滚到 $($previous.Release)"
