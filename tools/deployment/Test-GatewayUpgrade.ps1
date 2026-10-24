#Requires -Version 7.2
param([Parameter(Mandatory)][string] $ProgramPath)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'GatewayService.Common.ps1')
Assert-GatewayWindows

$source = Get-GatewayDirectory $ProgramPath
$root = Join-Path ([IO.Path]::GetTempPath()) ('scada-upgrade-test-' + [guid]::NewGuid().ToString('N'))
$serviceName = 'ScadaGatewayUpgradeTest'
$token = 'upgrade-test-token-abcdefghijklmnopqrstuvwxyz123456'
$v1 = Join-Path $root 'gateway v1'
$v2 = Join-Path $root 'gateway v2'
$data = Join-Path $root 'data with spaces'
$versions = Join-Path $root 'versions'
$upgrade = Join-Path $PSScriptRoot 'Upgrade-GatewayService.ps1'
$rollback = Join-Path $PSScriptRoot 'Rollback-GatewayService.ps1'

function Assert-Check($Condition, [string] $Message) { if (-not $Condition) { throw $Message } }
function Assert-Rejected([scriptblock] $Action, [string] $Message) {
    try { & $Action | Out-Null } catch { return }
    throw $Message
}
function New-FakePackage([string] $Target) {
    New-Item -ItemType Directory -Path $Target -Force | Out-Null
    foreach ($file in @('Scada.Gateway.Host.exe', 'Scada.Gateway.Host.dll', 'Scada.Gateway.Host.runtimeconfig.json')) {
        Copy-Item -LiteralPath (Join-Path $source $file) -Destination (Join-Path $Target $file)
    }
    return [IO.Path]::GetFullPath($Target).TrimEnd([IO.Path]::DirectorySeparatorChar)
}

try {
    New-Item -ItemType Directory -Path $root | Out-Null
    $v1 = New-FakePackage $v1
    $v2 = New-FakePackage $v2
    New-Item -ItemType Directory -Path $data | Out-Null
    @{ GatewayAgent = @{ ServiceName = $serviceName; GatewayId = 7; ServerUrl = 'https://127.0.0.1:1/'; Token = $token; DataRoot = $data } } |
        ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $v1 'gateway-service.json') -Encoding utf8
    @{ product = 'Scada.Gateway'; format = 1; serviceName = $serviceName; programPath = $v1; dataPath = $data; configPath = (Join-Path $v1 'gateway-service.json') } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $v1 'gateway-installation.json') -Encoding utf8

    # 1. 预演升级：生成快照与清单，但不碰服务。
    $plan = & $upgrade -ProgramPath $v2 -ServiceName $serviceName -VersionRoot $versions -CurrentProgramPath $v1 -Rehearse
    Assert-Check ($plan.From -eq $v1 -and $plan.To -eq $v2) '升级计划应从当前目录指向新目录。'
    Assert-Check ($plan.BinaryPath -like "*$v2*Scada.Gateway.Host.exe*") '计划中的启动路径应指向新版本。'
    Assert-Check (Test-Path -LiteralPath (Join-Path $plan.Snapshot 'snapshot.json')) '预演必须生成快照清单。'
    Assert-Check (Test-Path -LiteralPath (Join-Path $plan.Snapshot 'Scada.Gateway.Host.dll')) '快照必须包含旧版本程序。'
    $snapshotMeta = Get-Content -LiteralPath (Join-Path $plan.Snapshot 'snapshot.json') -Raw | ConvertFrom-Json
    Assert-Check ($snapshotMeta.programPath -eq $v1 -and $snapshotMeta.dataPath -eq $data) '快照须记录原路径与数据目录。'
    Assert-Check ($snapshotMeta.fingerprint.'Scada.Gateway.Host.dll' -eq (Get-GatewayPackageFingerprint $v1).'Scada.Gateway.Host.dll') '快照指纹须与旧版本一致。'
    Assert-Check (-not (Test-Path -LiteralPath (Join-Path $plan.Snapshot 'snapshot.json.bak'))) '不应产生多余文件。'

    $newConfig = Get-Content -LiteralPath (Join-Path $v2 'gateway-service.json') -Raw | ConvertFrom-Json
    Assert-Check ($newConfig.GatewayAgent.Token -eq $token -and $newConfig.GatewayAgent.GatewayId -eq 7) '升级必须沿用原身份与密钥。'
    Assert-Check ($newConfig.GatewayAgent.DataRoot -eq $data) '升级不能改数据目录。'
    $newManifest = Get-Content -LiteralPath (Join-Path $v2 'gateway-installation.json') -Raw | ConvertFrom-Json
    Assert-Check ($newManifest.programPath -eq $v2 -and $newManifest.configPath -eq (Join-Path $v2 'gateway-service.json')) '新清单须指向新目录。'

    # 2. 预演回滚：把快照清单改写回快照自身路径。
    $back = & $rollback -ServiceName $serviceName -VersionRoot $versions -CurrentProgramPath $v2 -Rehearse
    Assert-Check ($back.From -eq $v2 -and $back.To -eq $plan.Snapshot) '回滚计划应从新目录指向快照。'
    Assert-Check ($back.BinaryPath -like "*$($plan.Snapshot)*Scada.Gateway.Host.exe*") '回滚启动路径须指向快照目录。'
    $restoredManifest = Get-Content -LiteralPath (Join-Path $plan.Snapshot 'gateway-installation.json') -Raw | ConvertFrom-Json
    Assert-Check ($restoredManifest.programPath -eq $plan.Snapshot) '回滚后快照清单须改写为自身路径，供下次升级读取。'
    Assert-Check ($restoredManifest.configPath -eq (Join-Path $plan.Snapshot 'gateway-service.json')) '回滚后配置路径须指向快照内配置。'

    # 3. 拒绝场景。
    Assert-Rejected { & $upgrade -ProgramPath $v2 -ServiceName $serviceName -VersionRoot $versions -CurrentProgramPath $v1 -Rehearse } '已有安装清单的目录不能被再次升级接管。'
    Assert-Rejected { & $upgrade -ProgramPath $v2 -ServiceName $serviceName -VersionRoot (Join-Path $data 'versions') -CurrentProgramPath $v1 -Rehearse } '版本快照不能落在数据目录内。'
    Assert-Rejected { & $upgrade -ProgramPath $v1 -ServiceName $serviceName -VersionRoot $versions -CurrentProgramPath $v1 -Rehearse } '新目录与当前目录相同应被拒绝。'
    Assert-Rejected { & $rollback -ServiceName $serviceName -VersionRoot $versions -CurrentProgramPath $v2 -To '20000101-000000' -Rehearse } '不存在的快照名应被拒绝。'
    Assert-Rejected { & $upgrade -ProgramPath $v2 -ServiceName 'OtherName' -VersionRoot $versions -CurrentProgramPath $v1 -Rehearse } '跨服务升级应被拒绝。'

    $brokenRoot = Join-Path $root 'broken versions'
    Copy-Item -LiteralPath $versions -Destination $brokenRoot -Recurse
    $brokenSnapshot = @(Get-ChildItem -LiteralPath (Join-Path $brokenRoot $serviceName) -Directory)[0].FullName
    $brokenMeta = Get-Content -LiteralPath (Join-Path $brokenSnapshot 'snapshot.json') -Raw | ConvertFrom-Json
    $brokenMeta.dataPath = (Join-Path $root 'another data')
    $brokenMeta | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $brokenSnapshot 'snapshot.json') -Encoding utf8
    Assert-Rejected { & $rollback -ServiceName $serviceName -VersionRoot $brokenRoot -CurrentProgramPath $v2 -Rehearse } '数据目录不一致的快照须拒绝回滚。'

    $tamperRoot = Join-Path $root 'tamper versions'
    Copy-Item -LiteralPath $versions -Destination $tamperRoot -Recurse
    $tamperSnapshot = @(Get-ChildItem -LiteralPath (Join-Path $tamperRoot $serviceName) -Directory)[0].FullName
    [IO.File]::AppendAllText((Join-Path $tamperSnapshot 'Scada.Gateway.Host.dll'), 'tampered')
    Assert-Rejected { & $rollback -ServiceName $serviceName -VersionRoot $tamperRoot -CurrentProgramPath $v2 -Rehearse } '被改动的快照程序集须拒绝回滚。'

    Write-Output '升级/回滚预演通过：快照生成与指纹、身份与数据目录保留、清单改写、启动路径计算，以及跨服务、重复接管、目录嵌套、快照缺失或篡改的拒绝路径均符合预期；未改动 SCM。'
} finally {
    $resolved = [IO.Path]::GetFullPath($root)
    $temp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if ($resolved.StartsWith($temp, [StringComparison]::OrdinalIgnoreCase) -and [IO.Path]::GetFileName($resolved).StartsWith('scada-upgrade-test-')) {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
