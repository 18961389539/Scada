using Scada.Collector.Modbus;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Models;

namespace Scada.Collector.Tests;

public class ProtocolTests
{
    [Fact]
    public void RtuKnownCrcAndFragmentedReads()
    {
        Assert.Equal("01030000000AC5CD", Convert.ToHexString(RtuFrame.AppendCrc(Convert.FromHexString("01030000000A"))));
        using var port = new FakeSerial(RtuFrame.AppendCrc([1, 3, 4, 0x12, 0x34, 0xab, 0xcd]));
        Assert.Equal(new byte[] { 0x12, 0x34, 0xab, 0xcd }, RtuFrame.Read(port, 1, 3, 100, 2));
        Assert.Equal("01030064000285D4", Convert.ToHexString(Assert.Single(port.Requests)));
    }

    [Theory]
    [InlineData("01030212340000", "CRC")]
    [InlineData("0203021234", "站号")]
    [InlineData("0104021234", "功能码")]
    [InlineData("0103011234", "字节数")]
    public void RtuRejectsCorruptedOrMismatchedResponses(string hex, string error)
    {
        using var port = new FakeSerial(Convert.FromHexString(hex));
        Assert.Contains(error, Assert.Throws<IOException>(() => RtuFrame.Read(port, 1, 3, 0, 1)).Message);
        Assert.Single(port.Requests); // Never resend, including when a write acknowledgement is lost.
    }

    [Fact]
    public void RtuExceptionAndTruncatedFrameAreRejected()
    {
        using var exception = new FakeSerial(RtuFrame.AppendCrc([1, 0x83, 2]));
        Assert.Contains("0x02", Assert.Throws<IOException>(() => RtuFrame.Read(exception, 1, 3, 0, 1)).Message);
        using var truncated = new FakeSerial([1, 3, 2, 0]);
        Assert.Throws<EndOfStreamException>(() => RtuFrame.Read(truncated, 1, 3, 0, 1));
    }

    [Fact]
    public void RtuWritesValidateEchoAndDoNotBroadcastOrRetry()
    {
        using var coil = new FakeSerial(RtuFrame.AppendCrc([7, 5, 0, 4, 0xff, 0]));
        RtuFrame.WriteCoil(coil, 7, 4, true);
        Assert.Equal(RtuFrame.AppendCrc([7, 5, 0, 4, 0xff, 0]), Assert.Single(coil.Requests));
        using var registers = new FakeSerial(RtuFrame.AppendCrc([7, 16, 0, 10, 0, 2]));
        RtuFrame.WriteRegisters(registers, 7, 10, [0x41, 0x20, 0, 0]);
        Assert.Equal(new byte[] { 7, 16, 0, 10, 0, 2, 4, 0x41, 0x20, 0, 0 }, Assert.Single(registers.Requests)[..11]);
        using var mismatch = new FakeSerial(RtuFrame.AppendCrc([7, 5, 0, 5, 0xff, 0]));
        Assert.Throws<IOException>(() => RtuFrame.WriteCoil(mismatch, 7, 4, true)); Assert.Single(mismatch.Requests);
        using var broadcast = new FakeSerial([]);
        Assert.Throws<ArgumentOutOfRangeException>(() => RtuFrame.WriteCoil(broadcast, 0, 4, true)); Assert.Empty(broadcast.Requests);
    }

    [Fact]
    public async Task SerialBusSerializesStationsAndExpiredQueuedWorkNeverOpensPort()
    {
        var factory = new FakeFactory(); var buses = new SerialBusPool(factory);
        var device = new Device { SerialPort = "COM7", TimeoutMs = 1000 };
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = buses.ExecuteAsync(device, _ => { started.SetResult(); release.Task.GetAwaiter().GetResult(); return 1; }, default);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var queued = device.Clone(); queued.StationNumber = 2; queued.TimeoutMs = 100;
        await Assert.ThrowsAsync<TimeoutException>(() => buses.ExecuteAsync(queued, _ => 2, default));
        Assert.Equal(1, factory.Created);
        release.SetResult(); Assert.Equal(1, await first);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => buses.ExecuteAsync(device, _ => { Thread.Sleep(5); return 0; }, default)));
        Assert.Equal(1, factory.MaxActive); Assert.Equal(0, factory.Active);
    }

    [Fact]
    public async Task RtuDriverUsesStationAndByteOrderForPollingAndWrites()
    {
        var factory = new FakeFactory { Response = RtuFrame.AppendCrc([4, 3, 2, 0x34, 0x12]) };
        var driver = new ModbusRtuDriver(new SerialBusPool(factory));
        var device = new Device { Id = 1, Protocol = ProtocolType.ModbusRtu, StationNumber = 4, ByteOrder = ByteOrder.BADC };
        var tag = new Tag { Id = 9, DeviceId = 1, Name = "word", DataType = TagDataType.UInt16, Address = 10, Writable = true };
        await driver.ConnectAsync(device);
        var values = await driver.ReadAsync(device, new ReadPlanBuilder().Build(device, [tag]));
        Assert.Equal(0x1234, Assert.Single(values).Value); Assert.True(driver.IsConnected);
        factory.Response = RtuFrame.AppendCrc([4, 16, 0, 10, 0, 1]);
        await driver.WriteAsync(device, tag, 0x1234);
        Assert.Equal(new byte[] { 0x34, 0x12 }, factory.Last!.Requests.Single()[7..9]);
        factory.Response = [];
        await Assert.ThrowsAsync<IOException>(() => driver.ReadAsync(device, new ReadPlanBuilder().Build(device, [tag])));
        Assert.False(driver.IsConnected); Assert.Contains("地址 10", driver.LastError);
    }

    [Fact]
    public void S7PlanUsesByteOffsetsAndSeparatesAreasAndDataBlocks()
    {
        var device = new Device { Id = 1, Protocol = ProtocolType.SiemensS7, ByteOrder = ByteOrder.DCBA };
        Tag Tag(int id, int address, TagArea area = TagArea.DataBlock, int db = 1, TagDataType type = TagDataType.Float) =>
            new() { Id = id, DeviceId = 1, Name = $"t{id}", Address = address, Area = area, DbNumber = db, DataType = type };
        var tags = new[] { Tag(1, 1), Tag(2, 5, type: TagDataType.Bool), Tag(3, 5, type: TagDataType.Bool), Tag(4, 1, db: 2), Tag(5, 1, TagArea.Memory) };
        tags[2].BitOffset = 7;
        var plan = new ReadPlanBuilder().Build(device, tags);
        var block = plan.Blocks.First(); Assert.Equal(3, plan.Blocks.Count); Assert.Equal(1, block.StartAddress); Assert.Equal(5, block.Length);
        Assert.Equal(new[] { 0, 4, 4 }, block.Tags.Select(x => x.Offset)); Assert.Equal(7, block.Tags[2].BitOffset);
        Assert.All(block.Tags, x => Assert.Equal(ByteOrder.ABCD, x.ByteOrder));
        var many = Enumerable.Range(0, 120).Select(i => Tag(i + 1, i * 4)).ToList();
        Assert.All(new ReadPlanBuilder().Build(device, many).Blocks, x => Assert.InRange(x.Length, 1, 200));
    }

    [Fact]
    public void ProtocolConfigurationRejectsWrongAreasAndAddresses()
    {
        var s7 = new Device { Protocol = ProtocolType.SiemensS7, Name = "S7" };
        var tag = new Tag { DeviceId = 1, Name = "tag", Area = TagArea.DataBlock, DataType = TagDataType.Float, Address = 65533 };
        Assert.NotNull(ConfigurationValidator.Tag(tag, s7)); tag.Address = 100; Assert.Null(ConfigurationValidator.Tag(tag, s7));
        Assert.NotNull(ConfigurationValidator.Tag(tag, new Device()));
        tag.Area = TagArea.Input; tag.Writable = true; Assert.NotNull(ConfigurationValidator.Tag(tag, s7));
        tag.Writable = false; tag.BitOffset = 1; Assert.NotNull(ConfigurationValidator.Tag(tag, s7));
        tag.DataType = TagDataType.Bool; Assert.Null(ConfigurationValidator.Tag(tag, s7));
        Assert.NotNull(ConfigurationValidator.Device(new Device { Protocol = (ProtocolType)99, Name = "invalid" }));
        Assert.NotNull(ConfigurationValidator.Device(new Device { Protocol = ProtocolType.ModbusRtu, Name = "broadcast", StationNumber = 0 }));
    }

    private sealed class FakeSerial(byte[] response, Action? onOpen = null, Action? onDispose = null) : ISerialConnection
    {
        public List<byte[]> Requests { get; } = [];
        private int _offset;
        public void Open() => onOpen?.Invoke(); public void Close() { } public void DiscardInput() { }
        public void Write(byte[] bytes) => Requests.Add(bytes);
        public int Read(byte[] bytes, int offset, int count)
        { var received = Math.Min(Math.Min(count, 1), response.Length - _offset); response.AsSpan(_offset, received).CopyTo(bytes.AsSpan(offset)); _offset += received; return received; }
        public void Dispose() => onDispose?.Invoke();
    }
    private sealed class FakeFactory : ISerialConnectionFactory
    {
        public int Created; public int Active; public int MaxActive;
        public byte[] Response = []; public FakeSerial? Last;
        public ISerialConnection Create(Device device)
        {
            Interlocked.Increment(ref Created);
            Last = new FakeSerial(Response, () => { var n = Interlocked.Increment(ref Active); MaxActive = Math.Max(MaxActive, n); }, () => Interlocked.Decrement(ref Active));
            return Last;
        }
    }
}
