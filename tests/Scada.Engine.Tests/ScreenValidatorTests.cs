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
            Assert.Empty(ScreenValidator.Validate(new ScreenDocument { Nodes = [new ScreenNode { Id = type, Type = type, Symbol = type == "symbol" ? "isa:pump" : null, TemplateKey = type == "template" ? "pump" : null }] }));
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
}
