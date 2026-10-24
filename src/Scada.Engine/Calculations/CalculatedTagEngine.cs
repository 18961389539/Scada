using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Scada.Core.Calculations;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Interfaces;
using Scada.Core.Models;

namespace Scada.Engine.Calculations;

public sealed class CalculatedTagEngine(IConfigSnapshot configuration, IRealtimeCache cache, IRealtimeBus bus, ILogger<CalculatedTagEngine> logger) : BackgroundService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<int, long> _due = new();
    private IReadOnlyList<CalculationNode> _plan = [];
    private IReadOnlyList<Tag> _tags = [];
    private int _version = -1;
    private string? _planError;
    private IReadOnlyDictionary<int, Tag> _tagMap = new Dictionary<int, Tag>();
    private IReadOnlyDictionary<int, Device> _devices = new Dictionary<int, Device>();
    public async Task TickAsync(long now, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var version = configuration.Version; var loadedAt = configuration.LoadedAt;
            if (version != _version)
            {
                var all = configuration.Tags.Select(x => x.Clone()).ToArray();
                if (version != configuration.Version) return;
                var tags = all.Where(x => x.Kind == TagKind.Calculated).ToArray();
                foreach (var removed in _tags.Where(x => tags.All(t => t.Id != x.Id))) cache.Remove(removed.Id);
                _tags = tags; _due.Clear(); _planError = null;
                _tagMap = all.ToDictionary(x => x.Id); _devices = configuration.Devices.ToDictionary(x => x.Id);
                if (version != configuration.Version) return;
                try { _plan = CalculationGraph.Compile(all); }
                catch (TagExpressionException ex) { _plan = []; _planError = ex.Message; logger.LogWarning("计算配置无效：{Reason}", ex.Message); }
                _version = version;
            }
            if (now <= loadedAt) return;
            if (_planError is not null)
            {
                foreach (var tag in _tags.Where(x => _due.GetValueOrDefault(x.Id) <= now))
                {
                    if (version != configuration.Version) return;
                    _due[tag.Id] = now + Math.Clamp(tag.CalculationIntervalMs, 100, 600000);
                    Publish(tag, new(tag.Id, cache.Get(tag.Id)?.Value ?? 0, TagQuality.Bad, now, Error: _planError, DefinitionRevision: tag.CalculationRevision));
                }
                return;
            }
            foreach (var node in _plan)
            {
                ct.ThrowIfCancellationRequested();
                if (version != configuration.Version) return;
                var tag = node.Tag;
                if (_due.GetValueOrDefault(tag.Id) > now) continue;
                _due[tag.Id] = now + tag.CalculationIntervalMs;
                var result = CalculationEvaluator.Evaluate(tag, node.Expression, _tagMap, _devices, cache.Get, now, loadedAt, cache.Get(tag.Id));
                if (version == configuration.Version) Publish(tag, result);
            }
        }
        finally { _gate.Release(); }
    }
    private void Publish(Tag tag, TagValue current)
    {
        var old = cache.Get(tag.Id); if (old is not null && old.Ts >= current.Ts) return;
        cache.Set(current); bus.Publish(current);
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
        try { while (await timer.WaitForNextTickAsync(stoppingToken)) await TickAsync(ScadaTime.NowMs(), stoppingToken); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
