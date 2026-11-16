using System.Collections.Frozen;

namespace Scada.Core.Models;

// 图元能力位：把“哪些图元支持哪些字段”的谓词收拢到一张表，校验与展开都从这里取。
// 只描述能力，不反向拒绝：旧文档里出现的字段不会因为注册表收窄而突然失效。
[Flags]
public enum ScreenNodeFeature
{
    DataBinding = 1 << 0,
    MultiTag = 1 << 1,
    Window = 1 << 2,
    OeeLine = 1 << 3,
    GaugeBand = 1 << 4,
    Setpoint = 1 << 5,
    TableColumns = 1 << 6,
    ChartOptions = 1 << 7,
    ChartTargetLines = 1 << 8,
    PanelCommands = 1 << 9,
    PipePorts = 1 << 10,
    States = 1 << 11,
    Animation = 1 << 12,
    TemplateInstance = 1 << 13,
    Placeholder = 1 << 14,
    Momentary = 1 << 15,
    Positional = 1 << 16,
    Reset = 1 << 17,
    Silence = 1 << 18,
    Recipe = 1 << 19,
    RequiresCommand = 1 << 20,
    Symbol = 1 << 21
}

public sealed record ScreenNodeSpec(string Type, ScreenNodeFeature Features, IReadOnlyList<string>? TableColumns = null);

// 画面图元注册表：类型全集、能力位与字段的展开语义（原样复制 / 变换 / 清空）。
public static class ScreenNodeRegistry
{
    private static readonly string[] TableCardColumns = ["name", "value", "unit"];
    private static readonly string[] HistoryTableColumns = ["name", "last", "min", "max", "avg"];

    public static readonly FrozenDictionary<string, ScreenNodeSpec> Specs = new[]
    {
        new ScreenNodeSpec("text", 0),
        new ScreenNodeSpec("value", ScreenNodeFeature.DataBinding),
        new ScreenNodeSpec("pump", ScreenNodeFeature.DataBinding | ScreenNodeFeature.States | ScreenNodeFeature.Animation),
        new ScreenNodeSpec("valve", ScreenNodeFeature.DataBinding | ScreenNodeFeature.States | ScreenNodeFeature.Animation),
        new ScreenNodeSpec("tank", ScreenNodeFeature.DataBinding | ScreenNodeFeature.States | ScreenNodeFeature.Animation),
        new ScreenNodeSpec("pipe", ScreenNodeFeature.DataBinding | ScreenNodeFeature.Animation),
        new ScreenNodeSpec("rectangle", 0),
        new ScreenNodeSpec("ellipse", 0),
        new ScreenNodeSpec("button", 0),
        new ScreenNodeSpec("gauge", ScreenNodeFeature.DataBinding | ScreenNodeFeature.GaugeBand | ScreenNodeFeature.Setpoint),
        new ScreenNodeSpec("motor", ScreenNodeFeature.DataBinding | ScreenNodeFeature.States),
        new ScreenNodeSpec("fan", ScreenNodeFeature.DataBinding | ScreenNodeFeature.States),
        new ScreenNodeSpec("symbol", ScreenNodeFeature.DataBinding | ScreenNodeFeature.States | ScreenNodeFeature.Animation | ScreenNodeFeature.Symbol),
        new ScreenNodeSpec("template", ScreenNodeFeature.TemplateInstance | ScreenNodeFeature.Placeholder),
        new ScreenNodeSpec("led", ScreenNodeFeature.DataBinding),
        new ScreenNodeSpec("progress", ScreenNodeFeature.DataBinding),
        new ScreenNodeSpec("kpi", ScreenNodeFeature.DataBinding),
        new ScreenNodeSpec("clock", 0),
        new ScreenNodeSpec("image", 0),
        new ScreenNodeSpec("marquee", 0),
        new ScreenNodeSpec("dial", ScreenNodeFeature.DataBinding | ScreenNodeFeature.GaugeBand | ScreenNodeFeature.Setpoint),
        new ScreenNodeSpec("chart-bar", ScreenNodeFeature.MultiTag | ScreenNodeFeature.ChartOptions | ScreenNodeFeature.ChartTargetLines),
        new ScreenNodeSpec("chart-pie", ScreenNodeFeature.MultiTag | ScreenNodeFeature.ChartOptions),
        new ScreenNodeSpec("chart-line", ScreenNodeFeature.MultiTag | ScreenNodeFeature.Window | ScreenNodeFeature.ChartOptions | ScreenNodeFeature.ChartTargetLines),
        new ScreenNodeSpec("chart-bar-horizontal", ScreenNodeFeature.MultiTag | ScreenNodeFeature.ChartOptions | ScreenNodeFeature.ChartTargetLines),
        new ScreenNodeSpec("chart-bar-stack", ScreenNodeFeature.MultiTag | ScreenNodeFeature.ChartOptions | ScreenNodeFeature.ChartTargetLines),
        new ScreenNodeSpec("chart-area", ScreenNodeFeature.MultiTag | ScreenNodeFeature.Window | ScreenNodeFeature.ChartOptions | ScreenNodeFeature.ChartTargetLines),
        new ScreenNodeSpec("chart-radar", ScreenNodeFeature.MultiTag | ScreenNodeFeature.ChartOptions),
        new ScreenNodeSpec("chart-funnel", ScreenNodeFeature.MultiTag | ScreenNodeFeature.ChartOptions),
        new ScreenNodeSpec("chart-gauge", ScreenNodeFeature.ChartOptions),
        new ScreenNodeSpec("alarm-banner", 0),
        new ScreenNodeSpec("alarm-stats", 0),
        new ScreenNodeSpec("alarm-list", 0),
        new ScreenNodeSpec("table", ScreenNodeFeature.MultiTag | ScreenNodeFeature.TableColumns, TableCardColumns),
        new ScreenNodeSpec("history-table", ScreenNodeFeature.MultiTag | ScreenNodeFeature.Window | ScreenNodeFeature.TableColumns, HistoryTableColumns),
        new ScreenNodeSpec("oee-kpi", ScreenNodeFeature.Window | ScreenNodeFeature.OeeLine),
        new ScreenNodeSpec("switch", ScreenNodeFeature.RequiresCommand),
        new ScreenNodeSpec("slider", ScreenNodeFeature.RequiresCommand),
        new ScreenNodeSpec("spinner", ScreenNodeFeature.RequiresCommand),
        new ScreenNodeSpec("input", ScreenNodeFeature.RequiresCommand),
        new ScreenNodeSpec("polyline", ScreenNodeFeature.DataBinding | ScreenNodeFeature.PipePorts | ScreenNodeFeature.Animation),
        new ScreenNodeSpec("equipment-panel", ScreenNodeFeature.DataBinding | ScreenNodeFeature.PanelCommands | ScreenNodeFeature.States),
        new ScreenNodeSpec("reset", ScreenNodeFeature.Reset),
        new ScreenNodeSpec("jog", ScreenNodeFeature.Momentary | ScreenNodeFeature.RequiresCommand),
        new ScreenNodeSpec("pulse", ScreenNodeFeature.Momentary | ScreenNodeFeature.RequiresCommand),
        new ScreenNodeSpec("silence", ScreenNodeFeature.Silence),
        new ScreenNodeSpec("selector", ScreenNodeFeature.Positional | ScreenNodeFeature.States | ScreenNodeFeature.RequiresCommand),
        new ScreenNodeSpec("knob", ScreenNodeFeature.Positional | ScreenNodeFeature.States | ScreenNodeFeature.RequiresCommand),
        new ScreenNodeSpec("recipe", ScreenNodeFeature.Recipe)
    }.ToFrozenDictionary(spec => spec.Type, StringComparer.Ordinal);

    public static IReadOnlySet<string> Types { get; } = Specs.Keys.ToFrozenSet(StringComparer.Ordinal);

    public static bool Has(string? type, ScreenNodeFeature feature) =>
        type is not null && Specs.TryGetValue(type, out var spec) && (spec.Features & feature) == feature;

    // null 表示该类型不支持显示列；非 null 为可选列。
    public static IReadOnlyList<string>? TableColumns(string? type) =>
        type is not null && Specs.TryGetValue(type, out var spec) ? spec.TableColumns : null;

    // 展开语义分类：ScreenNode 的每个属性都必须恰好属于其中一组（反射测试锁定）。
    public static readonly FrozenSet<string> VerbatimFields = new[]
    {
        nameof(ScreenNode.Type), nameof(ScreenNode.Rotation), nameof(ScreenNode.Text), nameof(ScreenNode.Color),
        nameof(ScreenNode.ActiveColor), nameof(ScreenNode.Unit), nameof(ScreenNode.Decimals), nameof(ScreenNode.Min),
        nameof(ScreenNode.Max), nameof(ScreenNode.ActiveThreshold), nameof(ScreenNode.Symbol), nameof(ScreenNode.Texts),
        nameof(ScreenNode.FontSize), nameof(ScreenNode.Align), nameof(ScreenNode.Wrap), nameof(ScreenNode.AutoFit),
        nameof(ScreenNode.Bold), nameof(ScreenNode.Opacity), nameof(ScreenNode.BorderWidth), nameof(ScreenNode.Radius),
        nameof(ScreenNode.Hidden), nameof(ScreenNode.WindowSec), nameof(ScreenNode.LineId), nameof(ScreenNode.Animation),
        nameof(ScreenNode.Points), nameof(ScreenNode.States), nameof(ScreenNode.ReleaseValue), nameof(ScreenNode.PulseMs),
        nameof(ScreenNode.MaxHoldMs), nameof(ScreenNode.ResetMode), nameof(ScreenNode.SilenceMinutes),
        nameof(ScreenNode.RecipeTemplateId), nameof(ScreenNode.GaugeBands), nameof(ScreenNode.Setpoint),
        nameof(ScreenNode.TableColumns), nameof(ScreenNode.ChartOptions)
    }.ToFrozenSet(StringComparer.Ordinal);

    public static readonly FrozenSet<string> TransformedFields = new[]
    {
        nameof(ScreenNode.Id), nameof(ScreenNode.X), nameof(ScreenNode.Y), nameof(ScreenNode.Width), nameof(ScreenNode.Height),
        nameof(ScreenNode.TagId), nameof(ScreenNode.AlarmTagId), nameof(ScreenNode.Tags), nameof(ScreenNode.Source),
        nameof(ScreenNode.Target), nameof(ScreenNode.Commands), nameof(ScreenNode.Action)
    }.ToFrozenSet(StringComparer.Ordinal);

    public static readonly FrozenSet<string> ClearedFields = new[]
    {
        nameof(ScreenNode.Locked), nameof(ScreenNode.GroupId), nameof(ScreenNode.TemplateKey), nameof(ScreenNode.Bindings),
        nameof(ScreenNode.Bind), nameof(ScreenNode.AlarmBind), nameof(ScreenNode.TagBinds), nameof(ScreenNode.AnimationBind)
    }.ToFrozenSet(StringComparer.Ordinal);
}
