using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Scada.Core.Entities;
using Scada.Core.Models;
using Scada.Data;

namespace Scada.Server.Screens;

/// <summary>按发布时间或记下的修订号查找当时已发布的画面文档，供布局历史预览使用。</summary>
public static class ScreenHistoricalLookup
{
    public static async Task<ProcessScreenVersion?> FindAsync(
        ConfigDbContext db, int screenId, long? asOf, int? revision, CancellationToken cancellationToken)
    {
        if (revision is int rev and > 0)
        {
            return await db.ProcessScreenVersions.AsNoTracking()
                .SingleOrDefaultAsync(x => x.ScreenId == screenId && x.Revision == rev, cancellationToken);
        }
        if (asOf is long ts and > 0)
        {
            return await db.ProcessScreenVersions.AsNoTracking()
                .Where(x => x.ScreenId == screenId && x.PublishedTs <= ts)
                .OrderByDescending(x => x.PublishedTs)
                .ThenByDescending(x => x.Revision)
                .FirstOrDefaultAsync(cancellationToken);
        }
        return null;
    }
}
