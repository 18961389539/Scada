using Scada.Core.Scripting;

namespace Scada.Engine.Tests;

// 这两段逻辑原本嵌在 Worker/宿主内部，只能靠真实进程验证；
// 拆成纯函数后可以在没有硬件、没有子进程的条件下覆盖边界。
public sealed class ScriptHandshakeTests
{
    [Fact]
    public void AcceptsMatchingVersion()
    {
        Assert.Null(ScriptHandshake.Validate(new CSharpScriptHello(ScriptProtocol.Version, "10.0", "5.0"), ScriptProtocol.Version));
    }

    [Fact]
    public void ReportsMissingHandshake()
    {
        var error = Assert.IsType<string>(ScriptHandshake.Validate(null, ScriptProtocol.Version));
        Assert.Contains("did not announce", error, StringComparison.Ordinal);
    }

    [Fact]
    public void ReportsBothVersionsOnMismatch()
    {
        var error = Assert.IsType<string>(ScriptHandshake.Validate(new CSharpScriptHello(ScriptProtocol.Version - 1, "10.0", "5.0"), ScriptProtocol.Version));
        Assert.Contains($"v{ScriptProtocol.Version - 1}", error, StringComparison.Ordinal);
        Assert.Contains($"v{ScriptProtocol.Version}", error, StringComparison.Ordinal);
        Assert.Contains("ScriptWorker", error, StringComparison.Ordinal);
    }
}

public sealed class SerialCircuitBreakerTests
{
    [Fact]
    public void StaysClosedBelowThreshold()
    {
        var circuit = new SerialCircuitBreaker(failureThreshold: 3, cooldownMs: 10_000);
        circuit.Fail(1000);
        circuit.Fail(1000);
        Assert.False(circuit.IsCoolingDown(1000));
    }

    [Fact]
    public void OpensAfterConsecutiveFailuresAndExpires()
    {
        var circuit = new SerialCircuitBreaker(failureThreshold: 3, cooldownMs: 10_000);
        for (var attempt = 0; attempt < 3; attempt++) circuit.Fail(1000);

        Assert.True(circuit.IsCoolingDown(1000));
        Assert.Equal(10_000, circuit.RemainingMs(1000));
        Assert.False(circuit.IsCoolingDown(11_000));
    }

    [Fact]
    public void SuccessResetsTheCounter()
    {
        var circuit = new SerialCircuitBreaker(failureThreshold: 3, cooldownMs: 10_000);
        circuit.Fail(1000);
        circuit.Fail(1000);
        circuit.Succeed();
        circuit.Fail(1000);
        Assert.False(circuit.IsCoolingDown(1000));
    }

    [Fact]
    public void CooldownRestartsAfterAnotherCycle()
    {
        var circuit = new SerialCircuitBreaker(failureThreshold: 2, cooldownMs: 5_000);
        circuit.Fail(0); circuit.Fail(0);
        Assert.True(circuit.IsCoolingDown(0));
        Assert.False(circuit.IsCoolingDown(5_001));
        circuit.Fail(6_000); circuit.Fail(6_000);
        Assert.True(circuit.IsCoolingDown(6_000));
    }
}
