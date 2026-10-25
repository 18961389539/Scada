using System.Text.Json;
using Scada.Collector.Iec104;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Models;
using Scada.Core.Protocols;
using static Scada.Collector.Iec104.Iec104Types;
using ProtocolType = Scada.Core.Enums.ProtocolType;

namespace Scada.Collector.Tests;

public class Iec104Tests
{
    private static Tag T(int id, string address, TagDataType type, bool writable = false) =>
        new() { Id = id, DeviceId = 1, Name = $"t{id}", Area = TagArea.Address, NodeId = address, DataType = type, Writable = writable };

    private static Device D(int port, object? options = null) => new()
    {
        Id = 1, Name = "rtu", Protocol = ProtocolType.Iec104, Host = "127.0.0.1", Port = port, TimeoutMs = 1500,
        ProtocolOptions = JsonSerializer.Serialize(options ?? new { commonAddress = 7 })
    };

    private static async Task<T> Eventually<T>(Func<Task<T>> read, Func<T, bool> ok, int timeoutMs = 3000)
    {
        var until = Environment.TickCount64 + timeoutMs; T last;
        do { last = await read(); if (ok(last)) return last; await Task.Delay(30); } while (Environment.TickCount64 < until);
        return last;
    }

    [Fact]
    public void AddressAndDeviceValidation()
    {
        Assert.True(Iec104Address.TryParse("M_SP:1001;cmd=C_SC:5001;sbo=0", out var a, out _));
        Assert.Equal(Iec104Monitor.SP, a.Monitor); Assert.Equal(Iec104Command.SC, a.Command); Assert.Equal(5001, a.CommandIoa); Assert.False(a.SelectBeforeOperate);
        Assert.False(Iec104Address.TryParse("M_XX:1", out _, out _));
        Assert.False(Iec104Address.TryParse("M_SP:0", out _, out _));
        Assert.False(Iec104Address.TryParse("M_SP:1;sbo=1", out _, out _));
        var device = new Device { Name = "r", Protocol = ProtocolType.Iec104, Host = "h", Port = 2404 };
        Assert.Null(ConfigurationValidator.Device(device));
        device.ProtocolOptions = "{\"t2Sec\":20}"; Assert.Contains("t2", ConfigurationValidator.Device(device));
        device.ProtocolOptions = "{\"commonAddress\":65535}"; Assert.Contains("公共地址", ConfigurationValidator.Device(device));
        device.ProtocolOptions = "";
        Assert.Contains("Bool", ConfigurationValidator.Tag(T(1, "M_SP:1", TagDataType.Float), device));
        Assert.Contains("cmd", ConfigurationValidator.Tag(T(1, "M_ME_NC:1", TagDataType.Float, true), device));
        Assert.Contains("可写", ConfigurationValidator.Tag(T(1, "M_ME_NC:1;cmd=C_SE_NC:2", TagDataType.Float), device));
        Assert.Contains("Bool", ConfigurationValidator.Tag(T(1, "M_DP:1;cmd=C_DC:2", TagDataType.Int16, true), device));
        Assert.Null(ConfigurationValidator.Tag(T(1, "M_DP:1;cmd=C_DC:2", TagDataType.Bool, true), device));
    }

    [Fact]
    public void Cp56RoundTripAndInvalidFlag()
    {
        var t = new DateTime(2026, 10, 25, 3, 4, 5, 678, DateTimeKind.Utc);
        var bytes = Iec104Codec.Cp56(t);
        Assert.Equal(new DateTimeOffset(t).ToUnixTimeMilliseconds(), Iec104Codec.Cp56Ms(bytes, true));
        bytes[2] |= 0x80; Assert.Null(Iec104Codec.Cp56Ms(bytes, true));
    }

    [Fact]
    public async Task StartupInterrogatesSyncsClockAndMapsQualityAndTimestamps()
    {
        await using var rtu = new Iec104Simulator();
        var ts = new DateTime(2026, 1, 2, 3, 4, 5, 6, DateTimeKind.Utc);
        rtu.Image.AddRange([
            (M_SP_NA, 1001, [0x01]),
            (M_DP_NA, 2001, [0x02]),
            (M_DP_NA, 2002, [0x00]),
            (M_ME_NA, 3001, [0x00, 0x40, 0x00]),             // 0.5
            (M_ME_NB, 3002, Iec104Simulator.Scaled(-123, 0x40)), // NT → Uncertain
            (M_ME_NC, 3003, Iec104Simulator.Float(12.5f)),
            (M_ME_NC, 3004, Iec104Simulator.Float(1f, 0x80)),     // IV → Bad
            (M_IT_NA, 4001, Iec104Simulator.Counter(123456)),
        ]);
        var device = D(rtu.Port, new { commonAddress = 7, counterIntervalSec = 3600 });
        await using var driver = new Iec104Driver();
        await driver.ConnectAsync(device);
        Assert.Contains(U_StartDtAct, rtu.UFrames);
        var plan = new ReadPlan { Tags = [T(1, "M_SP:1001", TagDataType.Bool), T(2, "M_DP:2001", TagDataType.Bool), T(3, "M_DP:2002", TagDataType.Bool),
            T(4, "M_ME_NA:3001", TagDataType.Float), T(5, "M_ME_NB:3002", TagDataType.Int16), T(6, "M_ME_NC:3003", TagDataType.Float),
            T(7, "M_ME_NC:3004", TagDataType.Float), T(8, "M_IT:4001", TagDataType.Int32), T(9, "M_SP:9999", TagDataType.Bool), T(10, "M_DP:2001", TagDataType.UInt16)] };
        var v = (await Eventually(() => driver.ReadAsync(device, plan), _ => driver.GeneralInterrogationComplete)).ToDictionary(x => x.TagId);
        var received = rtu.Received.ToArray();
        Assert.Equal(C_CS_NA, received[0].TypeId); Assert.Equal(C_IC_NA, received[1].TypeId); Assert.Equal(20, received[1].Objects[0].Element[0]);
        Assert.Contains(received, x => x.TypeId == C_CI_NA && x.Objects[0].Element[0] == 5);
        Assert.Equal(1, v[1].Value); Assert.Equal(1, v[2].Value);
        Assert.Equal(TagQuality.Bad, v[3].Quality); Assert.Contains("中间", v[3].Error);
        Assert.Equal(0.5, v[4].Value); Assert.Equal(-123, v[5].Value); Assert.Equal(TagQuality.Uncertain, v[5].Quality);
        Assert.Equal(12.5, v[6].Value); Assert.Equal(TagQuality.Bad, v[7].Quality);
        Assert.Equal(123456, v[8].Value);
        Assert.Equal(TagQuality.Bad, v[9].Quality); Assert.Contains("未上送", v[9].Error);
        Assert.Equal(2, v[10].Value); // 非 Bool 的双点测点给出原始 DPI
        // 自发上送带时标（M_ME_TF）与 SQ=1 顺序信息体
        await rtu.Send(M_ME_TF, CotSpont, 3003, [.. Iec104Simulator.Float(99f)[..5], .. Iec104Codec.Cp56(ts)]);
        await rtu.SendSequence(M_SP_NA, CotSpont, 1001, [0x00], [0x01]);
        v = (await Eventually(() => driver.ReadAsync(device, plan), x => x.Single(y => y.TagId == 6).Value == 99 && x.Single(y => y.TagId == 1).Value == 0)).ToDictionary(x => x.TagId);
        Assert.Equal(new DateTimeOffset(ts).ToUnixTimeMilliseconds(), v[6].SourceTs);
        Assert.Equal(0, v[1].Value);
        // 其他公共地址的数据忽略
        await rtu.Send(M_ME_NC, CotSpont, 3003, Iec104Simulator.Float(-1f), ca: 8);
        await Task.Delay(100);
        Assert.Equal(99, (await driver.ReadAsync(device, plan)).Single(x => x.TagId == 6).Value);
    }

    [Fact]
    public async Task SelectBeforeOperateCommandsAndRejections()
    {
        await using var rtu = new Iec104Simulator();
        rtu.Image.Add((M_SP_NA, 1001, [0x00]));
        rtu.OnExecute = (type, ioa, e) => { if (type == C_SC_NA) _ = rtu.Send(M_SP_NA, CotSpont, 1001, [(byte)(e[0] & 1)]); };
        var device = D(rtu.Port);
        await using var driver = new Iec104Driver();
        await driver.ConnectAsync(device);
        var breaker = T(1, "M_SP:1001;cmd=C_SC:5001", TagDataType.Bool, true);
        await driver.WriteAsync(device, breaker, 1);
        var commands = rtu.Received.Where(x => x.TypeId == C_SC_NA).ToArray();
        Assert.Equal(2, commands.Length);
        Assert.Equal(0x81, commands[0].Objects[0].Element[0]); // SELECT, SCS=1
        Assert.Equal(0x01, commands[1].Objects[0].Element[0]); // EXECUTE
        Assert.Equal(5001, commands[1].Objects[0].Ioa);
        Assert.Equal(1, (await Eventually(() => driver.ReadAsync(device, new ReadPlan { Tags = [breaker] }), x => x[0].Value == 1))[0].Value);

        // 直接执行（sbo=0）设定值
        float? setpoint = null;
        rtu.OnExecute = (type, _, e) => { if (type == C_SE_NC) setpoint = BitConverter.ToSingle(e, 0); };
        await driver.WriteAsync(device, T(2, "M_ME_NC:3001;cmd=C_SE_NC:6001;sbo=0", TagDataType.Float, true), 42.5);
        Assert.Equal(42.5f, setpoint);
        Assert.Single(rtu.Received, x => x.TypeId == C_SE_NC);

        // 子站否定选择 → 不执行
        rtu.RejectSelect = true;
        var rejected = await Assert.ThrowsAsync<InvalidOperationException>(() => driver.WriteAsync(device, breaker, 0));
        Assert.Contains("未执行", rejected.Message);
        Assert.Equal(3, rtu.Received.Count(x => x.TypeId == C_SC_NA));
        rtu.RejectSelect = false;

        // 无 ACTTERM → 结果未知
        rtu.DropActTerm = true;
        var unknown = await Assert.ThrowsAsync<IOException>(() => driver.WriteAsync(device, T(3, "M_DP:2001;cmd=C_DC:5002", TagDataType.Bool, true), 1));
        Assert.Contains("结果未知", unknown.Message);
        Assert.Equal(0x82, rtu.Received.Last(x => x.TypeId == C_DC_NA).Objects[0].Element[0] | 0x80); // DCO=2 (ON)

        // 执行无 ACTCON → 结果未知（TimeoutException）
        rtu.DropActTerm = false; rtu.DropExecuteCon = true;
        await Assert.ThrowsAsync<TimeoutException>(() => driver.WriteAsync(device, breaker, 1));
        Assert.Throws<InvalidOperationException>(() => Iec104Driver.Encode(Iec104Command.SE_NA, 1.5));
    }

    [Fact]
    public async Task AcknowledgesWithSFramesAndDetectsDeadLinkViaTestFrame()
    {
        await using var rtu = new Iec104Simulator();
        var device = D(rtu.Port, new { commonAddress = 7, w = 2, k = 12, t1Sec = 2, t2Sec = 1, t3Sec = 1, clockSync = false });
        await using var driver = new Iec104Driver();
        await driver.ConnectAsync(device);
        for (var i = 0; i < 4; i++) await rtu.Send(M_SP_NA, CotSpont, 1, [0x01]);
        await Eventually(() => Task.FromResult(rtu.SFrames), x => x >= 2);
        Assert.True(rtu.SFrames >= 2); // w=2：每两个 I 帧确认一次（GI 的 ACTCON/ACTTERM 也计入）
        await Eventually(() => Task.FromResult(rtu.UFrames.Contains(U_TestFrAct)), x => x, 3000);
        Assert.Contains(U_TestFrAct, rtu.UFrames);
        Assert.True(driver.IsConnected);
        rtu.IgnoreTestFr = true;
        Assert.False(await Eventually(() => Task.FromResult(driver.IsConnected), x => !x, 6000));
        Assert.Contains("TESTFR", driver.LastError);
    }

    [Fact]
    public async Task SequenceErrorClosesConnectionAndReconnectRestartsInterrogation()
    {
        await using var rtu = new Iec104Simulator();
        var device = D(rtu.Port);
        await using var driver = new Iec104Driver();
        await driver.ConnectAsync(device);
        rtu.SkipSendSequence();
        await rtu.Send(M_SP_NA, CotSpont, 1, [0x01]);
        Assert.False(await Eventually(() => Task.FromResult(driver.IsConnected), x => !x));
        Assert.Contains("序号", driver.LastError);
        await Assert.ThrowsAsync<IOException>(() => driver.ReadAsync(device, new ReadPlan { Tags = [T(1, "M_SP:1", TagDataType.Bool)] }));
        await driver.ConnectAsync(device);
        Assert.True(driver.IsConnected);
        Assert.Equal(2, await Eventually(() => Task.FromResult(rtu.Received.Count(x => x.TypeId == C_IC_NA)), x => x == 2));
    }
}
