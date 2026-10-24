#Requires -Version 7.2
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)][string] $ProgramPath,
    [Parameter(Mandatory)][string] $DataPath,
    [Parameter(Mandatory)][string] $IdentityFile,
    [ValidatePattern('^[A-Za-z][A-Za-z0-9_-]{0,79}$')][string] $ServiceName = 'ScadaGateway',
    [switch] $Start,
    [switch] $AllowLoopbackHttp
)
. (Join-Path $PSScriptRoot 'GatewayService.Common.ps1')
Assert-GatewayWindows
$program = Get-GatewayDirectory $ProgramPath
$data = Get-GatewayDirectory $DataPath
Assert-GatewaySeparateDirectories $program $data
foreach ($file in @('Scada.Gateway.Host.exe', 'Scada.Gateway.Host.dll', 'Scada.Gateway.Host.runtimeconfig.json')) {
    if (-not (Test-Path -LiteralPath (Join-Path $program $file) -PathType Leaf)) { throw '发布目录不完整，请先生成网关部署包。' }
}
if ([Reflection.AssemblyName]::GetAssemblyName((Join-Path $program 'Scada.Gateway.Host.dll')).Name -ne 'Scada.Gateway.Host') { throw '发布目录中的网关程序集不匹配。' }
$packageItems = @(Get-ChildItem -LiteralPath $program -Force -Recurse)
if ($packageItems | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }) { throw '发布目录不能包含重解析点。' }
$configPath = Join-Path $program 'gateway-service.json'
$manifestPath = Join-Path $program 'gateway-installation.json'
if ((Test-Path -LiteralPath $configPath) -or (Test-Path -LiteralPath $manifestPath)) { throw '此发布目录已有安装配置；升级或改密须先核对并停止原服务，不能重新安装覆盖。' }
if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) { throw '同名服务已存在，拒绝覆盖。' }
if ((Test-Path -LiteralPath $data) -and @(Get-ChildItem -LiteralPath $data -Force).Count -gt 0) { throw '首次安装须使用空数据目录；已有队列不能被新安装接管。' }
try {
    if ((Get-Item -LiteralPath $IdentityFile).Length -gt 16384) { throw 'too large' }
    $identity = Get-Content -LiteralPath $IdentityFile -Raw | ConvertFrom-Json
    $id = [int]$identity.gatewayId
    $uri = [uri]$identity.serverUrl
    if ($id -lt 1 -or [decimal]$identity.gatewayId -ne $id -or $identity.token -cnotmatch '^[A-Za-z0-9_-]{40,256}$' -or -not $uri.IsAbsoluteUri -or $uri.UserInfo -or $uri.Query -or $uri.Fragment -or
        ($uri.Scheme -ne 'https' -and -not ($AllowLoopbackHttp -and $uri.Scheme -eq 'http' -and $uri.IsLoopback))) { throw 'invalid identity' }
} catch { throw '网关身份文件无效：需要有效的 gatewayId、serverUrl 和 token；默认仅接受 HTTPS。' }
$serverUrl = $uri.AbsoluteUri.TrimEnd('/') + '/'
$binPath = Get-GatewayBinaryPath $program $configPath
$plan = [pscustomobject]@{ ServiceName = $ServiceName; Account = "NT SERVICE\$ServiceName"; ProgramPath = $program; DataPath = $data; GatewayId = $id; ServerUrl = $serverUrl; Start = [bool]$Start }
if (-not $PSCmdlet.ShouldProcess("$ServiceName / $program / $data", '创建独立网关服务、受限配置及目录权限')) { return $plan }
Assert-GatewayAdministrator
$sid = Get-GatewayServiceSid $ServiceName
New-Item -ItemType Directory -Path $data -Force | Out-Null
$manifest = @{ product = 'Scada.Gateway'; format = 1; serviceName = $ServiceName; programPath = $program; dataPath = $data; configPath = $configPath }
Write-GatewayNewFile $manifestPath ($manifest | ConvertTo-Json)
$dataMarker = Join-Path $data 'installation.json'
Write-GatewayNewFile $dataMarker (@{ product = 'Scada.Gateway'; serviceName = $ServiceName; gatewayId = $id; serverUrl = $serverUrl; programPath = $program } | ConvertTo-Json)
# Secure the directories before writing any credential. The service can only read its program.
Set-GatewayAcl $program $sid $false -AdministratorsOwn
foreach ($item in $packageItems) { Set-GatewayAcl $item.FullName $sid $false -AdministratorsOwn }
Set-GatewayAcl $data $sid $true -AdministratorsOwn
$settings = @{ GatewayAgent = @{
    ServiceName = $ServiceName; ServerUrl = $serverUrl; GatewayId = $id; Token = $identity.token; DataRoot = $data
    AllowLoopbackHttp = [bool]$AllowLoopbackHttp
} }
Write-GatewayNewFile $configPath ($settings | ConvertTo-Json -Depth 4)
Set-GatewayAcl $configPath $sid $false -AdministratorsOwn
Set-GatewayAcl $manifestPath $sid $false -AdministratorsOwn
Set-GatewayAcl $dataMarker $sid $true -AdministratorsOwn
try {
    & (Join-Path $program 'Scada.Gateway.Host.exe') "--GatewayConfig=$configPath" '--ValidateConfig=true'
    if ($LASTEXITCODE -ne 0) { throw '发布程序未通过部署配置校验，尚未注册服务。' }
    # The virtual service account cannot create an Event Log source at first startup.
    if ([Diagnostics.EventLog]::SourceExists('Scada.Gateway.Host')) {
        if ([Diagnostics.EventLog]::LogNameFromSourceName('Scada.Gateway.Host', '.') -ne 'Application') { throw '网关事件源已被其他日志占用。' }
    } else { [Diagnostics.EventLog]::CreateEventSource('Scada.Gateway.Host', 'Application') }
    Invoke-GatewaySc -Arguments @('create', $ServiceName, 'binPath=', $binPath, 'start=', 'delayed-auto', 'obj=', "NT SERVICE\$ServiceName", 'DisplayName=', "SCADA Gateway ($ServiceName)") | Out-Null
    Invoke-GatewaySc -Arguments @('sidtype', $ServiceName, 'unrestricted') | Out-Null
    Invoke-GatewaySc -Arguments @('description', $ServiceName, 'SCADA remote acquisition gateway. Preserve its data directory when stopping or removing this service.') | Out-Null
    Invoke-GatewaySc -Arguments @('failure', $ServiceName, 'reset=', '86400', 'actions=', 'restart/5000/restart/15000/restart/60000') | Out-Null
    Invoke-GatewaySc -Arguments @('failureflag', $ServiceName, '1') | Out-Null
    if ($Start) {
        Start-Service -Name $ServiceName
        (Get-Service -Name $ServiceName).WaitForStatus('Running', [TimeSpan]::FromSeconds(45))
    }
    $plan
} catch {
    throw "服务部署未完成。配置与数据目录已保留，请核对 $ServiceName 的实际状态和事件日志后处理；不要重新分配设备或清空队列。$($_.Exception.Message)"
} finally { $identity = $null; $settings = $null }
