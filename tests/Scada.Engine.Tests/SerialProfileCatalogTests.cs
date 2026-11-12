using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Scada.Core.Entities;
using Scada.Core.Scripting;
using Scada.Engine.Calculations;

namespace Scada.Engine.Tests;

public sealed class SerialProfileCatalogTests
{
    [Fact]
    public async Task DatabaseProfilesOverrideConfigurationAndReloadAfterInvalidation()
    {
        var path = Path.Combine(Path.GetTempPath(), "serial-profiles-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            var factory = new CalcJobTests.FileFactory(path);
            await using (var db = factory.CreateDbContext())
            {
                await db.Database.EnsureCreatedAsync();
                db.SerialProfiles.Add(new SerialProfileConfig { Name = "scanner", Port = "COM9", Enabled = true, Revision = 1 });
                db.SerialProfiles.Add(new SerialProfileConfig { Name = "disabled", Port = "COM8", Enabled = false, Revision = 1 });
                await db.SaveChangesAsync();
            }
            var options = Options.Create(new CSharpScriptOptions
            {
                SerialProfiles = new()
                {
                    ["scanner"] = new SerialProfile { Port = "COM1" },
                    ["printer"] = new SerialProfile { Port = "COM2" }
                }
            });
            var catalog = new SerialProfileCatalog(factory, options, NullLogger<SerialProfileCatalog>.Instance);

            var snapshot = await catalog.GetAsync();
            Assert.Equal("COM9", snapshot.Profiles["scanner"].Port);
            Assert.Equal("database", snapshot.Sources["scanner"]);
            Assert.Equal("config", snapshot.Sources["printer"]);
            Assert.Equal(1, snapshot.DatabaseCount);
            Assert.False(snapshot.Profiles.ContainsKey("disabled"));

            await using (var db = factory.CreateDbContext())
            {
                var row = await db.SerialProfiles.SingleAsync(item => item.Name == "scanner");
                row.Port = "COM7";
                await db.SaveChangesAsync();
            }
            catalog.Invalidate();

            var reloaded = await catalog.GetAsync();
            Assert.Equal("COM7", reloaded.Profiles["scanner"].Port);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void TelemetryKeepsOnlyTheLatestCallPerProfile()
    {
        var telemetry = new SerialProfileTelemetry();
        telemetry.Record([new ScriptSerialUse("scanner", true, 12)]);
        telemetry.Record([new ScriptSerialUse("SCANNER", false, 34, "busy")]);

        var snapshot = telemetry.Snapshot();
        var usage = Assert.Single(snapshot);
        Assert.Equal("SCANNER", usage.Key, ignoreCase: true);
        Assert.False(usage.Value.Ok);
        Assert.Equal("busy", usage.Value.Error);
        Assert.Equal(34d, usage.Value.ElapsedMs);
    }

    [Theory]
    [InlineData("scanner", "COM3", 9600, 8, 2000, 4096, "utf-8", "\r\n", true)]
    [InlineData("", "COM3", 9600, 8, 2000, 4096, "utf-8", "\r\n", false)]
    [InlineData("scanner", "", 9600, 8, 2000, 4096, "utf-8", "\r\n", false)]
    [InlineData("scanner", "COM3", 100, 8, 2000, 4096, "utf-8", "\r\n", false)]
    [InlineData("scanner", "COM3", 9600, 8, 10, 4096, "utf-8", "\r\n", false)]
    [InlineData("scanner", "COM3", 9600, 8, 2000, 5000, "utf-8", "\r\n", false)]
    [InlineData("scanner", "COM3", 9600, 8, 2000, 4096, "gbk", "\r\n", false)]
    [InlineData("scanner", "COM3", 9600, 8, 2000, 4096, "utf-8", "|", false)]
    public void ValidationUsesSharedBounds(string name, string port, int baud, int dataBits, int timeout, int maxReply,
        string encoding, string newLine, bool expected)
    {
        var profile = new SerialProfile
        {
            Port = port, BaudRate = baud, DataBits = dataBits, ReadTimeoutMs = timeout,
            MaxReplyChars = maxReply, Encoding = encoding, NewLine = newLine
        };
        Assert.Equal(expected, SerialProfileRules.Validate(name, profile) is null);
    }
}
