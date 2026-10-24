using Scada.Core.Entities;
using Scada.Engine.Control;

namespace Scada.Engine.Tests;

// 顺控步骤图：旧格式规范化与静态校验（引用、不可达、汇合、无上限循环、分支路径）。
public class SequenceGraphTests
{
    private static ControlSequenceStep W(string id, string? next = null) => new() { Id = id, Name = id, TagId = 1, Value = 1, Next = next };
    private static string? Check(params ControlSequenceStep[] steps) => SequenceGraph.Validate(SequenceGraph.Normalize(steps));

    [Fact]
    public void LegacyLinearStepsAreNormalized()
    {
        var steps = SequenceGraph.Normalize([new() { Name = "a", TagId = 1 }, new() { Name = "b", TagId = 2, Type = "" }]);
        Assert.Equal(["s1", "s2"], steps.Select(x => x.Id));
        Assert.All(steps, x => Assert.Equal("write", x.Type));
        Assert.Null(SequenceGraph.Validate(steps));
    }

    [Fact]
    public void ValidBranchingGraphPasses()
    {
        Assert.Null(Check(
            new() { Id = "c", Type = "condition", Name = "选择", Expression = "#1 > 0", Then = "p", Else = "e" },
            new() { Id = "p", Type = "parallel", Name = "并行", Branches = ["b1", "b2"], Join = "j" },
            W("b1", "j"), W("b2", "lp"),
            new() { Id = "lp", Type = "jump", Name = "重试", Target = "b2x", MaxIterations = 3 },
            new() { Id = "b2x", Type = "condition", Name = "到位?", Expression = "#2 > 0", Then = "j", Else = "lp" },
            new() { Id = "j", Type = "join", Name = "汇合", JoinMode = "any" },
            new() { Id = "e", Type = "end", Name = "结束" }));
    }

    [Fact]
    public void StructuralErrorsAreReported()
    {
        Assert.Contains("不可达", Check(W("a", "c"), W("b"), new() { Id = "c", Type = "end", Name = "c" }));
        Assert.Contains("不存在", Check(W("a", "zz")));
        Assert.Contains("缺少汇合", Check(new ControlSequenceStep { Id = "p", Type = "parallel", Name = "p", Branches = ["a", "b"] }, W("a"), W("b")));
        Assert.Contains("缺少对应的并行", Check(W("a"), new() { Id = "j", Type = "join", Name = "j" }));
        Assert.Contains("没有次数上限的循环", Check(W("a"), W("b", "a")));
        Assert.Contains("没有次数上限的循环", Check(new ControlSequenceStep { Id = "a", Name = "a", TagId = 1, OnFailure = "goto", OnFailureGoto = "a" }));
        Assert.Contains("循环上限", Check(W("a"), new() { Id = "j", Type = "jump", Name = "j", Target = "a" }));
        Assert.Contains("成立时", Check(new ControlSequenceStep { Id = "c", Type = "condition", Name = "c", Expression = "1" }));
        Assert.Contains("未汇合", Check(new ControlSequenceStep { Id = "p", Type = "parallel", Name = "p", Branches = ["a", "x"], Join = "j" }, W("a", "j"),
            new() { Id = "x", Type = "end", Name = "x" }, new() { Id = "j", Type = "join", Name = "j" }));
        Assert.Contains("只能经由并行", Check(new ControlSequenceStep { Id = "p", Type = "parallel", Name = "p", Branches = ["a", "b"], Join = "j" }, W("a", "j"), W("b", "j"),
            new() { Id = "j", Type = "join", Name = "j", Next = "z" }, new() { Id = "z", Type = "jump", Name = "z", Target = "j", MaxIterations = 2 }));
        Assert.Contains("重复", Check(W("a"), W("a")));
    }
}
