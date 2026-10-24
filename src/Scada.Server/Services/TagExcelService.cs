using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Calculations;
using Scada.Core.Models;
using System.Text.RegularExpressions;
using Scada.Data;
using Scada.Server.Endpoints;

namespace Scada.Server.Services;

public sealed record ImportResult(int Created, int Updated, int Skipped, IReadOnlyList<string> Errors);

public sealed class TagExcelService
{
    private static readonly string[] Headers =
    [
        "设备名称", "测点名称", "描述", "区域", "地址", "数据类型", "系数", "偏移",
        "单位", "可写", "存储方式", "死区", "存储周期(秒)", "最小值", "最大值", "DB编号", "位偏移", "NodeId", "测点ID"
    ];

    public byte[] Export(IReadOnlyList<Device> devices, IReadOnlyList<Tag> tags)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add("测点");
        for (var i = 0; i < Headers.Length; i++)
        {
            sheet.Cell(1, i + 1).Value = Headers[i];
        }

        sheet.Row(1).Style.Font.Bold = true;
        sheet.SheetView.FreezeRows(1);
        var deviceNames = devices.ToDictionary(x => x.Id, x => x.Name);

        var row = 2;
        foreach (var tag in tags.Where(x => x.Kind == TagKind.Physical).OrderBy(x => x.DeviceId).ThenBy(x => x.Address))
        {
            sheet.Cell(row, 1).Value = tag.DeviceId is int id ? deviceNames.GetValueOrDefault(id, string.Empty) : "计算测点";
            sheet.Cell(row, 2).Value = tag.Name;
            sheet.Cell(row, 3).Value = tag.Description ?? string.Empty;
            sheet.Cell(row, 4).Value = tag.Area.ToString();
            sheet.Cell(row, 5).Value = tag.Address;
            sheet.Cell(row, 6).Value = tag.DataType.ToString();
            sheet.Cell(row, 7).Value = tag.Scale;
            sheet.Cell(row, 8).Value = tag.Offset;
            sheet.Cell(row, 9).Value = tag.Unit ?? string.Empty;
            sheet.Cell(row, 10).Value = tag.Writable ? "是" : "否";
            sheet.Cell(row, 11).Value = tag.ArchiveMode.ToString();
            sheet.Cell(row, 12).Value = tag.Deadband;
            sheet.Cell(row, 13).Value = tag.ArchivePeriodSec;
            sheet.Cell(row, 16).Value = tag.DbNumber;
            sheet.Cell(row, 17).Value = tag.BitOffset;
            sheet.Cell(row, 18).Value = tag.NodeId;
            sheet.Cell(row, 19).Value = tag.Id;
            if (tag.MinValue is double min)
            {
                sheet.Cell(row, 14).Value = min;
            }

            if (tag.MaxValue is double max)
            {
                sheet.Cell(row, 15).Value = max;
            }

            row++;
        }

        sheet.Columns().AdjustToContents();
        var calculations = tags.Where(x => x.Kind == TagKind.Calculated).OrderBy(x => x.Id).ToArray();
        if (calculations.Length > 0)
        {
            var calculated = workbook.Worksheets.Add("计算测点");
            var headers = new[] { "原测点ID", "名称", "表达式", "描述", "单位", "启用", "计算周期(ms)", "输入有效期(ms)", "存储方式", "死区", "存储周期(秒)" };
            for (var i = 0; i < headers.Length; i++) calculated.Cell(1, i + 1).Value = headers[i];
            calculated.Row(1).Style.Font.Bold = true; calculated.SheetView.FreezeRows(1); var index = 2;
            foreach (var tag in calculations)
            {
                calculated.Cell(index, 1).Value = tag.Id; calculated.Cell(index, 2).Value = tag.Name; calculated.Cell(index, 3).Value = tag.Expression;
                calculated.Cell(index, 4).Value = tag.Description ?? ""; calculated.Cell(index, 5).Value = tag.Unit ?? ""; calculated.Cell(index, 6).Value = tag.CalculationEnabled ? "是" : "否";
                calculated.Cell(index, 7).Value = tag.CalculationIntervalMs; calculated.Cell(index, 8).Value = tag.InputMaxAgeMs; calculated.Cell(index, 9).Value = tag.ArchiveMode.ToString();
                calculated.Cell(index, 10).Value = tag.Deadband; calculated.Cell(index, 11).Value = tag.ArchivePeriodSec; index++;
            }
            calculated.Columns().AdjustToContents();
        }
        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    public async Task<ImportResult> ImportAsync(
        Stream stream,
        IDbContextFactory<ConfigDbContext> contextFactory,
        CancellationToken cancellationToken, int? userId = null, string userName = "system")
    {
        using var workbook = new XLWorkbook(stream);
        var sheet = workbook.Worksheet(1);
        var lastRow = sheet.LastRowUsed()?.RowNumber() ?? 1;
        var errors = new List<string>();
        var created = 0;
        var updated = 0;
        var skipped = 0;

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var hasCalculations = workbook.Worksheets.TryGetWorksheet("计算测点", out var calculationSheet);
        var idMap = new Dictionary<int, Tag>();
        var physicalNames = new HashSet<(int DeviceId, string Name)>();
        var devices = await db.Devices.ToDictionaryAsync(x => x.Name, cancellationToken);
        var tags = await db.Tags.Where(x => x.Kind == TagKind.Physical).ToDictionaryAsync(x => (x.DeviceId!.Value, x.Name), cancellationToken);

        for (var row = 2; row <= lastRow; row++)
        {
            try
            {
                var deviceName = sheet.Cell(row, 1).GetString().Trim();
                var tagName = sheet.Cell(row, 2).GetString().Trim();
                if (string.IsNullOrWhiteSpace(deviceName) && string.IsNullOrWhiteSpace(tagName))
                {
                    continue;
                }

                if (!devices.TryGetValue(deviceName, out var device))
                {
                    errors.Add($"第 {row} 行：设备“{deviceName}”不存在");
                    skipped++;
                    continue;
                }

                if (string.IsNullOrWhiteSpace(tagName))
                {
                    errors.Add($"第 {row} 行：测点名称不能为空");
                    skipped++;
                    continue;
                }

                if (!TryParseEnum(sheet.Cell(row, 4).GetString(), out TagArea area))
                {
                    errors.Add($"第 {row} 行：区域“{sheet.Cell(row, 4).GetString()}”无效");
                    skipped++;
                    continue;
                }

                if (!TryParseEnum(sheet.Cell(row, 6).GetString(), out TagDataType dataType))
                {
                    errors.Add($"第 {row} 行：数据类型“{sheet.Cell(row, 6).GetString()}”无效");
                    skipped++;
                    continue;
                }

                if (!TryParseEnum(sheet.Cell(row, 11).GetString(), out ArchiveMode archiveMode))
                {
                    archiveMode = ArchiveMode.OnChange;
                }

                var key = (device.Id, tagName);
                if (hasCalculations && !physicalNames.Add(key)) throw new TagExpressionException("含计算定义的工作簿不能包含重复的物理测点名称");
                var exists = tags.TryGetValue(key, out var tag);
                // Validate a detached copy: a skipped invalid row must not change a tracked entity.
                tag = tag?.Clone() ?? new Tag { DeviceId = device.Id, Name = tagName };

                tag.Description = EmptyToNull(sheet.Cell(row, 3).GetString());
                tag.Area = area;
                tag.Address = GetInt(sheet.Cell(row, 5), 0);
                tag.DbNumber = GetInt(sheet.Cell(row, 16), 1);
                tag.BitOffset = GetInt(sheet.Cell(row, 17), 0);
                tag.NodeId = sheet.Cell(row, 18).GetString().Trim();
                tag.DataType = dataType;
                tag.Scale = GetDouble(sheet.Cell(row, 7), 1);
                tag.Offset = GetDouble(sheet.Cell(row, 8), 0);
                tag.Unit = EmptyToNull(sheet.Cell(row, 9).GetString());
                tag.Writable = ParseBool(sheet.Cell(row, 10).GetString());
                tag.ArchiveMode = archiveMode;
                tag.Deadband = GetDouble(sheet.Cell(row, 12), 0);
                tag.ArchivePeriodSec = GetInt(sheet.Cell(row, 13), 600);
                tag.MinValue = GetNullableDouble(sheet.Cell(row, 14));
                tag.MaxValue = GetNullableDouble(sheet.Cell(row, 15));

                var error = Scada.Core.Models.ConfigurationValidator.Tag(tag, device);
                if (error is not null)
                {
                    errors.Add($"第 {row} 行：{error}");
                    skipped++;
                    continue;
                }

                var sourceId = hasCalculations ? GetSourceId(sheet.Cell(row, 19)) : GetInt(sheet.Cell(row, 19), 0);
                if (hasCalculations && (sourceId < 1 || idMap.ContainsKey(sourceId))) throw new TagExpressionException("含计算定义的工作簿须为每个物理测点提供唯一的测点ID（第19列）");

                if (exists)
                {
                    db.Entry(tags[key]).CurrentValues.SetValues(tag);
                    updated++;
                }
                else
                {
                    db.Tags.Add(tag);
                    tags[key] = tag;
                    created++;
                }
                if (sourceId > 0) idMap[sourceId] = tags[key];
            }
            catch (Exception ex)
            {
                errors.Add($"第 {row} 行：{ex.Message}");
                skipped++;
            }
        }

        if (hasCalculations && errors.Count > 0) return new(0, 0, skipped, errors.Prepend("含计算定义的工作簿须整体有效，未导入任何行").Take(50).ToArray());
        await db.SaveChangesAsync(cancellationToken);
        if (hasCalculations)
        {
            try
            {
                var result = await ImportCalculationsAsync(db, calculationSheet!, idMap, userId, userName, cancellationToken);
                created += result.Created; updated += result.Updated;
            }
            catch (Exception ex) when (ex is TagExpressionException or ArgumentException or DbUpdateException)
            { return new(0, 0, skipped, [$"计算工作簿校验失败，全部回滚：{(ex is DbUpdateException ? "名称或引用冲突" : ex.Message)}"]); }
        }
        await transaction.CommitAsync(cancellationToken);
        return new ImportResult(created, updated, skipped, errors.Take(50).ToList());
    }

    private static async Task<(int Created, int Updated)> ImportCalculationsAsync(ConfigDbContext db, IXLWorksheet sheet, Dictionary<int, Tag> idMap, int? userId, string userName, CancellationToken ct)
    {
        if (sheet.Cell(1, 1).GetString() != "原测点ID" || sheet.Cell(1, 3).GetString() != "表达式") throw new TagExpressionException("计算工作表表头无效");
        var last = sheet.LastRowUsed()?.RowNumber() ?? 1;
        if (last > 2001) throw new TagExpressionException("计算工作表最多 2000 行");
        var existing = await db.Tags.Where(x => x.Kind == TagKind.Calculated).ToDictionaryAsync(x => x.Name, ct);
        var rows = new List<(Tag Tag, string Expression, string? Before)>(); var names = new HashSet<string>(); var created = 0; var updated = 0;
        for (var row = 2; row <= last; row++)
        {
            var name = sheet.Cell(row, 2).GetString().Trim(); if (name.Length == 0 && sheet.Cell(row, 3).IsEmpty()) continue;
            var oldId = GetSourceId(sheet.Cell(row, 1));
            if (oldId < 1 || idMap.ContainsKey(oldId) || !names.Add(name)) throw new TagExpressionException($"计算第 {row} 行：原编号或名称缺失/重复");
            if (!TryParseEnum(sheet.Cell(row, 9).GetString(), out ArchiveMode mode)) throw new TagExpressionException($"计算第 {row} 行：存储方式无效");
            if (!sheet.Cell(row, 7).TryGetValue<int>(out var interval) || !sheet.Cell(row, 8).TryGetValue<int>(out var age) ||
                !sheet.Cell(row, 10).TryGetValue<double>(out var deadband) || !sheet.Cell(row, 11).TryGetValue<int>(out var period)) throw new TagExpressionException($"计算第 {row} 行：周期、有效期或死区无效");
            var tag = new Tag { Kind = TagKind.Calculated, Name = name, Expression = sheet.Cell(row, 3).GetString().Trim(), Description = EmptyToNull(sheet.Cell(row, 4).GetString()),
                Unit = EmptyToNull(sheet.Cell(row, 5).GetString()), CalculationEnabled = ParseCalculationBool(sheet.Cell(row, 6).GetString()), CalculationIntervalMs = interval, InputMaxAgeMs = age,
                DataType = TagDataType.Double, ArchiveMode = mode, Deadband = deadband, ArchivePeriodSec = period };
            var error = ConfigurationValidator.Tag(tag); if (error is not null) throw new TagExpressionException($"计算第 {row} 行：{error}");
            var expression = tag.Expression; string? before = null;
            if (existing.TryGetValue(name, out var original))
            {
                before = System.Text.Json.JsonSerializer.Serialize(original); tag.Id = original.Id; tag.CalculationRevision = checked(original.CalculationRevision + 1);
                db.Entry(original).CurrentValues.SetValues(tag); tag = original; updated++;
            }
            else { db.Tags.Add(tag); created++; }
            idMap.Add(oldId, tag); rows.Add((tag, expression, before));
        }
        await db.SaveChangesAsync(ct);
        foreach (var row in rows)
            row.Tag.Expression = Regex.Replace(row.Expression, @"#([0-9]+)", match => idMap.TryGetValue(int.Parse(match.Groups[1].Value), out var target) ? $"#{target.Id}" : throw new TagExpressionException($"工作簿缺少输入 {match.Value} 的完整定义，不能按目标库编号猜测绑定"));
        await db.SaveChangesAsync(ct);
        var all = await db.Tags.AsNoTracking().ToListAsync(ct); var plan = CalculationGraph.Compile(all).ToDictionary(x => x.Tag.Id);
        foreach (var row in rows)
        {
            await db.TagDependencies.Where(x => x.TagId == row.Tag.Id).ExecuteDeleteAsync(ct);
            db.TagDependencies.AddRange(plan[row.Tag.Id].Expression.Dependencies.Select(x => new TagDependency { TagId = row.Tag.Id, InputTagId = x }));
            db.OperationLogs.Add(new OperationLog { UserId = userId, UserName = userName, TagId = row.Tag.Id, Ts = ScadaTime.NowMs(), Action = $"导入计算测点：{row.Tag.Name}", OldValue = row.Before, NewValue = System.Text.Json.JsonSerializer.Serialize(row.Tag), Result = "成功" });
        }
        await db.SaveChangesAsync(ct); return (created, updated);
    }

    private static bool TryParseEnum<TEnum>(string text, out TEnum value) where TEnum : struct, Enum
    {
        text = text.Trim();
        if (string.IsNullOrEmpty(text))
        {
            value = default;
            return false;
        }

        var aliases = typeof(TEnum) == typeof(TagArea)
            ? text switch
            {
                "线圈" => nameof(TagArea.Coil),
                "离散输入" => nameof(TagArea.DiscreteInput),
                "保持寄存器" => nameof(TagArea.HoldingRegister),
                "输入寄存器" => nameof(TagArea.InputRegister),
                _ => text
            }
            : typeof(TEnum) == typeof(TagDataType)
                ? text switch
                {
                    "布尔" => nameof(TagDataType.Bool),
                    "16位整数" => nameof(TagDataType.Int16),
                    "16位无符号整数" => nameof(TagDataType.UInt16),
                    "32位整数" => nameof(TagDataType.Int32),
                    "32位无符号整数" => nameof(TagDataType.UInt32),
                    "单精度浮点" => nameof(TagDataType.Float),
                    "双精度浮点" => nameof(TagDataType.Double),
                    _ => text
                }
                : typeof(TEnum) == typeof(ArchiveMode)
                    ? text switch
                    {
                        "不存储" => nameof(ArchiveMode.None),
                        "变化存储" => nameof(ArchiveMode.OnChange),
                        "周期存储" => nameof(ArchiveMode.Periodic),
                        _ => text
                    }
                    : text;

        return Enum.TryParse(aliases, true, out value);
    }

    private static int GetInt(IXLCell cell, int fallback) =>
        cell.TryGetValue<int>(out var value) ? value : fallback;

    private static int GetSourceId(IXLCell cell) =>
        cell.TryGetValue<double>(out var value) && double.IsFinite(value) && value >= 1 && value <= int.MaxValue && value == Math.Truncate(value)
            ? (int)value : throw new TagExpressionException("原测点ID须为正整数");

    private static bool ParseCalculationBool(string text) => text.Trim().ToLowerInvariant() switch
    {
        "是" or "1" or "true" => true,
        "否" or "0" or "false" => false,
        _ => throw new TagExpressionException("计算测点启用须为是/否、true/false 或 1/0")
    };

    private static double GetDouble(IXLCell cell, double fallback) =>
        cell.TryGetValue<double>(out var value) ? value : fallback;

    private static double? GetNullableDouble(IXLCell cell) =>
        cell.TryGetValue<double>(out var value) ? value : null;

    private static bool ParseBool(string text) =>
        text.Trim() is "是" or "1" or "true" or "True" or "TRUE";

    private static string? EmptyToNull(string text) =>
        string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
