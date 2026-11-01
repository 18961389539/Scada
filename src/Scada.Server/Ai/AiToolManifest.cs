namespace Scada.Server.Ai;

/// <summary>
/// 只读工具清单：把 /api/ai/* 的每个端点翻译成 OpenAI function-calling 风格的工具定义。
/// 模型拿到这份清单即可直接挂成工具，无需再手工拼 Function Calling 参数。
/// 清单内容与 AiEndpoints 一一对应：新增端点时同步补充，保持描述与实际行为一致。
/// </summary>
public static class AiToolManifest
{
    public static object Build(AiOptions options) => new
    {
        version = "v1",
        // 显式声明只读：模型据此知道这些工具不会改变现场状态，可以放心用于排障与问答。
        readOnly = true,
        baseUrl = "/api/ai",
        notes = "All endpoints are GET-only and never modify system state. Alarms, history and reports are filtered to the caller's authorized areas.",
        tools = new object[]
        {
            new
            {
                type = "function",
                function = new
                {
                    name = "scada_list_devices",
                    description = "List SCADA devices with their collector status (online, last poll time, cycle time, communication failures). Use this to check whether a device is reachable before reading its values. Devices outside the caller's authorized areas are omitted.",
                    parameters = new
                    {
                        type = "object",
                        properties = new Dictionary<string, object>
                        {
                            ["areaId"] = new { type = "integer", description = "Optional area ID to restrict the list." },
                            ["online"] = new { type = "boolean", description = "Optional filter for online (true) or offline (false) devices." },
                            ["keyword"] = new { type = "string", description = "Optional device name substring." },
                            ["limit"] = new { type = "integer", description = "Maximum devices to return, 1 to 500. Default 100." }
                        },
                        additionalProperties = false
                    }
                }
            },
            new
            {
                type = "function",
                function = new
                {
                    name = "scada_get_realtime",
                    description = "Read current cached values of SCADA tags, with quality and timestamps. Missing tags are returned in missingTagIds and must not be interpreted as zero. Never triggers device communication.",
                    parameters = new
                    {
                        type = "object",
                        properties = new Dictionary<string, object>
                        {
                            ["tagIds"] = new { type = "string", description = "Comma-separated tag IDs, at most 200." },
                            ["names"] = new { type = "string", description = "Comma-separated tag names. Used when tag IDs are unknown." },
                            ["deviceId"] = new { type = "integer", description = "Optional device ID to read all of its tags." }
                        },
                        additionalProperties = false
                    }
                }
            },
            new
            {
                type = "function",
                function = new
                {
                    name = "scada_get_trends",
                    description = "Query historical trend series for 1 to 10 tags over a bounded window (default last 24 hours). Returns possibly downsampled points with Unix millisecond timestamps. Use this to analyze how a value changed over time.",
                    parameters = new
                    {
                        type = "object",
                        properties = new Dictionary<string, object>
                        {
                            ["tagIds"] = new { type = "string", description = "Comma-separated tag IDs, at most 10." },
                            ["names"] = new { type = "string", description = "Comma-separated tag names." },
                            ["from"] = new { type = "string", description = "Start time as Unix milliseconds or ISO-8601. Default 24 hours before 'to'." },
                            ["to"] = new { type = "string", description = "End time as Unix milliseconds or ISO-8601. Default now." },
                            ["maxPoints"] = new { type = "integer", description = "Total point budget, 20 to 1000. Default 500." }
                        },
                        additionalProperties = false
                    }
                }
            },
            new
            {
                type = "function",
                function = new
                {
                    name = "scada_query_alarms",
                    description = "Query alarm history with optional state, level, keyword and tag filters. Only alarms in the caller's authorized areas are returned. Includes the rule's cause and recommended action when available. Read-only: nothing is acknowledged or shelved.",
                    parameters = new
                    {
                        type = "object",
                        properties = new Dictionary<string, object>
                        {
                            ["from"] = new { type = "string", description = "Start time as Unix milliseconds or ISO-8601. Default 7 days before 'to'." },
                            ["to"] = new { type = "string", description = "End time as Unix milliseconds or ISO-8601. Default now." },
                            ["state"] = new { type = "string", description = "Alarm state filter: Unack, Acked, RtnUnack, Active, Shelved or Suppressed." },
                            ["level"] = new { type = "string", description = "Alarm level filter, for example High, Medium or Low." },
                            ["keyword"] = new { type = "string", description = "Optional keyword matched against the alarm message." },
                            ["tagId"] = new { type = "integer", description = "Optional tag ID to filter alarms." },
                            ["offset"] = new { type = "integer", description = "Number of rows to skip. Default 0." },
                            ["limit"] = new { type = "integer", description = $"Page size, 1 to {options.MaxAlarmRows}. Default 50." }
                        },
                        additionalProperties = false
                    }
                }
            },
            new
            {
                type = "function",
                function = new
                {
                    name = "scada_get_reports",
                    description = "Build a daily or monthly aggregate report (average, min, max, sample count) for up to 50 tags. Returns the computed series as JSON; no file is written and nothing is sent out.",
                    parameters = new
                    {
                        type = "object",
                        properties = new Dictionary<string, object>
                        {
                            ["type"] = new { type = "string", description = "Report granularity: 'daily' or 'monthly'. Default 'daily'." },
                            ["from"] = new { type = "string", description = "Start time as Unix milliseconds or ISO-8601." },
                            ["to"] = new { type = "string", description = "End time as Unix milliseconds or ISO-8601." },
                            ["tagIds"] = new { type = "string", description = "Comma-separated tag IDs, at most 50." },
                            ["names"] = new { type = "string", description = "Comma-separated tag names." }
                        },
                        additionalProperties = false
                    }
                }
            },
            new
            {
                type = "function",
                function = new
                {
                    name = "scada_list_tags",
                    description = "List SCADA tags visible to the caller, with name, device, unit and writability. Use this first to resolve tag IDs and units before reading values, trends or reports.",
                    parameters = new
                    {
                        type = "object",
                        properties = new Dictionary<string, object>
                        {
                            ["keyword"] = new { type = "string", description = "Optional name or description substring." },
                            ["deviceId"] = new { type = "integer", description = "Optional device ID to restrict the list." },
                            ["areaId"] = new { type = "integer", description = "Optional area ID." },
                            ["limit"] = new { type = "integer", description = "Maximum tags to return, 1 to 500. Default 100." }
                        },
                        additionalProperties = false
                    }
                }
            }
        }
    };
}
