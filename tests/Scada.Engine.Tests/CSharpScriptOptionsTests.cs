using Microsoft.Extensions.Options;
using Scada.Core.Scripting;
using Scada.Engine.Calculations;

namespace Scada.Engine.Tests;

// 配置错误要在启动时暴露，而不是等到第一次用脚本才变成一条 500。
public sealed class CSharpScriptOptionsTests
{
    [Fact]
    public void DisabledFeatureSkipsValidation()
    {
        var options = new CSharpScriptOptions
        {
            Enabled = false,
            SerialProfiles = new() { ["scanner"] = new SerialProfile { Port = "" } }
        };
        Assert.Empty(CSharpScriptOptionsValidation.Validate(options));
    }

    [Fact]
    public void InvalidSerialProfileIsReported()
    {
        var options = new CSharpScriptOptions
        {
            Enabled = true,
            SerialProfiles = new() { ["scanner"] = new SerialProfile { Port = "" } }
        };
        Assert.Contains(CSharpScriptOptionsValidation.Validate(options), error => error.Contains("端口", StringComparison.Ordinal));
    }

    [Fact]
    public void ExecutionTimeoutMustExceedSerialReadTimeout()
    {
        var options = new CSharpScriptOptions
        {
            Enabled = true,
            TimeoutMs = 2000,
            SerialProfiles = new() { ["scanner"] = new SerialProfile { Port = "COM3", ReadTimeoutMs = 2000 } }
        };
        Assert.Contains(CSharpScriptOptionsValidation.Validate(options), error => error.Contains("TimeoutMs", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidConfigurationPasses()
    {
        var options = new CSharpScriptOptions
        {
            Enabled = true,
            TimeoutMs = 3000,
            SerialProfiles = new() { ["scanner"] = new SerialProfile { Port = "COM3", ReadTimeoutMs = 2000 } }
        };
        Assert.Empty(CSharpScriptOptionsValidation.Validate(options));
    }

    // 进程被强杀时 Stop 来不及删工作目录；宿主启动只清理一天前的 scada-script-<guid>，
    // 日志目录与编译缓存目录不能被误删（它们同样以 scada-script- 开头）。
    [Fact]
    public void OrphanWorkerDirectoriesAreSweptOnStartup()
    {
        var orphan = Path.Combine(Path.GetTempPath(), "scada-script-" + Guid.NewGuid().ToString("N"));
        var recent = Path.Combine(Path.GetTempPath(), "scada-script-" + Guid.NewGuid().ToString("N"));
        var cache = Path.Combine(Path.GetTempPath(), "scada-script-cache");
        Directory.CreateDirectory(orphan);
        Directory.CreateDirectory(recent);
        Directory.CreateDirectory(cache);
        try
        {
            Directory.SetLastWriteTimeUtc(orphan, DateTime.UtcNow.AddDays(-2));
            Directory.SetLastWriteTimeUtc(cache, DateTime.UtcNow.AddDays(-2));
            using var host = new CSharpScriptHost(Options.Create(new CSharpScriptOptions()));
            Assert.False(Directory.Exists(orphan));
            Assert.True(Directory.Exists(recent));
            Assert.True(Directory.Exists(cache));
        }
        finally
        {
            foreach (var path in new[] { recent, orphan })
                try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch (IOException) { }
        }
    }
}
