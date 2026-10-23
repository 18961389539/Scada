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
}

public sealed class ScreenAction
{
    public string Type { get; set; } = "none";
    public int? ScreenId { get; set; }
    public int? TagId { get; set; }
    public double Value { get; set; }
}

public sealed record ScreenIssue(string NodeId, string Message);

public static partial class ScreenValidator
{
    public static readonly HashSet<string> NodeTypes =
        ["text", "value", "pump", "valve", "tank", "pipe", "rectangle", "ellipse", "button", "gauge", "motor", "fan"];

    public static IReadOnlyList<ScreenIssue> Validate(
        ScreenDocument document,
        IReadOnlyDictionary<int, Tag>? tags = null,
        IReadOnlySet<int>? targetScreenIds = null)
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
            if (node.TagId is int tagId && (tagId <= 0 || tags is not null && !tags.ContainsKey(tagId))) Add(id, "绑定的测点不存在");
            var action = node.Action;
            if (action is null) { Add(id, "动作不能为空"); continue; }
            if (action.Type is not ("none" or "navigate" or "popup" or "command")) Add(id, "动作类型不支持");
            if (action.Type is "navigate" or "popup"
                && (action.ScreenId is not > 0 || targetScreenIds is not null && !targetScreenIds.Contains(action.ScreenId.Value)))
                Add(id, "目标画面不存在或尚未发布");
            if (action.Type == "command")
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
}
