#requires -Version 7.2
<#
.SYNOPSIS 把离线 BackupTool 发布进服务端绿色包的 tools 目录。
.EXAMPLE pwsh -File Publish-BackupTool.ps1 -PackageRoot D:\pkg\out
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory)][string]$PackageRoot,
  [string]$Configuration = 'Release',
  [string]$Runtime = 'win-x64'
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
if (-not (Test-Path -LiteralPath (Join-Path $PackageRoot 'Scada.Server.exe'))) {
  throw "$PackageRoot 不是服务端发布目录（缺少 Scada.Server.exe）"
}
$project = Join-Path $repo 'tools\Scada.BackupTool\Scada.BackupTool.csproj'
$out = Join-Path $PackageRoot 'tools'
New-Item -ItemType Directory -Force -Path $out | Out-Null
# 与绿色包同一运行时：依赖包内已有的共享框架程序集时仍给出独立可执行文件，避免现场再装 SDK。
dotnet publish $project -c $Configuration -r $Runtime --self-contained true -o $out --nologo
if ($LASTEXITCODE -ne 0) { throw "发布 BackupTool 失败（退出码 $LASTEXITCODE）" }
$exe = Join-Path $out 'Scada.BackupTool.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw "发布成功但未找到 $exe" }
Write-Host "已把 BackupTool 发布到 $exe"
