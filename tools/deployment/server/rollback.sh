#!/usr/bin/env bash
# 用法：sudo ./rollback.sh [--restore-db]
# 切回 upgrade.sh 记录的上一版本。新版本若已迁移配置库结构，必须带 --restore-db 用升级前快照覆盖（快照之后的配置修改会丢失）。
# 历史数据文件按天分片、格式向后兼容，不回滚。
source "$(dirname "$0")/common.sh"
need_root
PREVIOUS="$(cat "$PREFIX/previous-release" 2>/dev/null || true)"
[ -n "$PREVIOUS" ] && [ -d "$PREVIOUS" ] || die "没有可回滚的上一版本"
systemctl stop "$SERVICE"
if [ "${1:-}" = "--restore-db" ]; then
  SNAPSHOT="$(cat "$PREFIX/previous-snapshot" 2>/dev/null || true)"
  [ -n "$SNAPSHOT" ] && [ -f "$SNAPSHOT" ] || die "没有升级前快照"
  cp -a "$DATA/scada.db" "$DATA/upgrade-snapshots/failed-$(date +%Y%m%d-%H%M%S).db"
  rm -f "$DATA/scada.db-wal" "$DATA/scada.db-shm"
  cp -a "$SNAPSHOT" "$DATA/scada.db"; chown "$RUN_USER:$RUN_USER" "$DATA/scada.db"
fi
ln -sfn "$PREVIOUS" "$PREFIX/current"
systemctl start "$SERVICE"
wait_healthy || die "回滚后服务仍未就绪，查看 journalctl -u $SERVICE"
echo "已回滚到 $PREVIOUS"
