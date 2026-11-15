using Microsoft.Extensions.Options;
using Scada.Core.Scripting;
using Scada.Engine.Calculations;

namespace Scada.Engine.Tests;

// Worker 的策略表与错误通道：这层最容易被改坏，而且坏了只会表现为"脚本莫名失败"。
public sealed class ScriptWorkerProtocolTests
{
    [Theory]
    [InlineData("context.Output(\"ok\", 1); System.Diagnostics.Process.GetCurrentProcess();", "outside the script sandbox")]
    [InlineData("context.Output(\"ok\", 1); System.Environment.Exit(0);", "outside the script sandbox")]
    [InlineData("context.Output(\"ok\", 1); System.Console.WriteLine(\"x\");", "outside the script sandbox")]
    [InlineData("context.Output(\"ok\", 1); var loader = System.Runtime.Loader.AssemblyLoadContext.Default;", "outside the script sandbox")]
    [InlineData("unsafe { int* pointer = null; } context.Output(\"ok\", 1);", "unsafe code")]
    [InlineData("[System.Runtime.InteropServices.DllImport(\"user32.dll\")] static extern int MessageBoxW(int h, string t, string c, int u); context.Output(\"ok\", 1);", "DllImport")]
    [InlineData("context.Output(\"1bad\", 1);", "Output names")]
    public async Task PolicyRejectsHostileScriptsWithLocatedDiagnostics(string source, string expected)
    {
        using var host = Host();
        var reply = await host.CompileAsync(source);
        Assert.False(reply.Success);
        Assert.Contains(reply.Diagnostics, message => message.Contains(expected, StringComparison.OrdinalIgnoreCase));
        Assert.NotEmpty(reply.Spans!);
    }

    // 编译期有 span，运行期过去只有一句消息：这里锁住"运行时异常回到用户源码行"的行为。
    [Fact]
    public async Task RuntimeExceptionReportsTheScriptLine()
    {
        using var host = Host();
        const string source = "context.Output(\"value\", context.Read(1));\nthrow new InvalidOperationException(\"boom\");";
        var reply = await host.RunAsync(new("", "run", source, new() { [1] = new(5, true, 0) }));

        Assert.False(reply.Success);
        var detail = Assert.IsType<ScriptError>(reply.ErrorDetail);
        Assert.Equal(ScriptProtocol.ErrorScriptThrew, detail.Code);
        Assert.Equal("script.cs", detail.File);
        Assert.Equal(2, detail.Line);
        Assert.Contains("boom", detail.Message, StringComparison.Ordinal);
        Assert.Equal("script.cs", Assert.Single(reply.Spans!).File);
        Assert.Equal(2, Assert.Single(reply.Spans!).StartLine);
        Assert.Contains("script.cs(2,", reply.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompileFailureIsTaggedAsCompileError()
    {
        using var host = Host();
        var reply = await host.CompileAsync("context.Output(\"ok\", );");

        Assert.False(reply.Success);
        Assert.Equal(ScriptProtocol.ErrorCompile, Assert.IsType<ScriptError>(reply.ErrorDetail).Code);
        Assert.Null(reply.Error);
        Assert.NotEmpty(reply.Diagnostics);
        Assert.NotEmpty(reply.Spans!);
    }

    [Fact]
    public async Task SuccessReplyCarriesNoErrorDetail()
    {
        using var host = Host();
        var reply = await host.RunAsync(new("", "run", "context.Output(\"ok\", context.Read(1) + 1);", new() { [1] = new(41, true, 0) }));

        Assert.True(reply.Success, reply.Error);
        Assert.Null(reply.ErrorDetail);
        Assert.Equal(42, reply.Outputs["ok"]);
    }

    // 磁盘缓存是跨进程复用的：第一个 Worker 写下的产物要能被后续进程读到；
    // 产物损坏时必须回退重编译，而不是让脚本以一个看不懂的错误失败。
    [Fact]
    public async Task CompiledArtifactsSurviveWorkerRestartAndTolerateCorruption()
    {
        var cache = Path.Combine(Path.GetTempPath(), "script-cache-" + Guid.NewGuid().ToString("N"));
        try
        {
            const string source = "context.Output(\"double\", context.Read(1) * 2);";
            using (var first = Host(cache))
            {
                var reply = await first.RunAsync(new("", "run", source, new() { [1] = new(21, true, 0) }));
                Assert.True(reply.Success, reply.Error);
                Assert.Equal(42, reply.Outputs["double"]);
            }
            var artifacts = Directory.GetFiles(cache, "*.dll");
            Assert.Single(artifacts);
            Assert.Single(Directory.GetFiles(cache, "*.pdb"));
            Assert.Single(Directory.GetFiles(cache, "*.json"));

            File.WriteAllText(artifacts[0], "corrupted");
            using var second = Host(cache);
            var recovered = await second.RunAsync(new("", "run", source, new() { [1] = new(21, true, 0) }));
            Assert.True(recovered.Success, recovered.Error);
            Assert.Equal(42, recovered.Outputs["double"]);
        }
        finally
        {
            try { Directory.Delete(cache, recursive: true); } catch (IOException) { }
        }
    }

    private static CSharpScriptHost Host(string? cacheDirectory = null) => new(Options.Create(new CSharpScriptOptions
    {
        Enabled = true,
        WorkerPath = Path.Combine(AppContext.BaseDirectory, "ScriptWorker", "Scada.ScriptWorker.dll"),
        TimeoutMs = 5000,
        WorkerCount = 1,
        CacheDirectory = cacheDirectory ?? ""
    }));
}
