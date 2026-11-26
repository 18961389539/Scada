using System.IO.Ports;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Scada.Core.Entities;

namespace Scada.Engine.Traceability;

public static class TraceabilityScannerConnection
{

    public static bool IsLoopbackListen(string host) =>
        IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip);

    public static string? InvalidAllowedSources(IReadOnlyList<string>? sources)
    {
        if (sources is null) return null;
        foreach (var raw in sources)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            if (TryParseCidr(raw.Trim(), out _, out _)) continue;
            return $"来源 IP 允许名单“{raw}”不是有效的 IP 地址或 CIDR。";
        }
        return null;
    }

    public static bool IsAllowedSource(IPAddress address, IReadOnlyList<string>? sources)
    {
        if (sources is null || sources.Count == 0) return true;
        address = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
        foreach (var raw in sources)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            if (!TryParseCidr(raw.Trim(), out var network, out var prefix)) continue;
            if (network.AddressFamily != address.AddressFamily) continue;
            if (MatchesPrefix(address, network, prefix)) return true;
        }
        return false;
    }

    private static bool TryParseCidr(string value, out IPAddress network, out int prefix)
    {
        network = IPAddress.None;
        prefix = 0;
        var slash = value.IndexOf('/');
        var host = slash < 0 ? value : value[..slash];
        if (!IPAddress.TryParse(host, out network!)) return false;
        var bits = network.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
        if (slash < 0) { prefix = bits; return true; }
        return int.TryParse(value[(slash + 1)..], out prefix) && prefix is >= 0 && prefix <= bits;
    }

    private static bool MatchesPrefix(IPAddress address, IPAddress network, int prefix)
    {
        var left = address.GetAddressBytes();
        var right = network.GetAddressBytes();
        if (left.Length != right.Length) return false;
        var full = prefix / 8;
        for (var i = 0; i < full; i++) if (left[i] != right[i]) return false;
        var rem = prefix % 8;
        if (rem == 0) return true;
        var mask = (byte)(0xFF << (8 - rem));
        return (left[full] & mask) == (right[full] & mask);
    }

    public static async Task ReceiveTcpAsync(TraceabilityScannerOptions options, Action<string> state,
        Func<string, Task> receive, CancellationToken ct)
    {
        using var client = new TcpClient();
        if (options.TcpMode == TraceabilityTcpMode.Client)
        {
            state("connecting");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            await client.ConnectAsync(options.Host, options.Port, timeout.Token);
            state("connected");
            await ReadFramesAsync(client.GetStream(), options, receive, ct);
            return;
        }
        using var listener = new TcpListener(IPAddress.Parse(options.Host), options.Port);
        listener.Start();
        state("listening");
        if (!IsLoopbackListen(options.Host) && (options.AllowedSources is null || options.AllowedSources.Count == 0))
            state("listening-open");
        while (!ct.IsCancellationRequested)
        {
            var accepted = await listener.AcceptTcpClientAsync(ct);
            var remote = (accepted.Client.RemoteEndPoint as IPEndPoint)?.Address;
            if (remote is not null && !IsAllowedSource(remote, options.AllowedSources))
            {
                accepted.Dispose();
                continue;
            }
            state("connected");
            using (accepted)
                await ReadFramesAsync(accepted.GetStream(), options, receive, ct);
            return;
        }
    }

    public static Task ReceiveSerialAsync(TraceabilityScannerOptions options, Action<string> state,
        Func<string, Task> receive, CancellationToken ct) => Task.Run(async () =>
    {
        using var port = new SerialPort(options.SerialPort, options.BaudRate, (Parity)options.Parity, options.DataBits, (StopBits)options.StopBits)
        { ReadTimeout = 250, WriteTimeout = 1000 };
        state("connecting");
        port.Open();
        state("connected");
        var decoder = new FrameDecoder(options);
        var buffer = new byte[256];
        while (!ct.IsCancellationRequested)
        {
            int count;
            try { count = port.Read(buffer, 0, buffer.Length); }
            catch (TimeoutException) { continue; }
            for (var index = 0; index < count; index++)
                if (decoder.Push(buffer[index]) is string sn) await receive(sn);
        }
        ct.ThrowIfCancellationRequested();
    }, ct);

    public static async Task ReadFramesAsync(Stream stream, TraceabilityScannerOptions options, Func<string, Task> receive, CancellationToken ct)
    {
        var decoder = new FrameDecoder(options);
        var buffer = new byte[256];
        while (true)
        {
            var count = await stream.ReadAsync(buffer, ct);
            if (count == 0) return;
            for (var index = 0; index < count; index++)
                if (decoder.Push(buffer[index]) is string sn) await receive(sn);
        }
    }

    private sealed class FrameDecoder(TraceabilityScannerOptions options)
    {
        private readonly List<byte> _bytes = [];
        private readonly byte[] _terminator = options.Terminator switch
        {
            TraceabilityScanTerminator.CrLf => [13, 10],
            TraceabilityScanTerminator.Lf => [10],
            TraceabilityScanTerminator.Cr => [13],
            _ => throw new InvalidOperationException("Invalid scanner terminator.")
        };
        private readonly Encoding _encoding = options.Encoding switch
        {
            TraceabilityScanEncoding.Utf8 => new UTF8Encoding(false, true),
            TraceabilityScanEncoding.Ascii => Encoding.GetEncoding("us-ascii", EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback),
            _ => throw new InvalidOperationException("Invalid scanner encoding.")
        };

        public string? Push(byte value)
        {
            _bytes.Add(value);
            if (_bytes.Count > 802) throw new IOException("Scanner frame exceeds the 200-character SN limit.");
            if (_bytes.Count < _terminator.Length || !_bytes.TakeLast(_terminator.Length).SequenceEqual(_terminator)) return null;
            var text = _encoding.GetString(_bytes.Take(_bytes.Count - _terminator.Length).ToArray());
            _bytes.Clear();
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
    }
}