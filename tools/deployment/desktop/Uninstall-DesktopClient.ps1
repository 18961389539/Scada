#requires -Version 7.2
<#
.SYNOPSIS 卸载 SCADA 桌面客户端：任务、快捷方式、程序目录；默认保留用户配置。
#>
[CmdletBinding(SupportsShouldProcess)]
param(
  [string]$InstallRoot,
  [ValidateSet('CurrentUser', 'AllUsers')][string]$Scope = 'CurrentUser',
  [string]$TaskName = 'ScadaDesktop',
  [switch]$PurgeUserData
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'DesktopClient.Common.ps1')
if ($Scope -eq 'AllUsers') { Assert-Admin }
$InstallRoot = Resolve-InstallRoot -InstallRoot $InstallRoot -Scope $Scope

Stop-DesktopClient -TaskName $TaskName
if ($PSCmdlet.ShouldProcess($TaskName, '删除计划任务')) { Remove-DesktopTask -TaskName $TaskName }

$startMenu = if ($Scope -eq 'AllUsers') {
  Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\SCADA 桌面客户端.lnk'
} else {
  Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\SCADA 桌面客户端.lnk'
}
if (Test-Path $startMenu) { Remove-Item $startMenu -Force }

$manifestDirectory = if ($Scope -eq 'AllUsers') { Join-Path $env:ProgramData 'Scada\Desktop' } else { Join-Path $env:LOCALAPPDATA 'Scada\Desktop' }
Remove-Item (Join-Path $manifestDirectory 'install.json') -Force -ErrorAction SilentlyContinue

if ($PSCmdlet.ShouldProcess($InstallRoot, '删除程序目录')) {
  Remove-Item $InstallRoot -Recurse -Force -ErrorAction SilentlyContinue
}
if ($PurgeUserData) {
  Remove-Item (Join-Path $env:LOCALAPPDATA 'Scada\Desktop') -Recurse -Force -ErrorAction SilentlyContinue
  Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'ScadaDesktop' -ErrorAction SilentlyContinue
}
Write-Host '桌面客户端已卸载'
