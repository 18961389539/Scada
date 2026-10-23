using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Ports;
using Scada.Core.Entities;

namespace Scada.Collector.Modbus;

public interface ISerialConnection : IDisposable
{
    void Open();
    void Close();
    void DiscardInput();
    void Write(byte[] bytes);
    int Read(byte[] bytes, int offset, int count);
}

public interface ISerialConnectionFactory { ISerialConnection Create(Device device); }

public sealed class SerialConnectionFactory : ISerialConnectionFactory
{
    public ISerialConnection Create(Device device) => new Connection(device);
    private sealed class Connection(Device device) : ISerialConnection
    {
        private readonly SerialPort _port = new(device.SerialPort, device.BaudRate, (Parity)device.Parity, device.DataBits, (StopBits)device.StopBits)
        { ReadTimeout = device.TimeoutMs, WriteTimeout = device.TimeoutMs };
        public void Open() => _port.Open();
        public void Close() { try { _port.Close(); } catch (IOException) { } }
        public void DiscardInput() => _port.DiscardInBuffer();
        public void Write(byte[] bytes) => _port.Write(bytes, 0, bytes.Length);
        public int Read(byte[] bytes, int offset, int count) => _port.Read(bytes, offset, count);
        public void Dispose() => _port.Dispose();
    }
}

// One arbiter per physical port is shared by polling, control writes and connection probes.
// Each exchange opens/closes the port under the arbiter, allowing multiple slave stations.
public sealed class SerialBusPool(ISerialConnectionFactory factory)
{
    private readonly ConcurrentDictionary<string, Bus> _buses = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private sealed class Bus { public readonly SemaphoreSlim Gate = new(1, 1); public long LastActivity; }

    public async Task<T> ExecuteAsync<T>(Device device, Func<ISerialConnection, T> exchange, CancellationToken ct)
    {
        var bus = _buses.GetOrAdd(device.SerialPort.Trim(), _ => new Bus());
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(device.TimeoutMs);
        var entered = false;
        try
        {
            await bus.Gate.WaitAsync(deadline.Token); entered = true;
            var charBits = 1 + device.DataBits + (device.Parity == Core.Enums.SerialParity.None ? 0 : 1) + (int)device.StopBits;
            var quietMs = device.BaudRate > 19200 ? 1.75 : charBits * 3500d / device.BaudRate;
            var remaining = quietMs - (bus.LastActivity == 0 ? double.MaxValue : Stopwatch.GetElapsedTime(bus.LastActivity).TotalMilliseconds);
            if (remaining > 0) await Task.Delay(TimeSpan.FromMilliseconds(Math.Ceiling(remaining)), deadline.Token);
            using var connection = factory.Create(device);
            using var registration = deadline.Token.Register(connection.Close);
            // Keep the gate until the physical I/O finishes, even on cancellation. A timed-out
            // operation must never overlap the next request or be retried as a control write.
            return await Task.Run(() =>
            {
                deadline.Token.ThrowIfCancellationRequested();
                connection.Open();
                deadline.Token.ThrowIfCancellationRequested();
                connection.DiscardInput();
                var result = exchange(connection);
                deadline.Token.ThrowIfCancellationRequested();
                return result;
            }, CancellationToken.None);
        }
        catch (Exception) when (deadline.IsCancellationRequested)
        {
            ct.ThrowIfCancellationRequested();
            throw new TimeoutException($"串口 {device.SerialPort} 操作超时（{device.TimeoutMs} ms，含总线排队）");
        }
        finally
        {
            if (entered) { bus.LastActivity = Stopwatch.GetTimestamp(); bus.Gate.Release(); }
        }
    }
}
