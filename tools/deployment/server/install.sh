#!/usr/bin/env bash
# 用法：sudo ./install.sh <已发布目录> [版本号]
# 发布目录由 `dotnet publish src/Scada.Server -c Release -o <目录>` 生成（含前端 wwwroot）。
source "$(dirname "$0")/common.sh"
need_root
SRC="${1:?用法：install.sh <发布目录> [版本号]}"; VERSION="${2:-$(date +%Y%m%d%H%M%S)}"
[ -f "$SRC/Scada.Server.dll" ] || die "$SRC 不是服务端发布目录"
command -v dotnet >/dev/null || die "未安装 .NET 10 ASP.NET Core Runtime"
[ -e "$PREFIX/current" ] && die "已安装，请使用 upgrade.sh"

id "$RUN_USER" >/dev/null 2>&1 || useradd --system --home-dir "$DATA" --shell /usr/sbin/nologin "$RUN_USER"
mkdir -p "$PREFIX/releases/$VERSION" "$DATA" "$DATA/logs" "$ETC"
cp -a "$SRC/." "$PREFIX/releases/$VERSION/"
ln -sfn "$PREFIX/releases/$VERSION" "$PREFIX/current"
chown -R root:root "$PREFIX"; chown -R "$RUN_USER:$RUN_USER" "$DATA"; chmod 750 "$DATA"

if [ ! -f "$ETC/scada.env" ]; then
  KEY="$(head -c 48 /dev/urandom | base64 | tr -d '\n')"
  cat > "$ETC/scada.env" <<ENV
ASPNETCORE_ENVIRONMENT=Production
Kestrel__Endpoints__Http__Url=http://0.0.0.0:$PORT
Scada__ConfigDbPath=$DATA/scada.db
Scada__HistoryRoot=$DATA/history
Scada__BackupRoot=$DATA/backups
Scada__LogRoot=$DATA/logs
Jwt__Key=$KEY
# 主备：两台节点 Jwt__Key 与 Ha__SharedKey 必须相同，详见 docs/high-availability.md
# Ha__Enabled=true
# Ha__NodeId=node-a
# Ha__SelfUrl=http://10.0.0.11:$PORT
# Ha__PeerUrl=http://10.0.0.12:$PORT
# Ha__SharedKey=
# Ha__WitnessPath=/mnt/scada-witness
# Ha__StateDir=$DATA/ha
# Scada__MetricsToken=
ENV
  chmod 640 "$ETC/scada.env"; chown root:"$RUN_USER" "$ETC/scada.env"
fi

sed -e "s#@PROGRAM@#$PREFIX/current#g" -e "s#@USER@#$RUN_USER#g" -e "s#@ENVFILE@#$ETC/scada.env#g" \
  "$(dirname "$0")/scada.service" > "/etc/systemd/system/$SERVICE.service"
systemctl daemon-reload
systemctl enable --now "$SERVICE"
wait_healthy || die "服务未在 120 秒内就绪，查看 journalctl -u $SERVICE"
echo "已安装 $VERSION，配置 $ETC/scada.env，数据 $DATA"
