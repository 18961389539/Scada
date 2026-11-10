using Scada.Core.Enums;

namespace Scada.Core.Models;

/// <summary>一个已加载的驱动来源：内置驱动或外部插件。</summary>
public sealed record DriverRegistration(
    ProtocolType Protocol,
    string Name,
    string Version,
    bool IsPlugin,
    string? AssemblyPath,
    bool OverridesBuiltIn);

/// <summary>插件程序集加载失败的原因。加载失败不影响其余驱动与主程序启动。</summary>
public sealed record DriverPluginLoadFailure(string AssemblyPath, string Reason);

public sealed record DriverPluginCatalogSnapshot(
    long CapturedTs,
    string? PluginRoot,
    bool PluginLoadingEnabled,
    IReadOnlyList<DriverRegistration> Drivers,
    IReadOnlyList<DriverPluginLoadFailure> Failures);