using System.Diagnostics;

namespace Scada.Core.Calculations;

public enum ScriptTarget { Variable, Output, Tag }
public sealed record ScriptStatement(ScriptTarget Target, string Name, int TagId, TagExpression Expression, int Line);
public sealed record ScriptLimits(int MaxStatements = 200, int MaxSourceLength = 16_384, long MaxSteps = 100_000, int TimeoutMs = 50, int MaxWrites = 16, int MaxOutputs = 32);
public sealed record ScriptResult(Dictionary<string, double> Outputs, Dictionary<int, double> Writes, long Steps, double ElapsedMs);

// 受限脚本：在现有数值表达式引擎之上增加“赋值语句序列”。
// 语句形如：$变量 = 表达式;   @输出 = 表达式;   #测点 = 表达式;
// 没有循环、函数定义、字符串、对象、反射或 IO，读写的测点必须在白名单内；执行有步数与墙钟上限。
public sealed class CalcScript
{
    public IReadOnlyList<ScriptStatement> Statements { get; }
    public IReadOnlyList<int> Reads { get; }
    public IReadOnlyList<int> Writes { get; }
    public IReadOnlyList<string> Outputs { get; }
    public ScriptLimits Limits { get; }
    public static readonly string[] BuiltIns = ["dt", "hour", "minute", "weekday", "day", "month"];

    private CalcScript(List<ScriptStatement> statements, ScriptLimits limits)
    {
        Statements = statements; Limits = limits;
        Reads = statements.SelectMany(x => x.Expression.Dependencies).Distinct().Order().ToArray();
        Writes = statements.Where(x => x.Target == ScriptTarget.Tag).Select(x => x.TagId).Distinct().Order().ToArray();
        Outputs = statements.Where(x => x.Target == ScriptTarget.Output).Select(x => x.Name).Distinct().ToArray();
    }

    public static CalcScript Compile(string source, IReadOnlySet<int> readable, IReadOnlySet<int> writable, ScriptLimits? limits = null)
    {
        limits ??= new ScriptLimits();
        if (string.IsNullOrWhiteSpace(source)) throw new TagExpressionException("脚本不能为空");
        if (source.Length > limits.MaxSourceLength) throw new TagExpressionException($"脚本最长 {limits.MaxSourceLength} 个字符");
        var statements = new List<ScriptStatement>();
        var defined = new HashSet<string>(BuiltIns);
        var lines = source.Replace("\r", "").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var text = lines[i]; var comment = text.IndexOf("//", StringComparison.Ordinal); if (comment >= 0) text = text[..comment];
            foreach (var part in text.Split(';'))
            {
                var statement = part.Trim(); if (statement.Length == 0) continue;
                var line = i + 1;
                var eq = statement.IndexOf('=');
                if (eq <= 0 || eq + 1 < statement.Length && statement[eq + 1] == '=') throw new TagExpressionException($"第 {line} 行：语句须为 目标 = 表达式");
                var target = statement[..eq].Trim(); var body = statement[(eq + 1)..].Trim();
                TagExpression expression;
                try { expression = TagExpression.Compile(body, allowVariables: true); }
                catch (TagExpressionException ex) { throw new TagExpressionException($"第 {line} 行：{ex.Message}"); }
                foreach (var v in expression.Variables)
                    if (!defined.Contains(v)) throw new TagExpressionException($"第 {line} 行：变量 ${v} 未先赋值");
                foreach (var id in expression.Dependencies)
                    if (!readable.Contains(id)) throw new TagExpressionException($"第 {line} 行：测点 #{id} 不在可读白名单内");
                ScriptStatement item;
                if (target.StartsWith('$') && IsName(target[1..]))
                {
                    var name = target[1..].ToLowerInvariant();
                    if (BuiltIns.Contains(name)) throw new TagExpressionException($"第 {line} 行：${name} 是内置变量，不能赋值");
                    defined.Add(name); item = new(ScriptTarget.Variable, name, 0, expression, line);
                }
                else if (target.StartsWith('@') && IsName(target[1..]))
                    item = new(ScriptTarget.Output, target[1..], 0, expression, line);
                else if (target.StartsWith('#') && int.TryParse(target[1..], out var tagId) && tagId > 0)
                {
                    if (!writable.Contains(tagId)) throw new TagExpressionException($"第 {line} 行：测点 #{tagId} 不在可写白名单内");
                    item = new(ScriptTarget.Tag, "", tagId, expression, line);
                }
                else throw new TagExpressionException($"第 {line} 行：赋值目标须为 $变量、@输出 或 #测点编号");
                statements.Add(item);
                if (statements.Count > limits.MaxStatements) throw new TagExpressionException($"脚本最多 {limits.MaxStatements} 条语句");
            }
        }
        if (statements.Count == 0) throw new TagExpressionException("脚本没有任何语句");
        var script = new CalcScript(statements, limits);
        if (script.Writes.Count > limits.MaxWrites) throw new TagExpressionException($"脚本最多写 {limits.MaxWrites} 个测点");
        if (script.Outputs.Count > limits.MaxOutputs) throw new TagExpressionException($"脚本最多 {limits.MaxOutputs} 个输出");
        return script;
    }

    private static bool IsName(string name) => name.Length is > 0 and <= 32 && char.IsAsciiLetter(name[0]) && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');

    // 执行：读测点通过 read 回调（坏质量应抛 TagExpressionException），任何错误都使整次执行失败、不产生部分写入。
    public ScriptResult Execute(Func<int, double> read, IReadOnlyDictionary<string, double> builtIns)
    {
        var started = Stopwatch.GetTimestamp();
        var deadline = started + (long)(Limits.TimeoutMs / 1000.0 * Stopwatch.Frequency);
        var variables = new Dictionary<string, double>(builtIns);
        var outputs = new Dictionary<string, double>(); var writes = new Dictionary<int, double>();
        var context = new ExpressionContext(read, name => variables.TryGetValue(name, out var v) ? v : throw new TagExpressionException($"变量 ${name} 未定义"), Limits.MaxSteps, deadline);
        foreach (var statement in Statements)
        {
            double value;
            try { value = statement.Expression.Evaluate(context); }
            catch (TagExpressionException ex) { throw new TagExpressionException($"第 {statement.Line} 行：{ex.Message}"); }
            switch (statement.Target)
            {
                case ScriptTarget.Variable: variables[statement.Name] = value; break;
                case ScriptTarget.Output: outputs[statement.Name] = value; break;
                default: writes[statement.TagId] = value; break;
            }
            if (Stopwatch.GetTimestamp() > deadline) throw new TagExpressionException($"第 {statement.Line} 行：执行超时（{Limits.TimeoutMs} ms）");
        }
        return new(outputs, writes, context.Steps, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

    public static IReadOnlySet<int> ParseIds(string text) =>
        (text ?? "").Split([',', ' ', '\n', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => int.TryParse(x.TrimStart('#'), out var id) && id > 0 ? id : throw new TagExpressionException($"测点编号无效：{x}")).ToHashSet();
}
