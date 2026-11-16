using System.Text.Json;
using Scada.Core.Models;

namespace Scada.Core.Tests;

public class ScreenNodeRegistryTests
{
    [Fact]
    public void RegistryMatchesValidatorTypes()
    {
        Assert.Equal(49, ScreenNodeRegistry.Types.Count);
        Assert.True(ScreenNodeRegistry.Types.SetEquals(ScreenValidator.NodeTypes));
    }

    [Fact]
    public void EveryScreenNodePropertyBelongsToExactlyOneFieldGroup()
    {
        var groups = new[]
        {
            ScreenNodeRegistry.VerbatimFields, ScreenNodeRegistry.TransformedFields, ScreenNodeRegistry.ClearedFields
        };
        var properties = typeof(ScreenNode).GetProperties();

        foreach (var property in properties)
            Assert.True(groups.Count(group => group.Contains(property.Name)) == 1,
                $"{property.Name} 必须恰好属于原样复制 / 变换 / 清空 中的一组");

        Assert.Equal(properties.Length, groups.Sum(group => group.Count));
    }

    [Fact]
    public void TypesMatchSharedFixture()
    {
        // 前后端各有一份类型清单；共享 fixture 是跨语言契约，任一侧增减类型都会失败。
        var path = Path.Combine(Repo.Root(), "tests", "fixtures", "screen-node-types.json");
        var types = JsonSerializer.Deserialize<string[]>(File.ReadAllText(path))!;

        Assert.Equal(49, types.Length);
        Assert.Equal(types.Order(StringComparer.Ordinal), ScreenNodeRegistry.Types.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void FeatureFlagsDriveFieldCapabilities()
    {
        Assert.True(ScreenNodeRegistry.Has("gauge", ScreenNodeFeature.GaugeBand | ScreenNodeFeature.Setpoint));
        Assert.True(ScreenNodeRegistry.Has("dial", ScreenNodeFeature.GaugeBand | ScreenNodeFeature.Setpoint));
        Assert.False(ScreenNodeRegistry.Has("text", ScreenNodeFeature.GaugeBand));

        Assert.True(ScreenNodeRegistry.Has("history-table", ScreenNodeFeature.Window | ScreenNodeFeature.TableColumns));
        Assert.True(ScreenNodeRegistry.Has("chart-line", ScreenNodeFeature.ChartOptions | ScreenNodeFeature.ChartTargetLines));
        Assert.False(ScreenNodeRegistry.Has("chart-pie", ScreenNodeFeature.ChartTargetLines));
        Assert.False(ScreenNodeRegistry.Has("alarm-stats", ScreenNodeFeature.ChartOptions));

        Assert.True(ScreenNodeRegistry.Has("jog", ScreenNodeFeature.Momentary | ScreenNodeFeature.RequiresCommand));
        Assert.True(ScreenNodeRegistry.Has("knob", ScreenNodeFeature.Positional | ScreenNodeFeature.RequiresCommand));
        Assert.False(ScreenNodeRegistry.Has("switch", ScreenNodeFeature.Momentary));
        Assert.True(ScreenNodeRegistry.Has("template", ScreenNodeFeature.TemplateInstance | ScreenNodeFeature.Placeholder));
        Assert.False(ScreenNodeRegistry.Has(null, ScreenNodeFeature.TemplateInstance));
        Assert.False(ScreenNodeRegistry.Has("unknown", ScreenNodeFeature.TemplateInstance));
    }

    [Fact]
    public void TableColumnsOnlyExposeKnownColumnSets()
    {
        Assert.Equal(["name", "value", "unit"], ScreenNodeRegistry.TableColumns("table"));
        Assert.Equal(["name", "last", "min", "max", "avg"], ScreenNodeRegistry.TableColumns("history-table"));
        Assert.Null(ScreenNodeRegistry.TableColumns("text"));
        Assert.Null(ScreenNodeRegistry.TableColumns(null));
    }

}
