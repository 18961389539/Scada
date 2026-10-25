using System.Text.RegularExpressions;
using Scada.Core.Entities;

namespace Scada.Engine.Control;

// 顺控步骤图：规范化（兼容旧的线性步骤）与静态校验（引用、不可达、并行汇合、无上限循环）。
public static class SequenceGraph
{
    public const int MaxSteps = 100;
    public const int MaxIterationsLimit = 1000;
    public static readonly string[] Types = ["write", "wait", "condition", "parallel", "join", "jump", "end"];
    private static readonly Regex IdPattern = new("^[A-Za-z0-9_-]{1,40}$", RegexOptions.Compiled);

    // 旧格式（无 Id/Type）补齐：类型为 write，编号为 s1、s2…；空白字段置空。
    public static List<ControlSequenceStep> Normalize(IEnumerable<ControlSequenceStep> steps)
    {
        var list = steps.ToList();
        var used = new HashSet<string>(list.Where(x => !string.IsNullOrWhiteSpace(x.Id)).Select(x => x.Id!.Trim()));
        var n = 0;
        foreach (var s in list)
        {
            n++;
            s.Type = string.IsNullOrWhiteSpace(s.Type) ? "write" : s.Type.Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(s.Id))
            {
                var id = $"s{n}"; var k = n;
                while (used.Contains(id)) id = $"s{++k}_{n}";
                s.Id = id; used.Add(id);
            }
            else s.Id = s.Id.Trim();
            s.Name = s.Name?.Trim() ?? "";
            s.ConfirmExpression = Blank(s.ConfirmExpression); s.Expression = Blank(s.Expression);
            s.Next = Blank(s.Next); s.Then = Blank(s.Then); s.Else = Blank(s.Else); s.Join = Blank(s.Join); s.Target = Blank(s.Target);
            s.OnFailure = Blank(s.OnFailure)?.ToLowerInvariant(); s.OnFailureGoto = Blank(s.OnFailureGoto);
            s.JoinMode = s.Type == "join" ? (Blank(s.JoinMode)?.ToLowerInvariant() ?? "all") : null;
            s.Branches = s.Type == "parallel" ? (s.Branches ?? []).Select(x => x?.Trim() ?? "").ToList() : null;
            if (s.Type != "write") { s.TagId = null; s.Value = 0; s.ConfirmExpression = null; s.DelayMs = 0; }
            if (s.Type != "jump") s.MaxIterations = null;
        }
        return list;
    }

    // 从校验错误文本中找出涉及的步骤编号（"第 N 步" 或按词边界出现的编号）。
    public static string[] StepsInMessage(IReadOnlyList<ControlSequenceStep> steps, string message)
    {
        var found = new List<string>();
        foreach (Match m in Regex.Matches(message, @"第 (\d+) 步"))
            if (int.TryParse(m.Groups[1].Value, out var n) && n >= 1 && n <= steps.Count && steps[n - 1].Id is string sid) found.Add(sid);
        foreach (var s in steps)
            if (!string.IsNullOrEmpty(s.Id) && Regex.IsMatch(message, $@"(?<![A-Za-z0-9_\-]){Regex.Escape(s.Id)}(?![A-Za-z0-9_\-])")) found.Add(s.Id);
        return found.Distinct().ToArray();
    }

    // 后继（不含失败转向）。end 无后继；parallel 的后继为各分支首步；jump 为目标。
    public static IEnumerable<string> Successors(IReadOnlyList<ControlSequenceStep> steps, int index)
    {
        var s = steps[index];
        string? Fallthrough() => s.Next ?? (index + 1 < steps.Count ? steps[index + 1].Id : null);
        switch (s.Type)
        {
            case "end": yield break;
            case "jump": if (s.Target is not null) yield return s.Target; yield break;
            case "condition":
                if (s.Then is not null) yield return s.Then;
                if ((s.Else ?? Fallthrough()) is string e) yield return e;
                yield break;
            case "parallel": foreach (var b in s.Branches ?? []) yield return b; yield break;
            default: if (Fallthrough() is string f) yield return f; yield break;
        }
    }

    // 返回第一条错误（null 表示通过）。tagCheck：写步骤的测点校验（返回错误文本）。
    public static string? Validate(IReadOnlyList<ControlSequenceStep> steps, Func<string, string?>? expressionCheck = null, Func<ControlSequenceStep, string?>? tagCheck = null)
    {
        if (steps.Count is 0 or > MaxSteps) return $"步骤须为 1～{MaxSteps} 个";
        var index = new Dictionary<string, int>();
        for (var i = 0; i < steps.Count; i++)
        {
            var s = steps[i]; var label = $"第 {i + 1} 步（{s.Id}）";
            if (!IdPattern.IsMatch(s.Id ?? "")) return $"第 {i + 1} 步编号须为 1～40 个字母、数字、下划线或连字符";
            if (!index.TryAdd(s.Id!, i)) return $"步骤编号 {s.Id} 重复";
            if (s.Name.Length is 0 or > 100) return $"{label}名称须为 1～100 个字符";
            if (!Types.Contains(s.Type)) return $"{label}类型 {s.Type} 无效";
            if (s.OnFailure is not (null or "stop" or "continue" or "goto")) return $"{label}失败处理须为 stop、continue 或 goto";
            if (s.OnFailure == "goto" && s.OnFailureGoto is null) return $"{label}失败处理为转向时须指定目标步骤";
            if (s.Type is "write" or "wait" && s.TimeoutMs is < 100 or > 600_000) return $"{label}超时须为 100～600000 毫秒";
            if (s.DelayMs is < 0 or > 600_000) return $"{label}延时须为 0～600000 毫秒";
            switch (s.Type)
            {
                case "write":
                    if (s.TagId is null) return $"{label}须指定写入测点";
                    if (tagCheck?.Invoke(s) is string tagError) return $"{label}：{tagError}";
                    if (expressionCheck?.Invoke(s.ConfirmExpression ?? "") is string e1) return $"{label}完成条件：{e1}";
                    break;
                case "wait": case "condition":
                    if (s.Expression is null) return $"{label}须填写条件表达式";
                    if (expressionCheck?.Invoke(s.Expression) is string e2) return $"{label}条件：{e2}";
                    if (s.Type == "condition" && s.Then is null) return $"{label}须指定条件成立时转到的步骤";
                    break;
                case "parallel":
                    if (s.Branches is null || s.Branches.Count is < 2 or > 8) return $"{label}并行分支须为 2～8 个";
                    if (s.Join is null) return $"{label}缺少汇合步骤";
                    if (s.Branches.Distinct().Count() != s.Branches.Count) return $"{label}分支重复";
                    break;
                case "join":
                    if (s.JoinMode is not ("all" or "any")) return $"{label}汇合方式须为 all 或 any";
                    break;
                case "jump":
                    if (s.Target is null) return $"{label}须指定跳转目标";
                    if (s.MaxIterations is not int max || max < 1 || max > MaxIterationsLimit) return $"{label}须设置循环上限 1～{MaxIterationsLimit} 次";
                    break;
            }
        }
        for (var i = 0; i < steps.Count; i++)
        {
            var s = steps[i];
            foreach (var (field, target) in new[] { ("下一步", s.Next), ("成立分支", s.Then), ("否则分支", s.Else), ("汇合", s.Join), ("跳转目标", s.Target), ("失败转向", s.OnFailureGoto) }
                .Concat((s.Branches ?? []).Select(b => ("并行分支", (string?)b))))
                if (target is not null && !index.ContainsKey(target)) return $"步骤 {s.Id} 的{field} {target} 不存在";
            if (s.Type == "parallel")
            {
                if (steps[index[s.Join!]].Type != "join") return $"步骤 {s.Id} 的汇合 {s.Join} 不是汇合步骤";
                if (s.Branches!.Contains(s.Join!)) return $"步骤 {s.Id} 的分支不能直接是汇合步骤";
            }
        }
        // 每个汇合步骤只能属于一个并行步骤。
        foreach (var join in steps.Where(x => x.Type == "join"))
        {
            var owners = steps.Count(x => x.Type == "parallel" && x.Join == join.Id);
            if (owners == 0) return $"汇合步骤 {join.Id} 缺少对应的并行步骤";
            if (owners > 1) return $"汇合步骤 {join.Id} 被多个并行步骤使用";
        }

        // 不可达：从第一步出发（含失败转向）。
        var reach = new HashSet<int>(); var stack = new Stack<int>(); stack.Push(0);
        while (stack.Count > 0)
        {
            var i = stack.Pop(); if (!reach.Add(i)) continue;
            foreach (var t in Successors(steps, i)) stack.Push(index[t]);
            if (steps[i].OnFailureGoto is string g && steps[i].OnFailure == "goto") stack.Push(index[g]);
            if (steps[i].Type == "parallel") stack.Push(index[steps[i].Join!]);
        }
        var unreachable = steps.Where((x, i) => !reach.Contains(i)).Select(x => x.Id).ToList();
        if (unreachable.Count > 0) return $"步骤 {string.Join("、", unreachable)} 不可达";

        // 无上限循环：去掉跳转（有次数上限）的边后，图必须无环。并行步骤到汇合步骤视为一条边。
        var color = new int[steps.Count];
        string? cycle = null;
        bool Dfs(int i)
        {
            color[i] = 1;
            var next = steps[i].Type == "jump" ? [] : Successors(steps, i).Select(t => index[t]).ToList();
            if (steps[i].OnFailure == "goto" && steps[i].OnFailureGoto is string g) next.Add(index[g]);
            foreach (var j in next)
            {
                if (color[j] == 1) { cycle = steps[j].Id; return true; }
                if (color[j] == 0 && Dfs(j)) return true;
            }
            color[i] = 2; return false;
        }
        for (var i = 0; i < steps.Count; i++) if (color[i] == 0 && Dfs(i)) return $"步骤 {cycle} 处存在没有次数上限的循环（循环须通过设置了上限的跳转步骤实现）";

        if (PathError(steps, index, 0, null) is string mainError) return mainError;
        // 并行分支：每条分支的每条路径都必须到达其汇合步骤，不能结束运行或越过汇合。
        foreach (var p in steps.Where(x => x.Type == "parallel"))
            foreach (var b in p.Branches!)
                if (PathError(steps, index, index[b], p.Join!) is string error) return $"并行步骤 {p.Id} 的分支 {b}：{error}";
        return null;
    }

    // join 为空：主路径（允许结束，不能未经并行到达汇合步骤）；否则为并行分支（每条路径都必须到达 join）。
    private static string? PathError(IReadOnlyList<ControlSequenceStep> steps, Dictionary<string, int> index, int start, string? join)
    {
        var seen = new HashSet<int>(); var stack = new Stack<int>(); stack.Push(start);
        while (stack.Count > 0)
        {
            var i = stack.Pop();
            var s = steps[i];
            if (join is not null && s.Id == join) continue;
            if (!seen.Add(i)) continue;
            if (s.Type == "end") { if (join is null) continue; return $"在步骤 {s.Id} 结束运行，未汇合到 {join}"; }
            if (s.Type == "join") return join is null ? $"汇合步骤 {s.Id} 只能经由并行步骤到达" : $"到达了其他汇合步骤 {s.Id}，未汇合到 {join}";
            var next = new List<string>();
            // 嵌套并行整体视为一步：从其汇合步骤的后续继续。
            if (s.Type == "parallel") next.AddRange(Successors(steps, index[s.Join!]));
            else next.AddRange(Successors(steps, i));
            if (s.OnFailure == "goto" && s.OnFailureGoto is string g) next.Add(g);
            if (next.Count == 0) { if (join is null) continue; return $"在步骤 {s.Id} 之后没有后续步骤，未汇合到 {join}"; }
            foreach (var t in next) stack.Push(index[t]);
        }
        return null;
    }

    private static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
