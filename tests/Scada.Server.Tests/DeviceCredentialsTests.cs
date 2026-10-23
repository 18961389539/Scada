using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Scada.Core.Entities;
using Scada.Server.Services;

namespace Scada.Server.Tests;

public class DeviceCredentialsTests
{
    [Fact]
    public void DeviceCredentialsAreEncryptedAndNeverSerializedInDeviceResponses()
    {
        var service = new DeviceCredentials(new EphemeralDataProtectionProvider());
        var device = new Device { Name = "opc", OpcPasswordProtected = service.Protect("private-opc-password") };
        Assert.NotEqual("private-opc-password", device.OpcPasswordProtected); Assert.Equal("private-opc-password", service.Unprotect(device.OpcPasswordProtected));
        var json = JsonSerializer.Serialize(device, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.DoesNotContain("private-opc-password", json); Assert.DoesNotContain(device.OpcPasswordProtected, json); Assert.DoesNotContain("opcPasswordProtected", json);
        Assert.Contains("\"hasOpcPassword\":true", json);
    }
}
