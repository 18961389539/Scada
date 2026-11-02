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
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public Dictionary<string, string>? SymbolAssets { get; set; }
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
    // 主文字字号：为空时按控件类型使用默认字号，旧画面逐字不变。
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public double? FontSize { get; set; }
    // 文字与形状样式：对齐、自动换行/缩放、加粗，以及形状透明度、边框宽度与圆角。
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Align { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? Wrap { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? AutoFit { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? Bold { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public double? Opacity { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public double? BorderWidth { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public double? Radius { get; set; }
    // 编辑辅助：成组编号、锁定（不可拖动/框选）与隐藏（运行时不渲染）。
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? GroupId { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? Locked { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? Hidden { get; set; }
    // 图表/表格类图元绑定的多个测点。
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<int>? Tags { get; set; }
    // 仅模板内部：多测点（按顺序）与动画驱动测点的占位符。
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<string>? TagBinds { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? AnimationBind { get; set; }
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
    // 点动/瞬时按钮：释放值、脉冲宽度（瞬时）、最长保持时间（点动）。
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public double? ReleaseValue { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? PulseMs { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? MaxHoldMs { get; set; }
    // 复位按钮：alarms 确认可见报警；tag 向测点写复位命令。
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? ResetMode { get; set; }
    // 消音按钮：消音时长（分钟）。
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? SilenceMinutes { get; set; }
    // 配方控件：画面内选择并下发该模板下的配方。
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? RecipeTemplateId { get; set; }
    // 仪表色带：在量程弧线上按区间着色；设定值为仪表上的标记。
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<ScreenGaugeBand>? GaugeBands { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public double? Setpoint { get; set; }
    // 表格显示列：table 使用 name/value/unit；history-table 使用 name/last/min/max/avg。
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<string>? TableColumns { get; set; }
    // 图表显示选项与目标线。
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public ScreenChartOptions? ChartOptions { get; set; }
}

public sealed record ScreenPoint(double X, double Y);
public sealed record ScreenState(double Value, string Label, string Color);
public sealed record ScreenGaugeBand(double From, double To, string Color);
public sealed record ScreenTargetLine(double Value, string Color, string? Label);
public sealed class ScreenChartOptions
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? Legend { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? Grid { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? DataLabels { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? YUnit { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<ScreenTargetLine>? TargetLines { get; set; }
}
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
// 草稿使用当前图库，发布时将展开结果固化，运行与控制均不再读取可变模板。
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
                    Unit = inner.Unit, Decimals = inner.Decimals, Min = inner.Min, Max = inner.Max, ActiveThreshold = inner.ActiveThreshold, Symbol = inner.Symbol, Texts = inner.Texts, FontSize = inner.FontSize,
                    Align = inner.Align, Wrap = inner.Wrap, AutoFit = inner.AutoFit, Bold = inner.Bold, Opacity = inner.Opacity, BorderWidth = inner.BorderWidth, Radius = inner.Radius,
                    Tags = inner.TagBinds is not null ? inner.TagBinds.Select(Resolve).Where(id => id.HasValue).Select(id => id!.Value).ToList() : inner.Tags,
                    Animation = inner.Animation is { } animation ? new ScreenAnimation { Type = animation.Type, Source = animation.Source,
                        TagId = inner.AnimationBind is not null ? Resolve(inner.AnimationBind) : animation.TagId, From = animation.From, To = animation.To, OutFrom = animation.OutFrom, OutTo = animation.OutTo } : null,
                    WindowSec = inner.WindowSec, LineId = inner.LineId, Points = inner.Points, States = inner.States,
                    Source = inner.Source is { } source ? source with { NodeId = $"{node.Id}{Separator}{source.NodeId}" } : null,
                    Target = inner.Target is { } target ? target with { NodeId = $"{node.Id}{Separator}{target.NodeId}" } : null,
                    Commands = inner.Commands?.Select(command => command with { TagId = Resolve(command.Bind), Bind = null }).ToList(),
                    ReleaseValue = inner.ReleaseValue, PulseMs = inner.PulseMs, MaxHoldMs = inner.MaxHoldMs, ResetMode = inner.ResetMode, SilenceMinutes = inner.SilenceMinutes,
                    RecipeTemplateId = inner.RecipeTemplateId,
                    GaugeBands = inner.GaugeBands, Setpoint = inner.Setpoint, TableColumns = inner.TableColumns, ChartOptions = inner.ChartOptions,
                    TagId = inner.Bind is not null ? Resolve(inner.Bind) : null, AlarmTagId = inner.AlarmBind is not null ? Resolve(inner.AlarmBind) : null,
                    Action = new ScreenAction { Type = inner.Action?.Type ?? "none", ScreenId = inner.Action?.ScreenId, Value = inner.Action?.Value ?? 0,
                        SequenceId = inner.Action?.SequenceId, RecipeId = inner.Action?.RecipeId, JobId = inner.Action?.JobId,
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
    // 动作扩展：启动顺控流程或下发配方。
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? SequenceId { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? RecipeId { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? JobId { get; set; }
    // 仅模板内部：控制输出占位符。
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Bind { get; set; }
}

// 动作类型与目标字段的唯一对应表：校验拒绝无关字段，设计器按同一张表清理残留目标。
public static class ScreenActionRules
{
    public readonly record struct Targets(bool Screen, bool Tag, bool Bind, bool Sequence, bool Recipe, bool Job = false);

    public static Targets For(string? type) => type switch
    {
        "navigate" or "popup" => new(true, false, false, false, false),
        "command" => new(false, true, true, false, false),
        "sequence" => new(false, false, false, true, false),
        "recipe" => new(false, false, false, false, true),
        "script" => new(false, false, false, false, false, true),
        _ => default
    };

    public static bool HasForeignTarget(ScreenAction action)
    {
        var targets = For(action.Type);
        return (!targets.Screen && action.ScreenId is not null) || (!targets.Tag && action.TagId is not null)
            || (!targets.Bind && action.Bind is not null) || (!targets.Sequence && action.SequenceId is not null)
            || (!targets.Recipe && action.RecipeId is not null) || (!targets.Job && action.JobId is not null);
    }
}

public sealed record ScreenIssue(string NodeId, string Message);

public static partial class ScreenValidator
{
    public static readonly HashSet<string> NodeTypes =
        ["text", "value", "pump", "valve", "tank", "pipe", "rectangle", "ellipse", "button", "gauge", "motor", "fan", "symbol", "template",
         "led", "progress", "kpi", "clock", "image", "marquee", "dial", "chart-bar", "chart-pie", "chart-line", "chart-bar-horizontal", "chart-bar-stack", "chart-area", "chart-radar", "chart-funnel", "chart-gauge", "alarm-banner", "alarm-stats", "alarm-list", "table", "history-table", "oee-kpi", "switch", "slider", "spinner", "input", "polyline", "equipment-panel",
         "reset", "jog", "pulse", "silence", "selector", "knob", "recipe"];

    public static IReadOnlyList<ScreenIssue> Validate(
        ScreenDocument document,
        IReadOnlyDictionary<int, Tag>? tags = null,
        IReadOnlySet<int>? targetScreenIds = null,
        bool templateDocument = false,
        bool publishedDocument = false,
        bool expandedDocument = false)
    {
        var issues = new List<ScreenIssue>();
        void Add(string id, string message) => issues.Add(new(id, message));
        if (document.SymbolAssets is not null && !publishedDocument) Add("", "符号快照只能由发布生成，不能写入草稿或模板");
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
            if (!ValidNodeId(id, expandedDocument) || !ids.Add(id)) Add(id, "图元编号无效或重复");
            if (node.Type is null || !NodeTypes.Contains(node.Type)) Add(id, "图元类型不支持");
            if (!Finite(node.X, 0, document.Width) || !Finite(node.Y, 0, document.Height)
                || !Finite(node.Width, 16, document.Width) || !Finite(node.Height, 16, document.Height)
                || node.X + node.Width > document.Width + 0.01 || node.Y + node.Height > document.Height + 0.01)
                Add(id, "图元位置或尺寸超出画布");
            if (!Finite(node.Rotation, -360, 360)) Add(id, "旋转角度无效");
            if (!IsColor(node.Color) || !IsColor(node.ActiveColor)) Add(id, "图元颜色无效");
            if (node.Text is null || node.Text.Length > 500 || node.Unit is null || node.Unit.Length > 32) Add(id, "文字或单位过长");
            if (node.FontSize is double fontSize && !Finite(fontSize, 8, 96)) Add(id, "字号必须在 8 至 96 之间");
            if (node.Type == "image" && !IsImageUrl(node.Text)) Add(id, "图片地址必须以 / 或 http(s):// 开头");
            if (node.Align is not null && node.Align is not ("left" or "center" or "right")) Add(id, "文字对齐方式不支持");
            if (node.Opacity is double opacity && (!double.IsFinite(opacity) || opacity < 0 || opacity > 1)) Add(id, "透明度必须在 0 至 1 之间");
            if (node.BorderWidth is double borderWidth && (!double.IsFinite(borderWidth) || borderWidth < 0 || borderWidth > 10)) Add(id, "边框宽度必须在 0 至 10 之间");
            if (node.Radius is double radius && (!double.IsFinite(radius) || radius < 0 || radius > 200)) Add(id, "圆角必须在 0 至 200 之间");
            if (node.GroupId is not null && !GroupPattern().IsMatch(node.GroupId)) Add(id, "分组编号无效");
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
            if (templateDocument)
            {
                if (node.Tags is { Count: > 0 }) Add(id, "模板内的多测点必须使用占位符");
                if (node.Animation?.TagId is not null) Add(id, "模板内的动画测点必须使用占位符");
            }
            else
            {
                if (node.TagBinds is not null) Add(id, "多测点占位符只能用于图库模板");
                if (node.AnimationBind is not null) Add(id, "动画占位符只能用于图库模板");
            }
            if (node.TagBinds is { } tagBinds)
            {
                if (tagBinds.Count > 12 || tagBinds.Any(bind => bind is null || !KeyPattern().IsMatch(bind))) Add(id, "多测点占位符无效或过多");
            }
            if (node.AnimationBind is not null && (node.Animation is null || !KeyPattern().IsMatch(node.AnimationBind))) Add(id, "动画占位符无效");
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
            if (node.GaugeBands is { } gaugeBands)
            {
                if (node.Type is not ("gauge" or "dial")) Add(id, "色带只能用于仪表或表盘");
                else if (gaugeBands.Count > 6) Add(id, "仪表色带最多 6 条");
                else if (gaugeBands.Any(band => band is null || !double.IsFinite(band.From) || !double.IsFinite(band.To)
                    || band.From >= band.To || band.From < node.Min || band.To > node.Max || !IsColor(band.Color)))
                    Add(id, "仪表色带必须在量程内且起始值小于结束值");
            }
            if (node.Setpoint is double setpoint)
            {
                if (node.Type is not ("gauge" or "dial")) Add(id, "设定值标记只能用于仪表或表盘");
                else if (!double.IsFinite(setpoint) || setpoint < node.Min || setpoint > node.Max) Add(id, "设定值必须在量程内");
            }
            if (node.TableColumns is { } tableColumns)
            {
                var allowedColumns = node.Type switch
                {
                    "table" => new[] { "name", "value", "unit" },
                    "history-table" => new[] { "name", "last", "min", "max", "avg" },
                    _ => Array.Empty<string>()
                };
                if (allowedColumns.Length == 0) Add(id, "显示列只能用于数据表或历史统计表");
                else if (tableColumns.Count is < 1 || tableColumns.Count > allowedColumns.Length
                    || tableColumns.Any(column => column is null || !allowedColumns.Contains(column))
                    || tableColumns.Distinct().Count() != tableColumns.Count)
                    Add(id, "表格显示列无效或重复");
            }
            if (node.ChartOptions is { } chartOptions)
            {
                var chartTypes = new[] { "chart-bar", "chart-pie", "chart-line", "chart-bar-horizontal", "chart-bar-stack", "chart-area", "chart-radar", "chart-funnel", "chart-gauge" };
                if (!chartTypes.Contains(node.Type)) Add(id, "图表选项只能用于图表图元");
                else
                {
                    if (chartOptions.YUnit is { Length: > 32 }) Add(id, "图表 Y 轴单位过长");
                    if (chartOptions.TargetLines is { } targetLines)
                    {
                        if (targetLines.Count > 4) Add(id, "图表目标线最多 4 条");
                        else if (targetLines.Any(line => line is null || !double.IsFinite(line.Value) || !IsColor(line.Color) || line.Label is { Length: > 40 }))
                            Add(id, "图表目标线配置无效");
                        else if (targetLines.Count > 0 && node.Type is not ("chart-line" or "chart-area" or "chart-bar" or "chart-bar-horizontal" or "chart-bar-stack"))
                            Add(id, "该图表类型不支持目标线");
                    }
                }
            }
            if (node.Type == "reset" && node.ResetMode is not (null or "alarms" or "tag")) Add(id, "复位模式不支持");
            if (node.Type == "silence" && node.SilenceMinutes is int silenceMinutes && silenceMinutes is < 1 or > 120) Add(id, "消音时长必须在 1 至 120 分钟之间");
            if (node.Type is "jog" or "pulse")
            {
                if (node.ReleaseValue is not double releaseValue) Add(id, "点动或瞬时按钮必须配置释放值");
                else if (!double.IsFinite(releaseValue)) Add(id, "释放值无效");
                else if (node.Action is { } releaseAction && double.IsFinite(releaseAction.Value) && releaseValue == releaseAction.Value) Add(id, "释放值必须与动作值不同");
                else if (tags is not null && node.Action?.TagId is int releaseTagId && tags.TryGetValue(releaseTagId, out var releaseTag)
                    && !CommandValuePolicy.TryGetRawValue(releaseTag, releaseValue, out _, out var releaseError)) Add(id, releaseError);
                if (node.Type == "jog" && node.MaxHoldMs is int maxHoldMs && maxHoldMs is < 500 or > 30000) Add(id, "最长保持时间必须在 0.5 至 30 秒之间");
                if (node.Type == "pulse" && node.PulseMs is int pulseMs && pulseMs is < 100 or > 5000) Add(id, "脉冲宽度必须在 100 至 5000 毫秒之间");
            }
            if (node.Type is "selector" or "knob")
            {
                if (node.States is not { Count: >= 2 and <= 8 } positions) Add(id, "选择开关或旋钮必须配置 2 至 8 个位置");
                else if (node.Action?.TagId is int positionTagId && tags is not null && tags.TryGetValue(positionTagId, out var positionTag))
                    foreach (var position in positions)
                        if (!CommandValuePolicy.TryGetRawValue(positionTag, position.Value, out _, out var positionError)) { Add(id, positionError); break; }
            }
            if (node.Type == "recipe")
            {
                if (node.RecipeTemplateId is not > 0) Add(id, "配方控件必须选择配方模板");
                if (node.Action?.Type is not "none") Add(id, "配方控件不支持画面动作");
            }
            var requiresCommand = node.Type is "switch" or "slider" or "spinner" or "input" or "jog" or "pulse" or "selector" or "knob"
                || node.Type == "reset" && node.ResetMode == "tag";
            if (requiresCommand && node.Action?.Type != "command") Add(id, "控件必须配置控制动作");
            if (node.Type == "template")
            {
                if (templateDocument) Add(id, "模板内不能再嵌套模板");
                else if (node.TemplateKey is null || !KeyPattern().IsMatch(node.TemplateKey)) Add(id, "模板键无效");
                if (node.Bindings is { Count: > 100 } || node.Bindings?.Any(b => b is null || !KeyPattern().IsMatch(b.Key ?? "")) == true
                    || node.Bindings is { } bindings && bindings.Select(b => b?.Key).Distinct().Count() != bindings.Count) Add(id, "模板绑定无效或占位符重复");
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
            if (action.Type is not ("none" or "navigate" or "popup" or "command" or "sequence" or "recipe" or "script")) Add(id, "动作类型不支持");
            if (ScreenActionRules.HasForeignTarget(action)) Add(id, "动作配置包含与动作类型无关的目标");
            if (action.Type == "sequence" && action.SequenceId is not > 0) Add(id, "顺控动作必须选择顺控流程");
            if (action.Type == "recipe" && action.RecipeId is not > 0) Add(id, "配方动作必须选择配方");
            if (action.Type == "script" && action.JobId is not > 0) Add(id, "脚本动作必须选择已发布的计算任务");
            if (action.Type == "script" && !double.IsFinite(action.Value)) Add(id, "脚本参数必须是有限数值");
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
    // 草稿编号保留 "--" 给模板展开后的组合编号，并禁止首尾连字符，保证实例与内部编号的拼接可唯一拆分。
    private static bool ValidNodeId(string id, bool expanded)
    {
        if (id.Length is >= 1 and <= 80 && NodeIdPattern().IsMatch(id)) return true;
        if (!expanded || id.Length > 162) return false;
        var parts = id.Split(ScreenExpander.Separator, StringSplitOptions.None);
        return parts.Length == 2 && parts.All(part => part.Length is >= 1 and <= 80 && NodeIdPattern().IsMatch(part));
    }
    private static bool IsColor(string? value) => value is not null && ColorPattern().IsMatch(value);
    // 图片只允许站内相对路径或 http(s) 绝对地址，避免 javascript:/data: 等可执行 URL。
    private static bool IsImageUrl(string? value) => value is null || value.Length == 0 || ImageUrlPattern().IsMatch(value);
    [GeneratedRegex("^(/|\\./|\\.\\./|https?://)", RegexOptions.IgnoreCase)]
    private static partial Regex ImageUrlPattern();
    [GeneratedRegex("^[a-zA-Z0-9_-]{1,80}$")]
    private static partial Regex GroupPattern();
    [GeneratedRegex("^#[0-9a-fA-F]{6}$")]
    private static partial Regex ColorPattern();
    [GeneratedRegex("^(?!.*--)[a-zA-Z0-9_](?:[a-zA-Z0-9_-]*[a-zA-Z0-9_])?$")]
    private static partial Regex NodeIdPattern();
    [GeneratedRegex("^(isa|user):[a-z0-9][a-z0-9_-]{0,63}$")]
    private static partial Regex SymbolPattern();
    [GeneratedRegex("^[a-zA-Z][a-zA-Z0-9_-]{0,63}$")]
    public static partial Regex KeyPattern();
}