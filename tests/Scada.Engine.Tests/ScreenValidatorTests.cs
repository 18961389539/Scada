using Scada.Core.Entities;
using Scada.Core.Models;

namespace Scada.Engine.Tests;

public class ScreenValidatorTests
{
    private static ScreenNode Node() => new() { Id = "pump-1", Type = "pump", Width = 140, Height = 100 };

    [Fact]
    public void EmptyAndAllSupportedShapes_AreValid()
    {
        Assert.Empty(ScreenValidator.Validate(new ScreenDocument()));
        foreach (var type in ScreenValidator.NodeTypes)
        {
            var action = type is "switch" or "slider" or "spinner" or "input" ? new ScreenAction { Type = "command", TagId = 1 } : new ScreenAction();
            Assert.Empty(ScreenValidator.Validate(new ScreenDocument { Nodes = [new ScreenNode { Id = type, Type = type, Action = action, Symbol = type == "symbol" ? "isa:pump" : null, TemplateKey = type == "template" ? "pump" : null }] }));
        }
    }

    [Fact]
    public void RejectsDuplicateIdsAndNodesOutsideCanvas()
    {
        var doc = new ScreenDocument { Nodes = [Node(), Node()] };
        doc.Nodes[1].X = 1199;
        var issues = ScreenValidator.Validate(doc);
        Assert.Contains(issues, x => x.Message.Contains("重复"));
        Assert.Contains(issues, x => x.Message.Contains("超出画布"));
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("url(https://example.com)")]
    [InlineData("red; background: url(x)")]
    public void RejectsActiveContentInColorFields(string color)
    {
        var node = Node(); node.Color = color;
        Assert.NotEmpty(ScreenValidator.Validate(new ScreenDocument { Nodes = [node] }));
    }

    [Fact]
    public void PublishRejectsMissingBindingsAndUnpublishedNavigation()
    {
        var node = Node(); node.TagId = 42; node.Action = new() { Type = "navigate", ScreenId = 9 };
        var issues = ScreenValidator.Validate(new ScreenDocument { Nodes = [node] }, new Dictionary<int, Tag>(), new HashSet<int>());
        Assert.Contains(issues, x => x.Message.Contains("测点不存在"));
        Assert.Contains(issues, x => x.Message.Contains("尚未发布"));
    }

    [Fact]
    public void CommandMustResolveToWritableTag()
    {
        var node = Node(); node.Action = new() { Type = "command", TagId = 1, Value = 1 };
        var tags = new Dictionary<int, Tag> { [1] = new() { Id = 1, Writable = false } };
        Assert.Contains(ScreenValidator.Validate(new ScreenDocument { Nodes = [node] }, tags), x => x.Message.Contains("可写"));
        tags[1].Writable = true;
        Assert.Empty(ScreenValidator.Validate(new ScreenDocument { Nodes = [node] }, tags));
    }

    [Fact]
    public void PolylineAndDeviceStatesValidateTheirBoundaries()
    {
        var node = Node();
        node.Type = "polyline";
        node.Points = [new(0, 50), new(50, 50), new(50, 100)];
        node.States = [new(0, "Stopped", "#6b7580"), new(1, "Running", "#34d399")];
        Assert.Empty(ScreenValidator.Validate(new ScreenDocument { Nodes = [node] }));
        node.Points[0] = new(double.NaN, 0);
        Assert.Contains(ScreenValidator.Validate(new ScreenDocument { Nodes = [node] }), issue => issue.Message.Contains("折点"));
        node.Points[0] = new(0, 50);
        node.States.Add(new(1, "Fault", "#e03131"));
        Assert.Contains(ScreenValidator.Validate(new ScreenDocument { Nodes = [node] }), issue => issue.Message.Contains("状态"));
    }

    [Fact]
    public void DirectInputRequiresWritableCommandBinding()
    {
        var node = Node(); node.Type = "input";
        Assert.Contains(ScreenValidator.Validate(new ScreenDocument { Nodes = [node] }), issue => issue.Message.Contains("控制动作"));
        node.Action = new() { Type = "command", TagId = 1, Value = 10 };
        Assert.Empty(ScreenValidator.Validate(new ScreenDocument { Nodes = [node] }, new Dictionary<int, Tag> { [1] = new() { Id = 1, Writable = true } }));
    }

    [Fact]
    public void PanelCommandsAndPipeReferencesAreValidated()
    {
        var panel = Node(); panel.Type = "equipment-panel";
        panel.Commands = [new() { Key = "start", Label = "Start", TagId = 1, Value = 1 }];
        var pipe = new ScreenNode { Id = "pipe-1", Type = "polyline", Source = new(panel.Id, "right") };
        var tags = new Dictionary<int, Tag> { [1] = new() { Id = 1, Writable = true } };
        var document = new ScreenDocument { Nodes = [panel, pipe] };
        Assert.Empty(ScreenValidator.Validate(document, tags));
        pipe.Target = new("missing", "left");
        Assert.Contains(ScreenValidator.Validate(document, tags), issue => issue.Message.Contains("端口"));
        tags[1].Writable = false;
        Assert.Contains(ScreenValidator.Validate(document, tags), issue => issue.Message.Contains("可写"));
    }
}
