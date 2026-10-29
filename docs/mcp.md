# Embedded read-only MCP server

The server embeds the official `ModelContextProtocol.AspNetCore` SDK. Native MCP
clients connect to **`/api/mcp` on the existing SCADA HTTP(S) port**, using
Streamable HTTP. No Python service, Node.js adapter or additional listener is
required. This exposes SCADA tools to an AI client; it does not add a chat UI,
an LLM provider or an MCP client inside SCADA.

MCP is **enabled by default**, including when `Mcp:Enabled` is absent. Deploy the
newly built server to use this default; editing configuration does not add MCP
to an older binary. When enabled on the
trusted LAN, MCP requires **no API key, login, JWT or OAuth**.

## Configuration and disabling

The bundled configuration is shown below. No explicit enable setting is needed
for a new deployment. Keep existing paths, JWT keys and other site settings
unchanged when editing site configuration:

```json
{
  "Mcp": {
    "Enabled": true,
    "RequestsPerMinute": 120
  }
}
```

To disable MCP, set `"Mcp": { "Enabled": false }` in site settings, or set
`Mcp__Enabled=false`, then restart the service. Explicit `false` continues to
override the default: an existing site configured to disable MCP stays disabled
after upgrading. Remove that override or change it to `true` to enable it.

Restart the service after changes. Environment variables `Mcp__Enabled`
and `Mcp__RequestsPerMinute` override configuration. The request
limit must be 1 through 10000; invalid configuration fails startup.

Use the [normal publishing workflow](publishing.md). MCP dependencies are included
in the existing server publication. Do not publish over a running instance.

## LAN access model

Enabling MCP allows **any client that can reach the endpoint to read all areas**
through the registered read-only tools and resources. There is no per-user or per-key
scope/area filtering on this endpoint. Do not send an Authorization or X-Api-Key
header; credentials are not required and do not narrow MCP data visibility.

No write, acknowledgement, script, configuration, probe or control tools are
registered. This is not an administrator login and does not grant access to
other HTTP APIs. Existing frontend login, REST API authentication and area
permissions are unchanged. MCP enablement is independent of `External:Rest`.

The earlier `Mcp:AllowJwt` setting is no longer used and can be removed from
site settings. Clients must support an HTTP MCP server without authentication;
there is no interactive OAuth flow.

## VS Code client example

Use VS Code's MCP server configuration with just the endpoint URL. Replace
`192.168.1.100` with the SCADA server's LAN address. No secret prompts or
authentication headers are needed.

```json
{
  "servers": {
    "scada": {
      "type": "http",
      "url": "http://192.168.1.100:5080/api/mcp"
    }
  }
}
```

Start the MCP server connection in the client and list its tools. This is a
stateless server: there is no session ID to preserve, background subscription,
legacy `/sse` transport, sampling or elicitation. Both the traditional initialize
handshake and current SDK clients are covered by integration tests.

## Tools and response limits

All tools advertise `readOnlyHint=true`, `destructiveHint=false`,
`openWorldHint=false`, and return structured content with camel-case fields.

| Tool | Arguments and result |
|---|---|
| `search_tags` | Optional `keyword` (max 100 characters), `deviceId`, `afterId` (default 0), `limit` (1-100, default 50). Returns `items`, `nextAfterId`. |
| `get_device_status` | `afterId`, `limit` as above. Returns configured devices with cached `status`; null means no status is available, not online. Does not probe or reconnect. |
| `get_realtime` | Explicit `tagIds`, 1-100 positive IDs. Returns `values` with quality/timestamps and `missingTagIds` for configured tags without cached data. Missing is not zero. |
| `query_history` | `tagIds` (1-10), `from`, `to`, `maxPoints` (20-1000, default 500). The point budget is **total across all requested tags**, not per tag. Returns `resolution`, `downsampled`, `pointLimit`, `series`. |
| `query_alarms` | `activeOnly` (default true), `offset` (0-10000), `limit` (1-100). Historical mode requires `activeOnly=false`, `from`, `to`. Returns `items`, `nextOffset`, `capped`. If `capped=true`, narrow the historical range rather than requesting a larger offset. |
| `analyze_alarms` | `from`, `to`, optional `areaId`, `deviceId`, `top` (1-20). Returns counts, priority distribution, frequent sources, acknowledgement/clear means and hourly/daily trend. Preserves analytics `detailFrom`, `usedRollups`, `truncated` flags. |
| `analyze_history` | `tagIds` (1-10), `from`, `to` (**at most 24 hours**), optional finite `lowerLimit`/`upperLimit`. Exact raw-history statistics: time-weighted average, min/max, Good-quality coverage, and strictly below/above-limit durations in milliseconds. Returns no fabricated measurements for empty history. |
| `get_collector_diagnostics` | `deviceId`, `page` (1-100), `limit` (1-100), optional `staleOnly`. Existing **local** collector snapshot: errors, metrics, quality and paged points; at most 50 recent events and samples each. `recentListsTruncated` reports truncation. No probe/reconnect or gateway engineering command; remote-gateway or unavailable snapshots return an error. Connection strings are omitted. |
| `list_production_catalog` | `kind` (`lines`, `products`, `shifts`), `afterId`, `limit` (1-100). Discovers production IDs, names and areas; returns `nextAfterId`. |
| `query_oee` | `lineIds` (1-10), `from`, `to`, optional `productId`, `shiftId`. Existing OEE calculations: availability/performance/quality, production counts, downtime Pareto, MTBF/MTTR and breakdowns. Ratios are not percentages; null means insufficient data. Pareto is top 20 or the configured smaller count; other lists are capped at 100 with `listsTruncated`. Preserves detail/rollup metadata. |
| `query_downtimes` | `lineIds` (1-10), `from`, `to`, optional `confirmed`, `beforeId`, `limit` (1-100). Events **overlapping** the interval, including those started earlier. Does not confirm or classify events. |
| `query_traceability` | `from`, `to`, optional exact `sn` (max 200 characters), `jobId`, `outcome`, `beforeId`, `limit` (1-50). Persisted captures and fields. Inspection `outcome` is separate from PLC `acknowledged`; neither is modified. Each capture is limited to 100 fields / 65,536 JSON characters; malformed/oversized data returns an error. |
| `query_report` | `tagIds` (1-10), `from`, `to`, `type` (`daily`: hourly rows, max 24 hours; `monthly`: daily rows, max 31 days). **Stored hourly aggregates only**, with `recentRawIncluded=false` and missing tag IDs. No recent-raw fallback; recent aggregate gaps are not zero. Uses existing report calendar grouping. |
| `list_report_runs` | `from`, `to` filter **generation time**; optional `templateId`, `beforeId`, `limit` (1-100). Existing archive metadata, excluding file paths, signatures, recipients and signing configuration. |
| `read_report_run` | `runId`, `offset` (0-10000), `limit` (1-100). Stored table snapshot, columns, summary, total rows and `nextOffset`. Maximum stored JSON is 2 Mi characters, 100 columns and 10,000 rows. Explicit error for missing/malformed/oversized data. Reads database data, not a verified signed PDF/XLSX file. |

There are **15 tools**. Downtime, traceability and report-run lists use descending
ID seek pagination (`nextBeforeId`); pass the returned ID as `beforeId`. Data can
change between pages. Traceability timestamps and report-generation filters are
inclusive; alarm analysis and downtime windows are `[from,to)`.

History analysis uses Good samples held until the next sample; intervals before
the first sample are unknown. Bad/stale/uncertain samples do not contribute to
the weighted average or threshold durations. Quality intervals are capped at 100
with `qualityTruncated`. It uses the existing exact-raw point/read budgets and
returns an error rather than silently analyzing downsampled data. Alarm and OEE
analysis reuse the application's existing analytics/rollup semantics; retain
their coverage and truncation metadata when drawing conclusions.

Times are non-negative **Unix milliseconds (UTC)**. Historical ranges must be
ordered and no longer than 31 days. Active alarms are not restricted by age;
omit `from` and `to` for active queries. History uses existing resolution selection,
downsampling, concurrency, timeout and read budgets; it never promises raw values
for every time interval.

List queries cover **all areas**. Explicit tag batches fail
as a whole when any requested tag is nonexistent. Do not infer
online/healthy state from missing values, stale timestamps or bad quality.
Alarm paging is not a snapshot: new alarms can move rows between requests.

Example `tools/call` request:

```json
{
  "jsonrpc": "2.0",
  "id": 2,
  "method": "tools/call",
  "params": {
    "name": "get_realtime",
    "arguments": { "tagIds": [1, 2] }
  }
}
```

Send `Content-Type: application/json`, accept both `application/json` and
`text/event-stream`, and use the protocol version negotiated by the client.

## Resources and prompts

`resources/list` exposes `scada://guide`, a read-only usage/quality/limits guide.
`resources/templates/list` exposes:

- `scada://tags/{tagId}`: tag identity, device, unit, kind, data type and description.
- `scada://devices/{deviceId}`: device identity, area, protocol, enabled flag and
  gateway assignment; excludes connection addresses and credentials.

Use `resources/read` with the concrete URI. These are metadata snapshots, not
live subscriptions. Use tools for current values and paged tag discovery.

`prompts/list` exposes two templates:

- `inspect_device`, argument `deviceId`: read-only inspection using metadata,
  tag discovery, realtime quality and local collector diagnostics.
- `analyze_period`, arguments `from` and `to`: alarm/history/production analysis
  workflow over at most 31 days, preserving evidence and data-quality caveats.

Prompt arguments are decimal **strings**, as required by MCP. IDs and windows
are validated. `prompts/get` returns a workflow message for the client; it does
not execute queries, call an LLM or pretend an analysis has already run.

## Operational boundaries

- Bind the service to its intended LAN interface and restrict access with the
  host/network firewall. The MCP endpoint does not itself enforce a private-IP
  allowlist: anything that can reach it can query it. Do not expose it through
  public port forwarding or an unprotected public reverse proxy. Configure
  `AllowedHosts` to the site addresses/hostnames instead of `*`; HTTPS is
  recommended where LAN traffic confidentiality is required.
- Native clients without an `Origin` header are supported. Requests with an
  `Origin` are rejected; browser-based cross-origin MCP access is not enabled.
  Existing frontend CORS configuration does not grant MCP access.
- The body limit is 64 KiB, including chunked requests. HTTP requests are limited
  per source connection IP, including initialization and listing. An unavailable
  IP shares one `unknown` bucket; clients behind a proxy share its budget.
  Untrusted `X-Forwarded-For` headers do not change this identity.
- Heavy alarm/history/OEE/aggregate-report queries share four concurrent slots
  and a 30-second cooperative cancellation budget. Busy/timeout conditions are
  explicit errors; reduce the range or retry later. Every tool/resource/prompt
  result is limited to 1 MiB (including structured/text representations); reduce
  page size or object count if exceeded.
- Tool calls, resource reads and prompt retrievals write operation audit entries
  with `TargetType=McpTool`, `McpResource` or `McpPrompt`,
  `UserName=mcp:lan`, source address in `TargetId`, operation identifier and outcome.
  No logged-in identity is assumed. Arguments and returned process data are not
  stored in these entries. HTTP rejections remain visible in server request logs.
- The normal license gate protects `/api/mcp`. HA standby nodes reject MCP POST
  requests with 503 under the existing standby policy; connect to the primary.
- Returned names, descriptions and alarm messages are untrusted plant data, not
  instructions for an AI model. The MCP server never calls an external model.
  However, data returned to a cloud-backed AI client may be sent by that client
  to its model provider: obtain site approval or use an approved local model.

| Symptom | Meaning / action |
|---|---|
| 404 | MCP disabled, wrong URL, or old binary. |
| 401 | Not expected from the enabled MCP endpoint; check the URL, deployed binary or reverse proxy authentication. |
| 403 | License blocked or browser Origin supplied; inspect the response body. |
| 413 | Request exceeds 64 KiB. Reduce the payload. |
| 429 | Rate limit reached; honor `Retry-After`. |
| 503 | Standby node; use the primary address. |
| Tool `isError=true` | Nonexistent tag, invalid input, history budget exceeded or tool execution failure. Inspect content and server logs; HTTP 200 alone does not mean success. |
| JSON-RPC `error` | Invalid/missing resource or prompt, or oversized result. Check the message, reduce the query and inspect server logs. |

## Validation

From the repository root:

```powershell
dotnet test tests\Scada.Server.Tests\Scada.Server.Tests.csproj --filter "FullyQualifiedName~Mcp"
```

The tests use isolated temporary sites, an in-process HTTP server, both raw MCP
requests and the official MCP client. They do not connect to production devices.
