using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Scada.Core.Options;
using Scada.Engine.Health;

namespace Scada.Engine.Maintenance;

/// <summary>
/// 生产环境未配置备份口令时写入健康告警。只读配置与环境，不做 IO。
/// </summary>
public sealed class BackupSecurityHealth(IOptions<ScadaSettings> options, IHostEnvironment environment) : IHealthContributor
{
    public IEnumerable<SystemWarning> HealthWarnings()
    {
        if (BackupSecurity.UnencryptedWarning(environment.IsDevelopment(), options.Value) is { } warning)
            yield return warning;
    }
}
