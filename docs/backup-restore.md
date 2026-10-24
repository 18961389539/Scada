# 备份与恢复

## 备份包含什么

完整备份是一个 zip 包（`data/backups/full_yyyyMMdd_HHmmss_<唯一编号>.zip`），不再只有配置库：

| 成员 | 内容 | 说明 |
|---|---|---|
| `config.db` | 配置库（设备、测点、报警规则、用户、画面、模板、网关） | 用 SQLite 在线备份接口复制，写入期间取到的是一致的快照 |
| `history/raw_*.db`、`history/agg_*.db` | 历史原始库与聚合库 | 同样走在线备份，不直接复制正在写的文件 |
| `history/_spool/history-spool.ndjson` | 尚未补传入库的历史暂存 | 与运行目录一致；在追加/补传锁下取副本，再复制历史库 |
| `keys/*.xml` | Data Protection 密钥 | 缺失会导致已加密的设备凭据、网关配置无法解密 |
| `opc-pki/` | OPC UA 证书与信任列表（配置了 `Scada:OpcPkiRoot` 时） | 缺失需重新信任证书 |
| `manifest.json` | 生成时间、应用版本、各文件大小与 SHA256、设备/测点/规则数量 | 用于校验，也是恢复时的对照清单 |

配置库单独仍有一份夜间备份（`scada_yyyyMMdd_HHmmss.db`），只覆盖配置库，保留天数由 `Scada:BackupRetentionDays` 控制；完整备份包按同样天数自动清理。

完整备份默认**每周自动生成一次**：维护任务每 24 小时检查一次最新包的生成时间，超过 `Scada:FullBackupIntervalHours`（默认 168 小时，设 0 关闭）就新建一个。首次启动时若目录里没有完整包，会在启动约 10 秒后立即生成一份。

## 触发与校验

仅管理员可用（`Admin` 策略）：

```
GET  /api/maintenance/backups                 列出已有完整备份
POST /api/maintenance/backups                 立即生成一个完整备份
POST /api/maintenance/backups/{name}/verify   逐文件校验大小与 SHA256，并核对配置库数量
GET  /api/maintenance/backups/{name}          下载备份包
```

`verify` 会在临时目录解包后比对清单：任一文件缺失、大小不符、哈希不符，或配置库中设备/测点/规则数量与清单不一致，都会判为失败并列出具体问题。

停机窗口内用离线工具，不依赖运行中的服务：

```
dotnet tools/Scada.BackupTool/bin/Release/net10.0/Scada.BackupTool.dll verify  --backup <full_*.zip>
dotnet tools/Scada.BackupTool/bin/Release/net10.0/Scada.BackupTool.dll restore --backup <full_*.zip> --target <空目录>
```

`restore` 会先跑完整校验，校验不通过一律拒绝；目标目录非空同样拒绝，不会覆盖现场数据。

旧版包中的 `history/history-spool.ndjson` 会自动迁到 `history/_spool/history-spool.ndjson`，无需手工重排。备份先取暂存副本再复制数据库，并发补传可能使同一记录同时出现于两份快照；恢复入库按测点编号和时间戳幂等处理，不覆盖已经提交的样本，也不阻塞后续批次。连续生成使用唯一文件名，临时 zip 完成后才发布。

## 还原演练记录（2026-10-24）

在隔离目录完成一次端到端演练，全程走真实代码路径（服务生成备份 → 离线工具还原 → 服务从还原数据启动）：

| 步骤 | 结果 |
|---|---|
| 源实例（演示数据，独立端口 18591） | `/health` 200，设备 1 台、测点 12 个、报警规则 6 条 |
| 生成备份 | `full_20261024_163540.zip`，11,503 字节 |
| 校验 | `valid=true`，问题列表为空；清单记录 `config.db` 与 `keys/*.xml` 的 SHA256 |
| 停止源实例并离线还原到空目录 | 退出码 0，还原 4 个文件，设备 1 台、测点 12 个、报警规则 6 条 |
| 从还原目录启动（不种演示数据，端口 18592） | `/health` 200，登录成功（账号来自还原后的配置库），设备数 1 与源一致 |
| 结论 | 配置、用户、密钥随包恢复，服务可正常启动；`busDropped=0`、`historySpool` 无积压 |

本次演示数据没有生成历史库，所以演练覆盖的是「配置库 + 密钥 + 启动」链路。后续新增回归将实际待传样本放入完整备份、恢复后通过 `HistorySpool` 和 `HistoryStore` 续传并查询校验值，同时覆盖旧包路径兼容、已有数据库打开和连续备份。此前仅检查文件存在的测试未发现暂存路径错误，现已修正。真实现场库体积下的打包耗时与磁盘占用仍需在实际项目上测。

## 恢复步骤

在线恢复没有实现，也不建议实现：配置库被运行中的进程持有，热替换会同时破坏写入与缓存一致性。按下面步骤离线恢复：

1. 停止 SCADA 服务，确认没有残留进程占用 `data/` 下的文件。
2. 备份当前 `data/` 目录（即使它已经损坏，也先保留现场）。
3. 用离线工具 `restore` 还原到空临时目录；工具先核对清单，再处理新旧暂存目录。
4. 将还原结果按 `Scada:ConfigDbPath`、`HistoryRoot`、`OpcPkiRoot` 部署到现场位置；工具输出的配置库名为 `scada.db`，密钥放在其同级 `keys/`。
5. 若更换了主机或服务账户：Windows 上用 DPAPI 保护的密钥与新机器/账户绑定，需要重新录入设备与网关凭据；`keys/` 只能在同一账户下直接复用。
6. 启动服务，检查 `/health` 的 `historySpool.pendingBytes`、`busDropped`，并核对设备数、测点数与画面是否与清单一致。
7. 抽查最近一小时的历史曲线与报警记录，确认补传完成。

## 已知限制

- 备份为全量，没有增量；历史库较大时打包耗时与磁盘占用都随之上升，需要在现场按实际库大小规划备份盘空间。
- 定时任务只在服务运行期间触发；服务长期停机不会补做漏掉的周期。
- 备份期间会在 `BackupRoot` 下产生临时目录，磁盘不足会失败并记录日志，不会留下半个 zip。
- 恢复后 Data Protection 密钥若无法复用（换机器、换账户、密钥文件缺失），已加密的凭据必须重新录入，这一点无法靠备份规避。
