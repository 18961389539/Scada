# 迁移链基线（squash）决策与操作说明

> 状态：**仅决策与操作手册，不默认改动现有迁移链**。
> 适用：`src/Scada.Data/Migrations/Config`（配置库）与 `Migrations/Runtime`（运行库）。

## 1. 为什么先不改

配置库迁移链有 60+ 条、Designer/Snapshot 累计十几万行，看起来很吓人，但**条数本身不是问题**：
EF Core 的 `Migrate()` 启动时只做一次 `GetPendingMigrations()`（读 `__EFMigrationsHistory`），
已应用的历史迁移不参与执行。真正变慢的是「新装一次跑完全链」和「编译/发布体积」。

squash 的代价集中在现场库：代码里的迁移 ID 与每个现场库 `__EFMigrationsHistory` 必须一一对应。
折叠后老库里记录的是旧 ID，新基线 ID 不在历史表里，EF 会尝试重新执行基线 DDL（`CREATE TABLE` 已存在）
并在启动时直接失败。**这是停机级故障，不是代码重构。**

因此判断标准改成三个可观测信号，缺一不可：

| 信号 | 来源 | 触发阈值（建议） |
|---|---|---|
| 新装/升级实测迁移耗时 | `/metrics` → `scada_migrations_duration_seconds` | 持续 > 30 s |
| 迁移链总条数 | `/metrics` → `scada_migrations_total`；`analyze.py` | 持续 > 80 且仍在增长 |
| 编译与部署包体积 | `analyze.py` 的 `designer_snapshot_lines` | 明显拖慢 CI |

`/metrics` 里另有 `scada_migrations_applied`（本次启动应用条数）与
`scada_migrations_recorded_timestamp_seconds`（快照记录时间）。这四个值都是 **gauge**：
每次进程启动重新记录，重启归零属正常；长期趋势由 Prometheus 保存，不要在应用内累加。

体检命令（只读）：

```bash
python tools/migration-baseline/analyze.py
python tools/migration-baseline/analyze.py --json
python tools/migration-baseline/analyze.py --warn 80   # 接 CI，超阈值返回 1
```

## 2. 决定要做时的操作顺序

**前提**：所有现场库都有备份；先在**现场库副本**上完整演练一次；安排停机窗口。
`DatabaseInitializer` 在应用迁移前会自动备份（`BackupBeforeMigration`），但那是应急兜底，不是演练替代品。

1. 冻结模型变更，确认 `dotnet ef migrations list` 与现场库历史表一致。
2. 用 `dotnet ef migrations script 0 <最后一个迁移>` 生成全链 SQL 存档，作为基线 DDL 的对照物。
3. 在**空库**上生成基线迁移，保证新装路径可用：
   ```bash
   dotnet ef migrations add Baseline_YYYYMM --project src/Scada.Data --startup-project src/Scada.Data --context ConfigDbContext -o Migrations/Config
   ```
4. 删除旧迁移文件与旧 `ModelSnapshot`，只保留基线（Designer 随迁移一起保留）。
   注意 `OperationalDataMover` / `SqliteSchemaEditor` 的 marker 迁移依赖 `MigrationId` 文本匹配，
   删除前先 `grep` 一遍，别把带 marker 的搬迁迁移一起删掉。
5. **存量现场对齐**：这是唯一不可逆的一步。对每个现场库，在停机窗口内把
   `__EFMigrationsHistory` 里的旧 ID 折叠为新基线 ID：
   ```sql
   BEGIN IMMEDIATE;
   DELETE FROM "__EFMigrationsHistory";
   INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
   VALUES ('<新基线迁移的完整 ID>', '<EF Core 版本>');
   COMMIT;
   ```
   `ProductVersion` 从新基线 Designer 文件里抄，不要手写猜。
   这一步**不做**的话，启动时 EF 会对已存在的表重跑 `CREATE TABLE`。
6. 验证三条路径，缺一不可：
   - 空库新装：`dotnet ef database update` 后表结构与 `ModelSnapshot` 一致；
   - 老库升级：用第 5 步处理过的副本启动到 `Scada.Server`，确认 `scada_migrations_applied = 0`；
   - 数据完好：报警、审计、命令账本行数与折叠前一致（重点看 `OperationalDataMover` 搬过的表）。
7. 之后按季度归档：每季度末用 `analyze.py` 留一次快照，不再攒到几十条才想起来。

## 3. 明确不做的事

- **不自动改写 `__EFMigrationsHistory`**：脚本与文档都不碰现场库，这一步必须由 DBA 在窗口内执行。
- **不为「看着清爽」而删迁移**：历史迁移是现场升级路径的一部分，尤其本项目带 schema marker 的自定义迁移。
- **不把运行库迁移也一起折叠**：Runtime 只有个位数条，收益为零，风险不为零。
