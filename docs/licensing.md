# 软件授权（激活码）

## 结论先行

- **试用期 365 天（一年）**：首次启动即开始计时，无需任何配置。
- **到期前 15 天**：登录后每天弹一次提醒（可"今天不再提醒"）。
- **试用或授权过期、激活码无效、系统时间被回拨**：除登录、授权页和 `/health` 外，**全部 API 与 SignalR 请求返回 403**，界面弹出不可关闭的激活窗口。
- **激活码绑定机器码**：换机必须重新签发，复制 `data/` 到另一台机器不会生效。

## 状态机

| 状态 | 含义 | 是否阻断功能 |
|---|---|---|
| `Trial` | 试用期内 | 否 |
| `Active` | 已激活且剩余 > 15 天 | 否 |
| `ExpiringSoon` | 已激活且剩余 ≤ 15 天 | 否（前端提醒） |
| `Expired` | 试用或授权到期 | 是 |
| `Invalid` | 签名无效、机器码不匹配、格式损坏 | 是 |
| `Tampered` | 系统时间被回拨超过 5 分钟 | 是 |

状态落在 `data/license.json`，与配置库同目录，随数据目录一起备份。

## 激活码格式

```
SCADA1.<base64url(JSON 载荷)>.<base64url(ECDSA P-256 签名)>
```

签名对象是 base64url 载荷的 ASCII 字节，载荷包含：

```json
{
  "v": 1,
  "kid": "dev-2026",
  "lic": "SC-20260228-0001",
  "cus": "客户名称",
  "mid": "KFP5T-FC73Y-T86MR-6UHVL-U4R38-W",
  "iat": 1793172887,
  "exp": 1795764887,
  "dev": 200,
  "tag": 5000,
  "feat": ["ha", "opcua"]
}
```

- `dev` / `tag` 为 0 表示不限。
- `feat` 为空表示基础功能；可用值：`ha`、`opcua`、`report-signing`、`csharp-scripts`、`traceability`、`oee`。
- `mid` 为空表示不绑定机器（仅演示，正式授权必须绑定）。

客户端只内置**公钥**，无法伪造激活码；私钥只存在于厂商离线环境。

## 厂商侧：生成密钥与签发

```powershell
# 只做一次，私钥离线保管
dotnet run --project tools/license/Scada.LicenseTool -- keygen --out D:\license-keys --kid prod-2026

# 把 D:\license-keys\public.pem 覆盖到 src/Scada.Server/Licensing/license-public.pem 后重新发布
```

签发（客户提供机器码）：

```powershell
dotnet run --project tools/license/Scada.LicenseTool -- issue `
  --key D:\license-keys\private.pem `
  --machine "KFP5T-FC73Y-T86MR-6UHVL-U4R38-W" `
  --customer "XX 厂" --license-id "SC-20260228-0001" `
  --days 365 --devices 200 --tags 5000 --features ha,opcua
```

校验：

```powershell
dotnet run --project tools/license/Scada.LicenseTool -- inspect --public D:\license-keys\public.pem --code "SCADA1...."
```

`--expires 2027-12-31` 可代替 `--days` 指定固定到期日。

## 现场激活

1. 管理员登录 → 系统 → 软件授权（或直接访问 `/license`）。
2. 复制机器码给厂商。
3. 粘贴激活码 → 激活。
4. 激活成功会写操作日志，并立即解除阻断。

只有具备全局用户管理权限（`ManageUsers`）的账号可以激活。

## 时钟与换机

- 工控机必须启用 NTP 时间同步。时钟回拨超过 `Scada:License:ClockSkewSeconds`（默认 300 秒）会进入 `Tampered`，需校准时间并重新激活。
- 换主板/整机更换后机器码会变化，需用新机器码重新签发；旧激活码在新机器上会被拒绝。
- 更换网卡**不影响**机器码（指纹不包含 MAC）。

## 配置项

```json
"Scada": {
  "License": {
    "Enabled": true,
    "TrialDays": 365,
    "WarningDays": 15,
    "ClockSkewSeconds": 300,
    "PublicKey": ""
  }
}
```

`PublicKey` 留空时使用程序集内嵌公钥（推荐）。仅在轮换公钥或测试时通过配置覆盖。

## 已知边界

- 阻断发生在 HTTP/SignalR 入口；已建立的长连接不会立即断开，但前端会立刻显示不可关闭的激活弹窗。
- 采集与历史写入不因过期而停止（避免数据断档），但所有查看/控制/配置入口被拒绝。
- 客户端二进制可被改造绕过校验；本方案防的是"复制激活码/改时间/换机复用"，不防逆向。高价值场景需叠加代码签名与完整性校验。
- 删除 `data/license.json` 会重置试用起点。数据目录本身被删除时，组态数据同样会丢失，属同一风险面。