using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Scada.Core.Calculations;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Interfaces;
using Scada.Core.Models;
using Scada.Data;
using Scada.Engine.Calculations;
using Scada.Engine.Realtime;

namespace Scada.Engine.Tests;

public class CalculationTests
{
    [Theory]
    [InlineData("2+3*4", 14)]
    [InlineData("-2^2", -4)]
    [InlineData("2^3^2", 512)]
    [InlineData("2^-2", .25)]
    [InlineData("10-3-2", 5)]
    [InlineData("if(false,1/0,round(2.345,2))", 2.35)]
    [InlineData("false && sqrt(-1) || true", 1)]
    [InlineData("!false + (3>=2) + (1!=2)", 3)]
    [InlineData("max(1,min(2,3),clamp(8,0,5))", 5)]
    [InlineData("1e2 + .5 + 5%2", 101.5)]
    public void NumericGrammarAndShortCircuitHaveDefinedSemantics(string expression, double expected) =>
        Assert.Equal(expected, TagExpression.Compile(expression).Evaluate(_ => throw new Exception("No input expected")), 8);

    [Theory]
    [InlineData("number")][InlineData("reference")][InlineData("end")][InlineData("#0")][InlineData("#2147483648")]
    [InlineData("1..2")][InlineData("1e")][InlineData("1e999")][InlineData("abs()")][InlineData("if(1,2)")]
    [InlineData("System.IO.File.ReadAllText('x')")][InlineData("while(1)")][InlineData("1;2")][InlineData("1=1")]
    public void InvalidOrExecutableSyntaxIsRejected(string expression) => Assert.Throws<TagExpressionException>(() => TagExpression.Compile(expression));

    [Theory]
    [InlineData("1/0")][InlineData("1%0")][InlineData("sqrt(-1)")][InlineData("exp(999)")]
    [InlineData("round(1,1.5)")][InlineData("clamp(1,2,0)")]
    public void ArithmeticFailuresCannotBecomeGoodFiniteValues(string expression) =>
        Assert.Throws<TagExpressionException>(() => TagExpression.Compile(expression).Evaluate(_ => 0));

    [Fact]
    public void ParserBoundsDependenciesDepthAndSize()
    {
        var expression = TagExpression.Compile("#12+#2+#12"); Assert.Equal(new[] { 2, 12 }, expression.Dependencies);
        Assert.Throws<TagExpressionException>(() => expression.Evaluate(_ => double.NaN));
        Assert.Throws<TagExpressionException>(() => TagExpression.Compile(new string('(', 70) + "1" + new string(')', 70)));
        Assert.Throws<TagExpressionException>(() => TagExpression.Compile(new string(' ', 4097)));
        static string Balanced(string[] terms) => terms.Length == 1 ? terms[0] : $"({Balanced(terms[..(terms.Length/2)])}+{Balanced(terms[(terms.Length/2)..])})";
        Assert.Contains("64 个测点", Assert.Throws<TagExpressionException>(() => TagExpression.Compile(Balanced(Enumerable.Range(1, 65).Select(x => $"#{x}").ToArray()))).Message);
        Assert.Contains("512 个节点", Assert.Throws<TagExpressionException>(() => TagExpression.Compile(Balanced(Enumerable.Repeat("1", 300).ToArray()))).Message);
        Assert.Throws<TagExpressionException>(() => TagExpression.Compile("max(" + string.Join(",", Enumerable.Repeat("1", 17)) + ")"));
    }

    [Fact]
    public void GraphSortsChainsAndRejectsCyclesMissingInputsAndBothDepthOrders()
    {
        var source = new Tag { Id = 1, DeviceId = 1, Name = "input" };
        var order = CalculationGraph.Compile([Calc(3, "#2+1"), source, Calc(2, "#1+1")]);
        Assert.Equal(new[] { 2, 3 }, order.Select(x => x.Tag.Id));
        Assert.Throws<TagExpressionException>(() => CalculationGraph.Compile([Calc(1, "#1")]));
        Assert.Throws<TagExpressionException>(() => CalculationGraph.Compile([Calc(1, "#2"), Calc(2, "#1")]));
        Assert.Throws<TagExpressionException>(() => CalculationGraph.Compile([Calc(1, "#99")]));
        var chain = Enumerable.Range(1, 65).Select(x => Calc(x, x == 1 ? "1" : $"#{x-1}")).ToArray();
        Assert.Throws<TagExpressionException>(() => CalculationGraph.Compile(chain));
        Assert.Throws<TagExpressionException>(() => CalculationGraph.Compile(chain.Reverse().ToArray()));
    }

    [Fact]
    public void InputQualityDisabledDeviceAgeRevisionAndEpochInvalidateOutputAndKeepLastValue()
    {
        var physical = new Tag { Id = 1, DeviceId = 7, Name = "input" }; var tag = Calc(2, "#1+1"); tag.InputMaxAgeMs = 1000;
        var source = new TagValue(1, 10, TagQuality.Good, 1900, 1800);
        var device = new Device { Id = 7, Name = "plc", Enabled = true };
        var tags = new Dictionary<int, Tag> { [1] = physical, [2] = tag }; var devices = new Dictionary<int, Device> { [7] = device };
        TagValue Evaluate(TagValue? input, long epoch = 1000) => CalculationEvaluator.Evaluate(tag, TagExpression.Compile(tag.Expression), tags, devices, _ => input, 2000, epoch, new(2, 42, TagQuality.Good, 1500));
        Assert.Equal(11, Evaluate(source).Value); Assert.Equal(1800, Evaluate(source).SourceTs);
        foreach (var input in new TagValue?[] { null, source with { Quality = TagQuality.Bad }, source with { Ts = 900 }, source with { Ts = 3001 }, source with { Value = double.PositiveInfinity } })
        { var value = Evaluate(input); Assert.Equal(TagQuality.Uncertain, value.Quality); Assert.Equal(42, value.Value); Assert.NotNull(value.Error); }
        Assert.Equal(TagQuality.Uncertain, Evaluate(source, 1900).Quality);
        device.Enabled = false; Assert.Equal(TagQuality.Uncertain, Evaluate(source).Quality); device.Enabled = true;
        physical.Kind = TagKind.Calculated; physical.DeviceId = null;
        Assert.Equal(TagQuality.Uncertain, Evaluate(source).Quality);
        Assert.Equal(TagQuality.Good, Evaluate(source with { DefinitionRevision = physical.CalculationRevision }).Quality);
        tag.Expression = "1/0"; Assert.Equal(TagQuality.Bad, Evaluate(null).Quality);
        tag.CalculationEnabled = false; Assert.Equal(TagQuality.Uncertain, Evaluate(null).Quality);
    }

    [Fact]
    public async Task RuntimePublishesUnchangedScansInDependencyOrderAndInvalidatesConfigurationEpoch()
    {
        var snapshot = new Snapshot { Tags = [Calc(3, "#2+1"), Calc(2, "#1+1"), new() { Id = 1, DeviceId = 7, Name = "input" }] };
        var cache = new RealtimeCache(snapshot); using var bus = new RealtimeBus(); var reader = bus.Subscribe();
        using var engine = new CalculatedTagEngine(snapshot, cache, bus, NullLogger<CalculatedTagEngine>.Instance);
        cache.Set(new(1, 10, TagQuality.Good, 1100)); await engine.TickAsync(1200);
        Assert.Equal(12, cache.Get(3)!.Value); Assert.Equal(2, (await reader.ReadAsync()).TagId); Assert.Equal(3, (await reader.ReadAsync()).TagId);
        await engine.TickAsync(1250); Assert.False(reader.TryRead(out _));
        await engine.TickAsync(2200); Assert.Equal(2200, (await reader.ReadAsync()).Ts); Assert.Equal(2200, (await reader.ReadAsync()).Ts);
        snapshot.Version++; snapshot.LoadedAt = 2300;
        Assert.Equal(TagQuality.Uncertain, cache.Get(3)!.Quality);
        await engine.TickAsync(2400); Assert.Equal(TagQuality.Uncertain, cache.Get(2)!.Quality); Assert.Equal(TagQuality.Uncertain, cache.Get(3)!.Quality);
        cache.Set(new(1, 20, TagQuality.Good, 3300)); await engine.TickAsync(3400); Assert.Equal(22, cache.Get(3)!.Value);
        snapshot.Tags = [snapshot.Tags.Last()]; snapshot.Version++; snapshot.LoadedAt = 3500; await engine.TickAsync(3600);
        Assert.Null(cache.Get(2)); Assert.Null(cache.Get(3));
    }

    [Fact]
    public async Task OneArithmeticFailureDoesNotStopOtherCalculationsAndDisabledRevisionCannotRemainGood()
    {
        var broken = Calc(2, "1/0"); var good = Calc(3, "7"); var snapshot = new Snapshot { Tags = [broken, good] };
        var cache = new RealtimeCache(snapshot); using var bus = new RealtimeBus();
        using var engine = new CalculatedTagEngine(snapshot, cache, bus, NullLogger<CalculatedTagEngine>.Instance);
        await engine.TickAsync(1200); Assert.Equal(TagQuality.Bad, cache.Get(2)!.Quality); Assert.Equal(7, cache.Get(3)!.Value);
        good.CalculationRevision++; Assert.Equal(TagQuality.Uncertain, cache.Get(3)!.Quality);
        snapshot.Version++; await engine.TickAsync(1300); Assert.Equal(TagQuality.Good, cache.Get(3)!.Quality);
        good.CalculationEnabled = false; Assert.Equal(TagQuality.Uncertain, cache.Get(3)!.Quality);
        snapshot.Version++; await engine.TickAsync(1400); Assert.Equal(7, cache.Get(3)!.Value); Assert.Equal(TagQuality.Uncertain, cache.Get(3)!.Quality);
    }

    [Fact]
    public async Task DefinitionEditsAreAtomicVersionedAuditedAndReferencedInputsCannotBeDeleted()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync(); var factory = new Factory(connection);
        await using (var db = factory.CreateDbContext()) { await db.Database.MigrateAsync(); db.Devices.Add(new() { Id = 7, Name = "plc" }); await db.SaveChangesAsync(); db.Tags.Add(new() { Id = 1, DeviceId = 7, Name = "input" }); await db.SaveChangesAsync(); }
        var snapshot = new Snapshot(); var service = new CalculatedTagConfiguration(factory, new RealtimeCache(), snapshot);
        var first = await service.SaveAsync(null, new("first", "#1+1"), 42, "admin", default);
        var second = await service.SaveAsync(null, new("second", $"#{first.Id}+1"), 42, "admin", default);
        var conflict = await Assert.ThrowsAsync<CalculationRequestException>(() => service.SaveAsync(first.Id, new("first", "1", Revision: 0), 42, "admin", default)); Assert.Equal(409, conflict.Status);
        await Assert.ThrowsAsync<TagExpressionException>(() => service.SaveAsync(first.Id, new("first", $"#{second.Id}", Revision: 1), 42, "admin", default));
        await Assert.ThrowsAsync<TagExpressionException>(() => service.SaveAsync(null, new("invalid", "#999999"), 42, "admin", default));
        await using (var db = factory.CreateDbContext())
        {
            Assert.Equal(3, await db.Tags.CountAsync()); Assert.Equal("#1+1", (await db.Tags.FindAsync(first.Id))!.Expression);
            Assert.Equal(2, await db.TagDependencies.CountAsync()); Assert.Equal(2, await db.OperationLogs.CountAsync());
            Assert.Equal(42, (await db.OperationLogs.FirstAsync()).UserId);
            await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlRawAsync("DELETE FROM Tag WHERE Id=1"));
        }
        Assert.Equal(409, (await Assert.ThrowsAsync<CalculationRequestException>(() => service.DeleteAsync(first.Id, 1, 42, "admin", default))).Status);
        var changed = await service.SaveAsync(second.Id, new("second", "2", Revision: 1), 42, "admin", default); Assert.Equal(2, changed.CalculationRevision);
        await using (var db = factory.CreateDbContext())
        {
            db.AlarmEvents.Add(new() { TagId = first.Id, StartTs = 1000, AckUser = "operator", AckTs = 1100 }); await db.SaveChangesAsync();
        }
        await service.DeleteAsync(first.Id, 1, 42, "admin", default);
        await using var final = factory.CreateDbContext(); Assert.Empty(await final.TagDependencies.ToListAsync()); Assert.Equal(4, await final.OperationLogs.CountAsync());
        var closed = await final.AlarmEvents.SingleAsync(); Assert.NotNull(closed.EndTs); Assert.Equal("删除计算测点", closed.EndReason); Assert.Equal("operator", closed.AckUser);
    }

    [Fact]
    public async Task MigrationPreservesPhysicalReferencesAndRefusesDestructiveDowngrade()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync(); var factory = new Factory(connection);
        await using var db = factory.CreateDbContext(); var migrator = db.GetService<IMigrator>();
        // Discover the immediately preceding migration so the preservation test stays tied to the checked-in chain.
        var migrations = db.Database.GetMigrations().ToArray(); var target = migrations[Array.FindIndex(migrations, x => x.EndsWith("_AddCalculatedTags")) - 1];
        await migrator.MigrateAsync(target);
        // 旧库没有区域列，直接用 SQL 写入设备，避免当前实体模型的新列。
        await db.Database.ExecuteSqlRawAsync("INSERT INTO Device (Id, Name, Protocol, Host, Port, StationNumber, ByteOrder, PollIntervalMs, TimeoutMs, Enabled, DisabledSinceTs, Parity, Rack, OpcSecurityPolicy) VALUES (7, 'legacy', 0, '127.0.0.1', 502, 1, 0, 1000, 3000, 0, 0, 0, 0, 0)");
        await db.Database.ExecuteSqlRawAsync("INSERT INTO Tag (Id,DeviceId,Name,Area,Address,DataType,Scale,Offset,Writable,ArchiveMode,Deadband,ArchivePeriodSec,DbNumber,BitOffset,NodeId) VALUES (11,7,'legacy',3,0,5,0.1,10,0,1,0,600,1,0,'')");
        db.AlarmRules.Add(new() { Id = 5, TagId = 11, Type = AlarmRuleType.High, Threshold = 90, Enabled = true });
        db.AlarmEvents.Add(new() { TagId = 11, DeviceId = 7, RuleId = 5, StartTs = 10, AckUser = "operator", AckTs = 11 });
        db.ProcessScreens.Add(new() { Name = "legacy screen", DraftJson = "{\"tagId\":11}", PublishedJson = "{\"tagId\":11}", PublishedRevision = 1 }); await db.SaveChangesAsync();
        await db.Database.MigrateAsync(); var physical = await db.Tags.AsNoTracking().SingleAsync();
        Assert.Equal(11, physical.Id); Assert.Equal(7, physical.DeviceId); Assert.Equal(TagKind.Physical, physical.Kind); Assert.Equal(.1, physical.Scale);
        Assert.Equal(11, (await db.AlarmRules.AsNoTracking().SingleAsync()).TagId); Assert.Equal("operator", (await db.AlarmEvents.AsNoTracking().SingleAsync()).AckUser);
        Assert.Equal("{\"tagId\":11}", (await db.ProcessScreens.AsNoTracking().SingleAsync()).PublishedJson);
        db.Tags.Add(Calc(12, "#11")); await db.SaveChangesAsync(); db.TagDependencies.Add(new() { TagId = 12, InputTagId = 11 }); await db.SaveChangesAsync();
        await Assert.ThrowsAsync<SqliteException>(() => migrator.MigrateAsync(target));
        Assert.Equal(2, await db.Tags.CountAsync()); Assert.Single(await db.TagDependencies.ToListAsync());
        await db.Database.ExecuteSqlRawAsync("DELETE FROM Tag WHERE Id=12"); await migrator.MigrateAsync(target); await db.Database.MigrateAsync();
        Assert.Equal(11, (await db.Tags.AsNoTracking().SingleAsync()).Id);
    }

    private static Tag Calc(int id, string expression) => new() { Id = id, Kind = TagKind.Calculated, Name = "calc" + id, Expression = expression, DataType = TagDataType.Double };
    private sealed class Factory(SqliteConnection connection) : IDbContextFactory<ConfigDbContext>
    { public ConfigDbContext CreateDbContext() => new(new DbContextOptionsBuilder<ConfigDbContext>().UseSqlite(connection).Options); }
    private sealed class Snapshot : IConfigSnapshot
    {
        public IReadOnlyList<Device> Devices { get; set; } = [new() { Id = 7, Name = "plc", Enabled = true }];
        public IReadOnlyList<Tag> Tags { get; set; } = [];
        public IReadOnlyList<AlarmRule> AlarmRules => [];
        public int Version { get; set; } = 1; public long LoadedAt { get; set; } = 1000;
        public Task ReloadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public bool TryGetTag(int id, out Tag tag) { tag = Tags.FirstOrDefault(x => x.Id == id)!; return tag != null; }
        public bool TryGetDevice(int id, out Device device) { device = Devices.FirstOrDefault(x => x.Id == id)!; return device != null; }
    }
}
