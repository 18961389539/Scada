#requires -Version 7.2
<#
.SYNOPSIS 在停机窗口内，用完整备份包（full_*.zip / full_*.zip.enc）切换现场数据目录。
.DESCRIPTION
  流程：停止服务 → 把备份还原到空的暂存目录并校验 → 把当前数据目录改名为保留副本 →
  把暂存目录切成数据目录（保留 appsettings.Site.json 与 logs）→ 启动并做健康检查。
  任一步失败则切回保留副本并尝试重新拉起服务。
  本脚本只写空暂存目录，绝不把备份直接解到正在使用的数据目录，也绝不在服务仍运行时覆盖库文件。
  完整备份包与维护任务留下的配置库拷贝（scada_*.db）不是同一类东西：后者只有配置库，不能用本脚本恢复整站。
  完整备份含报表归档；还原时按新的 ReportsRoot 重写相对路径并校验 SHA256，缺失或哈希不符会拒绝还原，不会把记录标成已清理，也不重签旧文件。
.EXAMPLE
  pwsh -File Restore-ScadaService.ps1 -Backup 'D:\ScadaBackups\full_example.zip.enc' -InstallRoot 'C:\Program Files\Scada' -DataPath D:\ScadaData
#>
[CmdletBinding(SupportsShouldProcess)]
param(
  [Parameter(Mandatory)][string]$Backup,
  [Parameter(Mandatory)][string]$InstallRoot,
  [Parameter(Mandatory)][string]$DataPath,
  [string]$ServiceName = 'ScadaServer',
  [string]$BackupTool,
  [string]$Password,
  [int]$Port = 0,
  [switch]$PromptPassword
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ScadaService.Common.ps1')
Assert-Admin

$backupPath = (Resolve-Path -LiteralPath $Backup).Path
if (-not (Test-Path -LiteralPath $InstallRoot)) { throw "安装目录不存在：$InstallRoot" }
$dataPath = [System.IO.Path]::GetFullPath($DataPath)
if (-not $BackupTool) {
  $candidate = Join-Path $InstallRoot 'current\tools\Scada.BackupTool.exe'
  if (Test-Path -LiteralPath $candidate) { $BackupTool = $candidate }
  else { $BackupTool = Join-Path $InstallRoot 'current\Scada.BackupTool.exe' }
}
if (-not (Test-Path -LiteralPath $BackupTool)) {
  throw "找不到 BackupTool：$BackupTool。请使用随绿色包发布的 tools\Scada.BackupTool.exe。"
}

$metaFile = Join-Path $InstallRoot 'service.json'
if ($Port -le 0 -and (Test-Path -LiteralPath $metaFile)) {
  $meta = Get-Content -LiteralPath $metaFile -Raw | ConvertFrom-Json
  if ($meta.Port) { $Port = [int]$meta.Port }
  if ($meta.ServiceName) { $ServiceName = [string]$meta.ServiceName }
}
if ($Port -le 0) { $Port = 5080 }

$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if (-not $service) { throw "未找到服务 $ServiceName，请先用 Install-ScadaService.ps1 安装。" }

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$parent = Split-Path -Parent $dataPath
if (-not $parent) { throw "无法解析数据目录的父路径：$dataPath" }
$staging = Join-Path $parent "scada-restore-staging-$stamp"
$kept = Join-Path $parent "scada-data-before-restore-$stamp"
$failed = Join-Path $parent "scada-restore-failed-$stamp"
$wasRunning = $service.Status -eq 'Running'

if (Test-Path -LiteralPath $staging) { throw "暂存目录已存在：$staging" }
New-Item -ItemType Directory -Path $staging | Out-Null

function Invoke-BackupTool {
  param([string[]]$ToolArgs)
  $psi = @{
    FilePath = $BackupTool
    ArgumentList = $ToolArgs
    WorkingDirectory = (Split-Path -Parent $BackupTool)
    NoNewWindow = $true
    PassThru = $true
    Wait = $true
  }
  $proc = Start-Process @psi
  if ($proc.ExitCode -ne 0) { throw "BackupTool 退出码 $($proc.ExitCode)：$($ToolArgs -join ' ')" }
}

$toolArgs = @()
if ($PromptPassword) { $toolArgs += '--prompt-password' }
elseif ($Password) { $env:SCADA_BACKUP_PASSWORD = $Password }

try {
  Write-Host "校验备份 $backupPath"
  Invoke-BackupTool -ToolArgs (@('verify', '--backup', $backupPath) + $toolArgs)

  if ($wasRunning) {
    if ($PSCmdlet.ShouldProcess($ServiceName, '停止服务后再还原')) {
      Write-Host "停止服务 $ServiceName"
      Stop-Service -Name $ServiceName -Force
      $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(60))
    }
  } else {
    Write-Host "服务已停止，继续还原。"
  }

  # 再次确认没有进程占用数据目录。
  $exe = Join-Path $InstallRoot 'current\Scada.Server.exe'
  $still = Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path -eq $exe }
  if ($still) { throw "服务已报停止，但仍有 Scada.Server 进程占用程序目录，已中止还原。" }

  Write-Host "还原到空目录 $staging"
  Invoke-BackupTool -ToolArgs (@('restore', '--backup', $backupPath, '--target', $staging) + $toolArgs)
  if (-not (Test-Path -LiteralPath (Join-Path $staging 'scada.db'))) {
    throw "还原结果缺少 scada.db，未切换现场数据目录。"
  }

  if (Test-Path -LiteralPath $dataPath) {
    foreach ($keep in @('appsettings.Site.json', 'logs')) {
      $source = Join-Path $dataPath $keep
      $dest = Join-Path $staging $keep
      if (Test-Path -LiteralPath $source) {
        if ((Get-Item -LiteralPath $source).PSIsContainer) {
          Copy-Item -LiteralPath $source -Destination $dest -Recurse -Force
        } else {
          Copy-Item -LiteralPath $source -Destination $dest -Force
        }
      }
    }
  }

  if ($PSCmdlet.ShouldProcess($dataPath, '切换数据目录')) {
    if (Test-Path -LiteralPath $dataPath) {
      Rename-Item -LiteralPath $dataPath -NewName (Split-Path -Leaf $kept)
    }
    Rename-Item -LiteralPath $staging -NewName (Split-Path -Leaf $dataPath)
    $staging = $null
  }

  Start-Service -Name $ServiceName
  Wait-Healthy -Port $Port
  Write-Host "还原成功。切换前的数据目录保留在：$kept"
  Write-Host "这是完整备份包的整站恢复，不是维护任务留下的 scada_*.db 配置库拷贝。"
}
catch {
  Write-Error $_
  Write-Host "正在尝试切回原数据目录并拉起服务…"
  try { if ((Get-Service -Name $ServiceName).Status -eq 'Running') { Stop-Service -Name $ServiceName -Force } } catch { }
  if ($staging -and (Test-Path -LiteralPath $dataPath) -and (Test-Path -LiteralPath $kept)) {
    if (Test-Path -LiteralPath $failed) { Remove-Item -LiteralPath $failed -Recurse -Force -ErrorAction SilentlyContinue }
    Rename-Item -LiteralPath $dataPath -NewName (Split-Path -Leaf $failed) -ErrorAction SilentlyContinue
    Rename-Item -LiteralPath $kept -NewName (Split-Path -Leaf $dataPath) -ErrorAction SilentlyContinue
  } elseif ($staging -and -not (Test-Path -LiteralPath $dataPath) -and (Test-Path -LiteralPath $kept)) {
    Rename-Item -LiteralPath $kept -NewName (Split-Path -Leaf $dataPath) -ErrorAction SilentlyContinue
  }
  try { Start-Service -Name $ServiceName; Wait-Healthy -Port $Port } catch {
    Write-Warning "切回后服务仍未就绪，请检查数据目录 $dataPath 与 journal / 日志。"
  }
  if ($staging -and (Test-Path -LiteralPath $staging)) {
    Write-Host "未切换成功，暂存目录仍在：$staging"
  }
  throw
}
finally {
  if ($Password) { Remove-Item Env:SCADA_BACKUP_PASSWORD -ErrorAction SilentlyContinue }
}
