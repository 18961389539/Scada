#!/usr/bin/env bash
# 用法：sudo ./upgrade.sh <新发布目录> [版本号]
# 步骤：解压新版本到 releases/<版本> → 停服务 → 配置库快照 → 切换 current → 启动 → 健康检查；失败自动回滚。
# 主备部署时先升级备机，确认复制正常后手动切换，再升级原主机（见 docs/deployment.md）。
source "$(dirname "$0")/common.sh"
need_root
SRC="${1:?用法：upgrade.sh <发布目录> [版本号]}"; VERSION="${2:-$(date +%Y%m%d%H%M%S)}"
[ -f "$SRC/Scada.Server.dll" ] || die "$SRC 不是服务端发布目录"
PREVIOUS="$(current_release)"; [ -n "$PREVIOUS" ] || die "未安装，请使用 install.sh"
TARGET="$PREFIX/releases/$VERSION"; [ -e "$TARGET" ] && die "$TARGET 已存在"

mkdir -p "$TARGET"; cp -a "$SRC/." "$TARGET/"
systemctl stop "$SERVICE"
SNAPSHOT="$(backup_before_change)"
echo "$PREVIOUS" > "$PREFIX/previous-release"
[ -n "$SNAPSHOT" ] && echo "$SNAPSHOT" > "$PREFIX/previous-snapshot"
ln -sfn "$TARGET" "$PREFIX/current"
systemctl start "$SERVICE"
if wait_healthy; then
  echo "已升级到 $VERSION（上一版本 $PREVIOUS，配置库快照 ${SNAPSHOT:-无}）"
else
  echo "新版本未就绪，自动回滚" >&2
  "$(dirname "$0")/rollback.sh" --restore-db
  exit 1
fi
