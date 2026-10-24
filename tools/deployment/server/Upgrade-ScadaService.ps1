#requires -Version 7.2
<# .SYNOPSIS 升级：复制新版本 → 停服务 → 配置库快照 → 切换 current → 启动 → 健康检查；失败自动回滚并恢复快照。
主备部署先升级备机，确认复制正常后在“运维与高可用”页面切换，再升级原主机。 #>
[CmdletBinding()]
param([Parameter(Mandatory)][string]$PublishPath, [Parameter(Mandatory)][string]$InstallRoot, [string]$Version = (Get-Date -Format 'yyyyMMddHHmmss'))
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ScadaService.Common.ps1')
Assert-Admin
$meta = Get-Content (Join-Path $InstallRoot 'service.json') | ConvertFrom-Json
$release = Join-Path $InstallRoot "releases\$Version"
if (Test-Path $release) { throw "$release 已存在" }
$previous = Get-CurrentRelease $InstallRoot
New-Item -ItemType Directory -Path $release | Out-Null
Copy-Item -Path (Join-Path $PublishPath '*') -Destination $release -Recurse
Stop-Service $meta.ServiceName
$snapshot = Save-DbSnapshot $meta.DataPath
@{ Release = $previous; Snapshot = $snapshot } | ConvertTo-Json | Set-Content (Join-Path $InstallRoot 'previous.json')
Set-CurrentRelease -InstallRoot $InstallRoot -Release $release
Start-Service $meta.ServiceName
try { Wait-Healthy -Port $meta.Port; Write-Host "已升级到 $Version（上一版本 $previous）" }
catch { Write-Warning "新版本未就绪，自动回滚：$_"; & (Join-Path $PSScriptRoot 'Rollback-ScadaService.ps1') -InstallRoot $InstallRoot -RestoreDatabase; exit 1 }
