using System.Globalization;
using System.Text;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Models;

namespace Scada.Engine.Traceability;

public sealed record TraceabilityCycleResult(TraceabilityRecord? Record, int Reply, string? Error, string? ReplyError);

public static class TraceabilityCapture
{
    public static string ReadSn(IReadOnlyList<TagValue> registers, TraceabilitySnFormat format)
    {
        if (registers.Count == 0) throw new InvalidOperationException("未配置 SN 寄存器。");
        for (var index = 0; index < registers.Count; index++)
            if (registers[index].Quality != TagQuality.Good || !double.IsFinite(registers[index].Value))
                throw new InvalidOperationException($"SN 寄存器 #{registers[index].TagId}（第 {index + 1} 个）质量为 {registers[index].Quality} 或数值无效。");
        if (format == TraceabilitySnFormat.Numeric)
        {
            if (registers.Count != 1 || registers[0].Value < 0 || registers[0].Value != Math.Truncate(registers[0].Value))
                throw new InvalidOperationException($"数值型 SN 需要且仅需要一个非负整数寄存器（当前 {registers.Count} 个，首个值为 {registers[0].Value}）。");
            return registers[0].Value.ToString("0", CultureInfo.InvariantCulture);
        }
        if (format is not (TraceabilitySnFormat.AsciiLowByteFirst or TraceabilitySnFormat.AsciiHighByteFirst))
            throw new InvalidOperationException($"不支持的 SN 格式：{format}。");
        var bytes = new List<byte>();
        for (var index = 0; index < registers.Count; index++)
        {
            var register = registers[index];
            if (register.Value < 0 || register.Value > ushort.MaxValue || register.Value != Math.Truncate(register.Value))
                throw new InvalidOperationException($"ASCII SN 寄存器 #{register.TagId}（第 {index + 1} 个）值 {register.Value} 无效，须为 0～65535 的整数。");
            var word = (ushort)register.Value;
            var first = (byte)(format == TraceabilitySnFormat.AsciiLowByteFirst ? word & 255 : word >> 8);
            var second = (byte)(format == TraceabilitySnFormat.AsciiLowByteFirst ? word >> 8 : word & 255);
            bytes.Add(first); bytes.Add(second);
        }
        var end = bytes.IndexOf(0);
        var payload = bytes.Take(end < 0 ? bytes.Count : end).ToArray();
        foreach (var octet in payload)
            if (octet is < 32 or > 126)
                throw new InvalidOperationException($"SN 含非 ASCII 字符（字节 0x{octet:X2}），无法按 ASCII 解码。");
        return ValidateSn(Encoding.ASCII.GetString(payload));
    }

    public static string ValidateSn(string? sn)
    {
        var value = sn?.Trim() ?? "";
        if (value.Length is < 1 or > 200)
            throw new InvalidOperationException($"SN 长度须为 1～200 个字符，当前为 {value.Length} 个。");
        for (var index = 0; index < value.Length; index++)
            if (char.IsControl(value[index]))
                throw new InvalidOperationException($"SN 第 {index + 1} 个字符是控制字符（0x{(int)value[index]:X2}），须为可打印字符。");
        return value;
    }

    public static async Task<TraceabilityCycleResult> RunAsync(
        Func<CancellationToken, Task<TraceabilityRecord>> acquire,
        Func<TraceabilityRecord, CancellationToken, Task> persist,
        Func<int, CancellationToken, Task> reply,
        CancellationToken ct)
    {
        TraceabilityRecord? record = null;
        string? error = null;
        var code = 3;
        try
        {
            record = await acquire(ct);
            record.Sn = ValidateSn(record.Sn);
            await persist(record, ct);
            code = 2;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            record = null;
            error = ex.Message;
        }
        string? replyError = null;
        try { await reply(code, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested) { replyError = ex.Message; }
        return new(record, code, error, replyError);
    }
}