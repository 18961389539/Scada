#requires -Version 7.2
<#
.SYNOPSIS 安装 SCADA 桌面客户端：版本目录 + current 联接 + 开始菜单 + 登录计划任务。
.EXAMPLE pwsh -File Install-DesktopClient.ps1 -PackagePath D:\pkg\desktop -Version 1.1.0
.EXAMPLE pwsh -File Install-DesktopClient.ps1 -PackagePath D:\pkg\desktop -Scope AllUsers -ConfigPath D:\pkg\client.json
#>
[CmdletBinding(SupportsShouldProcess)]
param(
  [Parameter(Mandatory)][string]$PackagePath,
  [string]$InstallRoot,
  [ValidateSet('CurrentUser', 'AllUsers')][string]$Scope = 'CurrentUser',
  [string]$Version = '1.1.0',
  [string]$TaskName = 'ScadaDesktop',
  [string]$ConfigPath,
  [string]$ProvisionConfig,
  [string]$ManifestDirectory,
  [switch]$NoStart,
  [switch]$SkipTask,
  [switch]$NoShortcut
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'DesktopClient.Common.ps1')
if ($Scope -eq 'AllUsers') { Assert-Admin }
if (-not (Test-Path (Join-Path $PackagePath 'Scada.Desktop.exe'))) { throw "$PackagePath 不是桌面客户端发布目录" }

$InstallRoot = Resolve-InstallRoot -InstallRoot $InstallRoot -Scope $Scope
$release = Join-Path $InstallRoot "releases\$Version"
if ($PSCmdlet.ShouldProcess($release, '复制客户端')) {
  New-Item -ItemType Directory -Force -Path $release | Out-Null
  Copy-Item -Path (Join-Path $PackagePath '*') -Destination $release -Recurse -Force
  Set-CurrentRelease -InstallRoot $InstallRoot -Release $release
}

$exe = Join-Path $InstallRoot 'current\Scada.Desktop.exe'
if (-not $NoShortcut) {
  $startMenu = if ($Scope -eq 'AllUsers') {
    Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\SCADA 桌面客户端.lnk'
  } else {
    Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\SCADA 桌面客户端.lnk'
  }
  if ($PSCmdlet.ShouldProcess($startMenu, '创建快捷方式')) {
    New-DesktopShortcut -ShortcutPath $startMenu -ExePath $exe -WorkingDirectory (Join-Path $InstallRoot 'current')
  }
}

if (-not $SkipTask) {
  if ($PSCmdlet.ShouldProcess($TaskName, '注册登录计划任务')) {
    Register-DesktopTask -ExePath $exe -TaskName $TaskName -Scope $Scope
  }
}
Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'ScadaDesktop' -ErrorAction SilentlyContinue

if ($ConfigPath -and (Test-Path $ConfigPath)) {
  if ($Scope -eq 'AllUsers') {
    $machineDir = Join-Path $env:ProgramData 'Scada\Desktop'
    New-Item -ItemType Directory -Force -Path $machineDir | Out-Null
    Copy-Item $ConfigPath (Join-Path $machineDir 'client.json') -Force
  } else {
    & $exe --provision --config $ConfigPath --scope user
    if ($LASTEXITCODE -ne 0) { Write-Warning "用户配准返回 $LASTEXITCODE" }
  }
}

if (-not $ManifestDirectory) {
  $ManifestDirectory = if ($Scope -eq 'AllUsers') { Join-Path $env:ProgramData 'Scada\Desktop' } else { Join-Path $env:LOCALAPPDATA 'Scada\Desktop' }
}
Write-InstallManifest -Directory $ManifestDirectory -Version $Version -Scope $Scope -InstallRoot $InstallRoot -ManagedAutostart (-not $SkipTask)

if ($ProvisionConfig -and (Test-Path $ProvisionConfig)) {
  & $exe --provision --config $ProvisionConfig --scope user
  if ($LASTEXITCODE -ne 0) { Write-Warning "用户配准返回 $LASTEXITCODE" }
}

if (-not $NoStart) {
  if (-not $SkipTask -and $Scope -eq 'CurrentUser') {
    Start-ScheduledTask -TaskName $TaskName
  } elseif (-not $SkipTask) {
    Write-Host '已注册所有用户的登录任务；客户端将在用户下次登录时启动。'
  } else {
    Start-Process -FilePath $exe
  }
}
Write-Host "已安装 $Version 到 $release"
