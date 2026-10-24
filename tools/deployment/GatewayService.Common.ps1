#Requires -Version 7.2
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-GatewayWindows {
    if (-not $IsWindows) { throw '此部署工具仅支持 Windows。' }
}

function Assert-GatewayAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    if (-not ([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw '实际服务安装/卸载须在管理员 PowerShell 中执行；可先用 -WhatIf 预演。'
    }
}

function Get-GatewayDirectory([string] $Path) {
    if (-not [IO.Path]::IsPathFullyQualified($Path) -or $Path -match '[\x00-\x1f"]') { throw '部署目录须为有效绝对路径。' }
    $full = [IO.Path]::GetFullPath($Path).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $root = [IO.Path]::GetPathRoot($full)
    if ($full -eq $root.TrimEnd([IO.Path]::DirectorySeparatorChar) -or $full.StartsWith('\\')) { throw '部署目录不能是磁盘根目录或网络共享。' }
    $drive = [IO.DriveInfo]::new($root)
    if ($drive.DriveType -ne [IO.DriveType]::Fixed -or $drive.DriveFormat -ne 'NTFS') { throw '部署目录须位于本机固定 NTFS 卷。' }
    $cursor = $full
    while ($cursor) {
        if (Test-Path -LiteralPath $cursor) {
            if ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw '部署目录及其父目录不能包含重解析点。' }
        }
        $cursor = [IO.Path]::GetDirectoryName($cursor)
    }
    return $full
}

function Assert-GatewaySeparateDirectories([string] $ProgramPath, [string] $DataPath) {
    $separator = [IO.Path]::DirectorySeparatorChar
    if ($ProgramPath.Equals($DataPath, [StringComparison]::OrdinalIgnoreCase) -or
        $ProgramPath.StartsWith($DataPath + $separator, [StringComparison]::OrdinalIgnoreCase) -or
        $DataPath.StartsWith($ProgramPath + $separator, [StringComparison]::OrdinalIgnoreCase)) {
        throw '发布目录和数据目录须相互独立，不能相同或嵌套。'
    }
}

function Invoke-GatewaySc([string[]] $Arguments) {
    # Preserve embedded quotes in binPath on PowerShell versions with legacy
    # native argument passing. No credential is included in any argument.
    $start = [Diagnostics.ProcessStartInfo]::new((Join-Path $env:WINDIR 'System32/sc.exe'))
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($start)
    try {
        $output = $process.StandardOutput.ReadToEndAsync(); $errorOutput = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(15000)) { $process.Kill(); throw 'SCM 工具响应超时；操作可能已提交，请查询实际状态，不要直接重试。' }
        $result = $output.Result + $errorOutput.Result
        if ($process.ExitCode -ne 0) { throw "SCM 操作失败（$($Arguments[0])，退出码 $($process.ExitCode)）：$result" }
        return $result
    } finally { $process.Dispose() }
}

function Get-GatewayServiceSid([string] $Name) {
    $text = (Invoke-GatewaySc -Arguments @('showsid', $Name)) -join ' '
    $sid = [regex]::Match($text, 'S-1-5-80-(?:\d+-){4}\d+').Value
    if (-not $sid) { throw '无法计算网关服务 SID。' }
    return [Security.Principal.SecurityIdentifier]::new($sid)
}

function Set-GatewayAcl([string] $Path, [Security.Principal.SecurityIdentifier] $ServiceSid, [bool] $Writable, [switch] $AdministratorsOwn) {
    $item = Get-Item -LiteralPath $Path -Force
    $acl = if ($item.PSIsContainer) { [Security.AccessControl.DirectorySecurity]::new() } else { [Security.AccessControl.FileSecurity]::new() }
    $acl.SetAccessRuleProtection($true, $false)
    if ($AdministratorsOwn) { $acl.SetOwner([Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')) }
    $inherit = if ($item.PSIsContainer) { [Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit' } else { [Security.AccessControl.InheritanceFlags]::None }
    foreach ($sid in @('S-1-5-18', 'S-1-5-32-544')) {
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new($sid), 'FullControl', $inherit, 'None', 'Allow'))
    }
    $rights = if ($Writable) { 'Modify' } else { 'ReadAndExecute' }
    $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($ServiceSid, $rights, $inherit, 'None', 'Allow'))
    # Apply the DACL and only the explicitly requested owner change; leave group intact.
    if ($item.PSIsContainer) { [IO.FileSystemAclExtensions]::SetAccessControl([IO.DirectoryInfo]::new($Path), $acl) }
    else { [IO.FileSystemAclExtensions]::SetAccessControl([IO.FileInfo]::new($Path), $acl) }
}

function Get-GatewayBinaryPath([string] $ProgramPath, [string] $ConfigPath) {
    return '"{0}" --GatewayConfig="{1}"' -f (Join-Path $ProgramPath 'Scada.Gateway.Host.exe'), $ConfigPath
}

function Write-GatewayNewFile([string] $Path, [string] $Content) {
    # Atomic reservation: a second installer must not overwrite the first one's identity.
    $stream = [IO.FileStream]::new($Path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
    try { $bytes = [Text.UTF8Encoding]::new($false).GetBytes($Content); $stream.Write($bytes, 0, $bytes.Length); $stream.Flush($true) }
    finally { $stream.Dispose() }
}

function Read-GatewayInstallation([string] $ProgramPath) {
    $manifestPath = Join-Path $ProgramPath 'gateway-installation.json'
    try {
        if ((Get-Item -LiteralPath $manifestPath).Length -gt 16384) { throw 'too large' }
        $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
        if ($manifest.product -ne 'Scada.Gateway' -or $manifest.format -ne 1 -or $manifest.serviceName -notmatch '^[A-Za-z][A-Za-z0-9_-]{0,79}$' -or
            $manifest.programPath -ne $ProgramPath -or $manifest.configPath -ne (Join-Path $ProgramPath 'gateway-service.json')) { throw 'invalid manifest' }
        $dataPath = Get-GatewayDirectory $manifest.dataPath
        Assert-GatewaySeparateDirectories $ProgramPath $dataPath
        return $manifest
    } catch { throw '部署清单缺失或无效；拒绝操作无法确认归属的系统服务。' }
}

# 从 SCM 读取服务实际指向的发布目录；升级与回滚都以它为准，而不是让操作者口头声明。
function Get-GatewayServiceProgramPath([string] $ServiceName) {
    $service = Get-CimInstance -ClassName Win32_Service -Filter "Name='$ServiceName'" -ErrorAction SilentlyContinue
    if (-not $service) { throw "未找到服务 $ServiceName。" }
    $match = [regex]::Match([string]$service.PathName, '^"([^"]+\.exe)"')
    if (-not $match.Success) { throw "服务 $ServiceName 的启动路径不是带引号的网关程序，拒绝改动。" }
    return Get-GatewayDirectory ([IO.Path]::GetDirectoryName($match.Groups[1].Value))
}

function Set-GatewayServiceBinary([string] $ServiceName, [string] $ProgramPath, [string] $ConfigPath) {
    Invoke-GatewaySc -Arguments @('config', $ServiceName, 'binPath=', (Get-GatewayBinaryPath $ProgramPath $ConfigPath)) | Out-Null
}

function Stop-GatewayServiceIfRunning([string] $ServiceName) {
    $service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if (-not $service) { throw "未找到服务 $ServiceName。" }
    if ($service.Status -eq 'Stopped') { return $false }
    Stop-Service -Name $ServiceName -Force
    $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(60))
    return $true
}

function Start-GatewayServiceAndWait([string] $ServiceName) {
    Start-Service -Name $ServiceName
    (Get-Service -Name $ServiceName).WaitForStatus('Running', [TimeSpan]::FromSeconds(45))
}

# 用网关自己写出的诊断快照证明"确实是这个身份在跑"，而不是只看服务状态。
function Wait-GatewayStatus([string] $DataPath, [int] $GatewayId, [int] $TimeoutSeconds = 45) {
    $statusPath = Join-Path $DataPath 'status.json'
    $watch = [Diagnostics.Stopwatch]::StartNew()
    while ($watch.Elapsed.TotalSeconds -lt $TimeoutSeconds) {
        try {
            if (Test-Path -LiteralPath $statusPath) {
                $status = Get-Content -LiteralPath $statusPath -Raw | ConvertFrom-Json
                if ([int]$status.gatewayId -eq $GatewayId) { return $status }
            }
        } catch { }
        Start-Sleep -Milliseconds 250
    }
    throw "服务启动后 $TimeoutSeconds 秒内未在数据目录写出匹配的 status.json，判定升级/回滚未真正生效。"
}

function Get-GatewayPackageFingerprint([string] $ProgramPath) {
    $fingerprint = [ordered]@{ }
    foreach ($name in @('Scada.Gateway.Host.exe', 'Scada.Gateway.Host.dll')) {
        $path = Join-Path $ProgramPath $name
        $fingerprint[$name] = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
    }
    return $fingerprint
}

function Copy-GatewayDirectory([string] $Source, [string] $Target) {
    New-Item -ItemType Directory -Path $Target -Force | Out-Null
    foreach ($item in @(Get-ChildItem -LiteralPath $Source -Force)) {
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw '快照目录不能包含重解析点。' }
        Copy-Item -LiteralPath $item.FullName -Destination $Target -Recurse -Force
    }
}
