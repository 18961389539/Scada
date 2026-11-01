using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Scada.Core.Models;
using Scada.Data;
using Scada.Server.Endpoints;

namespace Scada.Server.Screens;

internal sealed record ScreenPublicationResult(ScreenDocument? Document, IReadOnlyList<ScreenIssue> Issues);

internal static class ScreenPublication
{
    internal const string RepublishMessage = "此画面的旧发布版本未固化图库，请由组态人员重新发布后再运行或控制";

    internal static bool RequiresRepublish(ScreenDocument document) => (document.Nodes ?? []).Any(node =>
        node?.Type == "template" || node?.Symbol is { } symbol && symbol.StartsWith("user:", StringComparison.Ordinal)
        && (document.SymbolAssets is null || !document.SymbolAssets.ContainsKey(symbol)));

    internal static async Task<ScreenPublicationResult> PrepareAsync(ConfigDbContext db, ScreenDocument document, int? selfId, CancellationToken ct)
    {
        var tags = await db.Tags.AsNoTracking().ToDictionaryAsync(x => x.Id, ct);
        var ids = (await db.ProcessScreens.Where(x => x.PublishedJson != null).Select(x => x.Id).ToListAsync(ct)).ToHashSet();
        if (selfId is int id) ids.Add(id);
        var issues = ScreenValidator.Validate(document, tags, ids).ToList();
        if (issues.Count > 0) return new(null, issues);
        var lookup = await HmiLibraryEndpoints.LookupAsync(db, ct);
        issues.AddRange(ScreenExpander.InstanceIssues(document, lookup));
        if (issues.Count > 0) return new(null, issues);
        var expanded = ScreenExpander.Expand(document, lookup);
        FreezeTemplateConnections(document, expanded);
        foreach (var issue in ScreenValidator.Validate(expanded, tags, ids, expandedDocument: true))
        {
            var separator = issue.NodeId.IndexOf(ScreenExpander.Separator, StringComparison.Ordinal);
            issues.Add(separator < 0 ? issue : new ScreenIssue(issue.NodeId[..separator], $"模板内 {issue.NodeId[(separator + 2)..]}：{issue.Message}"));
        }
        var symbolKeys = expanded.Nodes.Select(node => node.Symbol).OfType<string>()
            .Where(symbol => symbol.StartsWith("user:", StringComparison.Ordinal)).Distinct().ToList();
        if (symbolKeys.Count > 0)
        {
            var keys = symbolKeys.Select(symbol => symbol[5..]).ToList();
            var symbols = await db.HmiLibrary.AsNoTracking().Where(x => x.Kind == "symbol" && keys.Contains(x.Key)).ToDictionaryAsync(x => $"user:{x.Key}", ct);
            expanded.SymbolAssets = [];
            foreach (var key in symbolKeys)
            {
                if (!symbols.TryGetValue(key, out var item) || item.Svg is null)
                    issues.Add(new("", $"图库符号 {key} 不存在"));
                else expanded.SymbolAssets.Add(key, item.Svg);
            }
            issues.AddRange(ValidateAssets(expanded));
        }
        return new(issues.Count == 0 ? expanded : null, issues.Distinct().ToList());
    }

    internal static async Task<IReadOnlyList<ScreenIssue>> ValidatePublishedAsync(ConfigDbContext db, ScreenDocument document, int? selfId, CancellationToken ct)
    {
        if (RequiresRepublish(document)) return [new("", RepublishMessage)];
        var tags = await db.Tags.AsNoTracking().ToDictionaryAsync(x => x.Id, ct);
        var ids = (await db.ProcessScreens.Where(x => x.PublishedJson != null).Select(x => x.Id).ToListAsync(ct)).ToHashSet();
        if (selfId is int id) ids.Add(id);
        return ScreenValidator.Validate(document, tags, ids, publishedDocument: true, expandedDocument: true).Concat(ValidateAssets(document)).ToList();
    }

    private static void FreezeTemplateConnections(ScreenDocument draft, ScreenDocument expanded)
    {
        var frames = draft.Nodes.Where(node => node.Type == "template").Select(node => node.Id).ToHashSet();
        ScreenPoint? Port(ScreenPort? port)
        {
            if (port is null) return null;
            var target = draft.Nodes.Concat(expanded.Nodes).FirstOrDefault(node => node.Id == port.NodeId);
            if (target is null) return null;
            var dx = port.Port == "left" ? -target.Width / 2 : port.Port == "right" ? target.Width / 2 : 0;
            var dy = port.Port == "top" ? -target.Height / 2 : port.Port == "bottom" ? target.Height / 2 : 0;
            var angle = target.Rotation * Math.PI / 180;
            return new(target.X + target.Width / 2 + dx * Math.Cos(angle) - dy * Math.Sin(angle),
                target.Y + target.Height / 2 + dx * Math.Sin(angle) + dy * Math.Cos(angle));
        }
        for (var index = 0; index < expanded.Nodes.Count; index++)
        {
            var node = expanded.Nodes[index];
            if (node.Type != "polyline" || !frames.Contains(node.Source?.NodeId ?? "") && !frames.Contains(node.Target?.NodeId ?? "")) continue;
            var points = (node.Points ?? [new(0, 0), new(100, 100)])
                .Select(point => new ScreenPoint(node.X + point.X * node.Width / 100, node.Y + point.Y * node.Height / 100)).ToList();
            var source = Port(node.Source); var target = Port(node.Target);
            if (source is not null && target is not null)
            {
                var middle = (source.X + target.X) / 2;
                points = [source, new(middle, source.Y), new(middle, target.Y), target];
            }
            else { if (source is not null) points[0] = source; if (target is not null) points[^1] = target; }
            // Match the canvas routing before removing its transparent template selection frames.
            var frozen = JsonSerializer.Deserialize<ScreenNode>(JsonSerializer.Serialize(node))!;
            frozen.X = points.Min(point => point.X); frozen.Y = points.Min(point => point.Y);
            frozen.Width = Math.Max(16, points.Max(point => point.X) - frozen.X);
            frozen.Height = Math.Max(16, points.Max(point => point.Y) - frozen.Y);
            frozen.Points = points.Select(point => new ScreenPoint((point.X - frozen.X) / frozen.Width * 100, (point.Y - frozen.Y) / frozen.Height * 100)).ToList();
            frozen.Source = null; frozen.Target = null;
            expanded.Nodes[index] = frozen;
        }
    }

    internal static IReadOnlyList<ScreenIssue> ValidateAssets(ScreenDocument document)
    {
        var issues = new List<ScreenIssue>();
        if (document.SymbolAssets is not { } assets) return issues;
        if (assets.Count > 1000) return [new("", "符号快照最多 1000 项")];
        foreach (var (key, svg) in assets)
        {
            if (!key.StartsWith("user:", StringComparison.Ordinal) || !ScreenValidator.KeyPattern().IsMatch(key[5..]))
                issues.Add(new("", $"符号快照键无效：{key}"));
            if (string.IsNullOrWhiteSpace(svg) || svg.Length > HmiLibraryEndpoints.MaxSvgLength)
                issues.Add(new("", $"符号快照 {key} 为空或超过长度限制"));
            else if (HmiLibraryEndpoints.SvgError(svg) is { } error)
                issues.Add(new("", $"符号快照 {key}：{error}"));
        }
        return issues;
    }
}
