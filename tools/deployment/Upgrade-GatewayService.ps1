#Requires -Version 7.2
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)][string] $ProgramPath,
    [Parameter(Mandatory)][string] $ServiceName,
    [Parameter(Mandatory)][string] $VersionRoot,
    [switch] $NoStart,
    # 显式指定当前运行目录：SCM 查询受限或做预演时可省略服务查询。
    [string] $CurrentProgramPath = '',
    # 预演：完整生成快照与清单、算出要执行的切换命令，但不停止服务、不改 SCM。
    [switch] $Rehearse
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'GatewayService.Common.ps1')
Assert-GatewayWindows

$oldProgram = if ($CurrentProgramPath) { Get-GatewayDirectory $CurrentProgramPath } else { Get-GatewayServiceProgramPath $ServiceName }
$oldManifest = Read-GatewayInstallation $oldProgram
if ($oldManifest.serviceName -ne $ServiceName) { throw "服务 $ServiceName 的安装清单属于 $($oldManifest.serviceName)，拒绝跨服务升级。" }
$dataPath = $oldManifest.dataPath
try { $gatewayId = [int](Get-Content -LiteralPath $oldManifest.configPath -Raw | ConvertFrom-Json).GatewayAgent.GatewayId }
catch { throw '当前安装的网关配置无法读取身份，拒绝升级。' }

$newProgram = Get-GatewayDirectory $ProgramPath
if ($newProgram -eq $oldProgram) { throw '新发布目录与当前运行目录相同，没有可升级的内容。' }
Assert-GatewaySeparateDirectories $newProgram $dataPath
foreach ($file in @('Scada.Gateway.Host.exe', 'Scada.Gateway.Host.dll', 'Scada.Gateway.Host.runtimeconfig.json')) {
    if (-not (Test-Path -LiteralPath (Join-Path $newProgram $file) -PathType Leaf)) { throw '新发布目录不完整，请先生成网关部署包。' }
}
if ([Reflection.AssemblyName]::GetAssemblyName((Join-Path $newProgram 'Scada.Gateway.Host.dll')).Name -ne 'Scada.Gateway.Host') { throw '新发布目录中的网关程序集不匹配。' }
foreach ($name in @('gateway-installation.json', 'gateway-service.json')) {
    if (Test-Path -LiteralPath (Join-Path $newProgram $name)) { throw "新发布目录已含 $name，拒绝覆盖另一套安装。" }
}
$versionRoot = Get-GatewayDirectory $VersionRoot
Assert-GatewaySeparateDirectories $versionRoot $newProgram
Assert-GatewaySeparateDirectories $versionRoot $dataPath

$snapshot = Join-Path (Join-Path $versionRoot $ServiceName) (Get-Date -Format 'yyyyMMdd-HHmmss')
$plan = [pscustomobject]@{
    ServiceName = $ServiceName; From = $oldProgram; To = $newProgram; Snapshot = $snapshot
    DataPath = $dataPath; GatewayId = $gatewayId; Start = -not $NoStart; Rehearse = [bool]$Rehearse
    BinaryPath = (Get-GatewayBinaryPath $newProgram (Join-Path $newProgram 'gateway-service.json'))
}
if (-not $PSCmdlet.ShouldProcess("$ServiceName : $oldProgram -> $newProgram", '备份当前版本、切换服务启动路径并验证')) { return $plan }

if (-not $Rehearse) { Assert-GatewayAdministrator }
try {
    # 先留快照再动机器：升级失败时能原路退回，而不是靠现场重新出包。
    Copy-GatewayDirectory $oldProgram $snapshot
    Write-GatewayNewFile (Join-Path $snapshot 'snapshot.json') (@{
        product = 'Scada.Gateway'; format = 1; serviceName = $ServiceName
        programPath = $oldProgram; dataPath = $dataPath; configPath = $oldManifest.configPath
        takenAt = (Get-Date).ToString('o'); fingerprint = (Get-GatewayPackageFingerprint $oldProgram)
    } | ConvertTo-Json -Depth 4)

    $newConfig = Join-Path $newProgram 'gateway-service.json'
    Copy-Item -LiteralPath $oldManifest.configPath -Destination $newConfig -Force
    Write-GatewayNewFile (Join-Path $newProgram 'gateway-installation.json') (@{
        product = 'Scada.Gateway'; format = 1; serviceName = $ServiceName
        programPath = $newProgram; dataPath = $dataPath; configPath = $newConfig
    } | ConvertTo-Json)

    if ($Rehearse) {
        Write-Host "预演完成：快照 $snapshot 已生成，新目录清单已写入，服务未改动。实际切换命令：sc config $ServiceName binPath= $($plan.BinaryPath)"
        return $plan
    }

    Stop-GatewayServiceIfRunning $ServiceName | Out-Null

    $sid = Get-GatewayServiceSid $ServiceName
    foreach ($item in @(Get-ChildItem -LiteralPath $newProgram -Force -Recurse)) { Set-GatewayAcl $item.FullName $sid $false -AdministratorsOwn }
    Set-GatewayAcl $newProgram $sid $false -AdministratorsOwn

    Set-GatewayServiceBinary $ServiceName $newProgram $newConfig
    if (-not $NoStart) {
        Start-GatewayServiceAndWait $ServiceName
        Wait-GatewayStatus $dataPath $gatewayId | Out-Null
    }
    $plan
} catch {
    $failure = $_.Exception.Message
    $rollbackError = $null
    $recovered = $false
    try {
        Set-GatewayServiceBinary $ServiceName $oldProgram $oldManifest.configPath
        if (-not $NoStart) { Start-GatewayServiceAndWait $ServiceName }
        $recovered = $true
    } catch { $rollbackError = $_.Exception.Message }

    if ($recovered) { throw "升级失败并已自动回退到 $oldProgram：$failure" }
    throw "升级失败且自动回退未成功。原错误：$failure；回退错误：$rollbackError。请按快照 $snapshot 手工恢复；数据目录未改动：$dataPath"
}
