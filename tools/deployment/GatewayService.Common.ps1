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
