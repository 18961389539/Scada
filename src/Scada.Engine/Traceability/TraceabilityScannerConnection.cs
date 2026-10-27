using System.IO.Ports;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Scada.Core.Entities;

namespace Scada.Engine.Traceability;

public static class TraceabilityScannerConnection
{
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
        listener.Start(1);
        state("listening");
        using var accepted = await listener.AcceptTcpClientAsync(ct);
        state("connected");
        await ReadFramesAsync(accepted.GetStream(), options, receive, ct);
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