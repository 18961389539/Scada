#requires -Version 7.2
<#
.SYNOPSIS 将已发布的 SCADA 中心服务安装为 Windows 服务（管理员终端执行）。
.EXAMPLE pwsh -File Install-ScadaService.ps1 -PublishPath D:\pkg\scada-1.4 -InstallRoot 'C:\Program Files\Scada' -DataPath D:\ScadaData -Version 1.4
程序目录 releases\<版本> + current 目录联接；数据目录独立；SCM 失败动作设为 5 秒后重启（看门狗退出码 3 也会被拉起）。
#>
[CmdletBinding(SupportsShouldProcess)]
param(
  [Parameter(Mandatory)][string]$PublishPath,
  [Parameter(Mandatory)][string]$InstallRoot,
  [Parameter(Mandatory)][string]$DataPath,
  [string]$Version = (Get-Date -Format 'yyyyMMddHHmmss'),
  [string]$ServiceName = 'ScadaServer',
  [int]$Port = 5080,
  [switch]$Start
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ScadaService.Common.ps1')
Assert-Admin
if (-not (Test-Path (Join-Path $PublishPath 'Scada.Server.exe'))) { throw "$PublishPath 不是服务端发布目录（缺少 Scada.Server.exe）" }
if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) { throw "服务 $ServiceName 已存在，请使用 Upgrade-ScadaService.ps1" }

$release = Join-Path $InstallRoot "releases\$Version"
if ($PSCmdlet.ShouldProcess($release, '复制程序')) {
  New-Item -ItemType Directory -Force -Path $release, $DataPath, (Join-Path $DataPath 'logs') | Out-Null
  Copy-Item -Path (Join-Path $PublishPath '*') -Destination $release -Recurse
  Set-CurrentRelease -InstallRoot $InstallRoot -Release $release
  $settings = Join-Path $DataPath 'appsettings.Site.json'
  if (-not (Test-Path $settings)) {
    $key = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(48))
    @{
      Kestrel = @{ Endpoints = @{ Http = @{ Url = "http://0.0.0.0:$Port" } } }
      Scada = @{ ConfigDbPath = "$DataPath\scada.db"; HistoryRoot = "$DataPath\history"; BackupRoot = "$DataPath\backups"; LogRoot = "$DataPath\logs" }
      Jwt = @{ Key = $key }
      Ha = @{ Enabled = $false }
    } | ConvertTo-Json -Depth 6 | Set-Content -Path $settings -Encoding utf8
  }
  Protect-DataDirectory -DataPath $DataPath -ServiceName $ServiceName
}
$exe = Join-Path $InstallRoot 'current\Scada.Server.exe'
$bin = "`"$exe`" --contentRoot `"$(Join-Path $InstallRoot 'current')`" --SiteSettings `"$(Join-Path $DataPath 'appsettings.Site.json')`""
if ($PSCmdlet.ShouldProcess($ServiceName, '注册服务')) {
  New-Service -Name $ServiceName -BinaryPathName $bin -DisplayName 'SCADA Server' -StartupType Automatic | Out-Null
  sc.exe config $ServiceName obj= "NT SERVICE\$ServiceName" | Out-Null
  Protect-DataDirectory -DataPath $DataPath -ServiceName $ServiceName
  Set-RecoveryActions -ServiceName $ServiceName
  Set-Content -Path (Join-Path $InstallRoot 'service.json') -Value (@{ ServiceName = $ServiceName; DataPath = $DataPath; Port = $Port } | ConvertTo-Json)
  if ($Start) { Start-Service $ServiceName; Wait-Healthy -Port $Port }
}
Write-Host "已安装 $Version；站点配置 $DataPath\appsettings.Site.json"
