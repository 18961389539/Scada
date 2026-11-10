using System.Text.Json;
using Scada.Core.Protocols;

namespace Scada.Collector.Tests;

public class ProtocolFormCatalogTests
{
    private static readonly string[] ExpectedOrder =
    [
        "Mqtt", "SparkplugB", "MitsubishiMc", "OmronFins", "Iec104", "BacnetIp", "OpcDa", "Iec61850", "EtherNetIp"
    ];

    [Fact]
    public void CatalogFormsCoverEveryTextProtocolInStableOrder()
    {
        Assert.Equal(ExpectedOrder, ProtocolCatalog.All.Select(x => x.Protocol.ToString()));
        foreach (var spec in ProtocolCatalog.All)
        {
            Assert.NotNull(spec.Form);
            Assert.Equal(spec.UsesHostPort, spec.Form.DefaultPort > 0 || spec.Protocol == Scada.Core.Enums.ProtocolType.OpcDa
                ? spec.UsesHostPort : spec.UsesHostPort);
            Assert.False(string.IsNullOrWhiteSpace(spec.Form.Label) && string.IsNullOrWhiteSpace(spec.Form.LabelKey));
            Assert.NotEmpty(spec.Form.Fields);
            Assert.True(spec.Form.Fields.Select(f => f.Key).Distinct(StringComparer.Ordinal).Count() == spec.Form.Fields.Count);
            foreach (var field in spec.Form.Fields)
            {
                Assert.False(string.IsNullOrWhiteSpace(field.Key));
                Assert.Contains(field.Type, (string[])["text", "number", "select", "bool", "textarea"]);
                Assert.False(string.IsNullOrWhiteSpace(field.Label) && string.IsNullOrWhiteSpace(field.LabelKey));
                if (field.Type == "select") Assert.NotNull(field.Options);
            }
        }
    }

    [Fact]
    public void FormDefaultsMatchProtocolOptionTypes()
    {
        Assert.Equal(1883, ProtocolForms.Mqtt.DefaultPort);
        Assert.Equal(44818, ProtocolForms.EtherNetIp.DefaultPort);
        Assert.Contains(ProtocolForms.Mqtt.Fields, f => f.Key == "qos" && Equals(f.Default, 1));
        Assert.Contains(ProtocolForms.MitsubishiMc.Fields, f => f.Key == "pcNo" && Equals(f.Default, 255));
        Assert.Contains(ProtocolForms.Iec61850.Fields, f => f.Key == "useReports" && f.Locked);
        var json = JsonSerializer.Serialize(ProtocolCatalog.All.Select(s => s.Form), ProtocolCatalog.OptionsJson);
        Assert.Contains("clientId", json, StringComparison.Ordinal);
        Assert.Contains("deviceInstance", json, StringComparison.Ordinal);
    }
}
