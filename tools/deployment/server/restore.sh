#!/usr/bin/env bash
# 用法：sudo ./restore.sh --backup <full_*.zip|full_*.zip.enc> [--password-env SCADA_BACKUP_PASSWORD]
# 在停机窗口内用完整备份包切换数据目录：停服务 → 还原到空暂存 → 校验 → 切换 → 健康检查。
# 失败则切回切换前的数据目录并尝试拉起服务。绝不把备份直接解到正在运行的数据目录。
# 完整备份包与维护任务留下的 scada_*.db 配置库拷贝不是同一类东西，本脚本只接受完整包。
# 完整备份含报表归档；还原时按新的 ReportsRoot 重写相对路径并校验 SHA256，缺失或哈希不符会拒绝还原。
set -euo pipefail
source "$(dirname "$0")/common.sh"
need_root

BACKUP=""
PASSWORD_ENV="SCADA_BACKUP_PASSWORD"
while [ $# -gt 0 ]; do
  case "$1" in
    --backup) BACKUP="${2:-}"; shift 2 ;;
    --password-env) PASSWORD_ENV="${2:-}"; shift 2 ;;
    *) die "无法识别的参数：$1" ;;
  esac
done
[ -n "$BACKUP" ] && [ -f "$BACKUP" ] || die "请用 --backup 指定完整备份包"

TOOL="$PREFIX/current/tools/Scada.BackupTool"
if [ ! -x "$TOOL" ]; then
  TOOL="$PREFIX/current/Scada.BackupTool"
fi
[ -x "$TOOL" ] || die "找不到 BackupTool（应随绿色包发布到 current/tools/Scada.BackupTool）"

stamp="$(date +%Y%m%d-%H%M%S)"
parent="$(dirname "$DATA")"
staging="$parent/scada-restore-staging-$stamp"
kept="$parent/scada-data-before-restore-$stamp"
failed="$parent/scada-restore-failed-$stamp"
mkdir -p "$staging"

cleanup_staging() {
  if [ -d "$staging" ] && [ ! -f "$DATA/scada.db" ]; then
    echo "未切换成功，暂存目录仍在：$staging"
  elif [ -d "$staging" ] && [ "$staging" != "$DATA" ]; then
    rm -rf "$staging" || true
  fi
}

run_tool() {
  if [ -n "${!PASSWORD_ENV-}" ]; then
    SCADA_BACKUP_PASSWORD="${!PASSWORD_ENV}" "$TOOL" "$@"
  else
    "$TOOL" "$@"
  fi
}

echo "校验备份 $BACKUP"
run_tool verify --backup "$BACKUP"

systemctl stop "$SERVICE" || true

echo "还原到空目录 $staging"
run_tool restore --backup "$BACKUP" --target "$staging"
[ -f "$staging/scada.db" ] || die "还原结果缺少 scada.db，未切换现场数据目录"

if [ -d "$DATA" ]; then
  if [ -f "$DATA/appsettings.Site.json" ]; then cp -a "$DATA/appsettings.Site.json" "$staging/appsettings.Site.json"; fi
  if [ -d "$DATA/logs" ]; then cp -a "$DATA/logs" "$staging/logs"; fi
fi

swap_back() {
  systemctl stop "$SERVICE" || true
  if [ -d "$DATA" ] && [ -d "$kept" ]; then
    rm -rf "$failed" || true
    mv "$DATA" "$failed" || true
    mv "$kept" "$DATA" || true
  elif [ ! -d "$DATA" ] && [ -d "$kept" ]; then
    mv "$kept" "$DATA" || true
  fi
  systemctl start "$SERVICE" || true
}

if ! mv "$DATA" "$kept"; then
  cleanup_staging
  die "无法把当前数据目录改名为保留副本"
fi
if ! mv "$staging" "$DATA"; then
  swap_back
  die "无法把暂存目录切换为数据目录，已尝试切回"
fi
staging=""

if ! systemctl start "$SERVICE" || ! wait_healthy; then
  echo "启动或健康检查失败，正在切回 $kept"
  swap_back
  die "还原后服务未就绪；切换前的数据已切回。失败现场在 $failed（若已创建）"
fi

echo "还原成功。切换前的数据目录保留在：$kept"
echo "这是完整备份包的整站恢复，不是维护任务留下的 scada_*.db 配置库拷贝。"
