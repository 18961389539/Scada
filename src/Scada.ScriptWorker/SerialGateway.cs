using System.IO.Ports;
using System.Text;
using System.Text.Json;
using Scada.Core.Enums;
using Scada.Core.Scripting;

namespace Scada.ScriptWorker;

// 串口归 Worker 宿主持有：跨脚本执行复用连接，脚本程序集卸载不影响端口。
internal static class SerialGateway
{
    private sealed class Holder
    {
        public required SerialPort Port { get; init; }
        public required SemaphoreSlim Gate { get; init; }
        public required string Signature { get; init; }
        public int Disposed;
    }

    private static readonly Dictionary<string, Holder> Holders = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Sync = new();

    public static string Exchange(IReadOnlyDictionary<string, SerialProfile>? profiles, string name, string command, int timeoutMs)
    {
        if (!TryGetProfile(profiles, name, out var profile))
            throw new InvalidOperationException($"Serial profile '{name}' is not configured.");
        var deadline = Environment.TickCount64 + timeoutMs;
        var holder = GetHolder(name, profile);
        if (!holder.Gate.Wait(timeoutMs))
            throw new TimeoutException($"Serial profile '{name}' is busy.");
        try
        {
            var remaining = (int)Math.Max(50, deadline - Environment.TickCount64);
            var readTimeout = Math.Max(50, Math.Min(profile.ReadTimeoutMs, remaining));
            holder.Port.ReadTimeout = readTimeout;
            holder.Port.DiscardInBuffer();
            holder.Port.Write(command);
            var reply = holder.Port.ReadLine();
            if (reply.Length > Math.Clamp(profile.MaxReplyChars, 1, 4096))
                throw new InvalidOperationException($"Serial reply exceeds {profile.MaxReplyChars} characters.");
            return reply;
        }
        catch (TimeoutException ex)
        {
            // 超时可能留下迟到的回复，关闭端口让下一次从干净缓冲开始。
            Close(name, holder);
            throw new TimeoutException($"Serial profile '{name}' timed out: {ex.Message}", ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or DecoderFallbackException or ArgumentException)
        {
            Close(name, holder);
            throw new InvalidOperationException($"Serial profile '{name}' failed: {ex.Message}", ex);
        }
        finally { try { holder.Gate.Release(); } catch (ObjectDisposedException) { } }
    }

    private static bool TryGetProfile(IReadOnlyDictionary<string, SerialProfile>? profiles, string name, out SerialProfile profile)
    {
        profile = null!;
        if (profiles is null) return false;
        if (profiles.TryGetValue(name, out profile!)) return true;
        foreach (var (key, value) in profiles)
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase)) { profile = value; return true; }
        return false;
    }

    private static Holder GetHolder(string name, SerialProfile profile)
    {
        var signature = JsonSerializer.Serialize(profile);
        lock (Sync)
        {
            if (Holders.TryGetValue(name, out var existing))
            {
                if (existing.Signature == signature) return existing;
                Holders.Remove(name);
                Dispose(existing);
            }
            var port = new SerialPort(profile.Port, profile.BaudRate, Map(profile.Parity), profile.DataBits, Map(profile.StopBits))
            {
                Encoding = ResolveEncoding(profile.Encoding),
                NewLine = string.IsNullOrEmpty(profile.NewLine) ? "\r\n" : profile.NewLine,
                ReadTimeout = Math.Clamp(profile.ReadTimeoutMs, 50, 60_000),
                WriteTimeout = Math.Clamp(profile.ReadTimeoutMs, 50, 60_000),
                DtrEnable = profile.DtrEnable,
                RtsEnable = profile.RtsEnable
            };
            try { port.Open(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
            {
                port.Dispose();
                throw new InvalidOperationException($"Serial profile '{name}' cannot open {profile.Port}: {ex.Message}", ex);
            }
            var holder = new Holder { Port = port, Gate = new SemaphoreSlim(1, 1), Signature = signature };
            Holders[name] = holder;
            return holder;
        }
    }

    private static void Close(string name, Holder holder)
    {
        lock (Sync)
        {
            if (Holders.TryGetValue(name, out var current) && ReferenceEquals(current, holder)) Holders.Remove(name);
            Dispose(holder);
        }
    }

    private static void Dispose(Holder holder)
    {
        if (Interlocked.Exchange(ref holder.Disposed, 1) != 0) return;
        try { if (holder.Port.IsOpen) holder.Port.Close(); } catch (Exception ex) when (ex is IOException or InvalidOperationException) { }
        holder.Port.Dispose();
        // 不 Dispose Gate：出错时 catch 分支先 Close，finally 还要 Release 当前这次等待。
    }

    public static void DisposeAll()
    {
        lock (Sync)
        {
            foreach (var holder in Holders.Values) Dispose(holder);
            Holders.Clear();
        }
    }

    private static Parity Map(SerialParity parity) => parity switch
    {
        SerialParity.Odd => Parity.Odd,
        SerialParity.Even => Parity.Even,
        _ => Parity.None
    };

    private static StopBits Map(SerialStopBits stopBits) => stopBits == SerialStopBits.Two ? StopBits.Two : StopBits.One;

    private static Encoding ResolveEncoding(string? name) => (name ?? "").Trim().ToLowerInvariant() switch
    {
        "ascii" or "us-ascii" => Encoding.ASCII,
        _ => new UTF8Encoding(false, true)
    };
}
