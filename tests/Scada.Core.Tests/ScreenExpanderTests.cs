using System.Text.Json;
using Scada.Core.Models;

namespace Scada.Core.Tests;

public class ScreenExpanderTests
{
    private static readonly HashSet<string> NotCopiedVerbatim = new(StringComparer.Ordinal)
    {
        // 位置与尺寸按实例框缩放。
        nameof(ScreenNode.Id), nameof(ScreenNode.X), nameof(ScreenNode.Y), nameof(ScreenNode.Width), nameof(ScreenNode.Height),
        // 编辑态字段：展开后的节点不再被设计器操作。
        nameof(ScreenNode.Locked), nameof(ScreenNode.GroupId),
        // 模板占位符在展开时被解析成具体测点，不再原样保留。
        nameof(ScreenNode.Bind), nameof(ScreenNode.AlarmBind), nameof(ScreenNode.TagBinds), nameof(ScreenNode.AnimationBind),
        nameof(ScreenNode.TagId), nameof(ScreenNode.AlarmTagId), nameof(ScreenNode.Action), nameof(ScreenNode.Commands),
        // 端口节点号被重写为“实例--内部”。
        nameof(ScreenNode.Source), nameof(ScreenNode.Target)
    };

    private static HmiTemplateDefinition Template(ScreenNode inner) => new("tpl", 100, 100, [inner], []);

    private static ScreenDocument Document(ScreenNode node) => new() { Width = 800, Height = 600, Nodes = [node] };

    [Fact]
    public void HiddenTemplateInstanceIsNotExpandedAtAll()
    {
        var instance = new ScreenNode { Id = "inst", Type = "template", TemplateKey = "tpl", Width = 100, Height = 100, Hidden = true };

        var expanded = ScreenExpander.Expand(Document(instance), _ => Template(new ScreenNode { Id = "inner", Type = "value" }));

        // 运行时按 hidden 过滤（ScreenCanvas）；展开后实例节点本身消失，
        // 若继续展开内部图元，组态时隐藏的模板会在发布后整块显示出来。
        Assert.Empty(expanded.Nodes);
    }

    [Fact]
    public void HiddenChildOfTemplateStaysHidden()
    {
        var instance = new ScreenNode { Id = "inst", Type = "template", TemplateKey = "tpl", Width = 100, Height = 100 };
        var inner = new ScreenNode { Id = "inner", Type = "value", Hidden = true };

        var expanded = ScreenExpander.Expand(Document(instance), _ => Template(inner));

        Assert.True(Assert.Single(expanded.Nodes).Hidden);
    }

    [Fact]
    public void ExpandCopiesEveryRuntimeField()
    {
        var inner = new ScreenNode { Id = "inner", Type = "gauge" };
        Fill(inner);
        var instance = new ScreenNode
        {
            Id = "inst", Type = "template", TemplateKey = "tpl", Width = 100, Height = 100,
            Bindings = [new ScreenBinding { Key = "cmd", TagId = 9 }, new ScreenBinding { Key = "act", TagId = 12 }]
        };

        var expanded = ScreenExpander.Expand(Document(instance), _ => Template(inner));
        var node = Assert.Single(expanded.Nodes);

        foreach (var property in typeof(ScreenNode).GetProperties().Where(p => p.CanRead && !NotCopiedVerbatim.Contains(p.Name)))
        {
            var expected = Json(property.GetValue(inner));
            var actual = Json(property.GetValue(node));
            Assert.True(expected == actual, $"Expand 漏掉了 {property.Name}：模板内 {expected}，展开后 {actual}");
        }

        // 占位符解析到具体测点：这是展开的核心语义，单独断言。
        Assert.Equal(9, Assert.Single(node.Commands!).TagId);
        Assert.Null(node.Commands![0].Bind);
        Assert.Equal(12, node.Action.TagId);
        Assert.Null(node.Action.Bind);
    }

    [Fact]
    public void ExpandedNodeDoesNotShareMutableCollectionsWithTemplate()
    {
        var inner = new ScreenNode { Id = "inner", Type = "polyline", Points = [new(0, 0), new(10, 10)] };
        var instance = new ScreenNode { Id = "inst", Type = "template", TemplateKey = "tpl", Width = 100, Height = 100 };

        var expanded = ScreenExpander.Expand(Document(instance), _ => Template(inner));
        expanded.Nodes[0].Points!.Add(new(5, 5));

        // 发布物与图库模板共享 List 时，后续对模板的编辑会“隔着引用”改掉已发布的画面。
        Assert.Equal(2, inner.Points!.Count);
    }

    private static string Json(object? value) => JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    private static void Fill(ScreenNode n)
    {
        n.Text = "txt";
        n.Color = "#101010";
        n.ActiveColor = "#202020";
        n.Unit = "kW";
        n.Decimals = 2;
        n.Min = -10;
        n.Max = 90;
        n.ActiveThreshold = 0.75;
        n.Rotation = 15;
        n.Symbol = "isa:pump";
        n.FontSize = 22;
        n.Align = "center";
        n.Wrap = true;
        n.AutoFit = false;
        n.Bold = true;
        n.Opacity = 0.8;
        n.BorderWidth = 2;
        n.Radius = 6;
        n.Hidden = true;
        n.Tags = [3, 4];
        n.WindowSec = 900;
        n.LineId = 5;
        n.Points = [new(1, 2), new(3, 4)];
        n.States = [new(0, "停", "#111111"), new(1, "运行", "#222222")];
        n.Commands = [new ScreenPanelCommand { Key = "start", Label = "启动", Bind = "cmd", Value = 1 }];
        n.ReleaseValue = 0;
        n.PulseMs = 500;
        n.MaxHoldMs = 5000;
        n.ResetMode = "tag";
        n.SilenceMinutes = 10;
        n.RecipeTemplateId = 3;
        n.GaugeBands = [new(0, 50, "#00ff00")];
        n.Setpoint = 55;
        n.TableColumns = ["name", "value"];
        n.ChartOptions = new ScreenChartOptions
        {
            Legend = true, Grid = false, DataLabels = true, YUnit = "kW",
            TargetLines = [new ScreenTargetLine(60, "#ff0000", "上限")]
        };
        n.Texts = new Dictionary<string, string> { ["en-US"] = "Temperature" };
        n.Animation = new ScreenAnimation { Type = "level", Source = "value", TagId = 11, From = 0, To = 100, OutFrom = 0, OutTo = 100 };
        n.Action = new ScreenAction { Type = "command", Bind = "act", Value = 3 };
    }
}
