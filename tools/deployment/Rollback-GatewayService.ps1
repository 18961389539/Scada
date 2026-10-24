#Requires -Version 7.2
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)][string] $ServiceName,
    [Parameter(Mandatory)][string] $VersionRoot,
    [string] $To,
    [switch] $NoStart,
    [string] $CurrentProgramPath = '',
    # 预演：生成清单改写与切换命令，但不停止服务、不改 SCM。
    [switch] $Rehearse
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'GatewayService.Common.ps1')
Assert-GatewayWindows

$currentProgram = if ($CurrentProgramPath) { Get-GatewayDirectory $CurrentProgramPath } else { Get-GatewayServiceProgramPath $ServiceName }
$currentManifest = Read-GatewayInstallation $currentProgram
if ($currentManifest.serviceName -ne $ServiceName) { throw "服务 $ServiceName 的安装清单属于 $($currentManifest.serviceName)，拒绝跨服务回滚。" }
$dataPath = $currentManifest.dataPath
try { $gatewayId = [int](Get-Content -LiteralPath $currentManifest.configPath -Raw | ConvertFrom-Json).GatewayAgent.GatewayId }
catch { throw '当前安装的网关配置无法读取身份，拒绝回滚。' }

$root = Get-GatewayDirectory $VersionRoot
$serviceRoot = Join-Path $root $ServiceName
if (-not (Test-Path -LiteralPath $serviceRoot -PathType Container)) { throw "没有找到 $ServiceName 的版本快照目录：$serviceRoot" }
$snapshots = @(Get-ChildItem -LiteralPath $serviceRoot -Directory | Sort-Object Name -Descending)
if ($snapshots.Count -eq 0) { throw "没有可回滚的版本快照：$serviceRoot" }
$selected = if ($To) { $snapshots | Where-Object { $_.Name -eq $To } | Select-Object -First 1 } else { $snapshots[0] }
if (-not $selected) { throw "未找到指定快照 $To；可用：$((@($snapshots.Name)) -join ', ')" }

$target = Get-GatewayDirectory $selected.FullName
if ($target -eq $currentProgram) { throw '目标快照就是当前运行目录，无需回滚。' }
$snapshotFile = Join-Path $target 'snapshot.json'
if (-not (Test-Path -LiteralPath $snapshotFile -PathType Leaf)) { throw "快照 $($selected.Name) 缺少 snapshot.json，拒绝回滚。" }
$snapshot = Get-Content -LiteralPath $snapshotFile -Raw | ConvertFrom-Json
if ($snapshot.product -ne 'Scada.Gateway' -or $snapshot.serviceName -ne $ServiceName -or $snapshot.dataPath -ne $dataPath) {
    throw "快照 $($selected.Name) 与当前服务不属于同一安装，拒绝回滚。"
}
foreach ($file in @('Scada.Gateway.Host.exe', 'Scada.Gateway.Host.dll', 'Scada.Gateway.Host.runtimeconfig.json', 'gateway-service.json')) {
    if (-not (Test-Path -LiteralPath (Join-Path $target $file) -PathType Leaf)) { throw "快照 $($selected.Name) 不完整，缺少 $file。" }
}
$fingerprint = Get-GatewayPackageFingerprint $target
if ($snapshot.fingerprint.'Scada.Gateway.Host.dll' -and $fingerprint.'Scada.Gateway.Host.dll' -ne $snapshot.fingerprint.'Scada.Gateway.Host.dll') {
    throw "快照 $($selected.Name) 的程序集哈希与记录不一致，拒绝回滚。"
}

$plan = [pscustomobject]@{
    ServiceName = $ServiceName; From = $currentProgram; To = $target
    Snapshot = $selected.Name; DataPath = $dataPath; GatewayId = $gatewayId; Start = -not $NoStart; Rehearse = [bool]$Rehearse
    BinaryPath = (Get-GatewayBinaryPath $target (Join-Path $target 'gateway-service.json'))
}
if (-not $PSCmdlet.ShouldProcess("$ServiceName : $currentProgram -> $target", '切换到快照版本并验证')) { return $plan }

if (-not $Rehearse) { Assert-GatewayAdministrator }
try {
    # 快照里的清单仍指向旧路径，回滚时要改写成快照自身路径，否则下次升级会读到错误的归属信息。
    $targetConfig = Join-Path $target 'gateway-service.json'
    $manifestPath = Join-Path $target 'gateway-installation.json'
    if (Test-Path -LiteralPath $manifestPath) { Remove-Item -LiteralPath $manifestPath -Force }
    Write-GatewayNewFile $manifestPath (@{
        product = 'Scada.Gateway'; format = 1; serviceName = $ServiceName
        programPath = $target; dataPath = $dataPath; configPath = $targetConfig
    } | ConvertTo-Json)

    if ($Rehearse) {
        Write-Host "预演完成：快照 $($selected.Name) 清单已改写，服务未改动。实际切换命令：sc config $ServiceName binPath= $($plan.BinaryPath)"
        return $plan
    }

    Stop-GatewayServiceIfRunning $ServiceName | Out-Null
    $sid = Get-GatewayServiceSid $ServiceName
    foreach ($item in @(Get-ChildItem -LiteralPath $target -Force -Recurse)) { Set-GatewayAcl $item.FullName $sid $false -AdministratorsOwn }
    Set-GatewayAcl $target $sid $false -AdministratorsOwn

    Set-GatewayServiceBinary $ServiceName $target $targetConfig
    if (-not $NoStart) {
        Start-GatewayServiceAndWait $ServiceName
        Wait-GatewayStatus $dataPath $gatewayId | Out-Null
    }
    $plan
} catch {
    $failure = $_.Exception.Message
    $restoreError = $null
    $restored = $false
    try {
        Set-GatewayServiceBinary $ServiceName $currentProgram $currentManifest.configPath
        if (-not $NoStart) { Start-GatewayServiceAndWait $ServiceName }
        $restored = $true
    } catch { $restoreError = $_.Exception.Message }

    if ($restored) { throw "回滚失败并已恢复到 $currentProgram：$failure" }
    throw "回滚失败且未能恢复原版本。回滚错误：$failure；恢复错误：$restoreError。数据目录未改动：$dataPath"
}
