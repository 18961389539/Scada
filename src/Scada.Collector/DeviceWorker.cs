using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Scada.Collector.Observability;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Interfaces;
using Scada.Core.Models;
using Scada.Core.Options;
using Scada.Core.Protocols;

namespace Scada.Collector;

/// <summary>
/// 一台设备采集任务的不可变输入。参数收成一个记录，避免构造器十几项、调用点靠位置对齐。
/// </summary>
internal sealed record DeviceWorkerContext(
    Device Device,
    IReadOnlyList<Tag> Tags,
    IRealtimeCache Cache,
    IRealtimeBus Bus,
    IAlarmSink AlarmSink,
    ScadaSettings Settings,
    ILogger Logger,
    int ConfigVersion,
    ICollectorJournal? Journal);

/// <summary>
/// 单台设备的采集循环：连接、分档轮询、写命令、即时读取与诊断。
/// 一个 worker 只被一个循环线程驱动，内部状态（缓存基线、档位栅格）不需要加锁。
/// </summary>
internal sealed class DeviceWorker
{
    // 采集日志写失败后的重试间隔：磁盘满通常需要人工处理，但文件被临时占用/瞬时 IO 错误应能自行恢复。
    private const int JournalRetryMs = 5000;
    // 同类日志的节流：磁盘满会持续几小时，每 5 秒一条会把日志刷满。
    private const int JournalLogIntervalMs = 60_000;
    // 坏点摘要的重算间隔：它只是给人看的状态文本，不必每轮采集都遍历全量测点。
    private const int StatusSummaryIntervalMs = 2000;

    private readonly Device _device;
    private readonly IReadOnlyList<Tag> _tags;
    private readonly IDeviceDriver _driver;
    private readonly IReadPlanBuilder _planBuilder;
    private readonly IRealtimeCache _cache;
    private readonly IRealtimeBus _bus;
    private readonly IAlarmSink _alarmSink;
    private readonly ScadaSettings _settings;
    private readonly ILogger _logger;
    private readonly Action<CollectorDeviceStatus> _reportStatus;
    private readonly Channel<WriteCommand> _commands = Channel.CreateBounded<WriteCommand>(128);
    private readonly Channel<ReadCommand> _reads = Channel.CreateBounded<ReadCommand>(32);
    private readonly Channel<ProbeCommand> _probes = Channel.CreateBounded<ProbeCommand>(8);
    private readonly Channel<bool> _wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly Dictionary<int, TagValue> _lastPublished = new();
    private readonly Dictionary<int, Tag> _tagMap;
    // 复用的原始值索引：每轮重建会保留字典容量，避免按采集频率持续分配。
    private readonly Dictionary<int, RawTagValue> _rawMap = new();
    // 诊断展示用全量布局：读取按档位拆分，但点位地址视图仍应看到设备全部测点。
    private readonly ReadPlan _plan;
    private readonly IReadOnlyList<ScanLane> _lanes;
    private readonly DeviceDiagnostics _diagnostics;
    private readonly DeviceWriteVerifier _writeVerifier;
    private readonly ICollectorJournal? _journal;
    // 探针限流按测点计：设备级限流会让相邻测点的第二次调试请求被拒。
    private readonly ConcurrentDictionary<int, long> _lastProbeMs = new();
    private volatile bool _online;
    private int _reconnectRequested;
    private int? _offlineAlarmId;
    private int _reconnectAttempts;
    private long _commFailures;
    private long _journalFailures;
    private bool _journalFaulted;
    private bool _targetBlocked;
    private string? _journalError;
    private long _lastJournalLogMs;
    private long _lastSummaryMs;
    private string? _summaryText;
    private string? _lastError;
    private long _lastPollTs;
    private double _lastCycleMs;

    public DeviceWorker(
        DeviceWorkerContext context,
        IDeviceDriver driver,
        IReadPlanBuilder planBuilder,
        Action<CollectorDeviceStatus> reportStatus)
    {
        _device = context.Device;
        _tags = context.Tags;
        _driver = driver;
        _planBuilder = planBuilder;
        _cache = context.Cache;
        _bus = context.Bus;
        _alarmSink = context.AlarmSink;
        _settings = context.Settings;
        _logger = context.Logger;
        _reportStatus = reportStatus;
        _journal = context.Journal;
        _tagMap = _tags.ToDictionary(x => x.Id);
        _plan = _planBuilder.Build(_device, _tags);
        _lanes = BuildLanes(_device, _tags, _planBuilder);
        _diagnostics = new DeviceDiagnostics(_device, _tags, _plan, context.ConfigVersion);
        _writeVerifier = new DeviceWriteVerifier(_device, _driver, _planBuilder, _cache, _journal, _diagnostics, PublishIfChanged);
    }

    public CollectorDeviceDiagnostics Diagnostics(bool pending) => _diagnostics.Snapshot(_commands.Reader.Count, pending);

    public void RequestReconnect()
    {
        if (Interlocked.Exchange(ref _reconnectRequested, 1) == 0) _diagnostics.ReconnectRequested();
        _wake.Writer.TryWrite(true);
    }

    /// <summary>
    /// 卡死升级的最后一步：关掉驱动（套接字/串口），让卡在 ReadAsync 里的调用返回。
    /// 只会丢在途事务，比让整台设备永久停采好。
    /// </summary>
    public void ForceClose()
    {
        try { _driver.Disconnect(); }
        catch (Exception ex) { _logger.LogDebug(ex, "设备 {Device} 的驱动在强制关闭时抛出", _device.Name); }
    }

    // 复用采集连接做一次探针读取：与采集轮询在同一循环内串行，不会二次打开串口。
    // 返回 null 表示当前未在线，调用方回退到独立连接。
    public Task<CollectorProbeResult?> ProbeAsync(Tag tag, CancellationToken cancellationToken)
    {
        if (!_online || !_driver.IsConnected) return Task.FromResult<CollectorProbeResult?>(null);
        var now = Environment.TickCount64;
        // 近似限流：同一测点 1 秒内只放行一次，不同测点互不影响。
        if (now - _lastProbeMs.GetOrAdd(tag.Id, 0) < 1000)
            return Task.FromResult<CollectorProbeResult?>(new(true, null, "该测点测试读取过于频繁，请稍后再试"));
        if (_lastProbeMs.Count > 4096) _lastProbeMs.Clear();
        _lastProbeMs[tag.Id] = now;
        var completion = new TaskCompletionSource<CollectorProbeResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_probes.Writer.TryWrite(new ProbeCommand(tag, completion, cancellationToken)))
            return Task.FromResult<CollectorProbeResult?>(new(true, null, "测试队列已满或采集已停止，请稍后再试"));
        _wake.Writer.TryWrite(true);
        return completion.Task;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        // 采集循环遇到未预期异常（磁盘写满、驱动库抛出）时不能让设备永久停采：
        // 原先记录日志后任务即结束，只有配置变更或重启服务才会恢复，现场就是一段静默数据黑洞。
        // 这里按指数退避重启循环，退避上限 60 秒；取消信号仍然立即生效。
        var backoffMs = 1000;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await RunCoreAsync(cancellationToken);
                break; // RunCoreAsync 只在取消时正常返回
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _online = false;
                _lastError = $"采集任务异常停止，{backoffMs / 1000} 秒后自动重启：{ex.Message}";
                _diagnostics.Stopped(ex.Message);
                ReportStatus();
                _logger.LogError(ex, "设备 {Device} 的采集任务异常停止，{Delay} 秒后自动重启", _device.Name, backoffMs / 1000);
                try { _driver.Disconnect(); }
                catch (Exception) { /* 驱动已损坏时断开也会抛，重启路径必须继续 */ }
                try { await Task.Delay(backoffMs, cancellationToken); }
                catch (OperationCanceledException) { break; }
                backoffMs = Math.Min(backoffMs * 2, 60_000);
            }
        }
        _diagnostics.Stopped();
    }

    // 采集日志（网关缓冲/落盘）失败不能直接结束采集任务，但也绝不能把未落盘的数据发出去。
    // 返回 false 时调用方停住本轮、保持连接，退避后重试；恢复后自动接上。
    private async Task<bool> TryAppendJournalAsync(IReadOnlyList<TagValue> values, CancellationToken cancellationToken)
    {
        if (_journal is null) return true;
        try
        {
            await _journal.AppendAsync(_device.Id, values, cancellationToken);
            _journalError = null;
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _journalFailures++;
            _journalError = $"采集持久记录失败，本轮数据不发布，稍后重试（累计 {_journalFailures} 次）：{ex.Message}";
            // 磁盘满会持续很久：只在首次与之后每分钟记一条，避免把日志刷成同一个异常。
            var now = ScadaTime.NowMs();
            if (now - _lastJournalLogMs >= JournalLogIntervalMs)
            {
                _lastJournalLogMs = now;
                _logger.LogWarning(ex, "设备 {Device} 的采集日志写入失败，累计 {Count} 次", _device.Name, _journalFailures);
            }
            return false;
        }
    }

    // 一个档位 = 一个独立节拍 + 一份只含该档测点的读取计划。
    // 同一设备的档位共用一条连接，读取仍在单循环内串行，不会并发访问驱动。
    private sealed class ScanLane(int intervalMs, ReadPlan plan, IReadOnlyList<Tag> tags)
    {
        public int IntervalMs { get; } = intervalMs;
        public ReadPlan Plan { get; } = plan;
        public IReadOnlyList<Tag> Tags { get; } = tags;
        // 单调时钟上的下一个采集时点；0 表示尚未对齐（首轮、退避或日志故障后）。
        public long NextDueMs;
    }

    private static IReadOnlyList<ScanLane> BuildLanes(Device device, IReadOnlyList<Tag> tags, IReadPlanBuilder planBuilder)
    {
        if (tags.Count == 0) return [new ScanLane(ScanCadence.Effective(device), planBuilder.Build(device, tags), tags)];
        return tags.GroupBy(tag => ScanCadence.Effective(device, tag))
            .OrderBy(group => group.Key)
            .Select(group =>
            {
                var laneTags = group.ToList();
                return new ScanLane(group.Key, planBuilder.Build(device, laneTags), laneTags);
            })
            .ToArray();
    }

    private void ResetLanes()
    {
        foreach (var lane in _lanes) lane.NextDueMs = 0;
    }

    // 未对齐的档位优先（启动/退避恢复后先把各档都读一遍），其余取最早到期。
    private ScanLane NextLane()
    {
        ScanLane? earliest = null;
        foreach (var lane in _lanes)
        {
            if (lane.NextDueMs == 0) return lane;
            if (earliest is null || lane.NextDueMs < earliest.NextDueMs) earliest = lane;
        }
        return earliest!;
    }

    // 命令处理的时间预算：一批设定值下发时不能把采集饿死。
    // 取半个周期（最少 50ms、最多 500ms），超预算的命令留到下一轮。
    private static int CommandBudgetMs(int intervalMs) => Math.Clamp(intervalMs / 2, 50, 500);

    private async Task RunCoreAsync(CancellationToken cancellationToken)
    {
        var backoffMs = 0;
        // 各档位独立栅格；0 表示尚未对齐（首轮、退避或日志故障后）。
        ResetLanes();

        while (!cancellationToken.IsCancellationRequested)
        {
            var cycleStart = Stopwatch.GetTimestamp();

            if (Interlocked.Exchange(ref _reconnectRequested, 0) != 0)
            {
                _driver.Disconnect(); _online = false; backoffMs = 0;
                ResetLanes();
                _diagnostics.Reconnecting(); ReportStatus();
            }

            if (!_driver.IsConnected)
            {
                // 出站目标策略：保存时已校验，这里兜底直接改库、导入工程或下发网关配置绕过校验的情况。
                // 被拒绝时根本不会发起连接，固定 30 秒退避即可，不必按指数退避打满。
                if (OutboundTargetPolicy.ValidateDevice(_device, _settings) is string targetError)
                {
                    backoffMs = 30000;
                    ResetLanes();
                    _online = false;
                    _lastError = targetError;
                    if (!_targetBlocked)
                    {
                        _targetBlocked = true;
                        _commFailures++;
                        _diagnostics.ConnectionFailed(targetError, CollectorErrorKind.Configuration, backoffMs);
                        await MarkOfflineAsync(targetError, cancellationToken);
                    }
                    ReportStatus();
                    await ProcessCommandsAsync(cancellationToken);
                    await WaitForWorkOrDelayAsync(backoffMs, cancellationToken);
                    continue;
                }
                _targetBlocked = false;

                try
                {
                    _diagnostics.Connecting();
                    await _driver.ConnectAsync(_device, cancellationToken);
                    _diagnostics.Connected();
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _lastError = ex.Message;
                    _commFailures++;
                    backoffMs = backoffMs == 0 ? 1000 : Math.Min(backoffMs * 2, 30000);
                    ResetLanes();
                    _diagnostics.ConnectionFailed(ex.Message, CollectorErrorClassifier.Classify(ex), backoffMs);
                    await MarkOfflineAsync(ex.Message, cancellationToken);
                    _reconnectAttempts++;
                    ReportStatus();
                    await ProcessCommandsAsync(cancellationToken);
                    await WaitForWorkOrDelayAsync(backoffMs, cancellationToken);
                    continue;
                }
            }

            // 选出最早到期的档位。未对齐的档位（首轮/退避恢复）立即执行，
            // 因此启动时各档会依次读一遍，而稳态下每档按自己的周期独立运行。
            var lane = NextLane();
            var dueNowMs = Environment.TickCount64;
            lane.NextDueMs = PollSchedule.Align(lane.NextDueMs, dueNowMs, lane.IntervalMs);
            if (lane.NextDueMs > dueNowMs)
            {
                // 等待期间仍会处理写命令、重连与即时读取；被唤醒后重新评估最早到期的档位。
                await WaitForWorkOrDelayAsync(PollSchedule.DelayMs(lane.NextDueMs, dueNowMs, lane.IntervalMs), cancellationToken);
                await ProcessCommandsAsync(cancellationToken, CommandBudgetMs(lane.IntervalMs));
                continue;
            }

            var readStart = Stopwatch.GetTimestamp();
            _diagnostics.Polling();
            try
            {
                var rawValues = await _driver.ReadAsync(_device, lane.Plan, cancellationToken);
                var readDurationMs = Stopwatch.GetElapsedTime(readStart).TotalMilliseconds;
                var ts = ScadaTime.NowMs();
                // 复用索引：驱动返回重复测点时用下标覆盖，不会像 ToDictionary 那样整轮抛异常。
                _rawMap.Clear();
                foreach (var raw in rawValues) _rawMap[raw.TagId] = raw;
                var updates = new List<TagValue>(lane.Tags.Count);
                var badPoints = 0;

                foreach (var tag in lane.Tags)
                {
                    var raw = _rawMap.GetValueOrDefault(tag.Id) ?? new RawTagValue(tag.Id, 0, TagQuality.Bad, null, "本次读取未返回该测点");

                    if (tag.DataType == TagDataType.String)
                    {
                        var previous = _cache.Get(tag.Id);
                        var textQuality = raw.Quality == TagQuality.Good && raw.Text is null ? TagQuality.Bad : raw.Quality;
                        var text = textQuality == TagQuality.Good ? raw.Text : previous?.Text;
                        if (textQuality != TagQuality.Good) badPoints++;
                        updates.Add(new TagValue(tag.Id, 0, textQuality, ts, raw.SourceTs, raw.Error, tag.CalculationRevision, text));
                        continue;
                    }

                    var value = raw.Quality == TagQuality.Good
                        ? tag.DataType == TagDataType.Bool ? raw.Value : raw.Value * tag.Scale + tag.Offset
                        : _cache.Get(tag.Id)?.Value ?? 0;
                    var quality = raw.Quality;
                    var pointError = raw.Error;
                    if (!double.IsFinite(value)) { value = _cache.Get(tag.Id)?.Value ?? 0; quality = TagQuality.Bad; pointError = "工程量换算结果不是有限数值"; }
                    if (quality != TagQuality.Good) badPoints++;
                    var tagValue = new TagValue(tag.Id, value, quality, ts, raw.SourceTs, pointError, tag.CalculationRevision);
                    updates.Add(tagValue);
                }
                if (!await TryAppendJournalAsync(updates, cancellationToken))
                {
                    // 未落盘的批次既不能发布也不能写缓存：网关断线重传依赖本地日志，一旦发布就补不回来。
                    // 但这是"存储故障"而不是"通信故障"：不能把测点扫成 CommFailure，更不能报设备离线——
                    // 那会把一次磁盘满变成满屏设备离线报警，把运维引向线路排查。
                    MarkStorageFault(_journalError);
                    ResetLanes();
                    await ProcessCommandsAsync(cancellationToken);
                    await WaitForWorkOrDelayAsync(Math.Max(lane.IntervalMs, JournalRetryMs), cancellationToken);
                    continue;
                }
                if (_journalFaulted)
                {
                    _journalFaulted = false;
                    _diagnostics.StorageRecovered();
                    _logger.LogInformation("设备 {Device} 的采集日志已恢复，继续发布数据", _device.Name);
                }
                foreach (var tagValue in updates)
                {
                    var tag = _tagMap[tagValue.TagId]; var raw = _rawMap.GetValueOrDefault(tag.Id);
                    _cache.Set(tagValue);
                    _diagnostics.Point(tagValue, raw?.Quality == TagQuality.Good ? raw.Value : null);
                    PublishIfChanged(tagValue);
                }

                _online = true;
                // 采集日志的错误优先展示：它意味着这一段数据没进历史，比个别坏点更需要处理。
                _lastError = JoinErrors(_journalError, QualitySummary(ts));
                _reconnectAttempts = 0;
                _lastPollTs = ts;
                _lastCycleMs = Stopwatch.GetElapsedTime(cycleStart).TotalMilliseconds;

                if (_offlineAlarmId is int alarmId)
                {
                    await _alarmSink.RecoverDeviceOfflineAsync(alarmId, ts, cancellationToken);
                    _offlineAlarmId = null;
                }

                ReportStatus();
                _diagnostics.Polled(readDurationMs, _lastError);
                CollectorMetrics.Polled(_device.Protocol, readDurationMs, badPoints);
                backoffMs = 0;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _lastError = ex.Message;
                _commFailures++;
                backoffMs = backoffMs == 0 ? 1000 : Math.Min(backoffMs * 2, 30000);
                ResetLanes();
                var kind = CollectorErrorClassifier.Classify(ex);
                _diagnostics.PollFailed(Stopwatch.GetElapsedTime(readStart).TotalMilliseconds, ex.Message, kind, Math.Max(lane.IntervalMs, backoffMs));
                CollectorMetrics.PollFailed(_device.Protocol, kind);
                await MarkOfflineAsync(ex.Message, cancellationToken);
                _driver.Disconnect();
                ReportStatus();
                // 档位已全部重置，必须显式退避，否则下一轮会立刻重连重试。
                await ProcessCommandsAsync(cancellationToken);
                await WaitForWorkOrDelayAsync(backoffMs, cancellationToken);
                continue;
            }

            await ProcessCommandsAsync(cancellationToken, CommandBudgetMs(lane.IntervalMs));
            // 推进该档位的固定栅格：本档采集耗时由下一轮的 Align 扣减，
            // 落后超过一个周期时重新对齐，不连续补帧。
            lane.NextDueMs = PollSchedule.Advance(lane.NextDueMs, lane.IntervalMs);
        }
    }

    public async Task<IReadOnlyList<TagValue>> ExecuteReadAsync(IReadOnlyList<int> tagIds, CancellationToken cancellationToken)
    {
        if (tagIds.Count is < 1 or > 256 || tagIds.Any(id => !_tagMap.ContainsKey(id)))
            throw new InvalidOperationException("读取测点必须属于该设备，且一次 1～256 个");
        if (!_driver.IsConnected || !_online || Volatile.Read(ref _reconnectRequested) != 0)
            throw new IOException("设备离线或正在重连");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(Math.Max(_settings.CommandTimeoutMs, 1));
        var completion = new TaskCompletionSource<IReadOnlyList<TagValue>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var tags = tagIds.Distinct().Select(id => _tagMap[id]).ToArray();
        if (!_reads.Writer.TryWrite(new(tags, completion, deadline.Token))) throw new IOException("读取队列已满或采集已停止");
        _wake.Writer.TryWrite(true);
        return await completion.Task.WaitAsync(deadline.Token);
    }

    public async Task<CommandExecutionResult> ExecuteWriteAsync(Tag tag, double value, CancellationToken cancellationToken)
    {
        if (!_tagMap.ContainsKey(tag.Id))
        {
            _diagnostics.CommandNotSent(tag.Id, "测点不属于该设备");
            return CommandResults.Of(GatewayCommandOutcome.NotSent, "测点不属于该设备");
        }

        if (!_driver.IsConnected || !_online || Volatile.Read(ref _reconnectRequested) != 0)
        {
            _diagnostics.CommandNotSent(tag.Id, "设备离线或等待重连，无法下发");
            return CommandResults.Of(GatewayCommandOutcome.NotSent, "设备离线或等待重连，无法下发");
        }

        var tcs = new TaskCompletionSource<CommandExecutionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(Math.Max(_settings.CommandTimeoutMs, 1));
        var command = new WriteCommand(tag, value, tcs, deadline.Token);
        if (!_commands.Writer.TryWrite(command))
        {
            _diagnostics.CommandNotSent(tag.Id, "命令队列已满或采集已停止");
            return CommandResults.Of(GatewayCommandOutcome.NotSent, "命令队列已满或采集已停止");
        }
        _wake.Writer.TryWrite(true);

        try
        {
            return await tcs.Task.WaitAsync(deadline.Token);
        }
        catch (OperationCanceledException)
        {
            return CommandResults.Of(command.Started ? GatewayCommandOutcome.Unknown : GatewayCommandOutcome.NotSent, command.Started
                ? "命令超时或请求已取消，执行结果未知，请检查设备反馈；不会自动重发"
                : "命令在执行前已超时或取消，未下发");
        }
    }

    public async Task<CommandExecutionResult> ExecuteWriteTextAsync(Tag tag, string text, CancellationToken cancellationToken)
    {
        if (!_tagMap.ContainsKey(tag.Id))
        {
            _diagnostics.CommandNotSent(tag.Id, "测点不属于该设备");
            return CommandResults.Of(GatewayCommandOutcome.NotSent, "测点不属于该设备");
        }

        if (!_driver.IsConnected || !_online || Volatile.Read(ref _reconnectRequested) != 0)
        {
            _diagnostics.CommandNotSent(tag.Id, "设备离线或等待重连，无法下发");
            return CommandResults.Of(GatewayCommandOutcome.NotSent, "设备离线或等待重连，无法下发");
        }

        var tcs = new TaskCompletionSource<CommandExecutionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(Math.Max(_settings.CommandTimeoutMs, 1));
        var command = new WriteCommand(tag, 0, tcs, deadline.Token, text);
        if (!_commands.Writer.TryWrite(command))
        {
            _diagnostics.CommandNotSent(tag.Id, "命令队列已满或采集已停止");
            return CommandResults.Of(GatewayCommandOutcome.NotSent, "命令队列已满或采集已停止");
        }
        _wake.Writer.TryWrite(true);

        try
        {
            return await tcs.Task.WaitAsync(deadline.Token);
        }
        catch (OperationCanceledException)
        {
            return CommandResults.Of(command.Started ? GatewayCommandOutcome.Unknown : GatewayCommandOutcome.NotSent, command.Started
                ? "命令超时或请求已取消，执行结果未知，请检查设备反馈；不会自动重发"
                : "命令在执行前已超时或取消，未下发");
        }
    }

    /// <summary>
    /// 处理写命令与旁路读取。<paramref name="budgetMs"/> 是给写命令的时间预算：
    /// 一次批量下发 20 个设定值会让设备在秒级时间内不采样，趋势上就是一段断档。
    /// </summary>
    private async Task ProcessCommandsAsync(CancellationToken cancellationToken, int budgetMs = int.MaxValue)
    {
        var startedAt = Stopwatch.GetTimestamp();
        while (_commands.Reader.TryRead(out var command))
        {
            if (command.Token.IsCancellationRequested)
            {
                _diagnostics.CommandNotSent(command.Tag.Id, "命令已取消，未下发", expired: true);
                command.Completion.TrySetResult(CommandResults.Of(GatewayCommandOutcome.NotSent, "命令已取消，未下发"));
                continue;
            }
            if (!_driver.IsConnected || !_online)
            {
                _diagnostics.CommandNotSent(command.Tag.Id, "设备离线，无法下发");
                command.Completion.TrySetResult(CommandResults.Of(GatewayCommandOutcome.NotSent, "设备离线，无法下发"));
                continue;
            }

            try
            {
                command.Started = true;
                _diagnostics.CommandStarted(command.Tag.Id);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(command.Token, cancellationToken);
                linked.Token.ThrowIfCancellationRequested();
                if (command.Tag.DataType == TagDataType.String)
                {
                    var readBackText = await WriteTextAndVerifyAsync(command.Tag, command.Text ?? "", linked.Token);
                    var display = readBackText.Length > 64 ? readBackText[..64] + "…" : readBackText;
                    _diagnostics.CommandCompleted(command.Tag.Id, true, $"写入及回读成功：\"{display}\"");
                    command.Completion.TrySetResult(CommandResults.Of(GatewayCommandOutcome.Confirmed, "写入成功", null, readBackText));
                    CollectorMetrics.Commanded(_device.Protocol, nameof(GatewayCommandOutcome.Confirmed));
                    _logger.LogInformation(
                        "设备 {Device} 写入字符串测点 {Tag}，回读 {Length} 字符",
                        _device.Name,
                        command.Tag.Name,
                        readBackText.Length);
                }
                else
                {
                    var engineering = await WriteAndVerifyAsync(command.Tag, command.Value, linked.Token);
                    _diagnostics.CommandCompleted(command.Tag.Id, true, $"写入及回读成功：{engineering:0.######}");
                    command.Completion.TrySetResult(CommandResults.Of(GatewayCommandOutcome.Confirmed, "写入成功", engineering));
                    CollectorMetrics.Commanded(_device.Protocol, nameof(GatewayCommandOutcome.Confirmed));
                    _logger.LogInformation(
                        "设备 {Device} 写入测点 {Tag} = {Value}，回读 {ReadBack}",
                        _device.Name,
                        command.Tag.Name,
                        command.Value,
                        engineering);
                }
            }
            catch (OperationCanceledException)
            {
                _diagnostics.CommandCompleted(command.Tag.Id, false, "执行结果未知，请检查设备反馈；不会自动重发");
                command.Completion.TrySetResult(CommandResults.Of(GatewayCommandOutcome.Unknown, "执行结果未知，请检查设备反馈；不会自动重发"));
                CollectorMetrics.Commanded(_device.Protocol, nameof(GatewayCommandOutcome.Unknown));
            }
            catch (Exception ex)
            {
                _diagnostics.CommandCompleted(command.Tag.Id, false, ex.Message);
                _lastError = ex.Message;
                if (!_driver.IsConnected) await MarkOfflineAsync(ex.Message, cancellationToken);
                var outcome = CommandFailure.Read(ex);
                command.Completion.TrySetResult(CommandResults.Of(outcome, ex.Message));
                CollectorMetrics.Commanded(_device.Protocol, outcome.ToString());
            }

            // 至少做完一条再判断预算：单条命令本身的耗时无法抢占，但可以保证下一轮还有采样机会。
            if (budgetMs != int.MaxValue && Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds >= budgetMs) break;
        }
        await ProcessSideReadsAsync(cancellationToken);
    }

    // 即时读取和探针只把结果交还给请求方：不写缓存、不写采集日志、不改最近采样，也不结束本轮等待。
    private async Task ProcessSideReadsAsync(CancellationToken cancellationToken)
    {
        while (_reads.Reader.TryRead(out var read))
        {
            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(read.Token, cancellationToken);
                linked.Token.ThrowIfCancellationRequested();
                if (!_driver.IsConnected || !_online) throw new IOException("设备当前离线");
                var raw = await _driver.ReadAsync(_device, _planBuilder.Build(_device, read.Tags), linked.Token);
                var map = raw.ToDictionary(value => value.TagId);
                var now = ScadaTime.NowMs();
                var values = read.Tags.Select(tag =>
                {
                    if (!map.TryGetValue(tag.Id, out var value)) throw new IOException($"设备未返回测点 #{tag.Id}");
                    if (tag.DataType == TagDataType.String)
                        return new TagValue(tag.Id, 0, value.Quality, now, value.SourceTs, value.Error, tag.CalculationRevision, value.Text);
                    var engineering = tag.DataType == TagDataType.Bool ? value.Value : value.Value * tag.Scale + tag.Offset;
                    return new TagValue(tag.Id, engineering, double.IsFinite(engineering) ? value.Quality : TagQuality.Bad, now, value.SourceTs, value.Error, tag.CalculationRevision);
                }).ToArray();
                read.Completion.TrySetResult(values);
            }
            catch (OperationCanceledException) { read.Completion.TrySetCanceled(); }
            catch (Exception ex) { read.Completion.TrySetException(ex); }
        }
        while (_probes.Reader.TryRead(out var probe))
        {
            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(probe.Token, cancellationToken);
                linked.Token.ThrowIfCancellationRequested();
                if (!_driver.IsConnected || !_online) throw new IOException("设备当前离线，无法复用采集连接；请先恢复采集。");
                var raw = await _driver.ReadAsync(_device, _planBuilder.Build(_device, [probe.Tag]), linked.Token);
                probe.Completion.TrySetResult(new CollectorProbeResult(true, raw.FirstOrDefault(), null));
            }
            // 探针失败只回传请求方；但驱动在 ReadAsync 异常时会自行 IsConnected=false，是否重连由采集循环决定。
            catch (OperationCanceledException) { probe.Completion.TrySetResult(new CollectorProbeResult(true, null, "测试读取超时")); }
            catch (Exception ex) { probe.Completion.TrySetResult(new CollectorProbeResult(true, null, ex.Message)); }
        }
    }

    private void PublishIfChanged(TagValue tagValue)
    {
        if (!_lastPublished.TryGetValue(tagValue.TagId, out var previous)
            || previous.Value != tagValue.Value
            || previous.Text != tagValue.Text
            || previous.Quality != tagValue.Quality)
        {
            _bus.Publish(tagValue);
            _lastPublished[tagValue.TagId] = tagValue;
        }
    }

    private Task<double> WriteAndVerifyAsync(Tag tag, double value, CancellationToken cancellationToken) =>
        _writeVerifier.WriteAndVerifyAsync(tag, value, cancellationToken);

    private Task<string> WriteTextAndVerifyAsync(Tag tag, string text, CancellationToken cancellationToken) =>
        _writeVerifier.WriteTextAndVerifyAsync(tag, text, cancellationToken);

    /// <summary>
    /// 只处理通信链路故障：把测点标成 CommFailure（历史里要留一条断点）并触发设备离线报警。
    /// 存储故障走 <see cref="MarkStorageFault"/>，两者不能混。
    /// </summary>
    private async Task MarkOfflineAsync(string? error, CancellationToken cancellationToken)
    {
        if (!_online && _offlineAlarmId is not null)
        {
            return;
        }

        _online = false;
        var ts = ScadaTime.NowMs();
        var updates = _tags.Select(tag =>
        {
            var previous = _cache.Get(tag.Id);
            return new TagValue(tag.Id, previous?.Value ?? 0, TagQuality.CommFailure, ts, previous?.SourceTs, error, tag.CalculationRevision, previous?.Text);
        }).ToArray();
        // 存储已经故障时不再重复写日志：一次失败会被记成两次，且每轮白构造一份全量快照。
        if (!_journalFaulted) await TryAppendJournalAsync(updates, cancellationToken);
        foreach (var bad in updates)
        {
            var tag = _tagMap[bad.TagId]; _cache.Set(bad);
            _diagnostics.Point(bad, null);
            if (!_lastPublished.TryGetValue(tag.Id, out var previous) || previous.Quality != TagQuality.CommFailure)
            {
                _bus.Publish(bad);
                _lastPublished[tag.Id] = bad;
            }
        }

        if (_offlineAlarmId is null)
        {
            _offlineAlarmId = await _alarmSink.RaiseDeviceOfflineAsync(
                _device,
                error,
                ts,
                cancellationToken);
        }
    }

    /// <summary>
    /// 采集日志失败：这是本地存储问题，设备本身还在正常应答。
    /// 停住发布（未落盘的数据不能再发出去），但不改测点质量、不报设备离线。
    /// </summary>
    private void MarkStorageFault(string? error)
    {
        _online = false;
        _lastError = error;
        if (_journalFaulted) return;
        _journalFaulted = true;
        _diagnostics.StorageFaulted(error ?? "采集日志写入失败");
        _logger.LogError("设备 {Device} 的采集日志写入失败，采集暂停发布并保持连接：{Error}", _device.Name, error);
        ReportStatus();
    }

    private async Task WaitForWorkOrDelayAsync(int delayMs, CancellationToken cancellationToken)
    {
        var deadline = Environment.TickCount64 + Math.Max(delayMs, 10);
        while (!cancellationToken.IsCancellationRequested)
        {
            while (_wake.Reader.TryRead(out _)) { }
            // 写入和重连仍提前结束等待。即时读取在等待内完成，到点才开始下一轮采集。
            if (Volatile.Read(ref _reconnectRequested) != 0 || _commands.Reader.TryPeek(out _)) return;
            if (_reads.Reader.TryPeek(out _) || _probes.Reader.TryPeek(out _))
            {
                await ProcessSideReadsAsync(cancellationToken);
                continue;
            }
            var remaining = deadline - Environment.TickCount64;
            if (remaining <= 0) return;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linked.CancelAfter(TimeSpan.FromMilliseconds(remaining));
            try { await _wake.Reader.ReadAsync(linked.Token); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return; }
        }
    }

    /// <summary>
    /// 坏点摘要：只在状态里给人看，限频到 2 秒一次。原先每轮采集都遍历全部测点，
    /// 1000 点 × 10Hz 的纯开销换来的只是同一段文字。
    /// </summary>
    private string? QualitySummary(long nowMs)
    {
        if (nowMs - _lastSummaryMs < StatusSummaryIntervalMs) return _summaryText;
        _lastSummaryMs = nowMs;
        var parts = new List<string>(3);
        foreach (var tag in _tags)
        {
            var value = _cache.Get(tag.Id);
            // 多档位下，未采集到的档位在缓存里还没有值（首次启动时慢档尚未读过），
            // 这里必须排除 null，否则会把"还没采过"当成坏点并空引用。
            if (value is null || value.Quality == TagQuality.Good) continue;
            parts.Add($"#{value.TagId} {value.Error ?? value.Quality.ToString()}");
            if (parts.Count == 3) break;
        }
        _summaryText = parts.Count == 0 ? null : string.Join("；", parts);
        return _summaryText;
    }

    private static string? JoinErrors(params string?[] parts)
    {
        var text = string.Join("；", parts.Where(x => !string.IsNullOrEmpty(x)));
        return text.Length == 0 ? null : text;
    }

    private void ReportStatus()
    {
        _reportStatus(new CollectorDeviceStatus(
            _device.Id,
            _device.Name,
            _online,
            _lastError,
            _lastPollTs,
            _lastCycleMs,
            _tags.Count,
            _reconnectAttempts,
            _commFailures,
            ScadaTime.NowMs()));
    }

    public async ValueTask DisposeAsync()
    {
        _reads.Writer.TryComplete();
        while (_reads.Reader.TryRead(out var read)) read.Completion.TrySetException(new IOException("采集已停止"));
        _probes.Writer.TryComplete();
        while (_probes.Reader.TryRead(out var probe)) probe.Completion.TrySetResult(new CollectorProbeResult(true, null, "采集已停止"));
        _commands.Writer.TryComplete();
        while (_commands.Reader.TryRead(out var command))
        {
            _diagnostics.CommandNotSent(command.Tag.Id, "采集任务已停止，未下发", expired: true);
            command.Completion.TrySetResult(CommandResults.Of(GatewayCommandOutcome.NotSent, "设备配置变更或服务停止，未执行的命令已取消"));
        }
        await _driver.DisposeAsync();
    }

    private sealed record WriteCommand(Tag Tag, double Value, TaskCompletionSource<CommandExecutionResult> Completion, CancellationToken Token, string? Text = null)
    {
        public volatile bool Started;
    }

    private sealed record ReadCommand(IReadOnlyList<Tag> Tags, TaskCompletionSource<IReadOnlyList<TagValue>> Completion, CancellationToken Token);

    private sealed record ProbeCommand(Tag Tag, TaskCompletionSource<CollectorProbeResult?> Completion, CancellationToken Token);
}
