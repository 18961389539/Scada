# Embedded MCP server

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

Enabling MCP allows **any client that can reach the endpoint to read all areas
and to execute the registered operational and configuration tools** — alarm
acknowledgement and shelving, tag writes, control sequences, recipe downloads,
control-mode changes, lockouts and collector reconnects, plus screen, HMI-layout
and object-template drafts and publishing, downtime confirmation, report
template maintenance and report generation. There is no per-user or per-key
scope/area filtering on this endpoint. Do not send an Authorization or
X-Api-Key header; credentials are not required and do not narrow MCP scope.

Write tools are **enabled by default** and execute directly; there is no
prepare/confirm handshake. They run as user `mcp:lan`, pass through the same
control pipeline as the operator UI (active node, physical writable tag, enabled
device, remote mode, lockouts, value policy, limits, max step, permit expression,
read-back and audit) and are all recorded in the operation audit. Tags, sequences
and recipes that require dual approval are refused because MCP has no login user.
Remote-gateway engineering (including reconnect) returns `unsupported`.

Configuration tools separate drafts from publishing: saving a screen, HMI-layout
or object-template draft does not change the running HMI, and publishing
validates references first and appends immutable versions where the UI does.
Publishing a screen, HMI layout or object template **changes what operators see
and can operate**, and `generate_report` writes archive files and may send email
when the template has recipients.

Anyone who can reach this endpoint can therefore operate plant equipment. Only
enable MCP on a trusted, firewalled LAN, and disable it (see above) where that is
not acceptable. This is not an administrator login and does not grant access to
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

Read tools advertise `readOnlyHint=true`, `destructiveHint=false` and
`openWorldHint=false`; the dry-run tools `check_tag_write` and
`check_recipe_download` are also read-only. Operational tools that change state
set `readOnlyHint=false`, with `destructiveHint=true` for alarm shelving, tag
writes, sequences and recipe downloads. All tools return structured content
with camel-case fields.

| Tool | Arguments and result |
|---|---|
| `search_tags` | Optional `keyword` (max 100 characters), `deviceId`, `areaId`, `dataType`, exact `unit` (max 50 characters), `writable`, `afterId` (default 0), `limit` (1-100, default 50). `areaId` matches physical tags by device area and other tags by explicit area. Returns `items` with `areaId`/`writable`, plus `nextAfterId`. |
| `get_device_status` | `afterId`, `limit` as above. Returns configured devices with cached `status`; null means no status is available, not online. Does not probe or reconnect. |
| `list_areas` | Returns areas (`id`, `name`, `description`, `deviceCount`, `tagCount`, `isDefault`) at most 100; `truncated` reports truncation. Physical tags count in their device area; other tags count under an explicit area only. |
| `get_gateway_overview` | Returns acquisition gateways with lease/connection state, device counts, queue/dead-letter sizes, revisions and reported errors; at most 100, `truncated` reports truncation. Tokens and credentials are never returned. Does not reconnect, send commands or run engineering work. |
| `get_operations_context` | Exactly one of `deviceId` or `areaId`. Returns scope, device rows with cached collector status, tag problem counts (missing/non-Good/older than 5 minutes), up to 50 problem tags, active alarms attributed to the scope with exact counts, and up to 10 related gateways. Each list has `truncated`. Read-only; never probes, reconnects, acknowledges or writes. |
| `query_health` | Returns the existing health snapshot: `status`, `warnings`, HA role, device online counts, up to 50 offline devices (`truncated`), bus/backlog, history capacity and query limits, spool, disk/storage, webhook and notification counters. Does not probe, reconnect or run diagnostics. |
| `get_realtime` | Explicit `tagIds`, 1-100 positive IDs. Returns `values` with quality/timestamps and `missingTagIds` for configured tags without cached data. Missing is not zero. |
| `query_history` | `tagIds` (1-10), `from`, `to`, `maxPoints` (20-1000, default 500). The point budget is **total across all requested tags**, not per tag. Returns `resolution`, `downsampled`, `pointLimit`, `series`. |
| `query_aggregates` | `tagIds` (1-10), `from`, `to`, explicit `resolution` (`FiveMinute` or `Hour`), `maxPoints` (20-2000, default 500). Reads **stored aggregate buckets** with `avg`/`min`/`max`/`count`: 5-minute up to 7 days, hourly up to 366 days. No raw fallback; missing buckets are not zero. The bucket budget is total across tags; larger requests fail with `invalid_argument` instead of truncating. Uses the analysis query budget. |
| `query_alarms` | `activeOnly` (default true), `offset` (0-10000), `limit` (1-100). Historical mode requires `activeOnly=false`, `from`, `to`. Returns `items`, `nextOffset`, `truncated`. If `truncated=true`, narrow the historical range rather than requesting a larger offset. |
| `query_audit_log` | `from`, `to` (at most 31 days), optional exact `userName`/`targetType`, action/result `keyword`, `success`, `offset` (0-10000) and `limit` (1-100). Newest first. Returns operation audit entries; configuration change diffs (`details`) are deliberately omitted and only `hasDetails` is exposed. `oldValue`/`newValue` are capped at 1000 characters and the page sets `truncated` when cut. Read-only; no delete or export. |
| `get_alarm_summary` | Returns current live counts (`unack`, `ackableUnack`, `acked`, `rtnUnack`, `shelved`, `suppressed`, `chattering`), shelved/out-of-service rule counts, the 10-minute `flood` rate and per-area runtime `floodGuard` state including `firstOutEventId` and suppressed counts. Read-only. |
| `list_alarm_rules` | Optional `tagId`, `enabled`, `outOfService`, `afterId`, `limit` (1-100). Returns configured rules with type, threshold, level, delays, deadband, suppress condition and out-of-service state; ascending ID cursor with `nextAfterId`. Read-only. |
| `get_alarm_timeline` | `alarmId`. Returns one alarm view, its stored acknowledgement/note activities and notification delivery log, at most 100 of each with `activitiesTruncated`/`notificationsTruncated` and an aggregate `truncated`. Read-only; does not acknowledge, shelve, suppress or resend. |
| `analyze_alarms` | `from`, `to`, optional `areaId`, `deviceId`, `top` (1-20). Returns counts, priority distribution, frequent sources, acknowledgement/clear means and hourly/daily trend. Preserves analytics `detailFrom`, `usedRollups`, `truncated` flags. |
| `analyze_history` | `tagIds` (1-10), `from`, `to` (**at most 24 hours**), optional finite `lowerLimit`/`upperLimit`. Exact raw-history statistics: time-weighted average, min/max, Good-quality coverage, and strictly below/above-limit durations in milliseconds. Returns no fabricated measurements for empty history. |
| `get_collector_diagnostics` | `deviceId`, `page` (1-100), `limit` (1-100), optional `staleOnly`. Existing **local** collector snapshot: errors, metrics, quality and paged points; at most 50 recent events and samples each. `truncated` reports truncation. No probe/reconnect or gateway engineering command; remote-gateway (`unsupported`) or unavailable snapshots (`data_unavailable`) return an error. Connection strings are omitted. |
| `list_production_catalog` | `kind` (`lines`, `products`, `shifts`), `afterId`, `limit` (1-100). Discovers production IDs, names and areas; returns `nextAfterId`. |
| `query_oee` | `lineIds` (1-10), `from`, `to`, optional `productId`, `shiftId`. Existing OEE calculations: availability/performance/quality, production counts, downtime Pareto, MTBF/MTTR and breakdowns. Ratios are not percentages; null means insufficient data. Pareto is top 20 or the configured smaller count; other lists are capped at 100 with `truncated`. Preserves detail/rollup metadata. |
| `query_downtimes` | `lineIds` (1-10), `from`, `to`, optional `confirmed`, `beforeId`, `limit` (1-100). Events **overlapping** the interval, including those started earlier. Does not confirm or classify events. |
| `query_traceability` | `from`, `to`, optional exact `sn` (max 200 characters), `jobId`, `outcome`, `beforeId`, `limit` (1-50). Persisted captures and fields. Inspection `outcome` is separate from PLC `acknowledged`; neither is modified. Each capture is limited to 100 fields / 65,536 JSON characters; malformed/oversized data returns an error. |
| `query_report` | `tagIds` (1-10), `from`, `to`, `type` (`daily`: hourly rows, max 24 hours; `monthly`: daily rows, max 31 days). **Stored hourly aggregates only**, with `recentRawIncluded=false` and missing tag IDs. No recent-raw fallback; recent aggregate gaps are not zero. Uses existing report calendar grouping. |
| `list_report_runs` | `from`, `to` filter **generation time**; optional `templateId`, `beforeId`, `limit` (1-100). Existing archive metadata, excluding file paths, signatures, recipients and signing configuration. |
| `read_report_run` | `runId`, `offset` (0-10000), `limit` (1-100). Stored table snapshot, columns, summary, total rows and `nextOffset`. Maximum stored JSON is 2 Mi characters, 100 columns and 10,000 rows. Explicit error for missing/malformed/oversized data. Reads database data, not a verified signed PDF/XLSX file. |

The table above lists **24 read-only query tools**; the two dry-run tools below
are also read-only. Together with the 22 state-changing tools (12 operational
plus 10 configuration and production tools), `tools/list` returns **48 tools**
(26 read-only hints and 22 write hints). Downtime, traceability and report-run
lists use descending ID seek pagination (`nextBeforeId`); pass the returned ID as
`beforeId`. Data can change between pages. Traceability timestamps and
report-generation filters are inclusive; alarm analysis and downtime windows are
`[from,to)`.

### Operational tools

These 14 tools cover alarm handling, control and device operations. The two
dry-run tools (`check_tag_write`, `check_recipe_download`) write nothing; the
other 12 execute immediately as user `mcp:lan`. Policy refusals and failures
return structured `success=false` rather than `isError`. Use the dry-run tools
first when the current state is uncertain.

| Tool | Arguments and result |
|---|---|
| `acknowledge_alarm` | `alarmId`, optional `note` (max 1000). Acknowledges as `mcp:lan`; a second acknowledgement only appends the note and keeps the first acknowledgement. Does not clear, shelve or suppress. |
| `annotate_alarm` | `alarmId`, `note` (1-1000). Appends one timeline note without changing acknowledgement or alarm state. |
| `shelve_alarm_rule` | `ruleId`, `minutes` (1 to `AlarmShelveMaxMinutes`, default 480), required `reason` (max 500). Shelved alarms are still recorded but not announced or forwarded; the shelf expires automatically. |
| `unshelve_alarm_rule` | `ruleId`, optional `reason` (max 500). Removes the active shelf; affected active unacknowledged alarms are re-announced. `success=false` when the rule is not shelved. |
| `check_tag_write` | `tagId`, finite `value`. Dry-run of the control pipeline: current value, refusals, confirm/dual-approval flags, limits, max step, mode and lockouts. Writes nothing. |
| `write_tag_value` | `tagId`, finite `value`, optional `requestId` GUID. One write through the control pipeline with read-back and audit. Dual-approval tags are refused. `success=false` for refusals; `outcome=Unknown` means the command may have reached the device — verify before retrying. |
| `start_control_sequence` | `sequenceId`. Starts a run asynchronously; every step re-runs the full control checks. Dual-approval sequences are refused. Returns `runId`; inspect progress with the existing REST run endpoints. |
| `abort_control_sequence` | `runId`. Requests an abort; the current step finishes before it stops. `success=false` when the run is not executing. |
| `check_recipe_download` | `recipeId`, optional `version` (0 = current). Dry-run: per-parameter refusals, current values and the dual-approval flag. |
| `apply_recipe` | `recipeId`, optional `version` (0 = current). Pre-checks every parameter, rejects the whole batch on any refusal, otherwise downloads in the background. Dual-approval recipes are refused. |
| `set_device_control_mode` | `deviceId`, `mode` (`Remote`/`Local`), required `reason` (max 200). Local rejects all central writes, including MCP, until switched back. Audited with the previous mode. |
| `place_control_lockout` | `kind` (`NoOperate`), exactly one of `deviceId`/`tagId`, required `reason` (max 200). Blocks all central writes, including MCP, until removed. `Maintenance` lockouts are refused because they require a logged-in placer. |
| `remove_control_lockout` | `lockoutId`, required `reason` (max 200). Records removal; `success=false` when the lockout does not exist or was already removed. |
| `reconnect_device` | `deviceId`. Requests a local collector reconnect after the current poll and queued commands. Remote-gateway devices return `unsupported`; no data snapshot returns `data_unavailable`. |

### Configuration and production tools

These 10 tools maintain configuration and production archives. Draft/save tools
never change the running HMI: the matching publish tool performs full reference
validation, changes the operator HMI and appends immutable versions. Screen,
HMI-layout and object-template documents travel in the request body and therefore
share the 64 KiB limit — keep nodes compact and prefer editing an existing
document.

| Tool | Arguments and result |
|---|---|
| `create_screen` | `name` (max 100). Creates an empty screen draft and returns `screenId`, `revision`, `publishedRevision`. Drafts are invisible to operators until published. |
| `save_screen_draft` | `screenId`, `revision`, `name`, `document` (screen document). Optimistic revision check plus structural validation; referenced tags and navigation targets are checked only at publish. Returns `issues` and the new `revision`. |
| `publish_screen` | `screenId`, `revision`. Full reference validation (tags, writable control targets, published navigation targets, template instances), then publishes the draft to the operator HMI. Attempts a configuration snapshot; a snapshot failure is returned as `warning`, not a failed publish. |
| `save_hmi_layout` | `name`, `document`, optional `layoutId`, `revision` (0 when creating). Creates or replaces a layout draft; draft validation permits unpublished screens and empty regions. Returns `hasDraftChanges`. |
| `publish_hmi_layout` | `layoutId`, `revision`, required `note` (max 500), optional `rollbackToRevision`. Publishes the current draft, or republishes a stored revision as a rollback. Every screen region must reference a published screen; each publish/rollback appends an immutable version and affects assigned workstations immediately. |
| `save_object_template` | `name`, `document`, optional `templateId`, `revision` (0 when creating). Creates or replaces a device object-template draft; validates tag/alarm definitions, panel symbols and bindings. Returns `revision` and `latestVersion`. |
| `publish_object_template` | `templateId`, `revision`. Full validation (tags/alarms, complete placeholder and control-output bindings, published navigation targets) in a transaction, then appends an immutable template version. |
| `confirm_downtime` | `downtimeId`, optional `reasonId`, `note` (max 500). Confirms one event and optionally classifies it; repeating the same confirmation is a no-op. Confirmation schedules an OEE rollup rebuild from the event start. |
| `save_report_template` | `name`, `period`, `areaId`, `columns`, optional `templateId`, `revision` (0 when creating), `shiftStarts`, `scheduleEnabled`, `scheduleDelayMin` (0-1440, default 10), `emailChannelId`, `emailRecipients`. Validates period, shifts, columns/formulas, referenced tags and calculation jobs, the email channel and unique names. Schedule changes apply to future periods only; `scheduleSinceTs` marks when scheduling started. |
| `generate_report` | `templateId`, optional `at`. Generates the previous complete period, or the period containing `at` (Unix ms). Appends an immutable archive version produced by the existing report pipeline (XLSX/PDF, SHA256, signatures) and returns run metadata including hashes and `version` — never file paths. Sends email when the template has a channel and recipients. |

All 10 return structured `success=false` for revision conflicts, validation
failures and duplicate names, and `isError=true` only for invalid arguments or
missing records. `publish_hmi_layout` requires a note; rollback and publish both
append versions so history is never rewritten.

Write results carry `dataAsOf` and the same structured fields as read results,
plus `success` and a human-readable `message`. `success=false` is a normal
HTTP 200 result, not an error envelope: inspect `message` and `refusals`. A
tag-write `outcome` of `Unknown` means the write or its read-back is
unconfirmed — do not retry blindly. Every write call is audited under
`mcp:lan` with the action, target and outcome; policy refusals are recorded as
`失败：操作未成功`.

`query_health` and `get_alarm_summary` read current caches/runtime state, not
historical records; their counts and flood state change continuously. They never
replace `analyze_alarms` when a time range is required. `list_alarm_rules`,
`list_areas` and `scada://areas` are configuration snapshots. `query_health`
returns the same snapshot as `/api/health`, including a bounded offline-device
list; use `get_device_status` for paged device state.

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
for every time interval. `query_aggregates` is the only tool that selects stored
aggregate resolution explicitly; its window is 7 days for `FiveMinute` and 366
days for `Hour`.

`get_operations_context` is a one-call snapshot for triage, not a replacement for
the detailed tools: alarms are restricted to the selected device/area, system
alarms without a device or tag are excluded, and "stale" means older than five
minutes. Use `query_alarms`, `analyze_alarms`, `get_collector_diagnostics` or
`query_history` for history and deeper evidence.

List queries cover **all areas**. Explicit tag batches fail
as a whole when any requested tag is nonexistent. Do not infer
online/healthy state from missing values, stale timestamps or bad quality.
Alarm paging is not a snapshot: new alarms can move rows between requests.

## Result metadata and error contract

Every tool result carries two standard fields:

- `dataAsOf`: UTC Unix milliseconds when the underlying data was read. It is not
  the newest sample time; live values and records keep their own timestamps.
  `get_collector_diagnostics` uses the snapshot capture time. For static
  configuration tools (`search_tags`, `list_alarm_rules`, `list_areas`,
  `list_production_catalog`, `get_gateway_overview`, `query_audit_log`) it is the
  read time.
- `truncated`: `true` when a hard cap dropped data that paging cannot reach. A page
  with `nextAfterId`, `nextBeforeId` or `nextOffset` is not truncated by itself.
  Tool-specific flags (`activitiesTruncated`, `notificationsTruncated`,
  `qualityTruncated`) remain available.

Tool failures are HTTP 200 results with `isError=true`. `structuredContent.error`
contains a stable code, a message and a `retryable` flag:

| Code | Meaning | Retry |
|---|---|---|
| `invalid_argument` | Missing, malformed or out-of-range argument. | No; fix arguments. |
| `not_found` | Referenced tag, device, area, line, product, shift, alarm or report run does not exist. | No. |
| `unsupported` | Requested data is not available through MCP, for example remote-gateway diagnostics or reconnect. | No. |
| `data_unavailable` | A transient snapshot (collector diagnostics) is not cached yet. | Yes. |
| `data_integrity` | Stored JSON is malformed or contains invalid values. | No; inspect the record. |
| `result_too_large` | Result or stored snapshot exceeds its size budget, or history read limits were hit. | No; reduce range or page size. |
| `query_busy` | Four heavy analysis queries are already running. | Yes, later. |
| `query_timeout` | The 30-second analysis budget expired. | Yes, with a smaller range. |
| `internal` | Unexpected server failure. | No; inspect server logs. |

The text content of an error result repeats the same JSON envelope, so clients that
ignore `structuredContent` still see the code. Resource and prompt failures remain
JSON-RPC errors, and HTTP-level rejections (403/413/429/503) are unchanged.

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

`resources/list` exposes:

- `scada://guide`: querying and operational guidance, quality rules, write
  semantics, limits and the error contract.
- `scada://areas`: the area catalog with device/tag counts.
- `scada://metrics`: MCP usage for the last 24 hours — tool/resource/prompt call
  counts, success/failure counts and failure codes aggregated from the operation
  audit, plus analysis-budget saturation. Contains no arguments or plant data.
- `scada://contract`: the stable machine-readable contract — `contractVersion`,
  `dataAsOf`/`truncated` semantics, `writeActor`/`writeSemantics`, all error codes
  with default retryability and the request/result/page/aggregate limits.

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
  allowlist: anything that can reach it can query and operate it. Do not expose
  it through public port forwarding or an unprotected public reverse proxy.
  Configure `AllowedHosts` to the site addresses/hostnames instead of `*`; HTTPS is
  recommended where LAN traffic confidentiality is required.
- Native clients without an `Origin` header are supported. Requests with an
  `Origin` are rejected; browser-based cross-origin MCP access is not enabled.
  Existing frontend CORS configuration does not grant MCP access.
- The body limit is 64 KiB, including chunked requests. This also bounds
  screen, HMI-layout and object-template documents sent to the save tools. HTTP
  requests are limited per source connection IP, including initialization and
  listing. An unavailable IP shares one `unknown` bucket; clients behind a proxy
  share its budget. Untrusted `X-Forwarded-For` headers do not change this
  identity.
- Heavy alarm summary/analysis, history, OEE and aggregate-report queries share four concurrent slots
  and a 30-second cooperative cancellation budget. Busy (`query_busy`) and timeout
  (`query_timeout`) conditions are explicit errors; reduce the range or retry later.
  Every tool/resource/prompt result is limited to 1 MiB (including structured/text
  representations); an oversized tool result returns `result_too_large` with
  `isError=true`, while oversized resource/prompt results remain JSON-RPC errors.
- Tool calls, resource reads and prompt retrievals write operation audit entries
  with `TargetType=McpTool`, `McpResource` or `McpPrompt`,
  `UserName=mcp:lan`, source address in `TargetId`, operation identifier and outcome
  (policy refusals are recorded as `失败：操作未成功`). No logged-in identity is
  assumed. Arguments and returned process data are not stored in these entries.
  Operational tools additionally write domain audit rows through the control,
  alarm, mode, lockout and reconnect paths. Configuration and production tools
  write domain rows for screens, HMI layouts, object templates, downtime
  confirmation, report templates and generated report runs. HTTP rejections
  remain visible in server request logs. Audit entries are themselves readable through
  `query_audit_log`; configuration change diffs are omitted there and only
  `hasDetails` is exposed. `scada://metrics` aggregates these entries for the
  last 24 hours without arguments or plant data.
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
| Tool `isError=true` | Inspect `structuredContent.error.code` and `retryable`. Invalid input or missing records require changed arguments; `query_busy`/`query_timeout`/`data_unavailable` may be retried. HTTP 200 alone does not mean success. |
| Tool `success=false` | A write action was refused or failed. Read `message`/`refusals`; when a tag write reports `outcome=Unknown`, verify the actual device state before retrying. |
| JSON-RPC `error` | Invalid/missing resource or prompt, or oversized resource/prompt result. Check the message, reduce the query and inspect server logs. |

## Validation

From the repository root:

```powershell
dotnet test tests\Scada.Server.Tests\Scada.Server.Tests.csproj --filter "FullyQualifiedName~Mcp"
```

The tests use isolated temporary sites, an in-process HTTP server, both raw MCP
requests and the official MCP client. They do not connect to production devices.
