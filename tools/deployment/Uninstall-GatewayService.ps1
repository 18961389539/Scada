#Requires -Version 7.2
[CmdletBinding(SupportsShouldProcess)]
param([Parameter(Mandatory)][string] $ProgramPath)
. (Join-Path $PSScriptRoot 'GatewayService.Common.ps1')
Assert-GatewayWindows
$program = Get-GatewayDirectory $ProgramPath
$installation = Read-GatewayInstallation $program
$name = $installation.serviceName
$service = Get-CimInstance Win32_Service -Filter "Name='$name'"
if (-not $service) { throw '部署清单对应的服务不存在；未删除任何程序或数据文件。' }
$expected = Get-GatewayBinaryPath $program $installation.configPath
if ($service.PathName -ne $expected -or $service.StartName -ne "NT SERVICE\$name") { throw '服务程序路径或账户与部署清单不符，拒绝停止或删除。' }
if (-not $PSCmdlet.ShouldProcess($name, '正常停止并删除已核对归属的服务注册，保留全部程序、配置、密钥与队列')) { return }
Assert-GatewayAdministrator
$oldProcess = if ($service.ProcessId -gt 0) { Get-Process -Id $service.ProcessId -ErrorAction SilentlyContinue } else { $null }
if ($oldProcess) { $oldProcess.EnableRaisingEvents = $true }
if ($service.State -ne 'Stopped') {
    Stop-Service -Name $name -NoWait
    (Get-Service -Name $name).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(60))
}
$stopped = Get-CimInstance Win32_Service -Filter "Name='$name'"
if ($stopped.State -ne 'Stopped' -or $stopped.ProcessId -ne 0 -or $stopped.PathName -ne $expected -or $stopped.StartName -ne "NT SERVICE\$name") { throw '原服务尚未完全停止或归属已变化，保留服务注册及数据。' }
if ($oldProcess -and -not $oldProcess.WaitForExit(10000)) { throw 'SCM 已报告停止，但原进程尚未退出，保留服务注册及数据。' }
Invoke-GatewaySc -Arguments @('delete', $name) | Out-Null
Write-Output "服务注册已请求删除。保留程序和数据：$($installation.dataPath)。重新部署前确认服务注册已移除且旧进程确已退出。"
