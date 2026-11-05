namespace Scada.Core.Licensing;

/// <summary>
/// 授权对输出和配额的约束。0 表示不限制数量。采集和历史可以继续，下发和超限建点不行。
/// </summary>
public interface ILicenseEnforcement
{
    bool OutputsAllowed { get; }
    string BlockMessage { get; }
    int MaxDevices { get; }
    int MaxTags { get; }
}

public sealed class AllowAllLicense : ILicenseEnforcement
{
    public static readonly AllowAllLicense Instance = new();
    public bool OutputsAllowed => true;
    public string BlockMessage => "";
    public int MaxDevices => 0;
    public int MaxTags => 0;
}

public sealed class LicenseQuotaException(string message) : InvalidOperationException(message);

public static class LicenseQuota
{
    public static string? Deny(ILicenseEnforcement license, int devices, int tags, int addDevices = 0, int addTags = 0)
    {
        if (addDevices > 0 && license.MaxDevices > 0 && devices + addDevices > license.MaxDevices)
            return $"授权设备数上限为 {license.MaxDevices}，当前已有 {devices}";
        if (addTags > 0 && license.MaxTags > 0 && tags + addTags > license.MaxTags)
            return $"授权测点数上限为 {license.MaxTags}，当前已有 {tags}";
        return null;
    }
}
