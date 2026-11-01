using Microsoft.EntityFrameworkCore;
using Scada.Core.Entities;

namespace Scada.Data;

public static class ProcessScreenHistory
{
    // 每个画面保留最近版本，另加当前发布版本，避免发布历史随运行无限增长。
    public const int RetainedVersions = 100;

    public static void Record(ConfigDbContext db, ProcessScreen screen, long publishedTs, string publishedBy, string note, string kind, int? restoredFromRevision = null)
    {
        if (screen.PublishedJson is not { } json || screen.PublishedRevision is not int revision)
            throw new InvalidOperationException("Only published screens can have publication history.");
        db.ProcessScreenVersions.Add(new ProcessScreenVersion
        {
            ScreenId = screen.Id, Revision = revision, Name = screen.PublishedName ?? screen.Name, DocumentJson = json,
            PublishedTs = publishedTs, PublishedBy = publishedBy, Note = note, Kind = kind, RestoredFromRevision = restoredFromRevision
        });
    }

    public static async Task PreserveCurrentAsync(ConfigDbContext db, ProcessScreen screen, CancellationToken ct)
    {
        if (screen.PublishedJson is null || screen.PublishedRevision is not int revision) return;
        if (db.ProcessScreenVersions.Local.Any(x => x.ScreenId == screen.Id && x.Revision == revision)
            || await db.ProcessScreenVersions.AnyAsync(x => x.ScreenId == screen.Id && x.Revision == revision, ct)) return;
        Record(db, screen, screen.UpdatedTs, screen.UpdatedBy, "补录当前发布版本（时间与人员沿用画面最后修改记录）", "migration");
    }

    public static async Task TrimAsync(ConfigDbContext db, int screenId, CancellationToken ct)
    {
        var keep = new HashSet<int>();
        if (await db.ProcessScreens.AsNoTracking().Where(x => x.Id == screenId).Select(x => x.PublishedRevision).FirstOrDefaultAsync(ct) is int published)
            keep.Add(published);
        keep.UnionWith(await db.ProcessScreenVersions.Where(x => x.ScreenId == screenId)
            .OrderByDescending(x => x.Revision).Select(x => x.Revision).Take(RetainedVersions).ToListAsync(ct));
        await db.ProcessScreenVersions.Where(x => x.ScreenId == screenId && !keep.Contains(x.Revision)).ExecuteDeleteAsync(ct);
    }
}
