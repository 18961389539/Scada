using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Scada.Core.Calculations;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Interfaces;
using Scada.Core.Models;
using Scada.Core.Options;
using Scada.Data;
using Scada.Engine.Calculations;
using Scada.Engine.Realtime;

namespace Scada.Engine.Tests;

public class CalcJobTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    private const long T0 = 1_767_225_600_000; // 2026-01-01 00:00:00Z

    [Fact]
    public void PulseCounterHandlesRolloverResetAndAbnormalJumps()
    {
        var job = new CalcJob { Kind = CalcJobKind.Accumulator, Mode = AccumulatorMode.Pulse, Factor = 0.5, CounterMax = 65535, MaxDelta = 1000 };
        var state = new JobRuntimeState();
        void Feed(double raw, bool good = true) => CalcJobMath.Accumulate(job, state, new JobSample(raw, good, T0), T0);
        Feed(65000);                       // 首次只建立基准
        Assert.Equal(0, state.Get("total"));
        Feed(65500); Assert.Equal(250, state.Get("total"));
        Feed(10);    Assert.Equal(250 + (65535 - 65500 + 10 + 1) * 0.5, state.Get("total")); // 满量程回绕
        Assert.Contains("回绕", state.LastEvent);
        Feed(0, good: false); Feed(20);    // 坏质量不更新基准，恢复后补差
        Assert.Equal(273 + 5, state.Get("total"));
        Feed(5);                           // 未接近满量程的回落视为计数器复位，从 0 起算
        Assert.Contains("复位", state.LastEvent);
        Assert.Equal(278 + 2.5, state.Get("total"));
        Feed(5000);                        // 超过单次增量上限，丢弃
        Assert.Equal(280.5, state.Get("total")); Assert.Contains("超过上限", state.LastEvent);
    }

    [Theory]
    [InlineData(BadQualityPolicy.Hold, 100 * 3 + 100 * 3)]
    [InlineData(BadQualityPolicy.Skip, 100 * 3)]
    [InlineData(BadQualityPolicy.Zero, 100 * 3 + 50 * 1 + 50 * 1)]
    public void FlowIntegrationAppliesBadQualityPolicy(BadQualityPolicy policy, double expected)
    {
        // 恒定 100 单位/秒，采样间隔 1 秒；第 4、5 秒输入坏质量，第 7 秒起恢复。
        var job = new CalcJob { Kind = CalcJobKind.Accumulator, Mode = AccumulatorMode.Flow, Factor = 1, BadQuality = policy, MaxGapSec = 10 };
        var state = new JobRuntimeState();
        for (var i = 0; i <= 3; i++) CalcJobMath.Accumulate(job, state, new JobSample(100, true, T0 + i * 1000), T0 + i * 1000);
        CalcJobMath.Accumulate(job, state, new JobSample(0, false, T0 + 4000), T0 + 4000);
        if (policy == BadQualityPolicy.Zero)
        {
            CalcJobMath.Accumulate(job, state, new JobSample(100, true, T0 + 5000), T0 + 5000);
            Assert.Equal(expected, state.Get("total"), 6);
            return;
        }
        CalcJobMath.Accumulate(job, state, new JobSample(0, false, T0 + 5000), T0 + 5000);
        for (var i = 6; i <= 9; i++) CalcJobMath.Accumulate(job, state, new JobSample(100, true, T0 + i * 1000), T0 + i * 1000);
        Assert.Equal(policy == BadQualityPolicy.Hold ? 900 : 300 + 300, state.Get("total"), 6);
    }

    [Fact]
    public void FlowIntegrationDoesNotBridgeGapsLongerThanMaxGap()
    {
        var job = new CalcJob { Mode = AccumulatorMode.Flow, Factor = 1.0 / 3600, MaxGapSec = 5 };
        var state = new JobRuntimeState();
        CalcJobMath.Accumulate(job, state, new JobSample(3600, true, T0), T0);
        CalcJobMath.Accumulate(job, state, new JobSample(3600, true, T0 + 2000), T0 + 2000);
        Assert.Equal(2, state.Get("total"), 6);
        CalcJobMath.Accumulate(job, state, new JobSample(3600, true, T0 + 60_000), T0 + 60_000); // 间隔 58 秒 > 5 秒，不积分
        Assert.Equal(2, state.Get("total"), 6);
    }

    [Fact]
    public void RunHoursCountsRuntimeStartsAndMaintenanceDue()
    {
        var job = new CalcJob { Kind = CalcJobKind.RunHours, RunThreshold = 0.5, MaxGapSec = 3600, MaintenanceHours = 2 };
        var state = new JobRuntimeState();
        void At(double hour, double value) => CalcJobMath.RunHours(job, state, new JobSample(value, true, T0 + (long)(hour * 3_600_000)), T0 + (long)(hour * 3_600_000));
        At(0, 0); At(0.5, 1); At(1.5, 1); At(1.6, 0); At(2, 1); At(3, 1); At(3.2, 0);
        Assert.Equal(2, state.Get("starts"));
        Assert.Equal(1.1 + 1.2, state.Get("hours"), 6);
        Assert.Equal(1, state.Get("maintenanceDue"));
        state.MaintenanceBase = state.Get("hours");
        At(3.5, 0);
        Assert.Equal(0, state.Get("maintenanceDue"));
    }

    [Fact]
    public void EnergyIsSplitIntoTimeOfUseBands()
    {
        var tou = CalcJobMath.ParseTou("""[{"band":"peak","start":"08:00","end":"12:00"},{"band":"valley","start":"23:00","end":"07:00"}]""");
        var job = new CalcJob { Kind = CalcJobKind.Energy, Factor = 1.0 / 3600, MaxGapSec = 120 }; // kW × s / 3600 = kWh
        var state = new JobRuntimeState();
        // 07:59:00 起每分钟 60 kW 采样到 08:02，再在 23:30 采两次。
        for (var m = 0; m <= 3; m++) { var ts = T0 + (7 * 60 + 59 + m) * 60_000L; CalcJobMath.Energy(job, state, new(60, true, ts), ts, tou, Utc); }
        Assert.Equal(1, state.Get("period_flat"), 6);   // 07:59-08:00
        Assert.Equal(2, state.Get("period_peak"), 6);   // 08:00-08:02
        var night = T0 + (23 * 60 + 30) * 60_000L;
        CalcJobMath.Energy(job, state, new(60, true, night), night, tou, Utc);
        CalcJobMath.Energy(job, state, new(60, true, night + 60_000), night + 60_000, tou, Utc);
        Assert.Equal(1, state.Get("period_valley"), 6);
        Assert.Equal(4, state.Get("total"), 6);
        Assert.Throws<TagExpressionException>(() => CalcJobMath.ParseTou("""[{"band":"cheap","start":"01:00","end":"02:00"}]"""));
    }

    [Fact]
    public void PeriodRollKeepsSnapshotForDayShiftAndMonth()
    {
        var state = new JobRuntimeState();
        var day = new CalcJob { ResetPeriod = CalcResetPeriod.Day };
        Assert.Null(CalcJobMath.Roll(day, state, T0 + 3_600_000, Utc));
        state.Add("total", 10); state.Add("period", 10); state.Add("period_peak", 3);
        Assert.Null(CalcJobMath.Roll(day, state, T0 + 20 * 3_600_000, Utc));
        var snapshot = CalcJobMath.Roll(day, state, T0 + 25 * 3_600_000, Utc);
        Assert.NotNull(snapshot);
        Assert.Equal("2026-01-01", snapshot!.PeriodKey); Assert.Equal(10, snapshot.Values["period"]); Assert.Equal(T0, snapshot.PeriodStartTs);
        Assert.Equal(0, state.Get("period")); Assert.Equal(0, state.Get("period_peak")); Assert.Equal(10, state.Get("total"));

        var shift = new CalcJob { ResetPeriod = CalcResetPeriod.Shift, ShiftStarts = "08:00,20:00" };
        Assert.Equal("2025-12-31 20:00", CalcJobMath.Period(shift, T0 + 3_600_000, Utc).Key);       // 01:00 属于前一天夜班
        Assert.Equal("2026-01-01 08:00", CalcJobMath.Period(shift, T0 + 9 * 3_600_000, Utc).Key);
        Assert.Equal("2026-02", CalcJobMath.Period(new CalcJob { ResetPeriod = CalcResetPeriod.Month }, T0 + 40L * 86_400_000, Utc).Key);
    }

    [Fact]
    public void ScriptIsWhitelistedAndBounded()
    {
        var reads = new HashSet<int> { 1, 2 }; var writes = new HashSet<int> { 9 };
        var script = CalcScript.Compile("""
            // 注释
            $sum = #1 + #2; $avg = $sum / 2
            @avg = $avg; @night = if($hour >= 22 || $hour < 6, 1, 0)
            #9 = clamp($avg, 0, 100)
            """, reads, writes);
        Assert.Equal([1, 2], script.Reads); Assert.Equal([9], script.Writes); Assert.Equal(["avg", "night"], script.Outputs);
        var result = script.Execute(id => id == 1 ? 40 : 160, new Dictionary<string, double> { ["hour"] = 23, ["dt"] = 1, ["minute"] = 0, ["weekday"] = 1, ["day"] = 1, ["month"] = 1 });
        Assert.Equal(100, result.Outputs["avg"]); Assert.Equal(1, result.Outputs["night"]); Assert.Equal(100, result.Writes[9]);

        Assert.Contains("可读白名单", Assert.Throws<TagExpressionException>(() => CalcScript.Compile("@x = #3", reads, writes)).Message);
        Assert.Contains("可写白名单", Assert.Throws<TagExpressionException>(() => CalcScript.Compile("#8 = 1", reads, writes)).Message);
        Assert.Contains("未先赋值", Assert.Throws<TagExpressionException>(() => CalcScript.Compile("@x = $y + 1", reads, writes)).Message);
        Assert.Throws<TagExpressionException>(() => CalcScript.Compile("@x = File(1)", reads, writes));           // 无任意函数/IO
        Assert.Throws<TagExpressionException>(() => CalcScript.Compile("@x = \"/etc/passwd\"", reads, writes)); // 无字符串
        Assert.Throws<TagExpressionException>(() => CalcScript.Compile("$hour = 1", reads, writes));
        Assert.Throws<TagExpressionException>(() => CalcScript.Compile(string.Join(";", Enumerable.Range(0, 201).Select(i => $"$v{i} = 1")), reads, writes));

        var heavy = CalcScript.Compile(string.Join("\n", Enumerable.Range(0, 50).Select(i => $"$v{i} = " + string.Join(" + ", Enumerable.Repeat("#1", 30)))), reads, writes, new ScriptLimits(MaxSteps: 2000));
        Assert.Contains("步数", Assert.Throws<TagExpressionException>(() => heavy.Execute(_ => 1, new Dictionary<string, double>())).Message);
        var slow = CalcScript.Compile(string.Join("\n", Enumerable.Range(0, 80).Select(i => $"$v{i} = " + string.Join(" + ", Enumerable.Repeat("#1", 30)))), reads, writes, new ScriptLimits(TimeoutMs: 1));
        Assert.Contains("超时", Assert.Throws<TagExpressionException>(() => slow.Execute(_ => { Thread.SpinWait(2000); return 1; }, new Dictionary<string, double>())).Message);
        // 普通计算测点仍不允许变量
        Assert.Throws<TagExpressionException>(() => TagExpression.Compile("$x + 1"));
    }

    [Fact]
    public async Task EngineContinuesAfterRestartSnapshotsOnResetAndWritesOnlyOnChange()
    {
        var path = Path.Combine(Path.GetTempPath(), $"calcjob-{Guid.NewGuid():N}.db");
        var factory = new FileFactory(path);
        try
        {
            await using (var db = factory.CreateDbContext())
            {
                await db.Database.EnsureCreatedAsync();
                db.Devices.Add(new Device { Id = 7, Name = "plc", AreaId = Area.DefaultId });
                db.Tags.AddRange(new Tag { Id = 1, DeviceId = 7, Name = "flow" }, new Tag { Id = 2, DeviceId = 7, Name = "sp", Writable = true });
                await db.SaveChangesAsync();
            }
            var cache = new RealtimeCache(); using var bus = new RealtimeBus(); var writer = new FakeWriter();
            var config = new CalcJobConfiguration(factory, Engine(factory, cache, bus, writer));
            var flow = await config.SaveAsync(null, new CalcJobRequest("流量累计", CalcJobKind.Accumulator, Mode: AccumulatorMode.Flow, InputTagId: 1, Factor: 1, MaxGapSec: 5, ResetPeriod: CalcResetPeriod.Day, Unit: "m³"), 1, "admin", default);
            var script = await config.SaveAsync(null, new CalcJobRequest("设定跟随", CalcJobKind.Script, IntervalMs: 1000, Script: "@half = #1 / 2; #2 = round(#1 / 10)", ReadTags: "1", WriteTags: "2", MaxGapSec: 60), 1, "admin", default);
            Assert.Equal(1, script.RunAsUserId);

            var engine = Engine(factory, cache, bus, writer);
            var now = T0 + 10 * 3_600_000L;
            for (var i = 0; i < 5; i++, now += 1000) { cache.Set(new TagValue(1, 10, TagQuality.Good, now)); await engine.TickAsync(now); }
            await engine.FlushAsync();
            var outputs = (await config.ListAsync(default)).Single(x => x.Job.Id == flow.Id).Outputs.ToDictionary(x => x.Name, x => x.TagId);
            Assert.Equal(40, cache.Get(outputs["total"])!.Value, 6);
            Assert.Equal(5, cache.Get((await config.ListAsync(default)).Single(x => x.Job.Id == script.Id).Outputs.Single().TagId)!.Value);
            Assert.Single(writer.Writes);                       // 值不变只写一次

            // 模拟断电重启：新引擎实例从落盘状态继续；停机期间（60 秒）不积分。
            var restarted = Engine(factory, cache, bus, writer);
            now += 60_000;
            for (var i = 0; i < 3; i++, now += 1000) { cache.Set(new TagValue(1, 20, TagQuality.Good, now)); await restarted.TickAsync(now); }
            Assert.Equal(40 + 40, cache.Get(outputs["total"])!.Value, 6);
            Assert.Equal(2, writer.Writes.Count); Assert.Equal(2, writer.Writes[^1].Value);

            // 跨日：留快照并清零周期量。
            now = T0 + 24 * 3_600_000L + 1000;
            cache.Set(new TagValue(1, 20, TagQuality.Good, now)); await restarted.TickAsync(now);
            var snapshots = await config.SnapshotsAsync(flow.Id, 10, default);
            var snap = Assert.Single(snapshots);
            Assert.Equal("2026-01-01", snap.PeriodKey); Assert.Contains("\"period\":80", snap.ValuesJson);
            Assert.Equal(0, cache.Get(outputs["period"])!.Value);

            // 写入被拒（如权限被收回）不计为已写，记录事件，下次再试。
            writer.Deny = "执行身份无该测点区域的控制权限";
            now += 1000; cache.Set(new TagValue(1, 50, TagQuality.Good, now)); await restarted.TickAsync(now);
            var status = (await restarted.StatusAsync()).Single(x => x.JobId == script.Id);
            Assert.Contains("控制权限", status.LastEvent);

            // 输入坏质量：脚本整次失败，输出变 Bad，不写现场。
            var count = writer.Writes.Count;
            now += 1000; cache.Set(new TagValue(1, 70, TagQuality.Bad, now)); await restarted.TickAsync(now);
            Assert.Equal(count, writer.Writes.Count);
            Assert.Equal(TagQuality.Bad, cache.Get((await config.ListAsync(default)).Single(x => x.Job.Id == script.Id).Outputs.Single().TagId)!.Quality);

            await using var check = factory.CreateDbContext();
            Assert.Equal(2, await check.OperationLogs.CountAsync(x => x.TargetType == "CalcJob"));
            Assert.True(await check.Tags.AnyAsync(x => x.Kind == TagKind.Derived && x.Name == "流量累计.total" && x.Unit == "m³"));
        }
        finally { SqliteConnection.ClearAllPools(); try { File.Delete(path); } catch { } }
    }

    [Fact]
    public async Task ConfigurationRejectsInvalidJobsAndProtectsReferencedOutputs()
    {
        var path = Path.Combine(Path.GetTempPath(), $"calcjob-{Guid.NewGuid():N}.db");
        var factory = new FileFactory(path);
        try
        {
            await using (var db = factory.CreateDbContext())
            {
                await db.Database.EnsureCreatedAsync();
                db.Devices.Add(new Device { Id = 7, Name = "plc", AreaId = Area.DefaultId });
                db.Tags.AddRange(new Tag { Id = 1, DeviceId = 7, Name = "run" }, new Tag { Id = 2, DeviceId = 7, Name = "ro", Writable = false });
                await db.SaveChangesAsync();
            }
            var config = new CalcJobConfiguration(factory, Engine(factory, new RealtimeCache(), new RealtimeBus(), new FakeWriter()));
            async Task<string> Bad(CalcJobRequest request) => (await Assert.ThrowsAsync<CalculationRequestException>(() => config.SaveAsync(null, request, 1, "admin", default))).Message;
            Assert.Contains("输入测点", await Bad(new CalcJobRequest("a", CalcJobKind.RunHours)));
            Assert.Contains("区域", await Bad(new CalcJobRequest("a", CalcJobKind.RunHours, AreaId: 99, InputTagId: 1)));
            Assert.Contains("可写", await Bad(new CalcJobRequest("a", CalcJobKind.Script, Script: "#2 = 1", ReadTags: "", WriteTags: "2")));
            Assert.Contains("HH:mm", await Bad(new CalcJobRequest("a", CalcJobKind.RunHours, InputTagId: 1, ResetPeriod: CalcResetPeriod.Shift, ShiftStarts: "25:00")));
            Assert.Contains("1000", await Bad(new CalcJobRequest("a", CalcJobKind.Script, IntervalMs: 200, Script: "@x = 1")));

            var job = await config.SaveAsync(null, new CalcJobRequest("泵1", CalcJobKind.RunHours, InputTagId: 1, MaintenanceHours: 500), 1, "admin", default);
            var hoursTag = (await config.ListAsync(default)).Single().Outputs.Single(x => x.Name == "hours").TagId;
            await using (var db = factory.CreateDbContext())
            {
                db.Tags.Add(new Tag { Id = 50, Kind = TagKind.Calculated, Name = "cost", Expression = $"#{hoursTag} * 2" });
                db.TagDependencies.Add(new TagDependency { TagId = 50, InputTagId = hoursTag });
                await db.SaveChangesAsync();
            }
            Assert.Equal(409, (await Assert.ThrowsAsync<CalculationRequestException>(() => config.DeleteAsync(job.Id, job.Revision, 1, "admin", default))).Status);
            Assert.Equal(409, (await Assert.ThrowsAsync<CalculationRequestException>(() => config.SaveAsync(job.Id, new CalcJobRequest("泵1", CalcJobKind.RunHours, InputTagId: 1, Revision: 99), 1, "admin", default))).Status);
            var renamed = await config.SaveAsync(job.Id, new CalcJobRequest("泵1号", CalcJobKind.RunHours, InputTagId: 1, Revision: job.Revision), 1, "admin", default);
            Assert.Equal(2, renamed.Revision);
            await using var check = factory.CreateDbContext();
            Assert.Equal("泵1号.hours", (await check.Tags.SingleAsync(x => x.Id == hoursTag)).Name);
            var audit = await check.OperationLogs.Where(x => x.TargetType == "CalcJob").OrderBy(x => x.Id).LastAsync();
            Assert.Contains("泵1号", audit.Details);
        }
        finally { SqliteConnection.ClearAllPools(); try { File.Delete(path); } catch { } }
    }

    private static CalcJobEngine Engine(IDbContextFactory<ConfigDbContext> factory, IRealtimeCache cache, IRealtimeBus bus, ICalcTagWriter writer) =>
        new(factory, new Snapshot(), cache, bus, writer, Options.Create(new ScadaSettings { ShiftTimeZone = "UTC", CalcStatePersistSec = 3600 }), NullLogger<CalcJobEngine>.Instance);

    private sealed class FakeWriter : ICalcTagWriter
    {
        public string? Deny { get; set; }
        public List<(int TagId, double Value)> Writes { get; } = new();
        public Task<string?> WriteAsync(CalcJob job, int tagId, double value, CancellationToken ct)
        {
            if (Deny is not null) return Task.FromResult<string?>(Deny);
            Writes.Add((tagId, value)); return Task.FromResult<string?>(null);
        }
    }

    private sealed class FileFactory(string path) : IDbContextFactory<ConfigDbContext>
    {
        public ConfigDbContext CreateDbContext() => new(new DbContextOptionsBuilder<ConfigDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options);
    }

    private sealed class Snapshot : IConfigSnapshot
    {
        public IReadOnlyList<Device> Devices => [];
        public IReadOnlyList<Tag> Tags => [];
        public IReadOnlyList<AlarmRule> AlarmRules => [];
        public int Version => 1; public long LoadedAt => 0;
        public Task ReloadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public bool TryGetTag(int id, out Tag tag) { tag = null!; return false; }
        public bool TryGetDevice(int id, out Device device) { device = null!; return false; }
    }
}
