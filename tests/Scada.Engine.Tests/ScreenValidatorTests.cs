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
            var action = type is "switch" or "slider" or "spinner" or "input" or "jog" or "pulse" or "selector" or "knob"
                ? new ScreenAction { Type = "command", TagId = 1, Value = 1 } : new ScreenAction();
            var node = new ScreenNode
            {
                Id = type, Type = type, Action = action, Symbol = type == "symbol" ? "isa:pump" : null,
                TemplateKey = type == "template" ? "pump" : null, ReleaseValue = type is "jog" or "pulse" ? 0 : null,
                RecipeTemplateId = type == "recipe" ? 1 : null
            };
            if (type is "selector" or "knob") node.States = [new(0, "Off", "#6b7580"), new(1, "On", "#34d399")];
            Assert.Empty(ScreenValidator.Validate(new ScreenDocument { Nodes = [node] }));
        }
    }

    [Fact]
    public void MomentaryResetAndPositionControlsValidateTheirConfiguration()
    {
        var tags = new Dictionary<int, Tag> { [1] = new() { Id = 1, Writable = true } };
        var jog = new ScreenNode { Id = "jog-1", Type = "jog", Action = new() { Type = "command", TagId = 1, Value = 1 }, ReleaseValue = 0 };
        Assert.Empty(ScreenValidator.Validate(new ScreenDocument { Nodes = [jog] }, tags));
        jog.ReleaseValue = 1;
        Assert.Contains(ScreenValidator.Validate(new ScreenDocument { Nodes = [jog] }, tags), issue => issue.Message.Contains("释放值"));
        jog.ReleaseValue = 0; jog.MaxHoldMs = 100;
        Assert.Contains(ScreenValidator.Validate(new ScreenDocument { Nodes = [jog] }, tags), issue => issue.Message.Contains("最长保持"));

        var pulse = new ScreenNode { Id = "pulse-1", Type = "pulse", Action = new() { Type = "command", TagId = 1, Value = 1 }, ReleaseValue = 0, PulseMs = 50 };
        Assert.Contains(ScreenValidator.Validate(new ScreenDocument { Nodes = [pulse] }, tags), issue => issue.Message.Contains("脉冲宽度"));

        var selector = new ScreenNode { Id = "sel-1", Type = "selector", Action = new() { Type = "command", TagId = 1 }, States = [new(0, "Off", "#6b7580")] };
        Assert.Contains(ScreenValidator.Validate(new ScreenDocument { Nodes = [selector] }, tags), issue => issue.Message.Contains("位置"));
        selector.States.Add(new(1, "On", "#34d399"));
        Assert.Empty(ScreenValidator.Validate(new ScreenDocument { Nodes = [selector] }, tags));

        var reset = new ScreenNode { Id = "reset-1", Type = "reset", ResetMode = "tag" };
        Assert.Contains(ScreenValidator.Validate(new ScreenDocument { Nodes = [reset] }), issue => issue.Message.Contains("控制动作"));
        reset.ResetMode = "alarms";
        Assert.Empty(ScreenValidator.Validate(new ScreenDocument { Nodes = [reset] }));

        var recipe = new ScreenNode { Id = "recipe-1", Type = "recipe" };
        Assert.Contains(ScreenValidator.Validate(new ScreenDocument { Nodes = [recipe] }), issue => issue.Message.Contains("配方模板"));
        recipe.RecipeTemplateId = 1;
        Assert.Empty(ScreenValidator.Validate(new ScreenDocument { Nodes = [recipe] }));
        recipe.Action = new() { Type = "navigate", ScreenId = 2 };
        Assert.Contains(ScreenValidator.Validate(new ScreenDocument { Nodes = [recipe] }), issue => issue.Message.Contains("画面动作"));
    }

    [Fact]
    public void SequenceAndRecipeActionsRequireTheirTarget()
    {
        var node = Node();
        node.Action = new() { Type = "sequence" };
        Assert.Contains(ScreenValidator.Validate(new ScreenDocument { Nodes = [node] }), issue => issue.Message.Contains("顺控流程"));
        node.Action.SequenceId = 3;
        Assert.Empty(ScreenValidator.Validate(new ScreenDocument { Nodes = [node] }));
        node.Action = new() { Type = "recipe" };
        Assert.Contains(ScreenValidator.Validate(new ScreenDocument { Nodes = [node] }), issue => issue.Message.Contains("配方动作"));
        node.Action.RecipeId = 4;
        Assert.Empty(ScreenValidator.Validate(new ScreenDocument { Nodes = [node] }));
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

    [Theory]
    [InlineData("a--b")]
    [InlineData("-a")]
    [InlineData("a-")]
    public void DraftNodeIdsReserveTheExpansionSeparator(string id)
    {
        var node = Node(); node.Id = id;
        Assert.Contains(ScreenValidator.Validate(new ScreenDocument { Nodes = [node] }), issue => issue.Message.Contains("编号"));
        node.Id = "tpl--inner";
        Assert.Empty(ScreenValidator.Validate(new ScreenDocument { Nodes = [node] }, expandedDocument: true));
    }

    [Fact]
    public void ExpandedNodeIdsAllowExactlyOneSeparator()
    {
        var node = Node(); node.Id = "tpl--inner";
        Assert.Empty(ScreenValidator.Validate(new ScreenDocument { Nodes = [node] }, expandedDocument: true));
        foreach (var id in new[] { "tpl----inner", "tpl--inner--x", "tpl--", "--inner" })
        {
            node.Id = id;
            Assert.Contains(ScreenValidator.Validate(new ScreenDocument { Nodes = [node] }, expandedDocument: true), issue => issue.Message.Contains("编号"));
        }
    }

    [Fact]
    public void ScreenActionsRejectForeignTargets()
    {
        var node = Node();
        node.Action = new() { Type = "navigate", ScreenId = 1 };
        Assert.Empty(ScreenValidator.Validate(new ScreenDocument { Nodes = [node] }));
        node.Action.TagId = 2;
        Assert.Contains(ScreenValidator.Validate(new ScreenDocument { Nodes = [node] }), issue => issue.Message.Contains("无关的目标"));
        node.Action = new() { Type = "sequence", SequenceId = 3, RecipeId = 4 };
        Assert.Contains(ScreenValidator.Validate(new ScreenDocument { Nodes = [node] }), issue => issue.Message.Contains("无关的目标"));
        node.Action = new() { Type = "command", TagId = 1, Value = 1, ScreenId = 2 };
        Assert.Contains(ScreenValidator.Validate(new ScreenDocument { Nodes = [node] }), issue => issue.Message.Contains("无关的目标"));
        node.Action = new() { Type = "none" };
        Assert.Empty(ScreenValidator.Validate(new ScreenDocument { Nodes = [node] }));
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
