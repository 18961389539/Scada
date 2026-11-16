#requires -Version 7.2
<#
.SYNOPSIS 在临时目录验证安装、升级、回滚、卸载；不创建计划任务、不启动 UI。
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory)][string]$PackagePath,
  [string]$Version = '1.1.0'
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'DesktopClient.Common.ps1')
$root = Join-Path $env:TEMP ('scada-desktop-deploy-' + [Guid]::NewGuid().ToString('N'))
$manifest = Join-Path $root 'manifest'
try {
  & (Join-Path $PSScriptRoot 'Install-DesktopClient.ps1') -PackagePath $PackagePath -InstallRoot $root `
    -Scope CurrentUser -Version $Version -NoStart -SkipTask -NoShortcut -ManifestDirectory $manifest
  if (-not (Test-Path (Join-Path $root 'current\Scada.Desktop.exe'))) { throw '安装后 current 缺少 Scada.Desktop.exe' }
  if (-not (Test-Path (Join-Path $manifest 'install.json'))) { throw '安装清单缺失' }
  if (-not (Test-Path (Join-Path $PackagePath 'release-manifest.json'))) { throw '发布包缺少 release-manifest.json' }

  $second = "$Version-2"
  & (Join-Path $PSScriptRoot 'Install-DesktopClient.ps1') -PackagePath $PackagePath -InstallRoot $root `
    -Scope CurrentUser -Version $second -NoStart -SkipTask -NoShortcut -ManifestDirectory $manifest
  $current = Get-CurrentRelease -InstallRoot $root
  if (-not $current.EndsWith($second, [StringComparison]::OrdinalIgnoreCase)) { throw "升级后 current 指向 $current" }

  & (Join-Path $PSScriptRoot 'Rollback-DesktopClient.ps1') -InstallRoot $root -Scope CurrentUser -Version $Version -NoStart
  $current = Get-CurrentRelease -InstallRoot $root
  if (-not $current.EndsWith($Version, [StringComparison]::OrdinalIgnoreCase)) { throw "回滚后 current 指向 $current" }

  & (Join-Path $PSScriptRoot 'Uninstall-DesktopClient.ps1') -InstallRoot $root -Scope CurrentUser
  if (Test-Path $root) { throw '卸载后程序目录仍存在' }
  Write-Host '桌面客户端部署验证通过'
} finally {
  Remove-Item $root -Recurse -Force -ErrorAction SilentlyContinue
}
