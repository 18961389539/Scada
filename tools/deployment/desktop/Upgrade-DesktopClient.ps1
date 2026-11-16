#requires -Version 7.2
<#
.SYNOPSIS 升级 SCADA 桌面客户端：停任务与进程、安装新版本、切换 current、启动；失败自动回滚。
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory)][string]$PackagePath,
  [string]$InstallRoot,
  [ValidateSet('CurrentUser', 'AllUsers')][string]$Scope = 'CurrentUser',
  [Parameter(Mandatory)][string]$Version,
  [string]$TaskName = 'ScadaDesktop',
  [switch]$NoStart,
  [switch]$SkipTask,
  [switch]$NoShortcut
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'DesktopClient.Common.ps1')
if ($Scope -eq 'AllUsers') { Assert-Admin }
$InstallRoot = Resolve-InstallRoot -InstallRoot $InstallRoot -Scope $Scope
$previous = Get-CurrentRelease -InstallRoot $InstallRoot

Stop-DesktopClient -TaskName $TaskName
try {
  & (Join-Path $PSScriptRoot 'Install-DesktopClient.ps1') -PackagePath $PackagePath -InstallRoot $InstallRoot `
    -Scope $Scope -Version $Version -TaskName $TaskName -NoStart -NoShortcut:$NoShortcut -SkipTask:$SkipTask
  if ($LASTEXITCODE -ne 0) { throw "安装脚本返回 $LASTEXITCODE" }
} catch {
  if ($previous) { Set-CurrentRelease -InstallRoot $InstallRoot -Release $previous }
  throw
}

if (-not $NoStart -and -not $SkipTask -and $Scope -eq 'CurrentUser') {
  Start-ScheduledTask -TaskName $TaskName
  Start-Sleep -Seconds 8
  if (-not (Get-Process -Name 'Scada.Desktop' -ErrorAction SilentlyContinue)) {
    Stop-DesktopClient -TaskName $TaskName
    if ($previous) {
      Set-CurrentRelease -InstallRoot $InstallRoot -Release $previous
      Start-ScheduledTask -TaskName $TaskName
    }
    throw '新版本未启动，已切回上一版本'
  }
}
Write-Host "已升级到 $Version"
