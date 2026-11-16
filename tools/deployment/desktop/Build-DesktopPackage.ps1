#requires -Version 7.2
<#
.SYNOPSIS 构建 SCADA 桌面客户端发布包（ZIP + release-manifest.json + SHA-256）。
.EXAMPLE pwsh -File Build-DesktopPackage.ps1 -OutputPath D:\pkg\desktop -Version 1.1.0
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory)][string]$OutputPath,
  [string]$Version = '1.1.0',
  [string]$Runtime = 'win-x64'
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$project = Join-Path $repo 'src\Scada.Desktop\Scada.Desktop.csproj'

if (Test-Path $OutputPath) { Remove-Item $OutputPath -Recurse -Force }
New-Item -ItemType Directory -Force -Path $OutputPath | Out-Null

dotnet publish $project -c Release -r $Runtime --self-contained true `
  -p:PublishSingleFile=false -p:PublishTrimmed=false -p:Version=$Version -o $OutputPath --nologo
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish 失败' }
if (-not (Test-Path (Join-Path $OutputPath 'Scada.Desktop.exe'))) { throw '发布目录缺少 Scada.Desktop.exe' }

Copy-Item (Join-Path $PSScriptRoot '*.ps1') $OutputPath -Force
@{
  serverUrl = 'http://scada.example.com:5080'
  userName = 'kiosk01'
  target = @{ kind = 'screen'; screenId = 1 }
  display = @{ fullScreen = $true }
  autoStart = $true
  allowDownloads = $false
} | ConvertTo-Json -Depth 6 | Set-Content -Path (Join-Path $OutputPath 'client.sample.json') -Encoding utf8

$manifestScript = Join-Path $repo 'tools\deployment\package-manifest.mjs'
node $manifestScript $OutputPath --product Scada.Desktop `
  --require 'Scada.Desktop.exe,Microsoft.Web.WebView2.Core.dll,WebView2Loader.dll' --version $Version
if ($LASTEXITCODE -ne 0) { throw '生成 release-manifest.json 失败' }

$zip = "$OutputPath.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $OutputPath '*') -DestinationPath $zip -Force
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash
Set-Content -Path "$zip.sha256" -Value $hash -Encoding ascii
Write-Host "桌面客户端包：$zip"
