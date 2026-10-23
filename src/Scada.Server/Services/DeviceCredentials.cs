using Microsoft.AspNetCore.DataProtection;
using Scada.Core.Interfaces;

namespace Scada.Server.Services;

public sealed class DeviceCredentials(IDataProtectionProvider provider) : IDeviceCredentials
{
    private readonly IDataProtector _protector = provider.CreateProtector("Scada.DeviceCredentials.v1");
    public string Protect(string password) => _protector.Protect(password);
    public string Unprotect(string protectedPassword) => _protector.Unprotect(protectedPassword);
}
