# 公共函数（由 Install/Upgrade/Rollback 脚本点引用）。
function Assert-Admin {
  $principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
  if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw '需要管理员终端' }
}
function Set-CurrentRelease([string]$InstallRoot, [string]$Release) {
  $current = Join-Path $InstallRoot 'current'
  if (Test-Path $current) { (Get-Item $current).Delete() }
  New-Item -ItemType Junction -Path $current -Target $Release | Out-Null
}
function Get-CurrentRelease([string]$InstallRoot) { (Get-Item (Join-Path $InstallRoot 'current')).Target | Select-Object -First 1 }
function Set-RecoveryActions([string]$ServiceName) {
  # 失败后 5 秒、5 秒、30 秒重启；一天后重置计数。非 0 退出码（含看门狗退出码 3）也视为失败。
  sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/5000/restart/30000 | Out-Null
  sc.exe failureflag $ServiceName 1 | Out-Null
}
function Protect-DataDirectory([string]$DataPath, [string]$ServiceName) {
  icacls $DataPath /inheritance:r /grant:r 'SYSTEM:(OI)(CI)F' 'Administrators:(OI)(CI)F' "NT SERVICE\${ServiceName}:(OI)(CI)M" 2>$null | Out-Null
}
function Wait-Healthy([int]$Port) {
  for ($i = 0; $i -lt 60; $i++) {
    try { Invoke-RestMethod "http://127.0.0.1:$Port/health" -TimeoutSec 3 | Out-Null; return } catch { Start-Sleep 2 }
  }
  throw '服务未在 120 秒内就绪，查看数据目录 logs 与事件查看器'
}
function Save-DbSnapshot([string]$DataPath) {
  $db = Join-Path $DataPath 'scada.db'
  if (-not (Test-Path $db)) { return $null }
  $dir = New-Item -ItemType Directory -Force -Path (Join-Path $DataPath 'upgrade-snapshots')
  $target = Join-Path $dir "scada-$(Get-Date -Format 'yyyyMMdd-HHmmss').db"
  Copy-Item $db $target
  return $target
}
