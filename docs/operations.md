# 运维监控、日志与自恢复

## Prometheus 指标

`GET /metrics`（Prometheus 文本格式）。鉴权二选一：`Authorization: Bearer <Scada:MetricsToken>`（专供抓取，配置后生效），或具有全局“系统”权限的登录令牌。抓取配置示例：

```yaml
scrape_configs:
  - job_name: scada
    authorization: { credentials: "<MetricsToken>" }
    static_configs: [{ targets: ["10.0.0.11:5080", "10.0.0.12:5080"] }]
```

| 指标 | 含义 |
|---|---|
| `scada_up`、`scada_uptime_seconds`、`scada_process_working_set_bytes` | 进程存活、运行时长、内存 |
| `scada_ha_enabled`、`scada_ha_primary{node}`、`scada_ha_term`、`scada_ha_peer_up`、`scada_ha_role_changes_total`、`scada_ha_replication_age_seconds`、`scada_ha_history_rows_behind` | 主备状态与复制滞后（单机 `scada_ha_primary`=1） |
| `scada_device_online`、`scada_device_cycle_ms`（采集延迟）、`scada_device_poll_age_seconds`、`scada_device_comm_failures_total`、`scada_device_reconnects_total` | 每设备（标签 `device_id`、`device`） |
| `scada_alarms_active{level}`、`scada_alarms_unacknowledged` | 报警 |
| `scada_notification_outbox_pending`、`scada_notification_outbox_failed`、`scada_gateway_inbox_pending_batches` | 发件箱与网关收件箱积压 |
| `scada_history_spool_*`、`scada_bus_backlog`、`scada_bus_dropped_total` | 历史暂存积压/拒收、实时总线积压/丢弃 |
| `scada_disk_free_bytes`、`scada_disk_total_bytes`、`scada_config_db_bytes`、`scada_history_db_bytes` | 数据盘与库大小 |
| `scada_backup_last_success_timestamp_seconds`、`scada_backup_last_drill_ok`、`scada_backup_last_offsite_ok`、`scada_backup_local_count` | 最近备份/演练/异地结果与时间 |
| `scada_loop_age_seconds{loop}`、`scada_warnings` | 关键后台循环距上次心跳秒数（看门狗）、当前健康告警条数 |

建议告警：`scada_ha_primary` 两节点之和 ≠ 1；`scada_ha_peer_up == 0` 持续 1 分钟；`scada_device_poll_age_seconds > 60`；`scada_disk_free_bytes` 低于规划；`scada_notification_outbox_pending` 持续增长；最近备份超过间隔两倍或演练失败。

## 日志轮转

Serilog 按天滚动，单文件超过 `Scada:LogFileSizeLimitMb`（默认 50）也滚动，保留最近 `Scada:LogRetainedFiles`（默认 30）个文件；目录 `Scada:LogRoot`（默认程序目录下 `logs`，部署脚本放到数据目录）。诊断包从同一目录取最近日志。

## 故障自恢复

- **看门狗**：采集引擎、历史写入、主备协调循环定期打卡（`LivenessRegistry`）。任一循环超过 `Scada:WatchdogStallSeconds`（默认 180，0 关闭）未打卡，记录 Critical 日志并以退出码 3 停止进程，由 systemd（`Restart=always`）或 Windows SCM 失败动作在 5 秒后拉起。主备部署中主机退出即释放租约，备机接管。
- 采集断线自动重连、历史写入失败落盘暂存后续传、通知发件箱重试等既有机制不变。
- `/health` 汇总 `warnings[]`：磁盘、暂存、总线、备份失败/演练失败/异地失败、主备 `ha_peer_down`/`ha_witness_unreachable`/`ha_replication_failed`/`ha_replication_stalled`/`ha_no_primary`。

## 备份

定时备份 + 恢复演练 + 异地拷贝 + 保留策略见 `backup-restore.md`；运维页面“运维与高可用”展示主备、备份记录，可立即备份并演练。
