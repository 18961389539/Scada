# 中心服务安装、升级与回滚

网关的 Windows 部署包见 `gateway-windows-service.md`。本文针对中心服务（`Scada.Server`）。脚本位于 `tools/deployment/server/`。

## 发布

```bash
cd web && npm ci && npm run build && cd ..
dotnet publish src/Scada.Server -c Release -o publish/server-<版本>
```

目标主机需 .NET 10 ASP.NET Core Runtime（或加 `--self-contained -r linux-x64|win-x64`）。程序目录与数据目录分离：程序在 `releases/<版本>`，`current` 链接指向在用版本；升级与回滚只切换链接，数据目录不动。

## Linux（systemd）

```bash
sudo SCADA_PORT=5080 tools/deployment/server/install.sh publish/server-1.4 1.4
sudo tools/deployment/server/upgrade.sh publish/server-1.5 1.5
sudo tools/deployment/server/rollback.sh [--restore-db]
```

- 默认程序 `/opt/scada`、数据 `/var/lib/scada`、配置 `/etc/scada/scada.env`（环境变量形式，首次安装生成随机 `Jwt__Key`，含主备配置样例），可用 `SCADA_PREFIX/SCADA_DATA/SCADA_ETC/SCADA_SERVICE/SCADA_USER` 覆盖。
- 单元 `scada.service`：`Type=notify`（程序启用 `UseSystemd`），专用系统账户，`Restart=always`、5 秒重启，`ProtectSystem=full`。日志在 `/var/lib/scada/logs` 与 `journalctl -u scada`。

## Windows 服务

管理员 PowerShell 7.2+：

```powershell
pwsh -File tools\deployment\server\Install-ScadaService.ps1 -PublishPath D:\pkg\server-1.4 -InstallRoot 'C:\Program Files\Scada' -DataPath D:\ScadaData -Version 1.4 -Start
pwsh -File tools\deployment\server\Upgrade-ScadaService.ps1 -PublishPath D:\pkg\server-1.5 -InstallRoot 'C:\Program Files\Scada' -Version 1.5
pwsh -File tools\deployment\server\Rollback-ScadaService.ps1 -InstallRoot 'C:\Program Files\Scada' [-RestoreDatabase]
```

服务以虚拟账户 `NT SERVICE\ScadaServer` 运行；站点配置写在数据目录 `appsettings.Site.json`（通过 `--SiteSettings` 加载，升级不覆盖；环境变量与命令行仍优先）；SCM 失败动作 5 秒/5 秒/30 秒重启，非 0 退出码视为失败。

## 升级

脚本步骤：复制新版本到 `releases/<版本>` → 停服务 → 复制配置库到 `upgrade-snapshots/` → 切换 `current` → 启动 → 120 秒内 `/health` 就绪，否则自动回滚并恢复快照。建议升级前在“运维与高可用”页面先做一次“立即备份并演练”。

配置库迁移随启动自动执行，只向前；历史库格式向后兼容。

**主备滚动升级**（停机时间 ≈ 一次手动切换）：

1. 升级备机，确认页面上配置/历史复制时间持续更新（备机会对复制来的库执行新版本迁移）。
2. 在主机页面“切换到备机”，确认新主机采集、控制、网关在线正常。
3. 升级原主机，它以备机身份加入。如需恢复原主节点，再切换一次。

新旧版本混跑期间不要修改组态：新版本迁移过的配置库快照复制到旧版本节点后，旧程序不认识新增结构，可能读写失败；滚动升级应尽快完成，且不要把旧版本节点重新切成主机。

## 回滚

- 仅程序问题：`rollback.sh` / `Rollback-ScadaService.ps1` 切回上一版本。
- 新版本已迁移配置库（结构变化）：加 `--restore-db` / `-RestoreDatabase`，用升级前快照覆盖配置库；快照之后的组态修改、报警确认等会丢失，失败的库另存为 `failed-*.db`。历史数据不回滚。
- 主备部署回滚：先回滚备机并恢复快照，切换，再回滚另一台；或两台都停，回滚主机后以空库重建备机由复制追平。
- 更早的版本：用完整备份离线还原（`backup-restore.md`）。

## 未验证

本机没有 root/systemd 与 Windows 管理员环境：脚本做了 `bash -n` 语法检查与站点配置加载实测，实际 `systemctl`/SCM 安装、升级、自动回滚需在目标环境验收。
