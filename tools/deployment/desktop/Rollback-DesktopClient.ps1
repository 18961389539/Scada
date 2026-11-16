#requires -Version 7.2
<#
.SYNOPSIS 回滚 SCADA 桌面客户端到已安装的指定版本。
#>
[CmdletBinding()]
param(
  [string]$InstallRoot,
  [ValidateSet('CurrentUser', 'AllUsers')][string]$Scope = 'CurrentUser',
  [Parameter(Mandatory)][string]$Version,
  [string]$TaskName = 'ScadaDesktop',
  [switch]$NoStart
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'DesktopClient.Common.ps1')
if ($Scope -eq 'AllUsers') { Assert-Admin }
$InstallRoot = Resolve-InstallRoot -InstallRoot $InstallRoot -Scope $Scope
$release = Join-Path $InstallRoot "releases\$Version"
if (-not (Test-Path (Join-Path $release 'Scada.Desktop.exe'))) { throw "版本 $Version 不存在：$release" }

Stop-DesktopClient -TaskName $TaskName
Set-CurrentRelease -InstallRoot $InstallRoot -Release $release
$manifestDirectory = if ($Scope -eq 'AllUsers') { Join-Path $env:ProgramData 'Scada\Desktop' } else { Join-Path $env:LOCALAPPDATA 'Scada\Desktop' }
$managed = $null -ne (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue)
Write-InstallManifest -Directory $manifestDirectory -Version $Version -Scope $Scope -InstallRoot $InstallRoot -ManagedAutostart $managed

if (-not $NoStart) {
  $task = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
  if ($task) { Start-ScheduledTask -TaskName $TaskName }
  else { Start-Process -FilePath (Join-Path $InstallRoot 'current\Scada.Desktop.exe') }
}
Write-Host "已回滚到 $Version"
