using Microsoft.Extensions.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Scada.Core.Calculations;
using Scada.Core.Interfaces;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Models;
using Scada.Core.Scripting;
using Scada.Engine.Calculations;
using Scada.Engine.Realtime;

namespace Scada.Engine.Tests;

public sealed class CSharpScriptTests
{
    private static CSharpScriptHost Host(int timeoutMs = 1000, bool enabled = true)
    {
        var worker = Path.Combine(AppContext.BaseDirectory, "ScriptWorker", "Scada.ScriptWorker.dll");
        return new(Options.Create(new CSharpScriptOptions { Enabled = enabled, WorkerPath = worker, TimeoutMs = timeoutMs, WorkerCount = 1 }));
    }

    [Fact]
    public async Task CompilesRunsAndReusesWorkerWithBoundedResults()
    {
        using var host = Host();
        const string source = "context.Output(\"power\", context.Read(1) * context.Read(2)); context.Write(9, 1); context.Log(\"calculated\");";
        var compiled = await host.CompileAsync(source);
        Assert.True(compiled.Success, string.Join(";", compiled.Diagnostics)); Assert.Equal(["power"], compiled.OutputNames);
        var request = new CSharpScriptRequest("", "run", source, new() { [1] = new(10, true, 0), [2] = new(220, true, 0) }, [9]);
        for (var index = 0; index < 2; index++)
        {
            var result = await host.RunAsync(request);
            Assert.True(result.Success, result.Error); Assert.Equal(2200, result.Outputs["power"]); Assert.Equal(1, result.Writes[9]); Assert.Equal(["calculated"], result.Logs!);
        }
    }

    [Fact]
    public async Task CompileDiagnosticCoversTheIdentifier()
    {
        using var host = Host();
        const string source = "context.Output(\"answer\", missing);";
        var compiled = await host.CompileAsync(source);
        Assert.False(compiled.Success);
        var span = Assert.Single(compiled.Spans!, item => item.File == "script.cs" && item.Message.Contains("missing", StringComparison.Ordinal));
        Assert.Equal(source.IndexOf("missing", StringComparison.Ordinal) + 1, span.StartColumn);
        Assert.Equal(span.StartColumn + "missing".Length, span.EndColumn);
    }

    [Fact]
    public async Task LibraryDiagnosticStaysInTheLibrary()
    {
        using var host = Host();
        var compiled = await host.CompileAsync("await Task.CompletedTask;", "double Missing() => unknown;");
        Assert.False(compiled.Success);
        var span = Assert.Single(compiled.Spans!, item => item.File == "library.cs" && item.Message.Contains("unknown", StringComparison.Ordinal));
        Assert.True(span.EndColumn > span.StartColumn);
    }

    [Fact]
    public async Task PreviewKeepsOverridesAndTheScreenArgument()
    {
        var path = Path.Combine(Path.GetTempPath(), "csharp-preview-" + Guid.NewGuid().ToString("N") + ".db");
        var factory = new CalcJobTests.FileFactory(path);
        await using var runtimeConnection = TestRuntime.Open(); var runtime = TestRuntime.Factory(runtimeConnection);
        try
        {
            await using (var db = factory.CreateDbContext())
            {
                await db.Database.EnsureCreatedAsync();
                db.Devices.Add(new Device { Id = 7, Name = "plc", AreaId = Area.DefaultId });
                db.Tags.Add(new Tag { Id = 1, DeviceId = 7, Name = "input" });
                await db.SaveChangesAsync();
            }
            var runner = new BlockingRunner();
            runner.Completion.SetResult(new("", true, ["answer"], new() { ["answer"] = 1 }, new(), []));
            var cache = new RealtimeCache();
            using var engine = new CalcJobEngine(factory, new CalcJobTests.Snapshot(), cache, new RealtimeBus(), new UnknownWriter(), Options.Create(new Scada.Core.Options.ScadaSettings()), NullLogger<CalcJobEngine>.Instance, runtime, csharp: runner);
            cache.Set(new TagValue(1, 10, TagQuality.Good, ScadaTime.NowMs()));
            var job = new CalcJob { Kind = CalcJobKind.CSharp, Script = "source", ReadTags = "1", IntervalMs = 1000, MaxGapSec = 60 };
            var preview = await engine.PreviewCSharpAsync(job, default, 4, [new ScriptPreviewOverride(1, 3, "Good", 1_000)]);
            Assert.Equal(3, runner.Request!.Inputs![1].Value);
            Assert.True(runner.Request.Inputs[1].Good);
            Assert.Equal(4, runner.Request.Argument);
            Assert.Equal("input", preview.Inputs.Single().Name);
            Assert.Equal(3, preview.Inputs.Single().Value);
        }
        finally { SqliteConnection.ClearAllPools(); if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task TerminatesInfiniteLoopAndRecoversForNextScript()
    {
        using var host = Host(200);
        Assert.True((await host.CompileAsync("while (true) { }")).Success);
        var error = await Assert.ThrowsAsync<CSharpScriptException>(() => host.RunAsync(new("", "run", "while (true) { }")));
        Assert.Equal("timeout", error.Code);
        var next = await host.RunAsync(new("", "run", "context.Output(\"answer\", 42);"));
        Assert.True(next.Success, next.Error); Assert.Equal(42, next.Outputs["answer"]);
    }

    [Fact]
    public async Task CompilationAndRuntimeFailuresDoNotProduceWrites()
    {
        using var host = Host();
        Assert.False((await host.CompileAsync("context.Output(\"value\", );")).Success);
        Assert.False((await host.CompileAsync("var name = DateTime.Now.ToString(); context.Output(name, 1);")).Success);
        var denied = await host.RunAsync(new("", "run", "context.Write(999, 1);"));
        Assert.False(denied.Success); Assert.Empty(denied.Writes);
        var failed = await host.RunAsync(new("", "run", "context.Write(9, 1); throw new Exception(\"failed\");", WritableTags: [9]));
        Assert.False(failed.Success); Assert.Empty(failed.Writes);
        var read = await host.RunAsync(new("", "run", "context.Output(\"value\", context.Read(999));"));
        Assert.False(read.Success); Assert.Empty(read.Outputs);
    }

    [Fact]
    public async Task DisabledByDefaultAndDoesNotInheritServerSecrets()
    {
        using var disabled = Host(enabled: false);
        Assert.Equal("disabled", (await Assert.ThrowsAsync<CSharpScriptException>(() => disabled.CompileAsync("context.Output(\"value\", 1);"))).Code);
        Environment.SetEnvironmentVariable("SCADA_SCRIPT_TEST_SECRET", "must-not-be-inherited");
        try
        {
            using var host = Host();
            var result = await host.RunAsync(new("", "run", "context.Output(\"clean\", Environment.GetEnvironmentVariable(\"SCADA_SCRIPT_TEST_SECRET\") == null ? 1 : 0);"));
            Assert.True(result.Success, result.Error); Assert.Equal(1, result.Outputs["clean"]);
        }
        finally { Environment.SetEnvironmentVariable("SCADA_SCRIPT_TEST_SECRET", null); }
    }

    [Fact]
    public async Task CSharpTaskPublishesOutputsAndUnknownWritesRemainBlockedAcrossRestart()
    {
        var path = Path.Combine(Path.GetTempPath(), "csharp-task-" + Guid.NewGuid().ToString("N") + ".db");
        var factory = new CalcJobTests.FileFactory(path);
        await using var runtimeConnection = TestRuntime.Open(); var runtime = TestRuntime.Factory(runtimeConnection);
        try
        {
            await using (var db = factory.CreateDbContext())
            {
                await db.Database.EnsureCreatedAsync();
                db.Devices.Add(new Device { Id = 7, Name = "plc", AreaId = Area.DefaultId });
                db.Tags.AddRange(new Tag { Id = 1, DeviceId = 7, Name = "input" }, new Tag { Id = 2, DeviceId = 7, Name = "output", Writable = true });
                await db.SaveChangesAsync();
            }
            using var host = Host(); using var bus = new RealtimeBus();
            var cache = new RealtimeCache(); var writer = new UnknownWriter();
            CalcJobEngine Engine() => new(factory, new CalcJobTests.Snapshot(), cache, bus, writer, Options.Create(new Scada.Core.Options.ScadaSettings()), NullLogger<CalcJobEngine>.Instance, runtime, csharp: host);
            using var engine = Engine();
            var configuration = new CalcJobConfiguration(factory, runtime, engine);
            var job = await configuration.SaveAsync(null, new("C# task", CalcJobKind.CSharp, Script: "context.Output(\"doubleValue\", context.Read(1) * 2); context.Write(2, 1);", ReadTags: "1", WriteTags: "2"), 1, "admin", default);
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            cache.Set(new TagValue(1, 21, TagQuality.Good, now));
            await engine.TickAsync(now);
            await engine.WaitForScriptsAsync();
            var status = Assert.Single(await engine.StatusAsync()); Assert.NotNull(status.LastError); Assert.Equal(42, status.Values["doubleValue"]);
            await engine.TickAsync(now + 1000); await engine.WaitForScriptsAsync(); Assert.Equal(1, writer.Calls);
            using var restarted = Engine();
            await restarted.TickAsync(now + 2000); await restarted.WaitForScriptsAsync(); Assert.Equal(1, writer.Calls);
            Assert.True(await restarted.AcknowledgeWritesAsync(job.Id));
            await restarted.TickAsync(now + 3000); await restarted.WaitForScriptsAsync(); Assert.Equal(1, writer.Calls);
            Assert.Null(Assert.Single(await restarted.StatusAsync()).LastError);
            await using var check = factory.CreateDbContext();
            await check.CalcJobStates.Where(state => state.JobId == job.Id).ExecuteUpdateAsync(set => set.SetProperty(state => state.StateJson, "{broken"));
            using var corrupt = Engine();
            await corrupt.TickAsync(now + 4000); await corrupt.WaitForScriptsAsync(); await corrupt.FlushAsync();
            Assert.Equal(1, writer.Calls); Assert.Contains("corrupt", Assert.Single(await corrupt.StatusAsync()).LastError);
            Assert.Equal("{broken", await check.CalcJobStates.Where(state => state.JobId == job.Id).Select(state => state.StateJson).SingleAsync());
        }
        finally { SqliteConnection.ClearAllPools(); File.Delete(path); }
    }

    [Fact]
    public async Task SlowScriptDoesNotBlockStatusOtherJobsOrStartOverlappingRuns()
    {
        var path = Path.Combine(Path.GetTempPath(), "csharp-schedule-" + Guid.NewGuid().ToString("N") + ".db");
        var factory = new CalcJobTests.FileFactory(path);
        await using var runtimeConnection = TestRuntime.Open(); var runtime = TestRuntime.Factory(runtimeConnection);
        try
        {
            await using (var db = factory.CreateDbContext())
            {
                await db.Database.EnsureCreatedAsync();
                db.Devices.Add(new Device { Id = 7, Name = "plc", AreaId = Area.DefaultId });
                db.Tags.Add(new Tag { Id = 1, DeviceId = 7, Name = "input" }); await db.SaveChangesAsync();
            }
            var runner = new BlockingRunner(); using var bus = new RealtimeBus(); var cache = new RealtimeCache();
            using var engine = new CalcJobEngine(factory, new CalcJobTests.Snapshot(), cache, bus, new UnknownWriter(),
                Options.Create(new Scada.Core.Options.ScadaSettings()), NullLogger<CalcJobEngine>.Instance, runtime, csharp: runner);
            var configuration = new CalcJobConfiguration(factory, runtime, engine);
            var script = await configuration.SaveAsync(null, new("slow", CalcJobKind.CSharp, Script: "source", ReadTags: "1"), 1, "admin", default);
            var other = await configuration.SaveAsync(null, new("flow", CalcJobKind.Accumulator, InputTagId: 1, Mode: AccumulatorMode.Flow), 1, "admin", default);
            var now = ScadaTime.NowMs(); cache.Set(new TagValue(1, 21, TagQuality.Good, now));
            await engine.TickAsync(now).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True((await engine.StatusAsync().WaitAsync(TimeSpan.FromSeconds(2))).Single(status => status.JobId == script.Id).IsRunning);
            cache.Set(new TagValue(1, 99, TagQuality.Good, now + 1000));
            await engine.TickAsync(now + 1000).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(1, runner.Calls); Assert.Equal(21, runner.Request!.Inputs![1].Value);
            Assert.True((await engine.StatusAsync()).Single(status => status.JobId == other.Id).Values["total"] > 0);
            runner.Completion.SetResult(new("", true, ["answer"], new() { ["answer"] = 42 }, new(), []));
            await engine.WaitForScriptsAsync(); Assert.False((await engine.StatusAsync()).Single(status => status.JobId == script.Id).IsRunning);
        }
        finally { SqliteConnection.ClearAllPools(); File.Delete(path); }
    }

    [Fact]
    public async Task ReadSampleKeepsBadQualityAndNamesResolveOnSave()
    {
        using var host = Host();
        const string source = "var sample = context.ReadSample(\"input\"); context.Output(\"good\", sample.Good ? 1 : 0); context.Output(\"raw\", sample.Value); context.Write(\"output\", sample.Quality == \"Bad\" ? 0 : 1);";
        var result = await host.RunAsync(new("", "run", source,
            new() { [1] = new(4.5, false, 80, "Bad") }, [2], Names: new() { ["input"] = 1, ["output"] = 2 }));
        Assert.True(result.Success, result.Error);
        Assert.Equal(0, result.Outputs["good"]);
        Assert.Equal(4.5, result.Outputs["raw"]);
        Assert.Equal(0, result.Writes[2]);

        var path = Path.Combine(Path.GetTempPath(), "csharp-names-" + Guid.NewGuid().ToString("N") + ".db");
        var factory = new CalcJobTests.FileFactory(path);
        await using var runtimeConnection = TestRuntime.Open(); var runtime = TestRuntime.Factory(runtimeConnection);
        try
        {
            await using (var db = factory.CreateDbContext())
            {
                await db.Database.EnsureCreatedAsync();
                db.Devices.Add(new Device { Id = 7, Name = "plc", AreaId = Area.DefaultId });
                db.Tags.AddRange(new Tag { Id = 1, DeviceId = 7, Name = "input" }, new Tag { Id = 2, DeviceId = 7, Name = "output", Writable = true });
                await db.SaveChangesAsync();
            }
            using var runner = Host();
            var engine = new CalcJobEngine(factory, new CalcJobTests.Snapshot(), new RealtimeCache(), new RealtimeBus(), new UnknownWriter(), Options.Create(new Scada.Core.Options.ScadaSettings()), NullLogger<CalcJobEngine>.Instance, runtime, csharp: runner);
            var configuration = new CalcJobConfiguration(factory, runtime, engine);
            var saved = await configuration.SaveAsync(null, new("named", CalcJobKind.CSharp, Script: "context.Output(\"answer\", 1);", ReadTags: "input", WriteTags: "plc/output"), 1, "admin", default);
            Assert.Equal("1", saved.ReadTags);
            Assert.Equal("2", saved.WriteTags);
            await using (var db = factory.CreateDbContext())
            {
                db.Devices.Add(new Device { Id = 8, Name = "pump", AreaId = Area.DefaultId });
                db.Tags.Add(new Tag { Id = 30, DeviceId = 8, Name = "input" });
                await db.SaveChangesAsync();
            }
            var ambiguous = await Assert.ThrowsAsync<CalculationRequestException>(() => configuration.SaveAsync(saved.Id, new("named", CalcJobKind.CSharp, Script: "context.Output(\"answer\", 1);", ReadTags: "input", WriteTags: "output", Revision: saved.Revision), 1, "admin", default));
            Assert.Contains("不唯一", ambiguous.Message);
        }
        finally { SqliteConnection.ClearAllPools(); if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void RebindRewritesCallIdsAndLeavesStrings()
    {
        var catalog = new ScriptTagRef[] { new(9, "input", "plc"), new(8, "output", "plc"), new(4, "only", null) };
        Assert.Equal([4, 9], ScriptTagNames.Resolve("only, plc/input", catalog));
        Assert.Equal([9], ScriptTagNames.Resolve("input", [catalog[0], new(3, "other", "pump")]));
        var source = "context.Read(1); context.ReadSample( 1 ); context.Write(2, 1); var text = \"context.Read(1)\"; // context.Write(2, 1)";
        var rewritten = ScriptTagNames.RebindCalls(source, new Dictionary<int, int> { [1] = 9, [2] = 8 });
        Assert.Contains("context.Read(9)", rewritten);
        Assert.Contains("context.ReadSample( 9 )", rewritten);
        Assert.Contains("context.Write(8, 1)", rewritten);
        Assert.Contains("\"context.Read(1)\"", rewritten);
        Assert.Contains("// context.Write(2, 1)", rewritten);
    }

    [Fact]
    public async Task OnChangeWaitsForTheWatchedValueToMove()
    {
        var path = Path.Combine(Path.GetTempPath(), "csharp-change-" + Guid.NewGuid().ToString("N") + ".db");
        var factory = new CalcJobTests.FileFactory(path);
        await using var runtimeConnection = TestRuntime.Open(); var runtime = TestRuntime.Factory(runtimeConnection);
        try
        {
            await using (var db = factory.CreateDbContext())
            {
                await db.Database.EnsureCreatedAsync();
                db.Devices.Add(new Device { Id = 7, Name = "plc", AreaId = Area.DefaultId });
                db.Tags.Add(new Tag { Id = 1, DeviceId = 7, Name = "input" });
                await db.SaveChangesAsync();
            }
            var runner = new BlockingRunner();
            var cache = new RealtimeCache();
            using var engine = new CalcJobEngine(factory, new CalcJobTests.Snapshot(), cache, new RealtimeBus(), new UnknownWriter(), Options.Create(new Scada.Core.Options.ScadaSettings()), NullLogger<CalcJobEngine>.Instance, runtime, csharp: runner);
            var configuration = new CalcJobConfiguration(factory, runtime, engine);
            await configuration.SaveAsync(null, new("change", CalcJobKind.CSharp, Script: "source", ReadTags: "1", Trigger: CalcTrigger.OnChange, ChangeCooldownMs: 0), 1, "admin", default);
            var now = ScadaTime.NowMs();
            cache.Set(new TagValue(1, 10, TagQuality.Good, now));
            await engine.TickAsync(now);
            await engine.TickAsync(now + 1000);
            Assert.Equal(0, runner.Calls);
            cache.Set(new TagValue(1, 12, TagQuality.Good, now + 2000));
            runner.Completion.SetResult(new("", true, ["answer"], new() { ["answer"] = 1 }, new(), []));
            await engine.TickAsync(now + 2000);
            await engine.WaitForScriptsAsync();
            Assert.Equal(1, runner.Calls);
        }
        finally { SqliteConnection.ClearAllPools(); if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task DraftStaysOffTheRunnerUntilPublishAndInvokePassesTheArgument()
    {
        var path = Path.Combine(Path.GetTempPath(), "csharp-draft-" + Guid.NewGuid().ToString("N") + ".db");
        var factory = new CalcJobTests.FileFactory(path);
        await using var runtimeConnection = TestRuntime.Open(); var runtime = TestRuntime.Factory(runtimeConnection);
        try
        {
            await using (var db = factory.CreateDbContext())
            {
                await db.Database.EnsureCreatedAsync();
                db.Devices.Add(new Device { Id = 7, Name = "plc", AreaId = Area.DefaultId });
                db.Tags.Add(new Tag { Id = 1, DeviceId = 7, Name = "input" });
                await db.SaveChangesAsync();
            }
            var runner = new BlockingRunner();
            var cache = new RealtimeCache();
            using var engine = new CalcJobEngine(factory, new CalcJobTests.Snapshot(), cache, new RealtimeBus(), new UnknownWriter(), Options.Create(new Scada.Core.Options.ScadaSettings()), NullLogger<CalcJobEngine>.Instance, runtime, csharp: runner);
            var configuration = new CalcJobConfiguration(factory, runtime, engine);
            var saved = await configuration.SaveAsync(null, new("drafted", CalcJobKind.CSharp, Script: "published", ReadTags: "1"), 1, "admin", default);
            await configuration.SaveAsync(saved.Id, new("drafted", CalcJobKind.CSharp, Script: "draft", ReadTags: "1", Revision: saved.Revision), 1, "admin", default);
            runner.Completion.SetResult(new("", true, ["answer"], new() { ["answer"] = 1 }, new(), []));
            var now = ScadaTime.NowMs();
            cache.Set(new TagValue(1, 10, TagQuality.Good, now));
            await engine.TickAsync(now);
            await engine.WaitForScriptsAsync();
            Assert.Equal("published", runner.Request!.Source);

            await using (var db = factory.CreateDbContext())
            {
                var stored = await db.CalcJobs.SingleAsync(x => x.Id == saved.Id);
                stored.PublishedRevision = 0;
                await db.SaveChangesAsync();
            }
            runner.Calls = 0;
            engine.RequestReload();
            await engine.TickAsync(now + 1000);
            Assert.Equal(0, runner.Calls);

            var current = await configuration.GetAsync(saved.Id, default);
            await configuration.PublishAsync(saved.Id, current!.Revision, 1, "admin", default);
            await engine.InvokeAsync(saved.Id, 4);
            Assert.Equal("draft", runner.Request.Source);
            Assert.Equal(4, runner.Request.Argument);
        }
        finally { SqliteConnection.ClearAllPools(); if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task DeclaredHistoryAndAreaAlarmsReachTheRunner()
    {
        var path = Path.Combine(Path.GetTempPath(), "csharp-plant-" + Guid.NewGuid().ToString("N") + ".db");
        var factory = new CalcJobTests.FileFactory(path);
        await using var runtimeConnection = TestRuntime.Open(); var runtime = TestRuntime.Factory(runtimeConnection);
        try
        {
            await using (var db = factory.CreateDbContext())
            {
                await db.Database.EnsureCreatedAsync();
                db.Devices.Add(new Device { Id = 7, Name = "plc", AreaId = Area.DefaultId });
                db.Tags.Add(new Tag { Id = 1, DeviceId = 7, Name = "input" });
                await db.SaveChangesAsync();
            }
            await using (var db = runtime.CreateDbContext())
            {
                db.AlarmEvents.Add(new AlarmEvent { TagId = 1, Level = AlarmLevel.Warning, Message = "high", StartTs = 2_000 });
                await db.SaveChangesAsync();
            }
            var runner = new BlockingRunner();
            var cache = new RealtimeCache();
            using var engine = new CalcJobEngine(factory, new CalcJobTests.Snapshot(), cache, new RealtimeBus(), new UnknownWriter(), Options.Create(new Scada.Core.Options.ScadaSettings()), NullLogger<CalcJobEngine>.Instance, runtime, csharp: runner, history: new OnePointHistory());
            var configuration = new CalcJobConfiguration(factory, runtime, engine);
            await configuration.SaveAsync(null, new("plant", CalcJobKind.CSharp, Script: "source", ReadTags: "1", HistoryWindowSec: 60, AlarmSnapshot: true), 1, "admin", default);
            runner.Completion.SetResult(new("", true, ["answer"], new() { ["answer"] = 1 }, new(), []));
            var now = 10_000L;
            cache.Set(new TagValue(1, 10, TagQuality.Good, now));
            await engine.TickAsync(now);
            await engine.WaitForScriptsAsync();
            var status = await engine.StatusAsync();
            Assert.Null(status.Single().LastError);
            Assert.Equal(1.5, runner.Request!.History![1].Single().Value);
            Assert.Equal("high", runner.Request.Alarms!.Single().Message);
        }
        finally { SqliteConnection.ClearAllPools(); if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task HistoryAlarmsArgumentAndTraceRoundTripThroughTheWorker()
    {
        using var host = Host();
        const string source = "var points = context.History(\"input\"); var alarms = context.Alarms(); context.Output(\"n\", points.Count + alarms.Count + context.Argument);";
        var request = new CSharpScriptRequest("", "run", source,
            new() { [1] = new(10, true, 1) }, [], 5_000, 1,
            new() { ["input"] = 1 },
            new() { [1] = [new ScriptSample(3, "Good", 1_000, true)] },
            [new ScriptAlarm(9, "Warning", "high", false, 2_000)],
            4);
        var result = await host.RunAsync(request);
        Assert.True(result.Success, result.Error);
        Assert.Equal(6, result.Outputs["n"]);
        Assert.Contains(result.Trace!, line => line.StartsWith("History", StringComparison.Ordinal));
        Assert.Contains(result.Trace!, line => line.StartsWith("Alarms", StringComparison.Ordinal));
    }

    [Fact]
    public void QuotedTagNamesIgnoreComments()
    {
        var names = ScriptTagNames.QuotedArguments("context.Read(\"FT101\"); context.Write(\"pump/out\", 1); // context.Read(\"skip\")");
        Assert.Equal(["FT101", "pump/out"], names);
    }

    [Fact]
    public async Task LibraryFunctionIsCompiledWithTheTask()
    {
        using var host = Host();
        var request = new CSharpScriptRequest("", "run", "context.Output(\"n\", Twice(context.Read(1)));",
            new() { [1] = new(3, true, 0) }, LibrarySource: "double Twice(double value) => value * 2;");
        var result = await host.RunAsync(request);
        Assert.True(result.Success, (result.Error ?? "") + string.Join(" | ", result.Diagnostics));
        Assert.Equal(6, result.Outputs["n"]);
    }

    [Fact]
    public async Task DraftSaveDoesNotRetriggerStartupOrReplaceTheRunningInterval()
    {
        var path = Path.Combine(Path.GetTempPath(), "csharp-startup-" + Guid.NewGuid().ToString("N") + ".db");
        var factory = new CalcJobTests.FileFactory(path);
        await using var runtimeConnection = TestRuntime.Open(); var runtime = TestRuntime.Factory(runtimeConnection);
        try
        {
            await using (var db = factory.CreateDbContext())
            {
                await db.Database.EnsureCreatedAsync();
                db.Devices.Add(new Device { Id = 7, Name = "plc", AreaId = Area.DefaultId });
                db.Tags.Add(new Tag { Id = 1, DeviceId = 7, Name = "input" });
                await db.SaveChangesAsync();
            }
            var runner = new BlockingRunner();
            var cache = new RealtimeCache();
            using var engine = new CalcJobEngine(factory, new CalcJobTests.Snapshot(), cache, new RealtimeBus(), new UnknownWriter(), Options.Create(new Scada.Core.Options.ScadaSettings()), NullLogger<CalcJobEngine>.Instance, runtime, csharp: runner);
            var configuration = new CalcJobConfiguration(factory, runtime, engine);
            var saved = await configuration.SaveAsync(null, new("startup", CalcJobKind.CSharp, Script: "published", ReadTags: "1", Trigger: CalcTrigger.OnStartup, IntervalMs: 1000), 1, "admin", default);
            runner.Completion.SetResult(new("", true, ["answer"], new() { ["answer"] = 1 }, new(), []));
            var now = ScadaTime.NowMs();
            await engine.TickAsync(now);
            await engine.WaitForScriptsAsync();
            Assert.Equal(1, runner.Calls);
            await configuration.SaveAsync(saved.Id, new("startup", CalcJobKind.CSharp, Script: "draft", ReadTags: "1", Trigger: CalcTrigger.OnStartup, IntervalMs: 5000, Revision: saved.Revision), 1, "admin", default);
            await engine.TickAsync(now + 1000);
            await engine.WaitForScriptsAsync();
            Assert.Equal(1, runner.Calls);
            var current = await configuration.GetAsync(saved.Id, default);
            Assert.Equal(1000, current!.PublishedIntervalMs);
            Assert.Equal(5000, current.IntervalMs);
            await configuration.PublishAsync(saved.Id, current.Revision, 1, "admin", default);
            await engine.TickAsync(now + 6000);
            await engine.WaitForScriptsAsync();
            Assert.Equal(2, runner.Calls);
            Assert.Equal("draft", runner.Request!.Source);
        }
        finally { SqliteConnection.ClearAllPools(); if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task AlarmTriggerIgnoresOtherAreas()
    {
        var path = Path.Combine(Path.GetTempPath(), "csharp-alarm-area-" + Guid.NewGuid().ToString("N") + ".db");
        var factory = new CalcJobTests.FileFactory(path);
        await using var runtimeConnection = TestRuntime.Open(); var runtime = TestRuntime.Factory(runtimeConnection);
        try
        {
            await using (var db = factory.CreateDbContext())
            {
                await db.Database.EnsureCreatedAsync();
                db.Areas.Add(new Area { Id = 2, Name = "other" });
                db.Devices.Add(new Device { Id = 7, Name = "local", AreaId = Area.DefaultId });
                db.Devices.Add(new Device { Id = 8, Name = "remote", AreaId = 2 });
                db.Tags.Add(new Tag { Id = 1, DeviceId = 7, Name = "local" });
                db.Tags.Add(new Tag { Id = 30, DeviceId = 8, Name = "remote" });
                await db.SaveChangesAsync();
            }
            var runner = new BlockingRunner();
            using var engine = new CalcJobEngine(factory, new CalcJobTests.Snapshot(), new RealtimeCache(), new RealtimeBus(), new UnknownWriter(), Options.Create(new Scada.Core.Options.ScadaSettings()), NullLogger<CalcJobEngine>.Instance, runtime, csharp: runner);
            var configuration = new CalcJobConfiguration(factory, runtime, engine);
            await configuration.SaveAsync(null, new("alarmed", CalcJobKind.CSharp, Script: "source", ReadTags: "1", Trigger: CalcTrigger.OnAlarm, AlarmOnRaise: true), 1, "admin", default);
            var now = 5_000L;
            await engine.TickAsync(now);
            Assert.Equal(0, runner.Calls);
            await using (var db = runtime.CreateDbContext())
            {
                db.AlarmEvents.Add(new AlarmEvent { TagId = 30, Level = AlarmLevel.Warning, Message = "other", StartTs = now + 100 });
                await db.SaveChangesAsync();
            }
            await engine.TickAsync(now + 1000);
            Assert.Equal(0, runner.Calls);
            await using (var db = runtime.CreateDbContext())
            {
                db.AlarmEvents.Add(new AlarmEvent { TagId = 1, Level = AlarmLevel.Warning, Message = "local", StartTs = now + 200 });
                await db.SaveChangesAsync();
            }
            runner.Completion.SetResult(new("", true, ["answer"], new() { ["answer"] = 1 }, new(), []));
            await engine.TickAsync(now + 2000);
            await engine.WaitForScriptsAsync();
            Assert.Equal(1, runner.Calls);
        }
        finally { SqliteConnection.ClearAllPools(); if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task UnlicensedCSharpDoesNotRun()
    {
        var path = Path.Combine(Path.GetTempPath(), "csharp-license-" + Guid.NewGuid().ToString("N") + ".db");
        var factory = new CalcJobTests.FileFactory(path);
        await using var runtimeConnection = TestRuntime.Open(); var runtime = TestRuntime.Factory(runtimeConnection);
        try
        {
            await using (var db = factory.CreateDbContext())
            {
                await db.Database.EnsureCreatedAsync();
                db.Devices.Add(new Device { Id = 7, Name = "plc", AreaId = Area.DefaultId });
                db.Tags.Add(new Tag { Id = 1, DeviceId = 7, Name = "input" });
                await db.SaveChangesAsync();
            }
            var runner = new BlockingRunner();
            using var engine = new CalcJobEngine(factory, new CalcJobTests.Snapshot(), new RealtimeCache(), new RealtimeBus(), new UnknownWriter(), Options.Create(new Scada.Core.Options.ScadaSettings()), NullLogger<CalcJobEngine>.Instance, runtime, csharp: runner, license: new DeniedCSharp());
            var configuration = new CalcJobConfiguration(factory, runtime, engine);
            await configuration.SaveAsync(null, new("locked", CalcJobKind.CSharp, Script: "source", ReadTags: "1"), 1, "admin", default);
            await engine.TickAsync(ScadaTime.NowMs());
            Assert.Equal(0, runner.Calls);
            Assert.Contains("授权", (await engine.StatusAsync()).Single().LastError);
        }
        finally { SqliteConnection.ClearAllPools(); if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task BrokenLibraryIsRejectedBeforeItIsStored()
    {
        using var host = Host();
        var path = Path.Combine(Path.GetTempPath(), "csharp-bad-library-" + Guid.NewGuid().ToString("N") + ".db");
        var factory = new CalcJobTests.FileFactory(path);
        await using var runtimeConnection = TestRuntime.Open(); var runtime = TestRuntime.Factory(runtimeConnection);
        using var engine = new CalcJobEngine(factory, new CalcJobTests.Snapshot(), new RealtimeCache(), new RealtimeBus(), new UnknownWriter(), Options.Create(new Scada.Core.Options.ScadaSettings()), NullLogger<CalcJobEngine>.Instance, runtime, csharp: host);
        var configuration = new CalcJobConfiguration(factory, runtime, engine);
        var empty = await Assert.ThrowsAsync<CalculationRequestException>(() => configuration.SaveLibraryAsync(null, new ScriptLibraryInput("Empty", "   ", 0), "admin", default));
        Assert.Contains("不能为空", empty.Message);
        var broken = await Assert.ThrowsAsync<CalculationRequestException>(() => configuration.SaveLibraryAsync(null, new ScriptLibraryInput("Bad", "this is not c#", 0), "admin", default));
        Assert.Contains("CS", broken.Message);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task LibrarySourceStaysFrozenUntilTheTaskIsPublished()
    {
        var path = Path.Combine(Path.GetTempPath(), "csharp-library-" + Guid.NewGuid().ToString("N") + ".db");
        var factory = new CalcJobTests.FileFactory(path);
        await using var runtimeConnection = TestRuntime.Open(); var runtime = TestRuntime.Factory(runtimeConnection);
        try
        {
            await using (var db = factory.CreateDbContext())
            {
                await db.Database.EnsureCreatedAsync();
                db.Devices.Add(new Device { Id = 7, Name = "plc", AreaId = Area.DefaultId });
                db.Tags.Add(new Tag { Id = 1, DeviceId = 7, Name = "input" });
                await db.SaveChangesAsync();
            }
            var runner = new BlockingRunner();
            var cache = new RealtimeCache();
            using var engine = new CalcJobEngine(factory, new CalcJobTests.Snapshot(), cache, new RealtimeBus(), new UnknownWriter(), Options.Create(new Scada.Core.Options.ScadaSettings()), NullLogger<CalcJobEngine>.Instance, runtime, csharp: runner);
            var configuration = new CalcJobConfiguration(factory, runtime, engine);
            await configuration.SaveLibraryAsync(null, new ScriptLibraryInput("Twice", "double Twice(double value) => value * 2;", 0), "admin", default);
            var saved = await configuration.SaveAsync(null, new("uses-lib", CalcJobKind.CSharp, Script: "source", ReadTags: "1", Libraries: "Twice"), 1, "admin", default);
            runner.Completion.SetResult(new("", true, ["answer"], new() { ["answer"] = 1 }, new(), []));
            var now = ScadaTime.NowMs();
            cache.Set(new TagValue(1, 10, TagQuality.Good, now));
            await engine.TickAsync(now);
            await engine.WaitForScriptsAsync();
            Assert.Contains("value * 2", runner.Request!.LibrarySource);
            var library = (await configuration.LibrariesAsync(default)).Single();
            var drafted = await configuration.SaveLibraryAsync(library.Id, new ScriptLibraryInput(library.Name, "double Twice(double value) => value * 3;", library.Revision), "admin", default);
            await configuration.PublishLibraryAsync(drafted.Id, drafted.Revision, "admin", default);
            await engine.TickAsync(now + 1000);
            await engine.WaitForScriptsAsync();
            Assert.Contains("value * 2", runner.Request.LibrarySource);
            var current = await configuration.GetAsync(saved.Id, default);
            await configuration.PublishAsync(saved.Id, current!.Revision, 1, "admin", default);
            await engine.TickAsync(now + 2000);
            await engine.WaitForScriptsAsync();
            Assert.Contains("value * 3", runner.Request.LibrarySource);
        }
        finally { SqliteConnection.ClearAllPools(); if (File.Exists(path)) File.Delete(path); }
    }

    private sealed class DeniedCSharp : ICSharpScriptLicense
    {
        public bool AllowsCSharp => false;
    }

    private sealed class BlockingRunner : ICSharpScriptRunner
    {
        public bool Enabled => true;
        public int Calls;
        public CSharpScriptRequest? Request;
        public TaskCompletionSource<CSharpScriptReply> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<CSharpScriptReply> CompileAsync(string source, string? librarySource = null, CancellationToken ct = default) => Task.FromResult(new CSharpScriptReply("", true, ["answer"], new(), new(), []));
        public Task<CSharpScriptReply> RunAsync(CSharpScriptRequest request, CancellationToken ct = default) { Calls++; Request = request; return Completion.Task.WaitAsync(ct); }
    }

    private sealed class OnePointHistory : IHistoryStore
    {
        public Task<IReadOnlyList<HistorySeries>> QueryRawAsync(IReadOnlyCollection<int> tagIds, long from, long to, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<HistorySeries>>([new HistorySeries { TagId = tagIds.Single(), Points = { new HistoryPoint(1_000, 1.5, TagQuality.Good) } }]);
        public Task WriteRawAsync(IReadOnlyCollection<HistoryValue> values, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task WriteAggregate5MinAsync(IReadOnlyCollection<AggregateValue> values, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task WriteAggregateHourAsync(IReadOnlyCollection<AggregateValue> values, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<HistorySeries>> QueryAggregate5MinAsync(IReadOnlyCollection<int> tagIds, long from, long to, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<HistorySeries>> QueryAggregateHourAsync(IReadOnlyCollection<int> tagIds, long from, long to, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<AggregateValue>> QueryRawAggregatesAsync(long from, long to, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<AggregateValue>> Query5MinAggregatesAsync(long from, long to, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<AggregatePoint>> QueryHourAggregatePointsAsync(IReadOnlyCollection<int> tagIds, long from, long to, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<AggregatePoint>> QueryAggregatePointsAsync(IReadOnlyCollection<int> tagIds, long from, long to, HistoryResolution resolution, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class UnknownWriter : ICalcTagWriter
    {
        public int Calls;
        public Task<string?> CheckIdentityAsync(CalcJob job, IReadOnlyCollection<int> writeTags, CancellationToken ct) => Task.FromResult<string?>(null);
        public Task<CalcWriteResult> WriteAsync(CalcJob job, int tagId, double value, Guid commandId, CancellationToken ct)
        { Calls++; return Task.FromResult(new CalcWriteResult(false, "unknown result", GatewayCommandOutcome.Unknown)); }
    }
}