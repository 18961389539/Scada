using Scada.Collector;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Interfaces;
using Scada.Core.Models;

namespace Scada.Collector.Tests;

/// <summary>
/// 通道共享的语义：一条物理链路只建一次连接，事务在总线级串行，
/// 引用计数归零后链路释放。这里用假驱动验证，不依赖真实现场设备。
/// </summary>
public sealed class ChannelBusTests
{
    private sealed class StubDriver : IDeviceDriver
    {
        private int _active;
        public int ConnectCount;
        public int MaxConcurrent;
        public int Disposed;

        public bool IsConnected { get; private set; }
        public string? LastError => null;

        public Task ConnectAsync(Device device, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref ConnectCount);
            IsConnected = true;
            return Task.CompletedTask;
        }

        public void Disconnect() => IsConnected = false;

        public async Task<IReadOnlyList<RawTagValue>> ReadAsync(Device device, ReadPlan plan, CancellationToken cancellationToken = default)
        {
            // 记录并发进入次数：共享总线同一时刻只应有一个事务在跑。
            MaxConcurrent = Math.Max(MaxConcurrent, Interlocked.Increment(ref _active));
            await Task.Delay(30, cancellationToken);
            Interlocked.Decrement(ref _active);
            return [new RawTagValue(device.StationNumber, device.StationNumber)];
        }

        public Task WriteAsync(Device device, Tag tag, double value, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref Disposed);
            IsConnected = false;
            return ValueTask.CompletedTask;
        }
    }

    private static Device Device(int id, int? channelId, byte station = 1) => new()
    {
        Id = id,
        Name = $"device-{id}",
        Protocol = ProtocolType.ModbusTcp,
        Host = "10.0.0.10",
        Port = 502,
        StationNumber = station,
        ChannelId = channelId
    };

    private static ReadPlan Plan(Device device) => new() { DeviceId = device.Id };

    [Fact]
    public async Task DevicesOnSameChannelShareOneConnection()
    {
        var registry = new ChannelRegistry();
        var drivers = new List<StubDriver>();
        IDeviceDriver Factory(Device d) { var driver = new StubDriver(); drivers.Add(driver); return driver; }

        var first = registry.Acquire(Device(1, 7, 1), Factory);
        var second = registry.Acquire(Device(2, 7, 2), Factory);
        try
        {
            await first.ConnectAsync(Device(1, 7, 1));
            await second.ConnectAsync(Device(2, 7, 2));

            Assert.Single(drivers);
            Assert.Equal(1, registry.BusCount);
            Assert.True(first.IsConnected);
            Assert.True(second.IsConnected);
        }
        finally
        {
            await first.DisposeAsync();
            await second.DisposeAsync();
        }
        // 最后一个租约释放后链路必须关闭，否则改过端点的旧连接会一直占着端口。
        Assert.Equal(0, registry.BusCount);
        Assert.Equal(1, drivers[0].Disposed);
    }

    [Fact]
    public async Task ConcurrentReadsAreSerializedOnTheBus()
    {
        var registry = new ChannelRegistry();
        var drivers = new List<StubDriver>();
        IDeviceDriver Factory(Device d) { var driver = new StubDriver(); drivers.Add(driver); return driver; }

        var a = Device(1, 9, 1);
        var b = Device(2, 9, 2);
        var first = registry.Acquire(a, Factory);
        var second = registry.Acquire(b, Factory);
        try
        {
            await first.ConnectAsync(a);
            var results = await Task.WhenAll(first.ReadAsync(a, Plan(a)), second.ReadAsync(b, Plan(b)));

            Assert.Single(drivers);
            Assert.Equal(1, drivers[0].MaxConcurrent);
            // 站号来自各自的设备，不是通道：一次连接可以读多个从站。
            Assert.Equal(1, results[0][0].Value);
            Assert.Equal(2, results[1][0].Value);
        }
        finally
        {
            await first.DisposeAsync();
            await second.DisposeAsync();
        }
    }

    [Fact]
    public async Task DifferentChannelsKeepSeparateConnections()
    {
        var registry = new ChannelRegistry();
        var drivers = new List<StubDriver>();
        IDeviceDriver Factory(Device d) { var driver = new StubDriver(); drivers.Add(driver); return driver; }

        var first = registry.Acquire(Device(1, 3, 1), Factory);
        var second = registry.Acquire(Device(2, 4, 1), Factory);
        try
        {
            await first.ConnectAsync(Device(1, 3, 1));
            await second.ConnectAsync(Device(2, 4, 1));
            Assert.Equal(2, drivers.Count);
            Assert.Equal(2, registry.BusCount);
        }
        finally
        {
            await first.DisposeAsync();
            await second.DisposeAsync();
        }
        Assert.Equal(0, registry.BusCount);
    }

    [Fact]
    public async Task DeviceWithoutChannelKeepsItsOwnDriver()
    {
        var registry = new ChannelRegistry();
        var drivers = new List<StubDriver>();
        IDeviceDriver Factory(Device d) { var driver = new StubDriver(); drivers.Add(driver); return driver; }

        var first = registry.Acquire(Device(1, null), Factory);
        var second = registry.Acquire(Device(2, null), Factory);
        try
        {
            // 未绑定通道时不引入任何额外语义：一设备一驱动。
            Assert.Equal(2, drivers.Count);
            Assert.Equal(0, registry.BusCount);
            Assert.Same(drivers[0], first);
            Assert.Same(drivers[1], second);
        }
        finally
        {
            await first.DisposeAsync();
            await second.DisposeAsync();
        }
    }

    [Fact]
    public void EndpointKeyChangesWithEndpoint()
    {
        var baseline = ChannelBus.EndpointKey(Device(1, 5));
        var moved = Device(1, 5);
        moved.Port = 1502;
        // 端点变了必须换链路，否则新配置会复用旧连接。
        Assert.NotEqual(baseline, ChannelBus.EndpointKey(moved));
    }

    [Fact]
    public void SerialEndpointKeyUsesPortSettings()
    {
        var device = Device(1, 5);
        device.Protocol = ProtocolType.ModbusRtu;
        device.SerialPort = "COM3";
        var key = ChannelBus.EndpointKey(device);
        Assert.Contains("COM3", key);
        device.BaudRate = 19200;
        Assert.NotEqual(key, ChannelBus.EndpointKey(device));
    }
}
