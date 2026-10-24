using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Scada.Core.Calculations;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Interfaces;
using Scada.Core.Models;
using Scada.Data;

namespace Scada.Engine.Calculations;

public sealed class CalculationRequestException(int status, string message) : Exception(message) { public int Status { get; } = status; }
public sealed record CalculatedTagRequest(string Name, string Expression, string? Description = null, string? Unit = null,
    bool Enabled = true, int IntervalMs = 1000, int InputMaxAgeMs = 15000, ArchiveMode ArchiveMode = ArchiveMode.OnChange,
    double Deadband = 0, int ArchivePeriodSec = 600, long Revision = 0);
public sealed record CalculatedTagInfo(Tag Tag, IReadOnlyList<int> Dependencies, TagValue? Current, string? DefinitionError);
public sealed record CalculationPreview(IReadOnlyList<int> Dependencies, TagValue Value);

public sealed class CalculatedTagConfiguration(IDbContextFactory<ConfigDbContext> factory, IRealtimeCache cache, IConfigSnapshot snapshot)
{
    public async Task<IReadOnlyList<CalculatedTagInfo>> ListAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var tags = await db.Tags.AsNoTracking().Where(x => x.Kind == TagKind.Calculated).OrderBy(x => x.Id).ToListAsync(ct);
        return tags.Select(Info).ToArray();
    }
    private CalculatedTagInfo Info(Tag tag)
    {
        try { return new(tag, TagExpression.Compile(tag.Expression).Dependencies, cache.Get(tag.Id), null); }
        catch (TagExpressionException ex) { return new(tag, [], cache.Get(tag.Id), ex.Message); }
    }
    private static Tag Map(CalculatedTagRequest request, int id = 0) => new()
    {
        Id = id, DeviceId = null, Kind = TagKind.Calculated, Name = request.Name?.Trim() ?? "", Expression = request.Expression?.Trim() ?? "",
        Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim(), Unit = string.IsNullOrWhiteSpace(request.Unit) ? null : request.Unit.Trim(),
        DataType = TagDataType.Double, Scale = 1, Offset = 0, Writable = false, CalculationEnabled = request.Enabled, CalculationIntervalMs = request.IntervalMs,
        InputMaxAgeMs = request.InputMaxAgeMs, ArchiveMode = request.ArchiveMode, Deadband = request.Deadband, ArchivePeriodSec = request.ArchivePeriodSec
    };
    private static void Validate(Tag tag)
    {
        var error = ConfigurationValidator.Tag(tag); if (error is not null) throw new CalculationRequestException(400, error);
    }
    public async Task<CalculationPreview> PreviewAsync(int? id, CalculatedTagRequest request, CancellationToken ct)
    {
        var candidate = Map(request, id ?? 0); Validate(candidate);
        await using var db = await factory.CreateDbContextAsync(ct);
        var tags = await db.Tags.AsNoTracking().ToListAsync(ct);
        if (id is int existing && !tags.Any(x => x.Id == existing && x.Kind == TagKind.Calculated)) throw new CalculationRequestException(404, "计算测点不存在");
        tags.RemoveAll(x => x.Id == candidate.Id); tags.Add(candidate);
        CalculationGraph.Compile(tags);
        var expression = TagExpression.Compile(candidate.Expression);
        var devices = await db.Devices.AsNoTracking().ToDictionaryAsync(x => x.Id, ct);
        var value = CalculationEvaluator.Evaluate(candidate, expression, tags.ToDictionary(x => x.Id), devices, cache.Get, ScadaTime.NowMs(), snapshot.LoadedAt, id is int current ? cache.Get(current) : null);
        return new(expression.Dependencies, value);
    }
    public async Task<Tag> SaveAsync(int? id, CalculatedTagRequest request, int userId, string userName, CancellationToken ct)
    {
        var candidate = Map(request, id ?? 0); Validate(candidate);
        await using var db = await factory.CreateDbContextAsync(ct); await using var transaction = await db.Database.BeginTransactionAsync(ct);
        Tag tag; string? before = null;
        if (id is int existing)
        {
            tag = await db.Tags.SingleOrDefaultAsync(x => x.Id == existing && x.Kind == TagKind.Calculated, ct) ?? throw new CalculationRequestException(404, "计算测点不存在");
            if (request.Revision != tag.CalculationRevision) throw new CalculationRequestException(409, "计算定义已变化，请重新读取后核对");
            before = JsonSerializer.Serialize(tag); candidate.CalculationRevision = checked(tag.CalculationRevision + 1);
            db.Entry(tag).CurrentValues.SetValues(candidate);
        }
        else
        {
            if (request.Revision != 0) throw new CalculationRequestException(400, "新建计算测点不能携带旧版本");
            if (await db.Tags.CountAsync(x => x.Kind == TagKind.Calculated, ct) >= 2000) throw new CalculationRequestException(409, "计算测点已达 2000 个上限");
            tag = candidate; db.Tags.Add(tag);
        }
        await db.SaveChangesAsync(ct);
        var all = await db.Tags.AsNoTracking().ToListAsync(ct); var plan = CalculationGraph.Compile(all);
        var inputs = plan.Single(x => x.Tag.Id == tag.Id).Expression.Dependencies;
        await db.TagDependencies.Where(x => x.TagId == tag.Id).ExecuteDeleteAsync(ct);
        db.TagDependencies.AddRange(inputs.Select(x => new TagDependency { TagId = tag.Id, InputTagId = x }));
        Audit(db, tag, id is null ? "创建计算测点" : "修改计算测点", before, JsonSerializer.Serialize(tag), userId, userName);
        await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct); return tag;
    }
    public async Task DeleteAsync(int id, long revision, int userId, string userName, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct); await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var tag = await db.Tags.SingleOrDefaultAsync(x => x.Id == id && x.Kind == TagKind.Calculated, ct) ?? throw new CalculationRequestException(404, "计算测点不存在");
        if (revision != tag.CalculationRevision) throw new CalculationRequestException(409, "计算定义已变化，请重新读取后核对");
        if (await db.TagDependencies.AnyAsync(x => x.InputTagId == id, ct)) throw new CalculationRequestException(409, "测点仍被其他计算定义引用，请先修改依赖定义");
        var now = ScadaTime.NowMs();
        await db.AlarmEvents.Where(x => x.TagId == id && x.EndTs == null).ExecuteUpdateAsync(set => set.SetProperty(x => x.EndTs, now).SetProperty(x => x.EndReason, "删除计算测点"), ct);
        Audit(db, tag, "删除计算测点", JsonSerializer.Serialize(tag), null, userId, userName);
        db.Tags.Remove(tag); await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct); cache.Remove(id);
    }
    private static void Audit(ConfigDbContext db, Tag tag, string action, string? before, string? after, int userId, string userName) => db.OperationLogs.Add(new OperationLog
    { UserId = userId, UserName = userName, TagId = tag.Id, Ts = ScadaTime.NowMs(), Action = $"{action}：{tag.Name}", OldValue = before, NewValue = after, Result = "成功" });
}
