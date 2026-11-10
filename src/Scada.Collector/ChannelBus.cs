using System.Collections.Concurrent;
using System.Diagnostics;
using Scada.Core.Entities;
using Scada.Core.Interfaces;
using Scada.Core.Models;

namespace Scada.Collector;

/// <summary>
/// 一条共享物理链路（一个 TCP 连接或一个串口），可被同一通道上的多台从站设备租用。
///
/// 四个约束：
/// 1. 总线上同时只允许一个事务：Modbus 请求/响应没有多路复用标识（TCP 的 MBAP 事务号也不足以并发复用），
///    两个设备同时读写会交错拆帧，因此用信号量把"连接 + 一次请求"串起来。
/// 2. **互斥粒度是单个事务，不是一份读取计划**：一份计划可能有几十个读块，
///    整份加锁会让一台设备独占总线数百毫秒，把同总线上 100ms 周期的设备拖成秒级。
///    驱动侧通过 ITransactionGate 在每个请求前后排队（见 ModbusMasterDriver.Transact）。
/// 3. 连接只有一份：驱动实例按通道创建，站号与字节序仍来自各设备（见 DeviceLink.From），
///    所以一次连接天然可以读多个从站。
/// 4. 引用计数归零即释放：所有成员设备停采（删除、禁用、改绑其它通道）后链路必须关闭，
///    否则改过端点的旧连接会一直占着端口。
/// </summary>
public sealed class ChannelBus : IAsyncDisposable
{
    private readonly SemaphoreSlim _wire = new(1, 1);
    private readonly Func<Device, IDeviceDriver> _factory;
    private readonly Device _endpoint;
    private readonly BusGate _gate;
    private readonly ConcurrentDictionary<int, MemberCounters> _members = new();
    private readonly long _startedMs = ScadaTime.NowMs();
    private IDeviceDriver? _driver;
    private int _leases;
    // 内部按微秒累加：Modbus over TCP 的单次事务常在亚毫秒级，按毫秒取整会让占用统计恒为 0。
    private long _transactions, _waitUs, _waitMaxUs, _busyUs, _slowQueues;
    private string? _lastError;

    public ChannelBus(Device endpoint, Func<Device, IDeviceDriver> factory)
    {
        _endpoint = endpoint;
        _factory = factory;
        _gate = new BusGate(this);
    }

    public int ChannelId => _endpoint.ChannelId ?? 0;

    public string Endpoint => EndpointKey(_endpoint);

    public int LeaseCount => Volatile.Read(ref _leases);

    public long Transactions => Interlocked.Read(ref _transactions);

    public bool IsConnected => _driver is { IsConnected: true };

    public string? LastError => _lastError ?? _driver?.LastError;

    /// <summary>端点指纹：通道换端点后必须换链路，不能复用旧连接。</summary>
    public static string EndpointKey(Device device) => ChannelProtocols.UsesSerial(device.Protocol)
        ? $"{device.Protocol}|{device.SerialPort}|{device.BaudRate}|{device.DataBits}|{device.Parity}|{device.StopBits}|{device.TimeoutMs}"
        : $"{device.Protocol}|{device.Host}|{device.Port}|{device.TimeoutMs}";

    internal void AddLease() => Interlocked.Increment(ref _leases);

    internal void RemoveLease() => Interlocked.Decrement(ref _leases);

    public async Task ConnectAsync(CancellationToken cancellationToken) =>
        _ = await ConnectAndGetAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// 在总线上执行一次设备级操作。对支持按事务加锁的驱动（Modbus 家族）不再套外层总线锁：
    /// 驱动在每个请求前后自己排队，外层再锁一次就会退回"整份计划独占"。
    /// </summary>
    public async Task<T> RunAsync<T>(Func<IDeviceDriver, Task<T>> operation, CancellationToken cancellationToken)
    {
        var driver = _driver;
        if (driver is not { IsConnected: true })
            driver = await ConnectAndGetAsync(cancellationToken).ConfigureAwait(false);

        if (driver is ITransactionGatedDriver) return await operation(driver).ConfigureAwait(false);

        // 未实现事务钩子的驱动退化为整次操作加锁，行为与共享通道之前一致。
        await _wire.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await operation(driver).ConfigureAwait(false); }
        finally { _wire.Release(); }
    }

    public Task RunAsync(Func<IDeviceDriver, Task> operation, CancellationToken cancellationToken) =>
        RunAsync(async driver => { await operation(driver).ConfigureAwait(false); return true; }, cancellationToken);

    private async Task<IDeviceDriver> ConnectAndGetAsync(CancellationToken cancellationToken)
    {
        await _wire.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false); }
        finally { _wire.Release(); }
    }

    // 调用方必须已持有 _wire。
    private async Task<IDeviceDriver> EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (_driver is { IsConnected: true }) return _driver;
        await TearDownAsync().ConfigureAwait(false);

        var driver = _factory(_endpoint);
        if (driver is ITransactionGatedDriver gated) gated.TransactionGate = _gate;

        // 连接必须有时限：端点不可达时 TCP 会一直等 SYN 超时（Windows 约 21 秒），
        // 这段时间整条总线被占住，同通道上所有设备一起停摆。
        var budget = TimeSpan.FromMilliseconds(BusPolicies.ConnectBudgetMs(_endpoint.TimeoutMs));
        var connect = driver.ConnectAsync(_endpoint, cancellationToken);
        try
        {
            await connect.WaitAsync(budget, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            _lastError = $"连接超时（{budget.TotalMilliseconds:F0}ms）";
            // 连接任务可能还在内核里等 SYN。这里不能立刻释放驱动：正在 Connect 的对象被 Dispose
            // 会在后台线程抛异常并可能留下悬挂套接字。等它自己结束后再释放。
            _ = DisposeWhenSettledAsync(connect, driver);
            throw new TimeoutException(
                $"通道 {_endpoint.Name} 连接超时（{budget.TotalMilliseconds:F0}ms），端点不可达。已释放总线，不会阻塞同通道上的其它设备", ex);
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            await SafeDisposeAsync(driver).ConfigureAwait(false);
            throw;
        }

        _driver = driver;
        return driver;
    }

    private static async Task DisposeWhenSettledAsync(Task connect, IDeviceDriver driver)
    {
        try { await connect.ConfigureAwait(false); } catch (Exception) { /* 连接本身失败也不需要再报一次 */ }
        await SafeDisposeAsync(driver).ConfigureAwait(false);
    }

    private async Task TearDownAsync()
    {
        var driver = Interlocked.Exchange(ref _driver, null);
        if (driver is null) return;
        await SafeDisposeAsync(driver).ConfigureAwait(false);
    }

    private static async ValueTask SafeDisposeAsync(IDeviceDriver driver)
    {
        try { driver.Disconnect(); } catch (Exception) { /* 关闭异常忽略：串口可能已被拔出 */ }
        try { await driver.DisposeAsync().ConfigureAwait(false); } catch (Exception) { }
    }

    /// <summary>
    /// 总线观测快照。<paramref name="describe"/> 把设备 id 映射成名称与站号——采集侧有配置快照，
    /// 总线自己不认识业务主数据。
    /// </summary>
    public ChannelStatistics Snapshot(Func<int, (string Name, int Station)>? describe = null)
    {
        var now = ScadaTime.NowMs();
        var transactions = Interlocked.Read(ref _transactions);
        var totalBusyUs = Interlocked.Read(ref _busyUs);
        var totalWaitUs = Interlocked.Read(ref _waitUs);
        var uptimeUs = Math.Max(1_000, (now - _startedMs) * 1_000);

        var members = new List<ChannelMemberStatistics>(_members.Count);
        foreach (var (deviceId, counters) in _members)
        {
            var memberTransactions = Interlocked.Read(ref counters.Transactions);
            var memberBusyUs = Interlocked.Read(ref counters.BusyUs);
            var (name, station) = describe?.Invoke(deviceId) ?? ($"#{deviceId}", 0);
            members.Add(new(
                deviceId, name, station,
                memberTransactions,
                Interlocked.Read(ref counters.SlowQueues),
                Interlocked.Read(ref counters.Failures),
                memberTransactions == 0 ? 0 : Math.Round(Interlocked.Read(ref counters.WaitUs) / 1_000d / memberTransactions, 1),
                Math.Round(Interlocked.Read(ref counters.WaitMaxUs) / 1_000d, 1),
                Math.Round(memberBusyUs / 1_000d, 1),
                totalBusyUs == 0 ? 0 : Math.Round(memberBusyUs * 100d / totalBusyUs, 1),
                Interlocked.Read(ref counters.LastTs)));
        }
        // 按占用时间倒序：第一个就是"谁把总线吃满了"。
        members.Sort(static (a, b) => b.BusyMs.CompareTo(a.BusyMs));

        return new ChannelStatistics(
            ChannelId, "", "", Endpoint,
            IsConnected, 1, LeaseCount,
            transactions,
            Math.Round(Math.Min(100d, totalBusyUs * 100d / uptimeUs), 1),
            transactions == 0 ? 0 : Math.Round(totalWaitUs / 1_000d / transactions, 1),
            Math.Round(Interlocked.Read(ref _waitMaxUs) / 1_000d, 1),
            Interlocked.Read(ref _slowQueues),
            _lastError ?? _driver?.LastError,
            now,
            members);
    }

    private void RecordTransaction(int ownerId, double waitMs, double busyMs, bool success)
    {
        var waitUs = (long)Math.Round(waitMs * 1_000);
        var busyUs = (long)Math.Round(busyMs * 1_000);
        Interlocked.Increment(ref _transactions);
        Interlocked.Add(ref _waitUs, waitUs);
        Interlocked.Add(ref _busyUs, busyUs);
        if (waitUs >= BusPolicies.SlowQueueMs * 1_000L) Interlocked.Increment(ref _slowQueues);
        Interlocked.Exchange(ref _waitMaxUs, Math.Max(Interlocked.Read(ref _waitMaxUs), waitUs));

        var member = _members.GetOrAdd(ownerId, static _ => new MemberCounters());
        Interlocked.Increment(ref member.Transactions);
        Interlocked.Add(ref member.WaitUs, waitUs);
        Interlocked.Add(ref member.BusyUs, busyUs);
        if (waitUs >= BusPolicies.SlowQueueMs * 1_000L) Interlocked.Increment(ref member.SlowQueues);
        if (!success) Interlocked.Increment(ref member.Failures);
        Interlocked.Exchange(ref member.WaitMaxUs, Math.Max(Interlocked.Read(ref member.WaitMaxUs), waitUs));
        Interlocked.Exchange(ref member.LastTs, ScadaTime.NowMs());
    }

    public async ValueTask DisposeAsync()
    {
        await _wire.WaitAsync().ConfigureAwait(false);
        try { await TearDownAsync().ConfigureAwait(false); }
        finally { _wire.Release(); }
        _wire.Dispose();
    }

    private sealed class MemberCounters
    {
        public long Transactions, WaitUs, WaitMaxUs, BusyUs, SlowQueues, Failures, LastTs;
    }

    /// <summary>把总线互斥暴露成驱动侧的"单次事务"钩子，同时采集排队与占用。</summary>
    private sealed class BusGate(ChannelBus bus) : ITransactionGate
    {
        public T Execute<T>(int ownerId, Func<T> operation, CancellationToken cancellationToken)
        {
            var queuedAt = Stopwatch.GetTimestamp();
            bus._wire.Wait(cancellationToken);
            var waitedMs = Stopwatch.GetElapsedTime(queuedAt).TotalMilliseconds;
            var startedAt = Stopwatch.GetTimestamp();
            var ok = false;
            try
            {
                var result = operation();
                ok = true;
                return result;
            }
            finally
            {
                bus.RecordTransaction(ownerId, waitedMs, Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds, ok);
                bus._wire.Release();
            }
        }
    }
}

/// <summary>
/// 设备在共享通道上的租约。对 DeviceWorker 来说它就是一个普通的 IDeviceDriver，
/// 采集内核因此不需要理解"通道"——所有调用被转到共享总线。
/// </summary>
public sealed class ChannelLease : IDeviceDriver
{
    private readonly ChannelBus _bus;
    private readonly Func<ValueTask> _release;
    private int _released;

    public ChannelLease(ChannelBus bus, Func<ValueTask> release)
    {
        _bus = bus;
        _release = release;
    }

    public bool IsConnected => Volatile.Read(ref _released) == 0 && _bus.IsConnected;

    public string? LastError => _bus.LastError;

    public Task ConnectAsync(Device device, CancellationToken cancellationToken = default) => _bus.ConnectAsync(cancellationToken);

    /// <summary>
    /// 刻意不动共享链路：停一台设备（改配置、禁用、切备用）不该让同总线上的其它设备断线重连。
    /// 真正的链路故障驱动自己已经把 IsConnected 置为 false，下一次操作会重建连接，
    /// 所以这里不需要、也不应该主动拆除。
    /// </summary>
    public void Disconnect() { }

    public Task<IReadOnlyList<RawTagValue>> ReadAsync(Device device, ReadPlan plan, CancellationToken cancellationToken = default) =>
        _bus.RunAsync(driver => driver.ReadAsync(device, plan, cancellationToken), cancellationToken);

    public Task WriteAsync(Device device, Tag tag, double value, CancellationToken cancellationToken = default) =>
        _bus.RunAsync(driver => driver.WriteAsync(device, tag, value, cancellationToken), cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _released, 1) != 0) return;
        await _release().ConfigureAwait(false);
    }
}

/// <summary>按通道分配共享链路。设备没有绑定通道时仍然是一设备一驱动，行为与之前完全一致。</summary>
public sealed class ChannelRegistry
{
    private readonly Dictionary<string, ChannelBus> _buses = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public IDeviceDriver Acquire(Device device, Func<Device, IDeviceDriver> factory)
    {
        if (device.ChannelId is not int channelId)
        {
            // 未绑定通道：独占连接，直接返回驱动本身，不引入任何额外语义。
            return factory(device);
        }

        var key = $"{channelId}|{ChannelBus.EndpointKey(device)}";
        ChannelBus bus;
        lock (_gate)
        {
            if (!_buses.TryGetValue(key, out bus!)) _buses[key] = bus = new ChannelBus(device, factory);
            bus.AddLease();
        }
        return new ChannelLease(bus, () => ReleaseAsync(key, bus));
    }

    public int BusCount
    {
        get { lock (_gate) return _buses.Count; }
    }

    public List<ChannelBus> BusesOf(int channelId)
    {
        lock (_gate) return [.. _buses.Values.Where(x => x.ChannelId == channelId)];
    }

    private async ValueTask ReleaseAsync(string key, ChannelBus bus)
    {
        ChannelBus? retire = null;
        lock (_gate)
        {
            bus.RemoveLease();
            if (bus.LeaseCount <= 0 && _buses.TryGetValue(key, out var current) && ReferenceEquals(current, bus))
            {
                _buses.Remove(key);
                retire = bus;
            }
        }
        if (retire is not null) await retire.DisposeAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAllAsync()
    {
        List<ChannelBus> buses;
        lock (_gate)
        {
            buses = [.. _buses.Values];
            _buses.Clear();
        }
        foreach (var bus in buses) await bus.DisposeAsync().ConfigureAwait(false);
    }
}
