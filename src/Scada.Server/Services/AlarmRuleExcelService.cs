using System.Text;
using System.Text.Json;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Models;
using Scada.Data;
using Scada.Server.Endpoints;

namespace Scada.Server.Services;

public sealed record AlarmRuleImportResult(int Created, int Updated, int Skipped, IReadOnlyList<string> Errors);

// 报警规则导入导出：列名即语义，导入按稳定键或规则 ID 匹配，避免重复生成；
// 每行都走与接口相同的结构与目标校验，成功后由调用方热加载配置并写审计。
public sealed class AlarmRuleExcelService
{
    private static readonly string[] Headers =
    [
        "规则ID", "规则键", "测点ID", "测点名称", "类型", "触发条件", "阈值", "位号", "表达式",
        "等级", "类别", "后果", "允许响应时间(秒)", "自动优先级", "触发延时(秒)", "恢复延时(秒)", "恢复死区",
        "启用", "报警信息", "英文报警信息", "原因", "英文原因", "处置", "英文处置",
        "条件抑制方式", "条件测点ID", "停用", "停用原因"
    ];

    public byte[] ExportXlsx(IReadOnlyList<AlarmRule> rules, IReadOnlyDictionary<int, string>? tagNames = null)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add("报警规则");
        for (var i = 0; i < Headers.Length; i++) sheet.Cell(1, i + 1).Value = Headers[i];
        sheet.Row(1).Style.Font.Bold = true;
        sheet.SheetView.FreezeRows(1);
        var row = 2;
        foreach (var rule in rules.OrderBy(x => x.TagId).ThenBy(x => x.Type).ThenBy(x => x.Id))
        {
            WriteRow((index, value) => sheet.Cell(row, index + 1).Value = value, rule, tagNames);
            row++;
        }

        sheet.Columns().AdjustToContents();
        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    public byte[] ExportCsv(IReadOnlyList<AlarmRule> rules, IReadOnlyDictionary<int, string>? tagNames = null)
    {
        var lines = new List<string> { Join(Headers) };
        foreach (var rule in rules.OrderBy(x => x.TagId).ThenBy(x => x.Type).ThenBy(x => x.Id))
        {
            var values = new string[Headers.Length];
            WriteRow((index, value) => values[index] = value, rule, tagNames);
            lines.Add(Join(values));
        }

        return Encoding.UTF8.GetBytes("\uFEFF" + string.Join("\r\n", lines));
    }

    private static void WriteRow(Action<int, string> write, AlarmRule rule, IReadOnlyDictionary<int, string>? tagNames)
    {
        var values = new[]
        {
            rule.Id.ToString(), rule.Key, rule.TagId.ToString(), tagNames?.GetValueOrDefault(rule.TagId) ?? "", rule.Type.ToString(),
            rule.Type == AlarmRuleType.Discrete ? rule.ActiveWhen.ToString() : "",
            Format(rule.Threshold), rule.BitIndex?.ToString() ?? "", rule.Expression ?? "",
            rule.Level.ToString(), rule.Category.ToString(), rule.Consequence.ToString(),
            rule.ResponseTimeSec.ToString(), Bool(rule.AutoPriority), rule.DelaySec.ToString(),
            rule.OffDelaySec.ToString(), Format(rule.RecoverDeadband),
            Bool(rule.Enabled), rule.Message ?? "", rule.MessageEn ?? "", rule.Cause ?? "", rule.CauseEn ?? "",
            rule.Action ?? "", rule.ActionEn ?? "",
            rule.SuppressMode.ToString(), rule.SuppressTagId?.ToString() ?? "",
            Bool(rule.OutOfService), rule.OutOfServiceReason ?? ""
        };
        for (var i = 0; i < values.Length; i++) write(i, values[i]);
    }

    public async Task<AlarmRuleImportResult> ImportAsync(
        Stream stream,
        string fileName,
        IDbContextFactory<ConfigDbContext> contextFactory, IDbContextFactory<RuntimeDbContext> runtimeFactory,
        int? userId,
        string userName,
        CancellationToken cancellationToken)
    {
        var rows = fileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) ? ReadCsv(stream) : ReadXlsx(stream);
        var errors = new List<string>();
        var created = 0;
        var updated = 0;
        var skipped = 0;
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var runtime = await runtimeFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var tags = await db.Tags.AsNoTracking().ToDictionaryAsync(x => x.Id, cancellationToken);
        var byName = tags.Values.GroupBy(x => x.Name).Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.Single());
        var existingRules = await db.AlarmRules.ToListAsync(cancellationToken);
        var existingByKey = existingRules.Where(x => x.Key.Length > 0).GroupBy(x => x.Key).ToDictionary(g => g.Key, g => g.First());
        var existingById = existingRules.ToDictionary(x => x.Id);
        var changes = new List<(AlarmRule Rule, string? Before, string Action)>();
        var signatures = new HashSet<string>(StringComparer.Ordinal);

        foreach (var row in rows)
        {
            try
            {
                var name = Get(row, "测点名称");
                var tagId = ParseInt(Get(row, "测点ID"));
                var tag = tagId is int id && tags.TryGetValue(id, out var byId) ? byId
                    : !string.IsNullOrWhiteSpace(name) && byName.TryGetValue(name.Trim(), out var byTagName) ? byTagName : null;
                if (tag is null)
                {
                    errors.Add($"第 {row.RowNumber} 行：测点不存在（ID “{Get(row, "测点ID")}”，名称 “{name}”）");
                    skipped++;
                    continue;
                }

                var key = Get(row, "规则键").Trim();
                var oldId = ParseInt(Get(row, "规则ID")) ?? 0;
                var existing = (key.Length > 0 ? existingByKey.GetValueOrDefault(key) : null)
                    ?? (oldId > 0 ? existingById.GetValueOrDefault(oldId) : null);
                var rule = existing is null ? new AlarmRule() : Clone(existing);
                rule.TemplateFrom(row.Get, tag.Id);
                var structural = EndpointHelpers.ValidateAlarmRule(rule);
                if (structural is not null)
                {
                    errors.Add($"第 {row.RowNumber} 行：{structural}");
                    skipped++;
                    continue;
                }

                var target = await EndpointHelpers.ValidateAlarmRuleTargetAsync(db, rule, existing?.Id, cancellationToken);
                if (target is not null)
                {
                    errors.Add($"第 {row.RowNumber} 行：{target}");
                    skipped++;
                    continue;
                }

                if (rule.OutOfService && string.IsNullOrWhiteSpace(rule.OutOfServiceReason))
                {
                    errors.Add($"第 {row.RowNumber} 行：停用状态必须填写停用原因");
                    skipped++;
                    continue;
                }

                var effectiveLevel = rule.AutoPriority ? AlarmPriority.Compute(rule.Consequence, rule.ResponseTimeSec) : rule.Level;
                var signature = $"{rule.TagId}|{rule.Type}|{effectiveLevel}|{rule.Enabled}|{rule.Message}|{rule.BitIndex}|{rule.Expression}|{rule.ActiveWhen}|{rule.Threshold}";
                if (!signatures.Add(signature))
                {
                    errors.Add($"第 {row.RowNumber} 行：同一工作簿中存在相同的报警规则");
                    skipped++;
                    continue;
                }

                // 复合条件引用的测点必须存在，避免导入后运行期只报错不报警。
                if (rule.Type == AlarmRuleType.Expression && rule.Expression is { Length: > 0 } expressionText)
                {
                    var expression = Scada.Core.Calculations.TagExpression.Compile(expressionText);
                    var missing = expression.Dependencies.Where(id => !tags.ContainsKey(id)).Take(10).ToArray();
                    if (missing.Length > 0)
                    {
                        errors.Add($"第 {row.RowNumber} 行：表达式引用测点不存在：{string.Join(",", missing)}");
                        skipped++;
                        continue;
                    }
                }

                if (existing is null)
                {
                    // 批量导入保留汇总审计，抑制拦截器的逐条留痕。
                    using var importAudit = Scada.Core.Security.AuditContext.Suppress();
                    db.AlarmRules.Add(rule);
                    if (rule.Key.Length == 0) rule.Key = Guid.NewGuid().ToString("N");
                    existingByKey[rule.Key] = rule;
                    changes.Add((rule, null, "新增"));
                    created++;
                }
                else
                {
                    var before = JsonSerializer.Serialize(existing);
                    rule.Id = existing.Id;
                    rule.Key = existing.Key;
                    db.Entry(existing).CurrentValues.SetValues(rule);
                    changes.Add((existing, before, "修改"));
                    updated++;
                }
            }
            catch (Exception ex)
            {
                if (ex is DbUpdateException) throw;
                errors.Add($"第 {row.RowNumber} 行：{ex.Message}");
                skipped++;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        foreach (var (rule, before, action) in changes)
        {
            runtime.OperationLogs.Add(new OperationLog
            {
                UserId = userId, UserName = userName, Ts = ScadaTime.NowMs(), Action = $"导入报警规则：{action} #{rule.Id}",
                TargetType = "AlarmRule", TargetId = rule.Id.ToString(), OldValue = before, NewValue = JsonSerializer.Serialize(rule), Result = "成功"
            });
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await runtime.SaveChangesAsync(cancellationToken);
        return new AlarmRuleImportResult(created, updated, skipped, errors.Take(50).ToList());
    }

    private static AlarmRule Clone(AlarmRule source) => new()
    {
        Key = source.Key, TagId = source.TagId, Type = source.Type, ActiveWhen = source.ActiveWhen, Threshold = source.Threshold,
        Level = source.Level, Category = source.Category, Consequence = source.Consequence, ResponseTimeSec = source.ResponseTimeSec,
        AutoPriority = source.AutoPriority, DelaySec = source.DelaySec, RecoverDeadband = source.RecoverDeadband,
        Enabled = source.Enabled, Message = source.Message, MessageEn = source.MessageEn, Cause = source.Cause, CauseEn = source.CauseEn,
        Action = source.Action, ActionEn = source.ActionEn, OffDelaySec = source.OffDelaySec, BitIndex = source.BitIndex,
        Expression = source.Expression, SuppressTagId = source.SuppressTagId, SuppressMode = source.SuppressMode,
        OutOfService = source.OutOfService, OutOfServiceReason = source.OutOfServiceReason
    };

    private sealed record SheetRow(int RowNumber, Func<string, string> Get);

    private static string Get(SheetRow row, string header) => row.Get(header).Trim();

    private static List<SheetRow> ReadXlsx(Stream stream)
    {
        using var workbook = new XLWorkbook(stream);
        var sheet = workbook.Worksheet(1);
        var lastRow = sheet.LastRowUsed()?.RowNumber() ?? 1;
        var lastColumn = sheet.LastColumnUsed()?.ColumnNumber() ?? 1;
        var columns = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var column = 1; column <= lastColumn; column++)
        {
            var header = sheet.Cell(1, column).GetString().Trim();
            if (header.Length > 0) columns[header] = column;
        }

        var rows = new List<SheetRow>();
        for (var number = 2; number <= lastRow; number++)
        {
            var rowNumber = number;
            rows.Add(new SheetRow(rowNumber, header =>
                columns.TryGetValue(header, out var column) ? sheet.Cell(rowNumber, column).GetString() : ""));
        }

        return rows;
    }

    private static List<SheetRow> ReadCsv(Stream stream)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var records = ParseCsv(reader.ReadToEnd());
        if (records.Count == 0) return [];
        var headers = records[0].Select(x => x.Trim()).ToArray();
        var rows = new List<SheetRow>();
        for (var i = 1; i < records.Count; i++)
        {
            var values = records[i];
            var rowNumber = i + 1;
            rows.Add(new SheetRow(rowNumber, header =>
            {
                var index = Array.IndexOf(headers, header);
                return index >= 0 && index < values.Count ? values[index] : "";
            }));
        }

        return rows;
    }

    // 支持带引号、逗号与双引号转义的 CSV；空行忽略。
    private static List<List<string>> ParseCsv(string text)
    {
        var records = new List<List<string>>();
        var current = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (quoted)
            {
                if (ch == '"' && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                else if (ch == '"') quoted = false;
                else field.Append(ch);
                continue;
            }

            switch (ch)
            {
                case '"': quoted = true; break;
                case ',': current.Add(field.ToString()); field.Clear(); break;
                case '\r': break;
                case '\n':
                    current.Add(field.ToString()); field.Clear();
                    if (current.Count > 1 || current[0].Length > 0) records.Add(current);
                    current = [];
                    break;
                default: field.Append(ch); break;
            }
        }

        if (field.Length > 0 || current.Count > 0)
        {
            current.Add(field.ToString());
            if (current.Count > 1 || current[0].Length > 0) records.Add(current);
        }

        return records;
    }

    private static string Join(IEnumerable<string> values) =>
        string.Join(",", values.Select(value => "\"" + value.Replace("\"", "\"\"") + "\""));

    private static int? ParseInt(string text) => int.TryParse(text.Trim(), out var value) ? value : null;
    private static double ParseDouble(string text, double fallback = 0) => double.TryParse(text.Trim(), out var value) ? value : fallback;
    private static string Format(double value) => value.ToString("0.###");
    private static string Bool(bool value) => value ? "是" : "否";
}

// 把一行文本映射成规则字段；枚举同时接受英文名称与界面中文标签，方便现场直接改表。
internal static class AlarmRuleRowMapping
{
    private static readonly Dictionary<string, AlarmRuleType> Types = new(StringComparer.OrdinalIgnoreCase)
    {
        ["高限"] = AlarmRuleType.High, ["高高限"] = AlarmRuleType.HighHigh, ["低限"] = AlarmRuleType.Low, ["低低限"] = AlarmRuleType.LowLow,
        ["状态变化"] = AlarmRuleType.StateChange, ["位报警"] = AlarmRuleType.Discrete, ["质量坏"] = AlarmRuleType.BadQuality, ["复合条件"] = AlarmRuleType.Expression,
        ["High"] = AlarmRuleType.High, ["HighHigh"] = AlarmRuleType.HighHigh, ["Low"] = AlarmRuleType.Low, ["LowLow"] = AlarmRuleType.LowLow,
        ["StateChange"] = AlarmRuleType.StateChange, ["Discrete"] = AlarmRuleType.Discrete, ["BadQuality"] = AlarmRuleType.BadQuality, ["Expression"] = AlarmRuleType.Expression
    };

    private static readonly Dictionary<string, AlarmCategory> Categories = new(StringComparer.OrdinalIgnoreCase)
    {
        ["工艺"] = AlarmCategory.Process, ["设备"] = AlarmCategory.Equipment, ["安全"] = AlarmCategory.Safety, ["诊断"] = AlarmCategory.Diagnostic,
        ["Process"] = AlarmCategory.Process, ["Equipment"] = AlarmCategory.Equipment, ["Safety"] = AlarmCategory.Safety, ["Diagnostic"] = AlarmCategory.Diagnostic
    };

    private static readonly Dictionary<string, AlarmConsequence> Consequences = new(StringComparer.OrdinalIgnoreCase)
    {
        ["轻微"] = AlarmConsequence.Minor, ["一般"] = AlarmConsequence.Major, ["严重"] = AlarmConsequence.Severe, ["安全"] = AlarmConsequence.Safety,
        ["Minor"] = AlarmConsequence.Minor, ["Major"] = AlarmConsequence.Major, ["Severe"] = AlarmConsequence.Severe, ["Safety"] = AlarmConsequence.Safety
    };

    private static readonly Dictionary<string, AlarmSuppressMode> SuppressModes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["无条件"] = AlarmSuppressMode.None, ["条件为1"] = AlarmSuppressMode.WhenOn, ["条件为0"] = AlarmSuppressMode.WhenOff,
        ["None"] = AlarmSuppressMode.None, ["WhenOn"] = AlarmSuppressMode.WhenOn, ["WhenOff"] = AlarmSuppressMode.WhenOff
    };

    public static void TemplateFrom(this AlarmRule rule, Func<string, string> get, int tagId)
    {
        rule.TagId = tagId;
        rule.Type = Parse(get("类型"), Types, AlarmRuleType.High);
        rule.ActiveWhen = Parse(get("触发条件"), new Dictionary<string, AlarmActiveWhen>(StringComparer.OrdinalIgnoreCase)
        {
            ["ON"] = AlarmActiveWhen.On, ["OFF"] = AlarmActiveWhen.Off, ["变化"] = AlarmActiveWhen.Change,
            ["On"] = AlarmActiveWhen.On, ["Off"] = AlarmActiveWhen.Off, ["Change"] = AlarmActiveWhen.Change
        }, AlarmActiveWhen.On);
        rule.Threshold = Number(get("阈值"), 0);
        rule.BitIndex = Int(get("位号"));
        rule.Expression = Empty(get("表达式"));
        rule.Level = Parse(get("等级"), new Dictionary<string, AlarmLevel>(StringComparer.OrdinalIgnoreCase)
        {
            ["提示"] = AlarmLevel.Info, ["警告"] = AlarmLevel.Warning, ["严重"] = AlarmLevel.Critical,
            ["Info"] = AlarmLevel.Info, ["Warning"] = AlarmLevel.Warning, ["Critical"] = AlarmLevel.Critical
        }, AlarmLevel.Warning);
        rule.Category = Parse(get("类别"), Categories, AlarmCategory.Process);
        rule.Consequence = Parse(get("后果"), Consequences, AlarmConsequence.Major);
        rule.ResponseTimeSec = (int)Number(get("允许响应时间(秒)"), 600);
        rule.AutoPriority = Bool(get("自动优先级"));
        rule.DelaySec = (int)Number(get("触发延时(秒)"), 0);
        rule.OffDelaySec = (int)Number(get("恢复延时(秒)"), 0);
        rule.RecoverDeadband = Number(get("恢复死区"), 0);
        rule.Enabled = BoolDefault(get("启用"), true);
        rule.Message = Empty(get("报警信息"));
        rule.MessageEn = Empty(get("英文报警信息"));
        rule.Cause = Empty(get("原因"));
        rule.CauseEn = Empty(get("英文原因"));
        rule.Action = Empty(get("处置"));
        rule.ActionEn = Empty(get("英文处置"));
        rule.SuppressMode = Parse(get("条件抑制方式"), SuppressModes, AlarmSuppressMode.None);
        rule.SuppressTagId = Int(get("条件测点ID"));
        rule.OutOfService = Bool(get("停用"));
        rule.OutOfServiceReason = Empty(get("停用原因"));
        rule.ActiveWhen = AlarmRuleText.NormalizeActiveWhen(rule.Type, rule.ActiveWhen);
        if (rule.SuppressMode == AlarmSuppressMode.None) rule.SuppressTagId = null;
        if (rule.Type != AlarmRuleType.Discrete) rule.BitIndex = null;
        if (rule.Type != AlarmRuleType.Expression) rule.Expression = null;
        rule.ApplyAutoPriority();
    }

    private static T Parse<T>(string text, Dictionary<string, T> aliases, T fallback) where T : struct, Enum
    {
        var value = text.Trim();
        if (aliases.TryGetValue(value, out var alias)) return alias;
        return Enum.TryParse<T>(value, true, out var parsed) && Enum.IsDefined(parsed) ? parsed : fallback;
    }

    private static string? Empty(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    private static int? Int(string text) => int.TryParse(text.Trim(), out var value) ? value : null;
    private static double Number(string text, double fallback) => double.TryParse(text.Trim(), out var value) ? value : fallback;
    private static bool Bool(string text) => text.Trim().ToLowerInvariant() is "是" or "1" or "true" or "yes";
    private static bool BoolDefault(string text, bool fallback) => string.IsNullOrWhiteSpace(text) ? fallback : Bool(text);
}
