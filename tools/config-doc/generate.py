#!/usr/bin/env python3
"""从 ScadaSettings.cs 生成 docs/configuration.md。

为什么要有这个脚本：配置项 120+ 个，appsettings.json 只列了不到一半，其余全靠代码默认值。
手写文档必然与代码漂移，所以从源码单点生成，CI 里可以重跑对比。

用法：
    python tools/config-doc/generate.py            # 写到 docs/configuration.md
    python tools/config-doc/generate.py --check    # 只校验文档是否过期（CI 用），过期返回 1
"""

from __future__ import annotations

import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SOURCE = ROOT / "src" / "Scada.Core" / "Options" / "ScadaSettings.cs"
APPSETTINGS = ROOT / "src" / "Scada.Server" / "appsettings.json"
OUTPUT = ROOT / "docs" / "configuration.md"

# 属性声明：public <type> <Name> { get; set; } [= <default>];
# 末尾分号是可选的：C# 的自动属性没有初始化器时不写分号，只有 `= value;` 才有。
PROPERTY = re.compile(
    r"^\s*public\s+(?P<type>[\w\.\?<>\[\],\s]+?)\s+(?P<name>\w+)\s*\{\s*get;\s*set;\s*\}"
    r"(?:\s*=\s*(?P<default>[^;]+?))?\s*;?\s*$"
)

# 复杂类型：不进标量表格，单独成段说明。
COMPLEX_TYPES = ("List<", "Dictionary<", "[]", "Options>")

GROUP_ORDER = [
    "General", "History", "Alarm", "Notification", "Oee", "Calc", "Report",
    "Backup", "Maintenance", "Realtime", "Control", "Gateway", "Log",
    "Watchdog", "Clock", "Security", "Telemetry", "Other",
]


def group_of(name: str) -> str:
    """按属性名前缀分组，让 120 行表格可读。"""
    prefixes = [
        ("History", "History"), ("Archive", "History"), ("Agg", "History"),
        ("Alarm", "Alarm"), ("Notification", "Notification"),
        ("Oee", "Oee"), ("Calc", "Calc"), ("Report", "Report"),
        ("Backup", "Backup"), ("Diagnostic", "Maintenance"),
        ("Realtime", "Realtime"), ("Bus", "Realtime"),
        ("Command", "Control"), ("Control", "Control"),
        ("Gateway", "Gateway"),
        ("Log", "Log"), ("Watchdog", "Watchdog"), ("Clock", "Clock"),
        ("Metrics", "Security"), ("Health", "Security"), ("Security", "Security"),
        ("Api", "Security"), ("Anonymous", "Security"),
        ("ReadRequest", "Security"), ("WriteRequest", "Security"), ("MaxUpload", "Security"),
        ("Telemetry", "Telemetry"),
    ]
    for prefix, group in prefixes:
        if name.startswith(prefix):
            return group
    return "General"


def load_source() -> list[dict]:
    lines = SOURCE.read_text(encoding="utf-8").splitlines()
    items: list[dict] = []
    comments: list[str] = []
    depth = 0
    inside = False

    for line in lines:
        if not inside:
            if "class ScadaSettings" in line:
                inside = True
            continue

        depth += line.count("{") - line.count("}")
        if depth <= 0:
            break  # 顶层类结束

        # 嵌套类（BackupTargetOptions 等）里的属性不属于 Scada 段，跳过。
        if depth != 1:
            continue

        stripped = line.strip()
        if stripped.startswith("//"):
            comments.append(stripped[2:].strip())
            continue
        if stripped.startswith("["):
            # 特性行夹在注释与属性之间，不打断注释块。
            continue
        if stripped == "":
            comments = []
            continue

        match = PROPERTY.match(line)
        if not match:
            continue

        name = match.group("name")
        type_name = " ".join(match.group("type").split())
        default = (match.group("default") or "").strip()
        items.append({
            "name": name,
            "type": type_name,
            "default": default,
            "comment": " ".join(comments).strip(),
            "complex": any(token in type_name for token in COMPLEX_TYPES),
            "sensitive": False,
        })
        comments = []

    # [Sensitive] 特性行在属性上方（可能隔着其它特性），统一按属性名回溯确认。
    text = SOURCE.read_text(encoding="utf-8")
    for item in items:
        marker = f" {item['name']} {{"
        index = text.find(marker)
        window = text[max(0, index - 300):index] if index > 0 else ""
        item["sensitive"] = "[Sensitive]" in window

    return items


def appsettings_keys() -> set[str]:
    try:
        data = json.loads(APPSETTINGS.read_text(encoding="utf-8"))
    except json.JSONDecodeError as error:
        print(f"appsettings.json 不是合法 JSON：{error}", file=sys.stderr)
        raise SystemExit(1)

    keys: set[str] = set()

    def walk(node: object) -> None:
        if isinstance(node, dict):
            for key, value in node.items():
                keys.add(key)
                walk(value)

    walk(data.get("Scada", {}))
    return keys


def escape(text: str) -> str:
    return text.replace("|", "\\|")


def render() -> str:
    items = load_source()
    present = appsettings_keys()
    groups: dict[str, list[dict]] = {}
    complex_items: list[dict] = []
    for item in items:
        if item["complex"]:
            complex_items.append(item)
        else:
            groups.setdefault(group_of(item["name"]), []).append(item)

    lines: list[str] = []
    lines.append("# 配置参考")
    lines.append("")
    lines.append("> 由 `python tools/config-doc/generate.py` 从 `src/Scada.Core/Options/ScadaSettings.cs` 生成，请勿手改。")
    lines.append("> CI 用 `--check` 校验文档与代码一致。")
    lines.append("")
    lines.append("## 覆盖方式")
    lines.append("")
    lines.append("优先级从低到高：")
    lines.append("")
    lines.append("1. `src/Scada.Server/appsettings.json` 的 `Scada` 段；")
    lines.append("2. 站点配置文件（`data/appsettings.Site.json`，由 `StartupPaths.ResolveSiteSettings` 定位）；")
    lines.append("3. 环境变量，双下划线表示层级：`Scada__HistoryQueryMaxConcurrent=8`；")
    lines.append("4. 命令行：`--Scada:HistoryQueryMaxConcurrent 8`。")
    lines.append("")
    lines.append("**appsettings 的改动默认需要重启进程**；例外是 `Scada:DesktopClient`（桌面客户端版本门禁与心跳），"
                 "它用 `IOptionsMonitor` 读取，保存后一个心跳周期内生效。`ConfigRefreshService` 刷新的是数据库里的业务配置，不是这个文件。")
    lines.append("")
    lines.append("启动日志会打印「非默认配置 N 项」，用于对齐不同机器之间的差异（敏感项不落日志）。")
    lines.append("")
    lines.append("## TLS 与反向代理")
    lines.append("")
    lines.append("默认只监听明文 HTTP（`http://0.0.0.0:5080`）。局域网内可直接嗅探到 8 小时有效期的 JWT，")
    lines.append("生产环境必须二选一：")
    lines.append("")
    lines.append("1. **反向代理终结 TLS**：SCADA 放在 Nginx / IIS ARR 之后，保持 `Kestrel:Endpoints:Http` 不变；")
    lines.append("")
    lines.append("   代理终结时必须同时启用 `Scada:ReverseProxy` 并列出可信代理。否则服务端只看得到代理地址：")
    lines.append("   内部限流按「身份 + 终端地址」分区，几十台终端会被合并成一个配额桶；IP 白名单、审计与日志也都会记成代理 IP。")
    lines.append("")
    lines.append("   ```json")
    lines.append("   \"Scada\": { \"ReverseProxy\": { \"Enabled\": true, \"KnownProxies\": [\"10.0.0.5\"] } }")
    lines.append("   ```")
    lines.append("")
    lines.append("   只信任这里列出的地址（同机代理填 `127.0.0.1`），伪造的 `X-Forwarded-For` 不会改变限流分区。客户端直连（没有代理）时保持 `Enabled: false`；")
    lines.append("   请求带 `X-Forwarded-For` 却未启用时，服务端会每 5 分钟在日志里提示一次。")
    lines.append("2. **Kestrel 直接终结 TLS**：")
    lines.append("")
    lines.append("```json")
    lines.append("\"Kestrel\": {")
    lines.append("  \"Endpoints\": {")
    lines.append("    \"Http\":  { \"Url\": \"http://0.0.0.0:5080\" },")
    lines.append("    \"Https\": { \"Url\": \"https://0.0.0.0:5443\" }")
    lines.append("  },")
    lines.append("  \"Certificates\": {")
    lines.append("    \"Default\": { \"Path\": \"C:/certs/scada.pfx\", \"Password\": \"***\" }")
    lines.append("  }")
    lines.append("}")
    lines.append("```")
    lines.append("")
    lines.append("或用环境变量 `ASPNETCORE_Kestrel__Certificates__Default__Path` / `__Password` 指定 PFX。")
    lines.append("两种方式下 HTTPS 请求都会带 HSTS 头（`Middleware/SecurityHeaders.cs`）；")
    lines.append("未检测到 https 端点时，非 Development 环境启动会记录一条告警。")
    lines.append("")
    lines.append("## 标量配置")
    lines.append("")
    lines.append("「配置文件中」列表示该键是否显式出现在 `appsettings.json` 里；")
    lines.append("未出现不代表无效，只是使用下面的默认值。标 **敏感** 的项不会出现在启动日志中。")
    lines.append("")

    for group in GROUP_ORDER:
        if group not in groups:
            continue
        lines.append(f"### {group}")
        lines.append("")
        lines.append("| 配置键 | 默认值 | 配置文件 | 说明 |")
        lines.append("|---|---|---|---|")
        for item in groups[group]:
            default = escape(item["default"] or "（无）")
            if default.startswith('"') and default.endswith('"'):
                default = f"`{default}`"
            elif default:
                default = f"`{default}`"
            note = escape(item["comment"]) or "—"
            if item["sensitive"]:
                note = "**敏感** " + note
            present_mark = "✓" if item["name"] in present else "—"
            lines.append(f"| `{item['name']}` | {default} | {present_mark} | {note} |")
        lines.append("")

    if complex_items:
        lines.append("## 结构化配置")
        lines.append("")
        lines.append("以下项是数组或嵌套对象，改法与上面的标量不同，单列说明。")
        lines.append("")
        lines.append("| 配置键 | 类型 | 说明 |")
        lines.append("|---|---|---|")
        for item in complex_items:
            note = escape(item["comment"]) or "—"
            lines.append(f"| `{item['name']}` | `{item['type']}` | {note} |")
        lines.append("")

    lines.append("## 需要单独展开的段落")
    lines.append("")
    lines.append("- `Scada:Telemetry`（`TelemetryOptions`）：OTLP 端点、采样比例、EF Core 埋点开关。")
    lines.append("- `Scada:BackupTargets`（数组）：异地备份目标，`Type` 为 `Directory` 或 `S3`。")
    lines.append("- `Scada:Ai`、`Mcp`、`External:*`、`Ha`、`Jwt`：各有独立段落与校验，见 `appsettings.json`。")
    lines.append("")
    return "\n".join(lines)


def main() -> int:
    content = render()
    if "--check" in sys.argv:
        current = OUTPUT.read_text(encoding="utf-8") if OUTPUT.exists() else ""
        if current != content:
            print("docs/configuration.md 已过期：请运行 python tools/config-doc/generate.py", file=sys.stderr)
            return 1
        print("docs/configuration.md 与代码一致")
        return 0

    OUTPUT.write_text(content, encoding="utf-8", newline="\n")
    print(f"已写入 {OUTPUT.relative_to(ROOT)}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
