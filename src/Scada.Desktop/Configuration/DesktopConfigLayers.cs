namespace Scada.Desktop.Configuration;

/// <summary>
/// 用户配置与机器配置的分层写回。
///
/// 机器配置（ProgramData）在 <see cref="DesktopConfigResolver"/> 里覆盖用户配置，界面显示的是
/// 合并后的结果。保存时如果把这整份结果写进用户配置，管理员下发的 serverUrl/target 就会
/// 沉淀成"用户自己的设置"：撤掉机器配置后，终端仍然按旧目标跑。
/// 因此只把用户可编辑的字段写回用户层，锁定字段保留用户层原值。
/// </summary>
internal static class DesktopConfigLayers
{
    public static void ApplyEditableFields(
        DesktopSettings userLayer, DesktopSettings edited, IReadOnlyCollection<string> lockedFields)
    {
        if (!IsLocked(lockedFields, "serverUrl")) userLayer.ServerUrl = edited.ServerUrl;
        if (!IsLocked(lockedFields, "userName")) userLayer.UserName = edited.UserName;
        if (!IsLocked(lockedFields, "target")) userLayer.Target = edited.Target;
        if (!IsLocked(lockedFields, "display")) userLayer.Display = edited.Display;
        if (!IsLocked(lockedFields, "autoStart")) userLayer.AutoStart = edited.AutoStart;
        if (!IsLocked(lockedFields, "allowDownloads")) userLayer.AllowDownloads = edited.AllowDownloads;
        if (!IsLocked(lockedFields, "downloadDirectory")) userLayer.DownloadDirectory = edited.DownloadDirectory;

        // 以下字段不受机器配置管辖，始终跟随界面。
        userLayer.SavePassword = edited.SavePassword;
        userLayer.PasswordCipher = edited.PasswordCipher;
        userLayer.KeepAwake = edited.KeepAwake;
        userLayer.ClientId = edited.ClientId;
    }

    public static bool IsLocked(IReadOnlyCollection<string> lockedFields, string field) =>
        lockedFields.Contains(field, StringComparer.OrdinalIgnoreCase);
}
