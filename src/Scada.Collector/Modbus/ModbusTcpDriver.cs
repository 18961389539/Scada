using System.Globalization;
using IoTClient.Clients.Modbus;
using IoTClient.Enums;
using Microsoft.Extensions.Logging;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Interfaces;
using Scada.Core.Models;

namespace Scada.Collector.Modbus;

public sealed class ModbusTcpDriver : IDeviceDriver
{
    private readonly ILogger<ModbusTcpDriver> _logger;
    private ModbusTcpClient? _client;
    private string? _lastError;

    public ModbusTcpDriver(ILogger<ModbusTcpDriver> logger)
    {
        _logger = logger;
    }

    public bool IsConnected => _client?.Connected == true;
    public string? LastError => _lastError;

    public async Task ConnectAsync(Device device, CancellationToken cancellationToken = default)
    {
        Disconnect();
        var client = new ModbusTcpClient(device.Host, device.Port, device.StationNumber, EndianFormat.ABCD, false);
        try
        {
            var openResult = await RunWithTimeoutAsync(() => client.Open(), device.TimeoutMs, cancellationToken, "连接设备");
            EnsureSuccess(openResult);
            if (!client.Connected)
            {
                throw new IOException("连接未建立");
            }

            _client = client;
            _lastError = null;
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            try
            {
                client.Close();
            }
            catch
            {
                // ignored
            }

            _logger.LogWarning(ex, "连接设备 {Device} ({Host}:{Port}) 失败", device.Name, device.Host, device.Port);
            throw;
        }
    }

    public void Disconnect()
    {
        if (_client is null)
        {
            return;
        }

        try
        {
            _client.Close();
        }
        catch
        {
            // ignored
        }

        _client = null;
    }

    public async Task<IReadOnlyList<RawTagValue>> ReadAsync(Device device, ReadPlan plan, CancellationToken cancellationToken = default)
    {
        var client = _client ?? throw new InvalidOperationException("设备尚未连接");
        var results = new List<RawTagValue>(plan.Blocks.Sum(x => x.Tags.Count));

        foreach (var block in plan.Blocks)
        {
            byte[] payload;
            try
            {
                var readResult = await RunWithTimeoutAsync(
                    () => client.Read(
                        block.StartAddress.ToString(CultureInfo.InvariantCulture),
                        device.StationNumber,
                        ModbusValueCodec.FunctionCode(block.Area),
                        (ushort)block.Length,
                        false),
                    device.TimeoutMs,
                    cancellationToken,
                    "读取数据");
                EnsureSuccess(readResult);
                payload = readResult.Value ?? throw new IOException("读取返回空数据");
                // IoTClient 以反向字节序返回读取负载，先还原为 Modbus 报文原始顺序。
                Array.Reverse(payload);
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                Disconnect();
                throw;
            }

            var isBitArea = block.Area is TagArea.Coil or TagArea.DiscreteInput;
            foreach (var item in block.Tags)
            {
                double value;
                if (isBitArea)
                {
                    value = ModbusValueCodec.DecodeCoil(payload, item.Offset) ? 1 : 0;
                }
                else
                {
                    value = ModbusValueCodec.DecodeRegister(payload, item.Offset, item.DataType, item.ByteOrder);
                }

                results.Add(new RawTagValue(item.TagId, value));
            }
        }

        return results;
    }

    public async Task WriteAsync(Device device, Tag tag, double value, CancellationToken cancellationToken = default)
    {
        var client = _client ?? throw new InvalidOperationException("设备尚未连接");
        var address = tag.Address.ToString(CultureInfo.InvariantCulture);

        try
        {
            if (tag.Area == TagArea.Coil)
            {
                var coilResult = await RunWithTimeoutAsync(
                    () => client.Write(address, value != 0, device.StationNumber, 5),
                    device.TimeoutMs,
                    cancellationToken,
                    "写入线圈");
                EnsureSuccess(coilResult);
            }
            else if (tag.Area == TagArea.HoldingRegister)
            {
                var payload = ModbusValueCodec.EncodeRegister(value, tag.DataType, device.ByteOrder);
                var writeResult = await RunWithTimeoutAsync(
                    () => client.Write(address, payload, device.StationNumber, 16, false),
                    device.TimeoutMs,
                    cancellationToken,
                    "写入寄存器");
                EnsureSuccess(writeResult);
            }
            else
            {
                throw new InvalidOperationException("该区域不可写");
            }
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            Disconnect();
            throw;
        }
    }

    public ValueTask DisposeAsync()
    {
        Disconnect();
        return ValueTask.CompletedTask;
    }

    private static void EnsureSuccess(IoTClient.Result result)
    {
        if (!result.IsSucceed)
        {
            throw new IOException(string.IsNullOrWhiteSpace(result.Err) ? "Modbus 操作失败" : result.Err);
        }
    }

    private static async Task<T> RunWithTimeoutAsync<T>(Func<T> action, int timeoutMs, CancellationToken cancellationToken, string operation)
    {
        var task = Task.Run(action, CancellationToken.None);
        try
        {
            return await task.WaitAsync(TimeSpan.FromMilliseconds(Math.Max(timeoutMs, 100)), cancellationToken);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException($"{operation}超时（{timeoutMs} ms）");
        }
    }

    private static async Task RunWithTimeoutAsync(Action action, int timeoutMs, CancellationToken cancellationToken, string operation)
    {
        var task = Task.Run(action, CancellationToken.None);
        try
        {
            await task.WaitAsync(TimeSpan.FromMilliseconds(Math.Max(timeoutMs, 100)), cancellationToken);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException($"{operation}超时（{timeoutMs} ms）");
        }
    }
}

public sealed class ModbusTcpDriverFactory : IDeviceDriverFactory
{
    private readonly ILoggerFactory _loggerFactory;

    public ModbusTcpDriverFactory(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory;
    }

    public IDeviceDriver Create(Device device) =>
        new ModbusTcpDriver(_loggerFactory.CreateLogger<ModbusTcpDriver>());
}
