using System.Diagnostics;
using Scada.Collector;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Interfaces;
using Scada.Core.Models;

namespace Scada.Collector.Tests;

/// <summary>
/// 共享总线的调度语义。核心命题只有一条：总线互斥的粒度必须是「一次事务」，
/// 不能是「一份读取计划」——否则一台有几十个读块的设备每轮独占总线数百毫秒，
/// 同总线上按 100ms 采集的设备会被拖成秒级，而现场看到的只是"某台设备数据变慢"。
/// </summary>
public sealed class ChannelBusFairnessTests
{
    /// <summary>按事务加锁的假驱动：每个读块算一次总线事务。</summary>
    private sealed class GatedDriver(int transactionMs) : IDeviceDriver, ITransactionGatedDriver
    {
        private readonly Lock _orderGate = new();
        private readonly List<int> _order = [];
        private int _active;
        public int MaxConcurrent;
        public ITransactionGate? TransactionGate { get; set; }

        public bool IsConnected => true;
        public string? LastError => null;

        public Task ConnectAsync(Device device, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void Disconnect() { }
        public Task WriteAsync(Device device, Tag tag, double value, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public IReadOnlyList<int> Order { get { lock (_orderGate) return [.. _order]; } }

        public Task<IReadOnlyList<RawTagValue>> ReadAsync(Device device, ReadPlan plan, CancellationToken cancellationToken = default) =>
            Task.Run<IReadOnlyList<RawTagValue>>(() =>
            {
                var values = new List<RawTagValue>(plan.Blocks.Count);
                foreach (var block in plan.Blocks)
                {
                    // 每个读块是一次独立的请求/响应，必须各自排队。
                    TransactionGate!.Execute(device.Id, () =>
                    {
                        var concurrent = Interlocked.Increment(ref _active);
                        lock (_orderGate)
                        {
                            _order.Add(device.Id);
                            MaxConcurrent = Math.Max(MaxConcurrent, concurrent);
                        }
                        Thread.Sleep(transactionMs);
                        Interlocked.Decrement(ref _active);
                        return true;
                    }, cancellationToken);
                    values.Add(new(block.Tags[0].TagId, block.Tags[0].TagId));
                }
                return values;
            }, cancellationToken);
    }

    /// <summary>
    /// 握手式假驱动：长计划在做完第一个读块后停住，等对端完成一次事务才继续。
    /// 这样"总线是否在块之间让出"就变成一个确定性的先后关系，不依赖线程调度时序——
    /// 用墙钟时间断言公平性在并行测试里必然偶发失败。
    /// </summary>
    private sealed class HandshakeDriver : IDeviceDriver, ITransactionGatedDriver
    {
        private readonly ManualResetEventSlim _firstBlockDone = new(false);
        private readonly ManualResetEventSlim _peerServed = new(false);
        private int _active;
        public int MaxConcurrent;
        public ITransactionGate? TransactionGate { get; set; }

        public bool IsConnected => true;
        public string? LastError => null;
        public Task ConnectAsync(Device device, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void Disconnect() { }
        public Task WriteAsync(Device device, Tag tag, double value, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public bool WaitFirstBlock(TimeSpan timeout) => _firstBlockDone.Wait(timeout);
        public void ReleasePeer() => _peerServed.Set();

        public Task<IReadOnlyList<RawTagValue>> ReadAsync(Device device, ReadPlan plan, CancellationToken cancellationToken = default) =>
            Task.Run<IReadOnlyList<RawTagValue>>(() =>
            {
                var values = new List<RawTagValue>(plan.Blocks.Count);
                for (var index = 0; index < plan.Blocks.Count; index++)
                {
                    TransactionGate!.Execute(device.Id, () =>
                    {
                        MaxConcurrent = Math.Max(MaxConcurrent, Interlocked.Increment(ref _active));
                        Thread.Sleep(5);
                        Interlocked.Decrement(ref _active);
                        return true;
                    }, cancellationToken);
                    values.Add(new(plan.Blocks[index].Tags[0].TagId, index));

                    // 长计划在第一个块之后挂起：此时绝不能还持有总线锁。
                    if (device.Id == 1 && index == 0 && plan.Blocks.Count > 1)
                    {
                        _firstBlockDone.Set();
                        if (!_peerServed.Wait(TimeSpan.FromSeconds(10)))
                            throw new TimeoutException("对端读取没能在长计划的两个读块之间完成：总线仍按整份读取计划加锁");
                    }
                }
                return values;
            }, cancellationToken);
    }

    private sealed class NeverConnectDriver(Task release) : IDeviceDriver
    {
        public bool IsConnected => false;
        public string? LastError => null;

        public async Task ConnectAsync(Device device, CancellationToken cancellationToken = default)
        {
            // 模拟不可达端点：TCP 要等到 SYN 超时（Windows 约 21 秒）才会返回。
            await release.WaitAsync(cancellationToken);
            throw new IOException("连接被拒绝");
        }

        public void Disconnect() { }
        public Task<IReadOnlyList<RawTagValue>> ReadAsync(Device device, ReadPlan plan, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<RawTagValue>>([]);
        public Task WriteAsync(Device device, Tag tag, double value, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static Device Device(int id, int? channelId, byte station = 1, int timeoutMs = 1000) => new()
    {
        Id = id,
        Name = $"device-{id}",
        Protocol = ProtocolType.ModbusTcp,
        Host = "10.0.0.10",
        Port = 502,
        StationNumber = station,
        TimeoutMs = timeoutMs,
        ChannelId = channelId
    };

    private static ReadPlan Plan(Device device, int blocks) => new()
    {
        DeviceId = device.Id,
        Blocks = [.. Enumerable.Range(0, blocks).Select(i => new ReadBlock
        {
            Area = TagArea.HoldingRegister,
            StartAddress = i * 10,
            Length = 2,
            Tags = [new TagReadItem(device.Id * 100 + i, 0, TagDataType.UInt16, ByteOrder.ABCD)]
        })]
    };

    [Fact]
    public async Task LongPlanYieldsTheBusBetweenBlocks()
    {
        var registry = new ChannelRegistry();
        var driver = new HandshakeDriver();
        var a = Device(1, 21, 1);
        var b = Device(2, 21, 2);

        var first = registry.Acquire(a, _ => driver);
        var second = registry.Acquire(b, _ => driver);
        try
        {
            await first.ConnectAsync(a);
            var longRead = Task.Run(() => first.ReadAsync(a, Plan(a, blocks: 4)));

            // 长计划做完第一个读块后停住。若总线仍按"整份读取计划"加锁，
            // 下面这次读取会一直排队到 longRead 结束（握手超时），从而暴露退化。
            Assert.True(driver.WaitFirstBlock(TimeSpan.FromSeconds(10)), "长计划没有开始执行");
            var shortRead = await second.ReadAsync(b, Plan(b, blocks: 1));
            Assert.Single(shortRead);

            driver.ReleasePeer();
            var longValues = await longRead;
            Assert.Equal(4, longValues.Count);
            // 串行语义不能因此丢失：总线同一时刻仍然只有一个事务。
            Assert.Equal(1, driver.MaxConcurrent);
        }
        finally
        {
            driver.ReleasePeer();
            await first.DisposeAsync();
            await second.DisposeAsync();
        }
    }

    [Fact]
    public async Task StatisticsAttributeBusTimeToEachMember()
    {
        var registry = new ChannelRegistry();
        var driver = new GatedDriver(transactionMs: 10);
        var a = Device(1, 23, 1);
        var b = Device(2, 23, 2);
        var first = registry.Acquire(a, _ => driver);
        var second = registry.Acquire(b, _ => driver);
        try
        {
            await first.ConnectAsync(a);
            await Task.WhenAll(first.ReadAsync(a, Plan(a, blocks: 3)), second.ReadAsync(b, Plan(b, blocks: 2)));

            var bus = Assert.Single(registry.BusesOf(23));
            var stats = bus.Snapshot((id) => ($"设备{id}", id));

            Assert.Equal(23, stats.ChannelId);
            Assert.True(stats.Connected);
            Assert.Equal(2, stats.LeaseCount);
            Assert.Equal(5, stats.Transactions);
            Assert.Equal(2, stats.Members.Count);
            Assert.All(stats.Members, member => Assert.True(member.Transactions > 0));
            Assert.All(stats.Members, member => Assert.True(member.BusyMs > 0));
            // 两个成员的占用之和应当接近整条总线（百分比只做粗校验，避免舍入导致偶发失败）。
            Assert.InRange(stats.Members.Sum(x => x.BusySharePercent), 95, 105);
            Assert.InRange(stats.BusyPercent, 0, 100);
        }
        finally
        {
            await first.DisposeAsync();
            await second.DisposeAsync();
        }
    }

    [Fact]
    public async Task UnreachableEndpointReleasesTheBusAfterTheConnectBudget()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registry = new ChannelRegistry();
        var device = Device(1, 24, timeoutMs: 1000);
        var lease = registry.Acquire(device, _ => new NeverConnectDriver(release.Task));
        try
        {
            var stamp = Stopwatch.StartNew();
            var error = await Assert.ThrowsAsync<TimeoutException>(() => lease.ConnectAsync(device));
            stamp.Stop();

            // 预算 = clamp(TimeoutMs×2, 3s, 20s)。这里 TimeoutMs=1000 → 3s 下限。
            // 关键不是精确值，而是"不会拖到 TCP SYN 超时的 21 秒"。
            Assert.Contains("连接超时", error.Message);
            Assert.InRange(stamp.ElapsedMilliseconds, 2_500, 12_000);
        }
        finally
        {
            release.TrySetResult();
            await lease.DisposeAsync();
        }
    }

    [Theory]
    [InlineData(100, 3_000)]
    [InlineData(1_000, 3_000)]
    [InlineData(5_000, 10_000)]
    [InlineData(60_000, 20_000)]
    public void ConnectBudgetClampsToSafeRange(int deviceTimeoutMs, int expectedMs) =>
        Assert.Equal(expectedMs, BusPolicies.ConnectBudgetMs(deviceTimeoutMs));

    [Fact]
    public async Task UngatedDriverStillSerializesWholeOperation()
    {
        // 未实现事务钩子的驱动（例如将来新增的协议）必须保持原来的"整次操作加锁"语义，
        // 否则共享通道上会出现两个设备交错读写。
        var registry = new ChannelRegistry();
        var drivers = new List<UngatedDriver>();
        IDeviceDriver Factory(Device _) { var created = new UngatedDriver(); drivers.Add(created); return created; }
        var a = Device(1, 25, 1);
        var b = Device(2, 25, 2);
        var first = registry.Acquire(a, Factory);
        var second = registry.Acquire(b, Factory);
        try
        {
            await first.ConnectAsync(a);
            await Task.WhenAll(first.ReadAsync(a, Plan(a, 1)), second.ReadAsync(b, Plan(b, 1)));
            Assert.Equal(1, drivers[0].MaxConcurrent);
        }
        finally
        {
            await first.DisposeAsync();
            await second.DisposeAsync();
        }
    }

    private sealed class UngatedDriver : IDeviceDriver
    {
        private int _active;
        public int MaxConcurrent;
        public bool IsConnected => true;
        public string? LastError => null;
        public Task ConnectAsync(Device device, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void Disconnect() { }
        public Task WriteAsync(Device device, Tag tag, double value, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public async Task<IReadOnlyList<RawTagValue>> ReadAsync(Device device, ReadPlan plan, CancellationToken cancellationToken = default)
        {
            MaxConcurrent = Math.Max(MaxConcurrent, Interlocked.Increment(ref _active));
            await Task.Delay(30, cancellationToken);
            Interlocked.Decrement(ref _active);
            return [new(device.StationNumber, device.StationNumber)];
        }
    }
}
