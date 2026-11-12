using Scada.Core.Scripting;

namespace Scada.Engine.Tests;

public sealed class ScriptSerialTests
{
    [Fact]
    public void SerialDelegatesToTheWorkerGatewayWithoutLeakingThePayload()
    {
        string? profile = null;
        string? command = null;
        var timeout = 0;
        var context = new ScriptContext(new CSharpScriptRequest("", "run", "context.Serial(\"scanner\", \"start\\r\\n\");"), [],
            (name, text, milliseconds) => { profile = name; command = text; timeout = milliseconds; return "QR123,A"; });

        var reply = context.Serial("scanner", "start\r\n", 1500);

        Assert.Equal("QR123,A", reply);
        Assert.Equal("scanner", profile);
        Assert.Equal("start\r\n", command);
        Assert.Equal(1500, timeout);
        Assert.Contains("Serial scanner 7 chars", context.Trace);
        Assert.DoesNotContain(context.Trace, item => item.Contains("QR123", StringComparison.Ordinal));
        var use = Assert.Single(context.SerialUses);
        Assert.True(use.Ok);
        Assert.Equal("scanner", use.Profile);
    }

    [Fact]
    public void SerialRecordsFailuresForTelemetry()
    {
        var context = new ScriptContext(new CSharpScriptRequest("", "run", ""), [],
            (_, _, _) => throw new InvalidOperationException("port busy"));

        var error = Assert.Throws<InvalidOperationException>(() => context.Serial("scanner", "start"));
        Assert.Equal("port busy", error.Message);
        var use = Assert.Single(context.SerialUses);
        Assert.False(use.Ok);
        Assert.Equal("scanner", use.Profile);
        Assert.Contains("port busy", use.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void SerialClampsTimeoutsAndRejectsOversizedCommands()
    {
        var seen = 0;
        var context = new ScriptContext(new CSharpScriptRequest("", "run", ""), [],
            (_, _, milliseconds) => { seen = milliseconds; return ""; });

        context.Serial("scanner", "start", 10);
        Assert.Equal(50, seen);
        context.Serial("scanner", "start", 90_000);
        Assert.Equal(60_000, seen);

        Assert.Throws<InvalidOperationException>(() => context.Serial("scanner", new string('x', 513)));
        Assert.Throws<InvalidOperationException>(() => context.Serial("scanner", ""));
        Assert.Throws<InvalidOperationException>(() => context.Serial("", "start"));
    }

    [Fact]
    public void SerialFailsWhenTheHostHasNoSerialGateway()
    {
        var context = new ScriptContext(new CSharpScriptRequest("", "run", ""), []);
        Assert.Throws<InvalidOperationException>(() => context.Serial("scanner", "start"));
    }
}
