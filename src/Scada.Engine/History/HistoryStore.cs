using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Scada.Core.Interfaces;
using Scada.Core.Options;
using Scada.Data.Infrastructure;

namespace Scada.Engine.History;

/// <summary>
/// 默认历史库入口。实现为 SQLite 后端的薄封装，保留现有构造函数供测试与 DI 使用。
/// </summary>
public sealed class HistoryStore : SqliteHistoryBackend
{
    public HistoryStore(
        IHistoryDbFactory factory,
        IOptions<ScadaSettings>? settings = null,
        ILogger<SqliteHistoryBackend>? logger = null,
        INodeRole? role = null,
        IServiceProvider? services = null)
        : base(factory, settings, logger, role, services)
    {
    }
}