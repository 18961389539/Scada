#requires -Version 7.2
# 桌面客户端部署公共函数：版本目录、计划任务、快捷方式与安装清单。
function Assert-Admin {
  $principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
  if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw '需要管理员终端' }
}

function Set-CurrentRelease([string]$InstallRoot, [string]$Release) {
  $current = Join-Path $InstallRoot 'current'
  if (Test-Path $current) { (Get-Item $current).Delete() }
  New-Item -ItemType Junction -Path $current -Target $Release | Out-Null
}

function Get-CurrentRelease([string]$InstallRoot) {
  $current = Join-Path $InstallRoot 'current'
  if (-not (Test-Path $current)) { return $null }
  return (Get-Item $current).Target | Select-Object -First 1
}

function Stop-DesktopClient([string]$TaskName = 'ScadaDesktop') {
  $task = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
  if ($task) { try { Stop-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue } catch { } }
  Get-Process -Name 'Scada.Desktop' -ErrorAction SilentlyContinue | ForEach-Object {
    try { $_.Kill($true); $_.WaitForExit(5000) } catch { }
  }
  Start-Sleep -Milliseconds 300
}

function Register-DesktopTask([string]$ExePath, [string]$TaskName, [string]$Scope) {
  $action = New-ScheduledTaskAction -Execute $ExePath -Argument '--supervise' -WorkingDirectory (Split-Path $ExePath)
  $settings = New-ScheduledTaskSettingsSet -MultipleInstances IgnoreNew -RestartCount 999 `
    -RestartInterval (New-TimeSpan -Minutes 1) -ExecutionTimeLimit ([TimeSpan]::Zero) -StartWhenAvailable
  if ($Scope -eq 'AllUsers') {
    $trigger = New-ScheduledTaskTrigger -AtLogOn
    $principal = New-ScheduledTaskPrincipal -GroupId 'Users' -LogonType Group -RunLevel Limited
  } else {
    $user = "$env:USERDOMAIN\$env:USERNAME"
    $trigger = New-ScheduledTaskTrigger -AtLogOn -User $user
    $principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Limited
  }
  Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Settings $settings `
    -Principal $principal -Force | Out-Null
}

function Remove-DesktopTask([string]$TaskName = 'ScadaDesktop') {
  $task = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
  if ($task) { Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false }
}

function New-DesktopShortcut([string]$ShortcutPath, [string]$ExePath, [string]$WorkingDirectory) {
  $directory = Split-Path $ShortcutPath -Parent
  New-Item -ItemType Directory -Force -Path $directory | Out-Null
  $shell = New-Object -ComObject WScript.Shell
  $shortcut = $shell.CreateShortcut($ShortcutPath)
  $shortcut.TargetPath = $ExePath
  $shortcut.WorkingDirectory = $WorkingDirectory
  $shortcut.IconLocation = $ExePath
  $shortcut.Description = 'SCADA 桌面客户端'
  $shortcut.Save()
}

function Write-InstallManifest([string]$Directory, [string]$Version, [string]$Scope, [string]$InstallRoot, [bool]$ManagedAutostart) {
  New-Item -ItemType Directory -Force -Path $Directory | Out-Null
  $autostartState = if ($ManagedAutostart) { 'task' } else { 'none' }
  @{
    version = $Version
    scope = $Scope
    installRoot = $InstallRoot
    managedAutostart = $autostartState
  } | ConvertTo-Json -Depth 4 | Set-Content -Path (Join-Path $Directory 'install.json') -Encoding utf8
}

function Resolve-InstallRoot([string]$InstallRoot, [string]$Scope) {
  if ($InstallRoot) { return [System.IO.Path]::GetFullPath($InstallRoot) }
  if ($Scope -eq 'AllUsers') { return (Join-Path $env:ProgramFiles 'Scada Desktop') }
  return (Join-Path $env:LOCALAPPDATA 'Programs\Scada Desktop')
}
