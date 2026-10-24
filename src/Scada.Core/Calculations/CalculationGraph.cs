using Scada.Core.Entities;
using Scada.Core.Enums;

namespace Scada.Core.Calculations;

public sealed record CalculationNode(Tag Tag, TagExpression Expression);
public static class CalculationGraph
{
    public static IReadOnlyList<CalculationNode> Compile(IReadOnlyCollection<Tag> tags)
    {
        var all = tags.ToDictionary(x => x.Id); var calculated = tags.Where(x => x.Kind == TagKind.Calculated).ToDictionary(x => x.Id, x => new CalculationNode(x, TagExpression.Compile(x.Expression)));
        if (calculated.Count > 2000) throw new TagExpressionException("计算测点最多 2000 个");
        var order = new List<CalculationNode>(); var visited = new HashSet<int>(); var visiting = new HashSet<int>();
        var heights = new Dictionary<int, int>();
        void Visit(int id, int depth)
        {
            if (visited.Contains(id)) return;
            if (depth > 64) throw new TagExpressionException("计算依赖链超过 64 层");
            if (!visiting.Add(id)) throw new TagExpressionException($"计算测点存在循环依赖：#{id}");
            var node = calculated[id];
            var error = Scada.Core.Models.ConfigurationValidator.Tag(node.Tag);
            if (error is not null) throw new TagExpressionException($"计算测点 #{id}：{error}");
            foreach (var input in node.Expression.Dependencies)
            {
                if (!all.ContainsKey(input)) throw new TagExpressionException($"计算测点 #{id} 引用了不存在的测点 #{input}");
                if (calculated.ContainsKey(input)) Visit(input, depth + 1);
            }
            heights[id] = 1 + node.Expression.Dependencies.Where(calculated.ContainsKey).Select(x => heights[x]).DefaultIfEmpty(0).Max();
            if (heights[id] > 64) throw new TagExpressionException("计算依赖链超过 64 层");
            visiting.Remove(id); visited.Add(id); order.Add(node);
        }
        foreach (var id in calculated.Keys) Visit(id, 0);
        return order;
    }
}
