using Scada.Core.Scripting;

namespace Scada.Data.Infrastructure;

public interface IConfiguredSerialProfiles
{
    IReadOnlyDictionary<string, SerialProfile> Current { get; }
}
