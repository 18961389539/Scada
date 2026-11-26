using Microsoft.Extensions.Options;
using Scada.Core.Scripting;
using Scada.Data.Infrastructure;

namespace Scada.Engine.Calculations;

public sealed class ConfiguredSerialProfiles(IOptionsMonitor<CSharpScriptOptions> options) : IConfiguredSerialProfiles
{
    private static readonly IReadOnlyDictionary<string, SerialProfile> Empty =
        new Dictionary<string, SerialProfile>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, SerialProfile> Current => options.CurrentValue.SerialProfiles ?? Empty;
}
