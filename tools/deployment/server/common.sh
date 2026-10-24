# shellcheck shell=bash
# 公共函数：程序目录采用 releases/<版本> + current 符号链接，升级与回滚只切换链接，数据目录独立。
set -euo pipefail
PREFIX="${SCADA_PREFIX:-/opt/scada}"
DATA="${SCADA_DATA:-/var/lib/scada}"
ETC="${SCADA_ETC:-/etc/scada}"
SERVICE="${SCADA_SERVICE:-scada}"
RUN_USER="${SCADA_USER:-scada}"
PORT="${SCADA_PORT:-5080}"

die() { echo "错误：$*" >&2; exit 1; }
need_root() { [ "$(id -u)" -eq 0 ] || die "需要 root 权限"; }
current_release() { readlink -f "$PREFIX/current" 2>/dev/null || true; }
wait_healthy() {
  local i
  for i in $(seq 1 60); do
    if curl -fsS "http://127.0.0.1:$PORT/health" >/dev/null 2>&1; then return 0; fi
    sleep 2
  done
  return 1
}
backup_before_change() {
  # 变更前做一次离线一致的配置库拷贝（服务已停止），回滚时可用。
  local stamp; stamp="$(date +%Y%m%d-%H%M%S)"
  mkdir -p "$DATA/upgrade-snapshots"
  if [ -f "$DATA/scada.db" ]; then
    cp -a "$DATA/scada.db" "$DATA/upgrade-snapshots/scada-$stamp.db"
    echo "$DATA/upgrade-snapshots/scada-$stamp.db"
  fi
}
