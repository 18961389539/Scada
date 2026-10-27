using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Scada.Core.Entities;

namespace Scada.Core.Models;

public sealed class ScreenDocument
{
    public int SchemaVersion { get; set; } = 1;
    public double Width { get; set; } = 1200;
    public double Height { get; set; } = 720;
    public string Background { get; set; } = "#101b2d";
    public List<ScreenNode> Nodes { get; set; } = [];
}

public sealed class ScreenNode
{
    public string Id { get; set; } = string.Empty;
    public string Type { get; set; } = "text";
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; } = 120;
    public double Height { get; set; } = 80;
    public double Rotation { get; set; }
    public string Text { get; set; } = string.Empty;
    public string Color { get; set; } = "#7394b8";
    public string ActiveColor { get; set; } = "#34d399";
    public string Unit { get; set; } = string.Empty;
    public int Decimals { get; set; } = 1;
    public double Min { get; set; }
    public double Max { get; set; } = 100;
    public double ActiveThreshold { get; set; } = 0.5;
    public int? TagId { get; set; }
    public ScreenAction Action { get; set; } = new();
    // 以下为可选扩展字段；为空时不序列化，旧画面与旧工程包逐字不变。
    // symbol：图库符号键（isa:pump、user:xxx）。
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Symbol { get; set; }
    // 报警/故障状态测点：值 ≥ 0.5 时按报警色显示（ISA-101：只有异常才用彩色）。
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? AlarmTagId { get; set; }
    // template：引用图库模板（按键），Bindings 把模板占位符绑定到本实例的测点。
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? TemplateKey { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<ScreenBinding>? Bindings { get; set; }
    // 仅模板内部图元：输入测点占位符（实例化时由 Bindings 解析为 TagId）。
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Bind { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? AlarmBind { get; set; }
    // 多语言文字：语言 → 文字（如 en-US）；缺失时显示 Text。
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public Dictionary<string, string>? Texts { get; set; }
    // 图表/表格类图元绑定的多个测点。
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<int>? Tags { get; set; }
    // 统计窗口（秒）：趋势图/历史表格/OEE 看板。
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public double? WindowSec { get; set; }
    // OEE 看板绑定的产线。
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? LineId { get; set; }
    // 矢量动画：由测点值/状态驱动，前端以 CSS transform/clip-path 呈现。
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public ScreenAnimation? Animation { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<ScreenPoint>? Points { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<ScreenState>? States { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public ScreenPort? Source { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public ScreenPort? Target { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<ScreenPanelCommand>? Commands { get; set; }
}

public sealed record ScreenPoint(double X, double Y);
public sealed record ScreenState(double Value, string Label, string Color);
public sealed record ScreenPort(string NodeId, string Port);
public sealed record ScreenPanelCommand
{
    public string Key { get; init; } = "";
    public string Label { get; init; } = "";
    public int? TagId { get; init; }
    public string? Bind { get; init; }
    public double Value { get; init; }
}

// 矢量动画定义：rotate 旋转角度、level 液位比例、flow 流动；source 决定按值映射还是按状态触发。
public sealed class ScreenAnimation
{
    public string Type { get; set; } = "rotate";
    public string Source { get; set; } = "value";
    public int? TagId { get; set; }
    public double From { get; set; }
    public double To { get; set; } = 100;
    public double OutFrom { get; set; }
    public double OutTo { get; set; } = 360;
}

public sealed class ScreenBinding
{
    public string Key { get; set; } = "";
    public int? TagId { get; set; }
}

// 图库模板定义（运行时展开用）。
public sealed record HmiTemplateDefinition(string Key, double Width, double Height, IReadOnlyList<ScreenNode> Nodes, IReadOnlyList<string> Placeholders);

// 模板实例展开：模板内部图元按实例框缩放平移，编号为 “实例--内部”，占位符按实例绑定解析为测点。
// 服务端（校验、画面控制动作）与前端（渲染）使用相同规则；模板修改后所有实例随之更新。
public static class ScreenExpander
{
    public const string Separator = "--";

    public static ScreenDocument Expand(ScreenDocument document, Func<string, HmiTemplateDefinition?> lookup)
    {
        var result = new ScreenDocument { SchemaVersion = document.SchemaVersion, Width = document.Width, Height = document.Height, Background = document.Background, Nodes = [] };
        foreach (var node in document.Nodes ?? [])
        {
            if (node?.Type != "template") { if (node is not null) result.Nodes.Add(node); continue; }
            var template = node.TemplateKey is null ? null : lookup(node.TemplateKey);
            if (template is null) continue;
            var sx = node.Width / template.Width; var sy = node.Height / template.Height;
            var bindings = (node.Bindings ?? []).Where(x => x.Key is not null).GroupBy(x => x.Key).ToDictionary(g => g.Key, g => g.First().TagId);
            int? Resolve(string? key) => key is not null && bindings.TryGetValue(key, out var id) ? id : null;
            foreach (var inner in template.Nodes)
            {
                result.Nodes.Add(new ScreenNode
                {
                    Id = $"{node.Id}{Separator}{inner.Id}", Type = inner.Type, X = node.X + inner.X * sx, Y = node.Y + inner.Y * sy,
                    Width = inner.Width * sx, Height = inner.Height * sy, Rotation = inner.Rotation, Text = inner.Text, Color = inner.Color, ActiveColor = inner.ActiveColor,
                    Unit = inner.Unit, Decimals = inner.Decimals, Min = inner.Min, Max = inner.Max, ActiveThreshold = inner.ActiveThreshold, Symbol = inner.Symbol, Texts = inner.Texts,
                    Tags = inner.Tags, WindowSec = inner.WindowSec, LineId = inner.LineId, Animation = inner.Animation, Points = inner.Points, States = inner.States,
                    Source = inner.Source is { } source ? source with { NodeId = $"{node.Id}{Separator}{source.NodeId}" } : null,
                    Target = inner.Target is { } target ? target with { NodeId = $"{node.Id}{Separator}{target.NodeId}" } : null,
                    Commands = inner.Commands?.Select(command => command with { TagId = Resolve(command.Bind), Bind = null }).ToList(),
                    TagId = inner.Bind is not null ? Resolve(inner.Bind) : null, AlarmTagId = inner.AlarmBind is not null ? Resolve(inner.AlarmBind) : null,
                    Action = new ScreenAction { Type = inner.Action?.Type ?? "none", ScreenId = inner.Action?.ScreenId, Value = inner.Action?.Value ?? 0,
                        TagId = inner.Action?.Bind is not null ? Resolve(inner.Action.Bind) : null }
                });
            }
        }
        return result;
    }

    // 模板实例自身的校验：模板存在、每个占位符都已绑定。
    public static IEnumerable<ScreenIssue> InstanceIssues(ScreenDocument document, Func<string, HmiTemplateDefinition?> lookup)
    {
        foreach (var node in document.Nodes ?? [])
        {
            if (node?.Type != "template") continue;
            var template = node.TemplateKey is null ? null : lookup(node.TemplateKey);
            if (template is null) { yield return new(node.Id, $"模板 {node.TemplateKey} 不存在"); continue; }
            var bound = (node.Bindings ?? []).Where(x => x.TagId is not null).Select(x => x.Key).ToHashSet();
            var missing = template.Placeholders.Where(p => !bound.Contains(p)).ToList();
            if (missing.Count > 0) yield return new(node.Id, $"模板占位符未绑定：{string.Join("、", missing)}");
        }
    }
}

public sealed class ScreenAction
{
    public string Type { get; set; } = "none";
    public int? ScreenId { get; set; }
    public int? TagId { get; set; }
    public double Value { get; set; }
    // 仅模板内部：控制输出占位符。
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Bind { get; set; }
}

public sealed record ScreenIssue(string NodeId, string Message);

public static partial class ScreenValidator
{
    public static readonly HashSet<string> NodeTypes =
        ["text", "value", "pump", "valve", "tank", "pipe", "rectangle", "ellipse", "button", "gauge", "motor", "fan", "symbol", "template",
         "led", "progress", "kpi", "clock", "image", "marquee", "dial", "chart-bar", "chart-pie", "chart-line", "chart-bar-horizontal", "chart-bar-stack", "chart-area", "chart-radar", "chart-funnel", "chart-gauge", "alarm-banner", "alarm-stats", "alarm-list", "table", "history-table", "oee-kpi", "switch", "slider", "spinner", "input", "polyline", "equipment-panel"];

    public static IReadOnlyList<ScreenIssue> Validate(
        ScreenDocument document,
        IReadOnlyDictionary<int, Tag>? tags = null,
        IReadOnlySet<int>? targetScreenIds = null,
        bool templateDocument = false)
    {
        var issues = new List<ScreenIssue>();
        void Add(string id, string message) => issues.Add(new(id, message));
        if (document.SchemaVersion != 1) Add("", "画面格式版本不支持");
        if (!Finite(document.Width, 320, 7680) || !Finite(document.Height, 200, 4320)) Add("", "画布尺寸超出范围");
        if (!IsColor(document.Background)) Add("", "画布颜色必须为十六进制颜色");
        if (document.Nodes is null || document.Nodes.Count > 1000)
        {
            Add("", "每个画面最多 1000 个图元");
            return issues;
        }
        var ids = new HashSet<string>();
        foreach (var node in document.Nodes)
        {
            if (node is null) { Add("", "图元不能为空"); continue; }
            var id = node.Id ?? "";
            if (id.Length is < 1 or > 80 || !NodeIdPattern().IsMatch(id) || !ids.Add(id)) Add(id, "图元编号无效或重复");
            if (node.Type is null || !NodeTypes.Contains(node.Type)) Add(id, "图元类型不支持");
            if (!Finite(node.X, 0, document.Width) || !Finite(node.Y, 0, document.Height)
                || !Finite(node.Width, 16, document.Width) || !Finite(node.Height, 16, document.Height)
                || node.X + node.Width > document.Width + 0.01 || node.Y + node.Height > document.Height + 0.01)
                Add(id, "图元位置或尺寸超出画布");
            if (!Finite(node.Rotation, -360, 360)) Add(id, "旋转角度无效");
            if (!IsColor(node.Color) || !IsColor(node.ActiveColor)) Add(id, "图元颜色无效");
            if (node.Text is null || node.Text.Length > 500 || node.Unit is null || node.Unit.Length > 32) Add(id, "文字或单位过长");
            if (node.Decimals is < 0 or > 6 || !double.IsFinite(node.Min) || !double.IsFinite(node.Max)
                || node.Min >= node.Max || !double.IsFinite(node.ActiveThreshold)) Add(id, "量程或显示精度无效");
            if (node.Type == "symbol" && (node.Symbol is null || !SymbolPattern().IsMatch(node.Symbol))) Add(id, "图库符号无效");
            if (node.Points is { } points && (points.Count is < 2 or > 64 || points.Any(point => point is null || !Finite(point.X, 0, 100) || !Finite(point.Y, 0, 100))))
                Add(id, "管道折点必须为 2 至 64 个，坐标范围为 0 至 100");
            if (node.States is { } states && (states.Count > 16 || states.Any(state => state is null || !double.IsFinite(state.Value) || string.IsNullOrWhiteSpace(state.Label) || state.Label.Length > 40 || !IsColor(state.Color))
                || states.Where(state => state is not null).Select(state => state.Value).Distinct().Count() != states.Count))
                Add(id, "设备状态配置无效：最多 16 个状态，状态值不能重复");
            foreach (var port in new[] { node.Source, node.Target }.OfType<ScreenPort>())
                if (node.Type != "polyline" || node.Rotation != 0 || port.Port is not ("left" or "right" or "top" or "bottom")
                    || !document.Nodes.Any(other => other is not null && other.Id == port.NodeId && other.Id != id && other.Type != "polyline"))
                    Add(id, "管道连接端口无效");
            if (node.Commands is { } panelCommands)
            {
                if (panelCommands.Count > 8 || panelCommands.Any(command => command is null || !KeyPattern().IsMatch(command.Key ?? "") || string.IsNullOrWhiteSpace(command.Label) || command.Label.Length > 40)
                    || panelCommands.Where(command => command is not null).Select(command => command.Key).Distinct().Count() != panelCommands.Count) Add(id, "设备面板命令无效");
                foreach (var command in panelCommands.Where(command => command is not null))
                {
                    if (templateDocument ? command.TagId is not null || command.Bind is null || !KeyPattern().IsMatch(command.Bind)
                        : command.Bind is not null || command.TagId is not > 0 || tags is not null && (!tags.TryGetValue(command.TagId.Value, out var panelTag) || !panelTag.Writable)) Add(id, "设备面板命令必须绑定可写测点或模板占位符");
                    if (!double.IsFinite(command.Value)) Add(id, "设备面板命令值无效");
                    else if (tags is not null && command.TagId is int commandTagId && tags.TryGetValue(commandTagId, out var commandTag)
                        && !CommandValuePolicy.TryGetRawValue(commandTag, command.Value, out _, out var error)) Add(id, error);
                }
            }
            if (node.Tags is { } chartTags)
            {
                if (chartTags.Count > 12) Add(id, "图表绑定的测点过多");
                if (chartTags.Any(t => t <= 0 || tags is not null && !tags.ContainsKey(t))) Add(id, "图表绑定的测点不存在");
            }
            if (node.WindowSec is double windowSec && (!double.IsFinite(windowSec) || windowSec < 10 || windowSec > 86400)) Add(id, "统计窗口超出范围");
            if (node.Animation is { } anim)
            {
                if (anim.Type is not ("rotate" or "level" or "flow")) Add(id, "动画类型不支持");
                if (anim.Source is not ("value" or "state")) Add(id, "动画触发方式不支持");
                if (anim.TagId is int animTag && (animTag <= 0 || tags is not null && !tags.ContainsKey(animTag))) Add(id, "动画绑定的测点不存在");
                if (!double.IsFinite(anim.From) || !double.IsFinite(anim.To) || anim.From >= anim.To
                    || !double.IsFinite(anim.OutFrom) || !double.IsFinite(anim.OutTo) || anim.OutFrom >= anim.OutTo)
                    Add(id, "动画映射区间无效");
            }
            if (node.LineId is int lineId && lineId <= 0) Add(id, "OEE 产线无效");
            if (node.Type is "switch" or "slider" or "spinner" or "input" && node.Action?.Type != "command") Add(id, "控件必须配置控制动作");
            if (node.Type == "template")
            {
                if (templateDocument) Add(id, "模板内不能再嵌套模板");
                else if (node.TemplateKey is null || !KeyPattern().IsMatch(node.TemplateKey)) Add(id, "模板键无效");
                if (node.Bindings is { Count: > 100 } || node.Bindings?.Any(b => b is null || !KeyPattern().IsMatch(b.Key ?? "")) == true) Add(id, "模板绑定无效");
                foreach (var b in node.Bindings ?? []) if (b?.TagId is int bt && (bt <= 0 || tags is not null && !tags.ContainsKey(bt))) Add(id, $"占位符 {b.Key} 绑定的测点不存在");
            }
            if (node.Texts is { } texts && (texts.Count > 10 || texts.Any(x => x.Key.Length is 0 or > 10 || x.Value is null || x.Value.Length > 500))) Add(id, "多语言文字无效");
            if (!templateDocument && (node.Bind is not null || node.AlarmBind is not null || node.Action?.Bind is not null)) Add(id, "占位符只能用于图库模板");
            if (templateDocument && (node.TagId is not null || node.AlarmTagId is not null || node.Action?.TagId is not null)) Add(id, "模板内请使用占位符而不是具体测点");
            foreach (var bind in new[] { node.Bind, node.AlarmBind, node.Action?.Bind }) if (bind is not null && !KeyPattern().IsMatch(bind)) Add(id, $"占位符 {bind} 无效");
            if (node.AlarmTagId is int alarmTagId && (alarmTagId <= 0 || tags is not null && !tags.ContainsKey(alarmTagId))) Add(id, "报警状态测点不存在");
            if (node.TagId is int tagId && (tagId <= 0 || tags is not null && !tags.ContainsKey(tagId))) Add(id, "绑定的测点不存在");
            var action = node.Action;
            if (action is null) { Add(id, "动作不能为空"); continue; }
            if (action.Type is not ("none" or "navigate" or "popup" or "command")) Add(id, "动作类型不支持");
            if (action.Type is "navigate" or "popup"
                && (action.ScreenId is not > 0 || targetScreenIds is not null && !targetScreenIds.Contains(action.ScreenId.Value)))
                Add(id, "目标画面不存在或尚未发布");
            if (action.Type == "command" && templateDocument)
            {
                if (action.Bind is null) Add(id, "模板中的控制动作必须使用占位符");
                if (!double.IsFinite(action.Value)) Add(id, "命令值无效");
            }
            else if (action.Type == "command")
            {
                if (action.TagId is not > 0 || tags is not null && (!tags.TryGetValue(action.TagId.Value, out var tag) || !tag.Writable))
                    Add(id, "动作必须绑定可写测点");
                if (!double.IsFinite(action.Value)) Add(id, "命令值无效");
                else if (tags is not null && action.TagId is int commandTagId && tags.TryGetValue(commandTagId, out var commandTag)
                    && !CommandValuePolicy.TryGetRawValue(commandTag, action.Value, out _, out var error))
                    Add(id, error);
            }
        }
        return issues;
    }

    private static bool Finite(double value, double min, double max) => double.IsFinite(value) && value >= min && value <= max;
    private static bool IsColor(string? value) => value is not null && ColorPattern().IsMatch(value);
    [GeneratedRegex("^#[0-9a-fA-F]{6}$")]
    private static partial Regex ColorPattern();
    [GeneratedRegex("^[a-zA-Z0-9_-]+$")]
    private static partial Regex NodeIdPattern();
    [GeneratedRegex("^(isa|user):[a-z0-9][a-z0-9_-]{0,63}$")]
    private static partial Regex SymbolPattern();
    [GeneratedRegex("^[a-zA-Z][a-zA-Z0-9_-]{0,63}$")]
    public static partial Regex KeyPattern();
}