#Requires -Version 7.2
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)][string] $OutputPath,
    [ValidateSet('win-x64', 'win-arm64')][string] $Runtime = 'win-x64',
    [switch] $SelfContained
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not [IO.Path]::IsPathFullyQualified($OutputPath)) { throw '部署包输出须使用绝对路径。' }
$output = [IO.Path]::GetFullPath($OutputPath)
if ((Test-Path -LiteralPath $output) -and @(Get-ChildItem -LiteralPath $output -Force).Count -gt 0) { throw '输出目录非空，拒绝覆盖已有部署包或运行数据。' }
$project = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../src/Scada.Gateway.Host/Scada.Gateway.Host.csproj'))
if (-not $PSCmdlet.ShouldProcess($output, "发布 $Runtime 网关及部署脚本（SelfContained=$([bool]$SelfContained)）")) { return }
& dotnet publish $project -c Release -r $Runtime --self-contained ([bool]$SelfContained).ToString().ToLowerInvariant() -o $output --nologo
if ($LASTEXITCODE -ne 0) { throw '网关发布失败；输出目录保留供排查，请勿作为完整安装包使用。' }
foreach ($name in @('GatewayService.Common.ps1', 'Install-GatewayService.ps1', 'Uninstall-GatewayService.ps1', 'Test-GatewayDeployment.ps1')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination (Join-Path $output $name)
}
$docsPath = Join-Path $output 'docs'
New-Item -ItemType Directory -Path $docsPath | Out-Null
Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot '../../docs') -Filter '*.md' | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $docsPath $_.Name) }
[IO.File]::WriteAllText((Join-Path $output 'INSTALL.md'), "# SCADA Gateway`n`n部署与验证见 [Windows 服务部署说明](docs/gateway-windows-service.md)。`n", [Text.UTF8Encoding]::new($false))
Write-Output "部署包：$output"
