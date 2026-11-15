#!/usr/bin/env python3
"""配置库/运行库迁移链体检（只读，绝不改动迁移文件）。

用法：
    python tools/migration-baseline/analyze.py                # 人读报告
    python tools/migration-baseline/analyze.py --json         # 机器读，便于接 CI/定时任务
    python tools/migration-baseline/analyze.py --warn 40      # 超过阈值时以退出码 1 提示

为什么要这个脚本：squash 基线是个不可逆的决定（现场 __EFMigrationsHistory 与代码里的
迁移 ID 必须一一对应），不能凭"感觉迁移有点多"就动手。这里把条数、代码规模、时间跨度、
最老的迁移摆出来，配合 /metrics 里的 scada_migrations_duration_seconds 一起判断。

本脚本不会生成、删除或重命名任何迁移。真正执行 squash 的步骤见
docs/quality/migration-baseline.md，且必须先在现场库的副本上验证。
"""

from __future__ import annotations

import argparse
import json
import re
import sys
from collections import Counter
from datetime import datetime
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
MIGRATIONS = ROOT / "src" / "Scada.Data" / "Migrations"
CONTEXTS = ("Config", "Runtime")
MIGRATION_FILE = re.compile(r"^(\d{14})_(.+)\.cs$")


def scan(context: str) -> dict:
    folder = MIGRATIONS / context
    entries = []
    designer_lines = 0
    for path in sorted(folder.glob("*.cs")):
        if path.name.endswith(".Designer.cs") or path.name.endswith("ModelSnapshot.cs"):
            designer_lines += count_lines(path)
            continue
        match = MIGRATION_FILE.match(path.name)
        if not match:
            continue
        stamp, name = match.groups()
        try:
            when = datetime.strptime(stamp, "%Y%m%d%H%M%S")
        except ValueError:
            when = None
        entries.append(
            {
                "id": f"{stamp}_{name}",
                "name": name,
                "timestamp": when.isoformat() if when else None,
                "quarter": f"{when.year}-Q{(when.month - 1) // 3 + 1}" if when else "unknown",
                "lines": count_lines(path),
                "file": path.relative_to(ROOT).as_posix(),
            }
        )
    entries.sort(key=lambda x: x["id"])
    quarters = Counter(x["quarter"] for x in entries)
    return {
        "context": context,
        "count": len(entries),
        "migration_lines": sum(x["lines"] for x in entries),
        "designer_snapshot_lines": designer_lines,
        "first": entries[0]["id"] if entries else None,
        "last": entries[-1]["id"] if entries else None,
        "span_days": (
            (datetime.fromisoformat(entries[-1]["timestamp"]) - datetime.fromisoformat(entries[0]["timestamp"])).days
            if entries and entries[0]["timestamp"] and entries[-1]["timestamp"]
            else 0
        ),
        "by_quarter": dict(sorted(quarters.items())),
        "largest": sorted(entries, key=lambda x: -x["lines"])[:5],
    }


def count_lines(path: Path) -> int:
    with path.open("r", encoding="utf-8-sig", errors="replace") as handle:
        return sum(1 for _ in handle)


def report(data: list[dict]) -> None:
    print("Scada 迁移链体检（只读）")
    print("=" * 68)
    for item in data:
        print(f"\n[{item['context']}DbContext]")
        print(f"  迁移条数        : {item['count']}")
        print(f"  手写迁移代码    : {item['migration_lines']} 行")
        print(f"  Designer+快照   : {item['designer_snapshot_lines']} 行")
        print(f"  首 / 末         : {item['first']}  →  {item['last']}")
        print(f"  时间跨度        : {item['span_days']} 天")
        if item["by_quarter"]:
            print("  季度分布        : " + ", ".join(f"{k}={v}" for k, v in item["by_quarter"].items()))
        if item["largest"]:
            print("  最大的迁移      : " + ", ".join(f"{x['name']}({x['lines']}行)" for x in item["largest"]))
    total = sum(x["count"] for x in data)
    print("\n" + "=" * 68)
    print(f"合计 {total} 个迁移。判断要不要 squash，看三个信号，缺一不可：")
    print("  1) /metrics 的 scada_migrations_duration_seconds：新装/升级实测启动耗时；")
    print("  2) scada_migrations_total：条数是否还在稳定增长；")
    print("  3) 本报告的 designer_snapshot_lines：编译与部署包体积。")
    print("只满足第 2 条就做基线，通常得不偿失（现场库 __EFMigrationsHistory 对齐成本高）。")
    print("\n相关的只读命令（生成 SQL 供评审，不会改库）：")
    print("  dotnet ef migrations list --project src/Scada.Data --startup-project src/Scada.Data --context ConfigDbContext --no-build")
    print("  dotnet ef migrations script 0 <目标迁移> --project src/Scada.Data --startup-project src/Scada.Data --context ConfigDbContext -o review.sql")
    print("\n执行 squash 前请读 docs/quality/migration-baseline.md，并在现场库副本上演练。")


def main() -> int:
    parser = argparse.ArgumentParser(description="只读分析 EF Core 迁移链")
    parser.add_argument("--json", action="store_true", help="输出 JSON")
    parser.add_argument("--warn", type=int, default=0, metavar="N", help="迁移条数超过 N 时返回退出码 1")
    args = parser.parse_args()

    data = [scan(context) for context in CONTEXTS]
    if args.json:
        print(json.dumps(data, ensure_ascii=False, indent=2))
    else:
        report(data)
    return 1 if args.warn and sum(x["count"] for x in data) > args.warn else 0


if __name__ == "__main__":
    sys.exit(main())
