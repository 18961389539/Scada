#Requires -Version 7.2
param([Parameter(Mandatory)][string] $ProgramPath)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$program = [IO.Path]::GetFullPath($ProgramPath)
. (Join-Path $program 'GatewayService.Common.ps1')
Assert-GatewayWindows
$program = Get-GatewayDirectory $program
$root = Join-Path ([IO.Path]::GetTempPath()) ('scada-deployment-test-' + [guid]::NewGuid().ToString('N'))
$data = Join-Path $root 'data with spaces'
$identityFile = Join-Path $root 'identity.json'
$serviceName = 'ScadaGatewayTest_' + [guid]::NewGuid().ToString('N')
$token = 'deployment-test-token-abcdefghijklmnopqrstuvwxyz123456'
$installer = Join-Path $program 'Install-GatewayService.ps1'
$process = $null
function Assert-Deployment($Condition, [string] $Message) { if (-not $Condition) { throw $Message } }
function Assert-Rejected([scriptblock] $Action) {
    $rejected = $false
    try { & $Action | Out-Null } catch { $rejected = $true }
    Assert-Deployment $rejected '应拒绝无效部署请求。'
}
function Start-Probe([string] $ConfigPath, [switch] $ValidateOnly) {
    $start = [Diagnostics.ProcessStartInfo]::new((Join-Path $program 'Scada.Gateway.Host.exe'))
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
    $start.ArgumentList.Add("--GatewayConfig=$ConfigPath")
    if ($ValidateOnly) { $start.ArgumentList.Add('--ValidateConfig'); $start.ArgumentList.Add('true') }
    $start.Environment['GatewayAgent__GatewayId'] = '99'
    $start.Environment['GatewayAgent__Token'] = 'x' * 43
    $start.Environment['GatewayAgent__ServerUrl'] = 'https://127.0.0.1:1/'
    $start.Environment['GatewayAgent__DataRoot'] = (Join-Path $root 'wrong ambient directory')
    $start.Environment['ValidateConfig'] = 'true'
    $child = [Diagnostics.Process]::Start($start)
    return @{ Child = $child; Output = $child.StandardOutput.ReadToEndAsync(); Error = $child.StandardError.ReadToEndAsync() }
}
function Stop-Probe($Probe) {
    if (-not $Probe.Child.HasExited) { $Probe.Child.Kill($true) }
    if (-not $Probe.Child.WaitForExit(30000)) { throw '测试进程未退出。' }
    Assert-Deployment (-not ($Probe.Output.Result + $Probe.Error.Result).Contains($token)) '进程输出不能包含网关密钥。'
    $Probe.Child.Dispose()
}
function Wait-Status($Probe) {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    while ($watch.Elapsed.TotalSeconds -lt 15 -and -not $Probe.Child.HasExited) {
        $path = Join-Path $data 'status.json'
        if (Test-Path -LiteralPath $path) { return (Get-Content -LiteralPath $path -Raw | ConvertFrom-Json) }
        Start-Sleep -Milliseconds 100
    }
    throw '配置文件模式的实际网关未生成诊断快照。'
}
try {
    New-Item -ItemType Directory -Path $root | Out-Null
    @{ gatewayId = 7; serverUrl = 'https://127.0.0.1:1/'; token = $token } | ConvertTo-Json | Set-Content -LiteralPath $identityFile -Encoding utf8
    $before = @(Get-ChildItem -LiteralPath $program -Force -Recurse).Count
    $preview = & $installer -ProgramPath $program -DataPath $data -IdentityFile $identityFile -ServiceName $serviceName -WhatIf
    Assert-Deployment ($preview.GatewayId -eq 7 -and $preview.ServiceName -eq $serviceName) '预演应返回核对后的部署信息。'
    Assert-Deployment (-not ($preview | ConvertTo-Json).Contains($token)) '预演不能输出密钥。'
    Assert-Deployment (-not (Test-Path -LiteralPath $data)) '预演不能创建数据目录。'
    Assert-Deployment (@(Get-ChildItem -LiteralPath $program -Force -Recurse).Count -eq $before) '预演不能改写发布目录。'
    Assert-Deployment (-not (Get-Service -Name $serviceName -ErrorAction SilentlyContinue)) '预演不能注册系统服务。'
    $reserved = Join-Path $root 'reservation.json'
    Write-GatewayNewFile $reserved 'first installation'
    Assert-Rejected { Write-GatewayNewFile $reserved 'another installation' }
    Assert-Deployment ((Get-Content -LiteralPath $reserved -Raw) -eq 'first installation') '并发或重复安装不能覆盖已有预留文件。'
    $serviceSid = Get-GatewayServiceSid $serviceName
    Assert-Deployment ($serviceSid.Value.StartsWith('S-1-5-80-')) '只读 SID 计算失败。'
    $aclDirectory = Join-Path $root 'acl validation'; New-Item -ItemType Directory -Path $aclDirectory | Out-Null
    $originalAcl = Get-Acl -LiteralPath $aclDirectory
    try {
        foreach ($writable in @($false, $true)) {
            Set-GatewayAcl $aclDirectory $serviceSid $writable
            $acl = Get-Acl -LiteralPath $aclDirectory
            $rules = @($acl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier]))
            Assert-Deployment ($acl.AreAccessRulesProtected -and $rules.Count -eq 3) '部署 ACL 须关闭继承并仅含三个明确主体。'
            $serviceRule = @($rules | Where-Object { $_.IdentityReference.Value -eq $serviceSid.Value })
            $expectedRights = if ($writable) { [Security.AccessControl.FileSystemRights]::Modify } else { [Security.AccessControl.FileSystemRights]::ReadAndExecute }
            Assert-Deployment ($serviceRule.Count -eq 1 -and ($serviceRule[0].FileSystemRights -band $expectedRights) -eq $expectedRights) '服务权限不符合程序只读/数据可修改的分工。'
            if (-not $writable) { Assert-Deployment (($serviceRule[0].FileSystemRights -band [Security.AccessControl.FileSystemRights]::WriteData) -eq 0) '服务不应能修改程序文件。' }
        }
    } finally {
        $restoreAcl = [Security.AccessControl.DirectorySecurity]::new()
        $restoreAcl.SetSecurityDescriptorSddlForm($originalAcl.GetSecurityDescriptorSddlForm('Access'), 'Access')
        [IO.FileSystemAclExtensions]::SetAccessControl([IO.DirectoryInfo]::new($aclDirectory), $restoreAcl)
    }
    Assert-Rejected { & $installer -ProgramPath $program -DataPath $program -IdentityFile $identityFile -WhatIf }
    New-Item -ItemType Directory -Path $data | Out-Null
    [IO.File]::WriteAllText((Join-Path $data 'keep.txt'), 'do not adopt this directory')
    Assert-Rejected { & $installer -ProgramPath $program -DataPath $data -IdentityFile $identityFile -WhatIf }
    $uninstallProgram = Join-Path $root 'uninstall validation'; New-Item -ItemType Directory -Path $uninstallProgram | Out-Null
    $uninstallConfig = Join-Path $uninstallProgram 'gateway-service.json'
    @{ product = 'Scada.Gateway'; format = 1; serviceName = $serviceName; programPath = $uninstallProgram; dataPath = $data; configPath = $uninstallConfig } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $uninstallProgram 'gateway-installation.json') -Encoding utf8
    $fakeService = [pscustomobject]@{ PathName = (Get-GatewayBinaryPath $uninstallProgram $uninstallConfig); StartName = "NT SERVICE\$serviceName"; State = 'Running'; ProcessId = 123 }
    function Get-CimInstance { param([string] $ClassName, [string] $Filter) return $fakeService }
    try {
        & (Join-Path $program 'Uninstall-GatewayService.ps1') -ProgramPath $uninstallProgram -WhatIf
        $fakeService.PathName = 'C:\OtherService.exe'
        Assert-Rejected { & (Join-Path $program 'Uninstall-GatewayService.ps1') -ProgramPath $uninstallProgram -WhatIf }
    } finally { Remove-Item Function:\Get-CimInstance }
    Remove-Item -LiteralPath (Join-Path $data 'keep.txt')
    @{ gatewayId = 7; serverUrl = 'http://remote.example/'; token = $token } | ConvertTo-Json | Set-Content -LiteralPath $identityFile -Encoding utf8
    Assert-Rejected { & $installer -ProgramPath $program -DataPath $data -IdentityFile $identityFile -WhatIf }
    $configPath = Join-Path $root 'service config.json'
    @{ GatewayAgent = @{ ServiceName = $serviceName; GatewayId = 7; ServerUrl = 'https://127.0.0.1:1/'; Token = $token; DataRoot = $data; HttpTimeoutMs = 500 } } |
        ConvertTo-Json | Set-Content -LiteralPath $configPath -Encoding utf8
    $process = Start-Probe $configPath -ValidateOnly
    Assert-Deployment ($process.Child.WaitForExit(10000) -and $process.Child.ExitCode -eq 0) '配置预检应成功返回。'
    Stop-Probe $process; $process = $null
    Assert-Deployment (@(Get-ChildItem -LiteralPath $data -Force).Count -eq 0) '配置预检不能初始化数据库或采集。'
    $process = Start-Probe $configPath
    $first = Wait-Status $process
    Assert-Deployment ($first.gatewayId -eq 7 -and $first.state -eq 'Connecting') '实际进程应读取部署文件并等待中心，不能采用环境中的其他身份。'
    Stop-Probe $process; $process = $null
    Remove-Item -LiteralPath (Join-Path $data 'status.json')
    $process = Start-Probe $configPath
    $second = Wait-Status $process
    Assert-Deployment ($second.streamId -eq $first.streamId) '同一安装重启应保留持久数据流。'
    Assert-Deployment (-not (Test-Path -LiteralPath (Join-Path $root 'wrong ambient directory'))) '环境不能改投数据目录。'
    Stop-Probe $process; $process = $null
    @{ GatewayAgent = @{ GatewayId = $token; Token = $token; ServerUrl = 'https://127.0.0.1:1/' } } | ConvertTo-Json | Set-Content -LiteralPath $configPath -Encoding utf8
    $process = Start-Probe $configPath
    Assert-Deployment ($process.Child.WaitForExit(10000) -and $process.Child.ExitCode -eq 1) '类型错误的服务配置须失败，不能回退到其他环境身份。'
    Stop-Probe $process; $process = $null
    Assert-Deployment (-not (Test-Path -LiteralPath (Join-Path $root 'wrong ambient directory'))) '失败配置不能启动其他身份。'
    Write-Output '部署预演、ACL、输入拒绝、实际进程配置隔离、错误输出脱敏及重启数据流保留均通过；未安装 SCM 服务。'
} finally {
    if ($process) { Stop-Probe $process }
    $resolved = [IO.Path]::GetFullPath($root)
    $temp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if ($resolved.StartsWith($temp, [StringComparison]::OrdinalIgnoreCase) -and [IO.Path]::GetFileName($resolved).StartsWith('scada-deployment-test-')) {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
