using System.Globalization;
using System.Text.Json.Nodes;

namespace Scada.Server.Reports;

// 预览、Excel、PDF 共用同一套定点文本。double 直接四舍五入时，JS toFixed 与 .NET 的十进制展开可能差 1 个末位。
public static class ReportNumbers
{
    public static string Format(double value, int decimals)
    {
        decimals = Math.Clamp(decimals, 0, 6);
        if (!double.IsFinite(value)) return "—";
        decimal rounded;
        try { rounded = Math.Round((decimal)value, decimals, MidpointRounding.AwayFromZero); }
        catch (OverflowException) { return value.ToString("G17", CultureInfo.InvariantCulture); }
        return rounded.ToString("F" + decimals, CultureInfo.InvariantCulture);
    }

    public static void ApplyDisplay(JsonNode table)
    {
        var columns = table["columns"]?.AsArray();
        if (columns is null) return;
        if (table["rows"] is JsonArray rows) foreach (var row in rows) FormatRow(row, columns);
        FormatRow(table["summary"], columns);
    }

    private static void FormatRow(JsonNode? row, JsonArray columns)
    {
        if (row?["values"] is not JsonArray values) return;
        for (var i = 0; i < values.Count && i < columns.Count; i++)
        {
            if (!TryNumber(values[i], out var number)) continue;
            var decimals = columns[i]?["decimals"]?.GetValue<int>() ?? 2;
            values[i] = Format(number, decimals);
        }
    }

    private static bool TryNumber(JsonNode? node, out double value)
    {
        value = 0;
        if (node is not JsonValue v) return false;
        if (v.TryGetValue<double>(out value)) return double.IsFinite(value);
        if (v.TryGetValue<long>(out var whole)) { value = whole; return true; }
        return false;
    }
}
