using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Data;
using Scada.Server.Endpoints;

namespace Scada.Server.Services;

public sealed record ImportResult(int Created, int Updated, int Skipped, IReadOnlyList<string> Errors);

public sealed class TagExcelService
{
    private static readonly string[] Headers =
    [
        "设备名称", "测点名称", "描述", "区域", "地址", "数据类型", "系数", "偏移",
        "单位", "可写", "存储方式", "死区", "存储周期(秒)", "最小值", "最大值", "DB编号", "位偏移"
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
        foreach (var tag in tags.OrderBy(x => x.DeviceId).ThenBy(x => x.Address))
        {
            sheet.Cell(row, 1).Value = deviceNames.GetValueOrDefault(tag.DeviceId, string.Empty);
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
        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    public async Task<ImportResult> ImportAsync(
        Stream stream,
        IDbContextFactory<ConfigDbContext> contextFactory,
        CancellationToken cancellationToken)
    {
        using var workbook = new XLWorkbook(stream);
        var sheet = workbook.Worksheet(1);
        var lastRow = sheet.LastRowUsed()?.RowNumber() ?? 1;
        var errors = new List<string>();
        var created = 0;
        var updated = 0;
        var skipped = 0;

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var devices = await db.Devices.ToDictionaryAsync(x => x.Name, cancellationToken);
        var tags = await db.Tags.ToDictionaryAsync(x => (x.DeviceId, x.Name), cancellationToken);

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
                var exists = tags.TryGetValue(key, out var tag);
                // Validate a detached copy: a skipped invalid row must not change a tracked entity.
                tag = tag?.Clone() ?? new Tag { DeviceId = device.Id, Name = tagName };

                tag.Description = EmptyToNull(sheet.Cell(row, 3).GetString());
                tag.Area = area;
                tag.Address = GetInt(sheet.Cell(row, 5), 0);
                tag.DbNumber = GetInt(sheet.Cell(row, 16), 1);
                tag.BitOffset = GetInt(sheet.Cell(row, 17), 0);
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
            }
            catch (Exception ex)
            {
                errors.Add($"第 {row} 行：{ex.Message}");
                skipped++;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        return new ImportResult(created, updated, skipped, errors.Take(50).ToList());
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

    private static double GetDouble(IXLCell cell, double fallback) =>
        cell.TryGetValue<double>(out var value) ? value : fallback;

    private static double? GetNullableDouble(IXLCell cell) =>
        cell.TryGetValue<double>(out var value) ? value : null;

    private static bool ParseBool(string text) =>
        text.Trim() is "是" or "1" or "true" or "True" or "TRUE";

    private static string? EmptyToNull(string text) =>
        string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
