using Microsoft.Extensions.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Scada.Core.Calculations;
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
            CalcJobEngine Engine() => new(factory, new CalcJobTests.Snapshot(), cache, bus, writer, Options.Create(new Scada.Core.Options.ScadaSettings()), NullLogger<CalcJobEngine>.Instance, csharp: host);
            using var engine = Engine();
            var configuration = new CalcJobConfiguration(factory, engine);
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
                Options.Create(new Scada.Core.Options.ScadaSettings()), NullLogger<CalcJobEngine>.Instance, csharp: runner);
            var configuration = new CalcJobConfiguration(factory, engine);
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

    private sealed class BlockingRunner : ICSharpScriptRunner
    {
        public bool Enabled => true;
        public int Calls;
        public CSharpScriptRequest? Request;
        public TaskCompletionSource<CSharpScriptReply> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<CSharpScriptReply> CompileAsync(string source, CancellationToken ct = default) => Task.FromResult(new CSharpScriptReply("", true, ["answer"], new(), new(), []));
        public Task<CSharpScriptReply> RunAsync(CSharpScriptRequest request, CancellationToken ct = default) { Calls++; Request = request; return Completion.Task.WaitAsync(ct); }
    }

    private sealed class UnknownWriter : ICalcTagWriter
    {
        public int Calls;
        public Task<string?> CheckIdentityAsync(CalcJob job, IReadOnlyCollection<int> writeTags, CancellationToken ct) => Task.FromResult<string?>(null);
        public Task<CalcWriteResult> WriteAsync(CalcJob job, int tagId, double value, Guid commandId, CancellationToken ct)
        { Calls++; return Task.FromResult(new CalcWriteResult(false, "unknown result", GatewayCommandOutcome.Unknown)); }
    }
}