using System.Globalization;

namespace Scada.Core.Calculations;

public sealed class TagExpressionException(string message) : Exception(message);

// Numeric expressions only: no reflection, objects, strings, loops, I/O or executable scripts.
public sealed class TagExpression
{
    private readonly Node _root;
    public IReadOnlyList<int> Dependencies { get; }
    private TagExpression(Node root, IEnumerable<int> dependencies) { _root = root; Dependencies = dependencies.Order().ToArray(); }
    public static TagExpression Compile(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression) || expression.Length > 4096) throw new TagExpressionException("表达式须为 1～4096 个字符");
        var parser = new Parser(expression); var root = parser.Expression(0, 0); parser.RequireEnd();
        if (parser.Dependencies.Count > 64) throw new TagExpressionException("单个表达式最多引用 64 个测点");
        return new(root, parser.Dependencies);
    }
    public double Evaluate(Func<int, double> input) => Finite(_root.Evaluate(input));
    private static double Finite(double value) => double.IsFinite(value) ? value : throw new TagExpressionException("计算结果不是有限数值，请检查除零、溢出或函数定义域");
    private static bool Truth(double value) => value != 0;
    private abstract record Node(int Depth) { public abstract double Evaluate(Func<int, double> input); }
    private sealed record Number(double Value) : Node(1) { public override double Evaluate(Func<int, double> input) => Value; }
    private sealed record Reference(int Id) : Node(1) { public override double Evaluate(Func<int, double> input) => Finite(input(Id)); }
    private sealed record Unary(string Op, Node Value) : Node(Value.Depth + 1)
    {
        public override double Evaluate(Func<int, double> input) => Op switch { "-" => Finite(-Value.Evaluate(input)), "+" => Value.Evaluate(input), "!" => Truth(Value.Evaluate(input)) ? 0 : 1, _ => throw new TagExpressionException("无效的一元运算") };
    }
    private sealed record Binary(string Op, Node Left, Node Right) : Node(Math.Max(Left.Depth, Right.Depth) + 1)
    {
        public override double Evaluate(Func<int, double> input)
        {
            var a = Left.Evaluate(input);
            if (Op == "&&" && !Truth(a)) return 0; if (Op == "||" && Truth(a)) return 1;
            var b = Right.Evaluate(input);
            return Finite(Op switch
            {
                "+" => a + b, "-" => a - b, "*" => a * b, "/" => b == 0 ? throw new TagExpressionException("表达式除数为零") : a / b,
                "%" => b == 0 ? throw new TagExpressionException("表达式余数运算除数为零") : a % b, "^" => Math.Pow(a, b),
                "<" => a < b ? 1 : 0, "<=" => a <= b ? 1 : 0, ">" => a > b ? 1 : 0, ">=" => a >= b ? 1 : 0,
                "==" => a == b ? 1 : 0, "!=" => a != b ? 1 : 0, "&&" => Truth(b) ? 1 : 0, "||" => Truth(b) ? 1 : 0,
                _ => throw new TagExpressionException("无效的二元运算")
            });
        }
    }
    private sealed record Call(string Name, Node[] Arguments) : Node(Arguments.Max(x => x.Depth) + 1)
    {
        public override double Evaluate(Func<int, double> input)
        {
            if (Name == "if") return Arguments[Truth(Arguments[0].Evaluate(input)) ? 1 : 2].Evaluate(input);
            var a = Arguments.Select(x => x.Evaluate(input)).ToArray();
            return Finite(Name switch
            {
                "abs" => Math.Abs(a[0]), "sqrt" => Math.Sqrt(a[0]), "floor" => Math.Floor(a[0]), "ceil" => Math.Ceiling(a[0]),
                "round" => Math.Round(a[0], a.Length == 1 ? 0 : Digits(a[1]), MidpointRounding.AwayFromZero),
                "min" => a.Min(), "max" => a.Max(), "pow" => Math.Pow(a[0], a[1]),
                "clamp" => a[1] <= a[2] ? Math.Clamp(a[0], a[1], a[2]) : throw new TagExpressionException("clamp 最小值不能大于最大值"),
                "sin" => Math.Sin(a[0]), "cos" => Math.Cos(a[0]), "tan" => Math.Tan(a[0]), "log" => Math.Log(a[0]), "exp" => Math.Exp(a[0]),
                _ => throw new TagExpressionException("不支持的函数")
            });
        }
        private static int Digits(double value) => value is >= 0 and <= 12 && value == Math.Truncate(value) ? (int)value : throw new TagExpressionException("round 位数须为 0～12 的整数");
    }

    private sealed class Parser(string text)
    {
        private int _position, _nodes; private string _token = ""; private double _number; private int _reference;
        public HashSet<int> Dependencies { get; } = [];
        private bool _started;
        private void Next()
        {
            while (_position < text.Length && char.IsWhiteSpace(text[_position])) _position++;
            if (_position == text.Length) { _token = "@end"; return; }
            var start = _position; var current = text[_position++];
            if (char.IsAsciiDigit(current) || current == '.')
            {
                while (_position < text.Length && (char.IsAsciiDigit(text[_position]) || text[_position] == '.')) _position++;
                if (_position < text.Length && text[_position] is 'e' or 'E')
                { _position++; if (_position < text.Length && text[_position] is '+' or '-') _position++; while (_position < text.Length && char.IsAsciiDigit(text[_position])) _position++; }
                if (!double.TryParse(text[start.._position], NumberStyles.Float, CultureInfo.InvariantCulture, out _number) || !double.IsFinite(_number)) throw Error("数值格式无效");
                _token = "@number"; return;
            }
            if (current == '#')
            {
                var first = _position; while (_position < text.Length && char.IsAsciiDigit(text[_position])) _position++;
                if (!int.TryParse(text[first.._position], out _reference) || _reference < 1) throw Error("测点引用须使用 # 加正整数编号");
                _token = "@reference"; return;
            }
            if (char.IsAsciiLetter(current))
            { while (_position < text.Length && char.IsAsciiLetter(text[_position])) _position++; _token = text[start.._position].ToLowerInvariant(); return; }
            if (_position < text.Length && new[] { "&&", "||", "==", "!=", "<=", ">=" }.Contains(text[start..(_position + 1)])) { _token = text[start..++_position]; return; }
            if ("+-*/%^!<>() ,".Contains(current)) { _token = current.ToString(); return; }
            throw Error("包含不支持的字符");
        }
        private T Count<T>(T node) where T : Node
        { if (++_nodes > 512 || node.Depth > 64) throw Error("表达式过于复杂（最多 512 个节点、64 层）"); return node; }
        public Node Expression(int binding, int depth)
        {
            if (depth > 64) throw Error("表达式嵌套过深");
            if (!_started) { _started = true; Next(); }
            Node left;
            var token = _token;
            if (token == "@number") { left = Count(new Number(_number)); Next(); }
            else if (token == "@reference") { Dependencies.Add(_reference); left = Count(new Reference(_reference)); Next(); }
            else if (token is "+" or "-" or "!") { Next(); left = Count(new Unary(token, Expression(7, depth + 1))); }
            else if (token == "(") { Next(); left = Expression(0, depth + 1); Expect(")"); }
            else if (token is "pi" or "e" or "true" or "false") { left = Count(new Number(token switch { "pi" => Math.PI, "e" => Math.E, "true" => 1, _ => 0 })); Next(); }
            else
            {
                Next(); Expect("("); var arguments = new List<Node>();
                if (_token != ")") { while (true) { arguments.Add(Expression(0, depth + 1)); if (_token != ",") break; Next(); if (arguments.Count >= 16) throw Error("函数参数过多"); } }
                Expect(")");
                var range = token switch { "if" or "clamp" => (3, 3), "pow" => (2, 2), "min" or "max" => (2, 16), "round" => (1, 2), "abs" or "sqrt" or "floor" or "ceil" or "sin" or "cos" or "tan" or "log" or "exp" => (1, 1), _ => throw Error("不支持的函数或标识符") };
                if (arguments.Count < range.Item1 || arguments.Count > range.Item2) throw Error("函数参数数量不正确");
                left = Count(new Call(token, arguments.ToArray()));
            }
            while (true)
            {
                var power = _token switch { "||" => 1, "&&" => 2, "==" or "!=" => 3, "<" or "<=" or ">" or ">=" => 4, "+" or "-" => 5, "*" or "/" or "%" => 6, "^" => 8, _ => 0 };
                if (power == 0 || power <= binding) break;
                var op = _token; Next(); var right = Expression(op == "^" ? power - 1 : power, depth + 1); left = Count(new Binary(op, left, right));
            }
            return left;
        }
        private void Expect(string value) { if (_token != value) throw Error($"缺少 {value}"); Next(); }
        public void RequireEnd() { if (_token != "@end") throw Error("表达式末尾包含多余内容"); }
        private TagExpressionException Error(string message) => new($"{message}（位置 {_position}）");
    }
}
