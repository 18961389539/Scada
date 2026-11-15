using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Models;

namespace Scada.Core.Tests;

public class ConfigurationValidationTests
{
    [Fact]
    public void DeviceIssuesAggregateIndependentProblems()
    {
        var device = new Device { Name = "", PollIntervalMs = 10, TimeoutMs = 10 };

        var issues = ConfigurationValidator.DeviceIssues(device);

        Assert.Contains(issues, x => x.Code == "device.name" && x.Field == nameof(Device.Name));
        Assert.Contains(issues, x => x.Code == "device.polling" && x.Field == nameof(Device.PollIntervalMs));
        // 旧入口保持"第一条文案"的语义，调用点的 `is string error` 不需要改。
        Assert.Equal(issues[0].Message, ConfigurationValidator.Device(device));
    }

    [Fact]
    public void DeviceIssuesReportProtocolSpecErrorWithCode()
    {
        var device = new Device
        {
            Id = 1,
            Name = "汇川",
            Protocol = ProtocolType.Inovance,
            ProtocolOptions = """{"series":"H3U","transport":"Tcp","maxGap":16}""",
            Host = "127.0.0.1",
            Port = 502
        };
        var tag = new Tag
        {
            Kind = TagKind.Physical, DeviceId = 1, Name = "t", Area = TagArea.Address,
            NodeId = "D999999", DataType = TagDataType.Int16
        };

        var issues = ConfigurationValidator.TagIssues(tag, device);

        var issue = Assert.Single(issues);
        Assert.Equal("tag.address.Inovance", issue.Code);
        Assert.Contains("超出范围", issue.Message);
    }

    [Fact]
    public void StringTagReportsEveryInvalidOption()
    {
        var tag = new Tag
        {
            Kind = TagKind.Physical, DeviceId = 1, Name = "t", Area = TagArea.HoldingRegister,
            DataType = TagDataType.String, TextLength = 0, Scale = 2, Deadband = 1, MinValue = 0, MaxValue = 10
        };

        var issues = ConfigurationValidator.TagIssues(tag);

        Assert.Contains(issues, x => x.Code == "tag.text.length");
        Assert.Contains(issues, x => x.Code == "tag.text.scale");
        Assert.Contains(issues, x => x.Code == "tag.text.deadband");
        Assert.Contains(issues, x => x.Code == "tag.text.range");
    }

    [Fact]
    public void CalculatedTagReportsExpressionError()
    {
        var tag = new Tag
        {
            Kind = TagKind.Calculated, Name = "calc", DataType = TagDataType.Double,
            Expression = "#1 +", CalculationIntervalMs = 1000, InputMaxAgeMs = 5000
        };

        var issues = ConfigurationValidator.TagIssues(tag);

        Assert.Contains(issues, x => x.Code == "tag.calculated.expression" && x.Field == nameof(Tag.Expression));
    }

    [Fact]
    public void SerialValidationIsSharedByEveryCaller()
    {
        // 同一句文案原来在通道、设备、汇川、信捷、台达各写一遍；现在只允许有一处实现。
        Assert.Equal("串口名称无效", Scada.Core.Protocols.SerialSettings.ValidatePortName(""));
        Assert.Equal("串口名称无效", Scada.Core.Protocols.SerialSettings.Validate("", 9600, 8, SerialParity.None, SerialStopBits.One));
        Assert.Equal("串口通信参数无效，Modbus RTU 使用 8 个数据位",
            Scada.Core.Protocols.SerialSettings.Validate("COM3", 9600, 7, SerialParity.None, SerialStopBits.One));
        Assert.Null(Scada.Core.Protocols.SerialSettings.Validate("COM3", 9600, 8, SerialParity.None, SerialStopBits.One));
        Assert.Equal("RTU 采集站号须在 1～247 之间，不支持广播写入", Scada.Core.Protocols.SerialSettings.ValidateStation(0));
    }

    [Fact]
    public void ChannelIssuesReportHostAndTargetSeparately()
    {
        var channel = new AcquisitionChannel { Name = "ch", Protocol = ProtocolType.ModbusTcp, Host = "8.8.8.8", Port = 502 };

        var issues = ConfigurationValidator.ChannelIssues(channel, new Scada.Core.Options.ScadaSettings());

        Assert.Contains(issues, x => x.Code == "channel.target" && x.Field == nameof(AcquisitionChannel.Host));
    }
}
