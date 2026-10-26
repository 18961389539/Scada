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
        if (registers.Count == 0 || registers.Any(value => value.Quality != TagQuality.Good || !double.IsFinite(value.Value)))
            throw new InvalidOperationException("SN registers are missing or invalid.");
        if (format == TraceabilitySnFormat.Numeric)
        {
            if (registers.Count != 1 || registers[0].Value < 0 || registers[0].Value != Math.Truncate(registers[0].Value))
                throw new InvalidOperationException("Numeric SN requires one non-negative integer.");
            return registers[0].Value.ToString("0", CultureInfo.InvariantCulture);
        }
        if (format is not (TraceabilitySnFormat.AsciiLowByteFirst or TraceabilitySnFormat.AsciiHighByteFirst))
            throw new InvalidOperationException("Unsupported SN format.");
        var bytes = new List<byte>();
        foreach (var register in registers)
        {
            if (register.Value < 0 || register.Value > ushort.MaxValue || register.Value != Math.Truncate(register.Value))
                throw new InvalidOperationException("ASCII SN requires unsigned 16-bit registers.");
            var word = (ushort)register.Value;
            var first = (byte)(format == TraceabilitySnFormat.AsciiLowByteFirst ? word & 255 : word >> 8);
            var second = (byte)(format == TraceabilitySnFormat.AsciiLowByteFirst ? word >> 8 : word & 255);
            bytes.Add(first); bytes.Add(second);
        }
        var end = bytes.IndexOf(0);
        var payload = bytes.Take(end < 0 ? bytes.Count : end).ToArray();
        if (payload.Any(value => value is < 32 or > 126)) throw new InvalidOperationException("SN contains non-ASCII characters.");
        return ValidateSn(Encoding.ASCII.GetString(payload));
    }

    public static string ValidateSn(string? sn)
    {
        var value = sn?.Trim() ?? "";
        if (value.Length is < 1 or > 200 || value.Any(char.IsControl))
            throw new InvalidOperationException("SN must contain 1 to 200 printable characters.");
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