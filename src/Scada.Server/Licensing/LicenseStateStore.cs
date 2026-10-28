using System.Text.Json;

namespace Scada.Server.Licensing;

/// <summary>授权状态落盘：与配置库同目录（<c>data/license.json</c>），随数据目录一起备份。</summary>
public sealed class LicenseStateStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly string _path;

    public LicenseStateStore(string configDbPath)
    {
        var directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(configDbPath))
                        ?? throw new InvalidOperationException("无法确定授权状态目录：配置库路径缺少目录");
        Directory.CreateDirectory(directory);
        _path = System.IO.Path.Combine(directory, "license.json");
    }

    public string FilePath => _path;

    public LicenseStateFile Load()
    {
        if (!File.Exists(_path)) return new LicenseStateFile();
        try
        {
            return JsonSerializer.Deserialize<LicenseStateFile>(File.ReadAllText(_path), Json) ?? new LicenseStateFile();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // 文件损坏时按未激活处理，避免因为一个坏文件让服务起不来。
            return new LicenseStateFile();
        }
    }

    public void Save(LicenseStateFile state)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(state, Json));
        File.Move(temp, _path, overwrite: true);
    }
}