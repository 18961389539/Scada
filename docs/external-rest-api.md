# 对外 REST / HTTP API（`/api/v1`）

稳定对外集成契约。内部 Web UI 继续使用 `/api/*`，二者分离；OpenAPI 文档：`GET /openapi/v1.json`（匿名）。

## 认证

1. **API 密钥**（推荐）
   - 请求头：`X-Api-Key: scada_…` 或 `Authorization: Bearer scada_…`
   - 明文只在创建时返回一次，库中存 SHA256；可按区域与 scope 限制；支持每分钟限流。
   - Scope：`ReadTags` / `ReadHistory` / `ReadAlarms` / `Write`
2. **登录 JWT**（可选，`External:Rest:AllowJwt=true`）
   - 与内部相同的 Bearer JWT；scope 由用户区域权限映射（View→读，Control→写）。

管理密钥：`/api/external/keys`（需全局 System 权限）。每次 `/api/v1` 调用写审计。

## 端点

| 方法 | 路径 | Scope | 说明 |
|---|---|---|---|
| GET | `/api/v1/health` | 任一 | 健康与主备角色 |
| GET | `/api/v1/tags` | ReadTags | 可见测点列表 |
| GET | `/api/v1/tags/{id}` | ReadTags | 单个测点 |
| GET | `/api/v1/realtime?tagIds=` | ReadTags | 当前值 |
| POST | `/api/v1/realtime/batch` | ReadTags | `{ "tagIds": [1,2] }` |
| GET | `/api/v1/history?tagIds=&from=&to=` | ReadHistory | 历史（最多 50 点、一年） |
| GET | `/api/v1/alarms?activeOnly=&from=&to=&limit=` | ReadAlarms | 报警 |
| POST | `/api/v1/commands` | Write | `{ tagId, value, confirmed?, requestId? }`，**走现场控制闭环** |

无权区域的测点在列表/实时中静默过滤；历史/控制跨区域返回 403。

## 配置（`appsettings` → `External:Rest`）

```json
{
  "External": {
    "Rest": {
      "Enabled": true,
      "AllowJwt": true,
      "CorsOrigins": ["https://mes.example.com"],
      "DefaultRateLimitPerMinute": 120
    }
  }
}
```

CORS 与内部 `web` 策略分离。密钥本身不进工程包。

## 不做的事项

- 不替代内部 `/api` 管理面
- 不绕过双人复核/挂牌/就地/限幅等控制策略
- 不提供 MQTT 发布或 OPC UA 服务端（见对应文档）
