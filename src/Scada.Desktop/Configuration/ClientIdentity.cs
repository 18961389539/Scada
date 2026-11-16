namespace Scada.Desktop.Configuration;

internal static class ClientIdentity
{
    /// <summary>首次启动生成稳定的 ClientId；已有值绝不重建，避免在线记录重复。</summary>
    public static string Ensure(DesktopSettings settings, SettingsStore store)
    {
        if (Guid.TryParse(settings.ClientId, out var existing))
        {
            settings.ClientId = existing.ToString("D");
            return settings.ClientId;
        }

        settings.ClientId = Guid.NewGuid().ToString("D");
        try
        {
            // settings 是合并机器配置后的对象。已有用户配置时只回写 ClientId，
            // 整份保存会把管理员下发的 serverUrl/target 写进用户配置，撤掉机器配置后残留。
            var userLayer = store.Load(out _);
            if (userLayer is null)
            {
                store.Save(settings); // 首次配置：这份对象由设置窗口产出，本身就是用户层的值。
                return settings.ClientId;
            }
            userLayer.ClientId = settings.ClientId;
            store.Save(userLayer);
        }
        catch (Exception exception)
        {
            DesktopLogger.Warn("保存 ClientId 失败：" + exception.Message);
        }
        return settings.ClientId;
    }
}
