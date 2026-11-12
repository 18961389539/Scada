using System.Security.Cryptography;
using System.Text;

namespace Scada.Server.Startup;

// 启动期路径解析：webroot、数据根、站点配置。逻辑原样从 Program.cs 搬出，行为不变。
internal static class StartupPaths
{
    // 静态文件根：WebApplicationBuilder 既解析不了 --webroot，也不支持建好之后再改 WebRootPath，
    // 所以在创建 builder 前先取出来交给 WebApplicationOptions。这样 E2E 能直接指向 web/dist，
    // 不会误用程序目录下可能陈旧的 wwwroot 副本。
    public static string? ResolveWebRoot(string[] args)
    {
        const string prefix = "--webroot=";
        foreach (var arg in args)
        {
            if (arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && arg.Length > prefix.Length)
            {
                var path = Path.GetFullPath(arg[prefix.Length..]);
                if (Directory.Exists(path)) return path;
            }
        }
        var fromEnv = Environment.GetEnvironmentVariable("ASPNETCORE_WEBROOT");
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            var path = Path.GetFullPath(fromEnv);
            if (Directory.Exists(path)) return path;
        }
        var bundled = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        return Directory.Exists(bundled) ? bundled : null;
    }

    // 数据根：便携部署落在程序目录 data/，与进程工作目录解耦（Windows 服务的 CWD 是 System32，双击则是程序目录）。
    // 顺序：Scada:DataRoot → SCADA_DATA_ROOT → 程序目录 data/；程序目录不可写时回退公共应用数据目录。
    public static string ResolveDataRoot(IConfiguration configuration)
    {
        var configured = configuration["Scada:DataRoot"];
        if (string.IsNullOrWhiteSpace(configured))
        {
            configured = Environment.GetEnvironmentVariable("SCADA_DATA_ROOT");
        }
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Path.GetFullPath(configured);
        }

        var portable = Path.Combine(AppContext.BaseDirectory, "data");
        try
        {
            Directory.CreateDirectory(portable);
            return portable;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var fallback = OperatingSystem.IsWindows()
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Scada", "data")
                : "/var/lib/scada";
            Directory.CreateDirectory(fallback);
            Console.WriteLine($"[startup] 程序目录不可写，数据改存 {fallback}（原因：{ex.Message}）");
            return fallback;
        }
    }

    // 站点配置：显式 --SiteSettings 优先；其次程序目录、数据目录下已有的 appsettings.Site.json；
    // 生产环境首次运行自动生成（含随机 JWT 密钥），让“拷贝文件夹后双击 exe”可以直接启动。
    public static string? ResolveSiteSettings(IConfiguration configuration, string dataRoot, IWebHostEnvironment environment)
    {
        var explicitPath = configuration["SiteSettings"];
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            return Path.GetFullPath(explicitPath);
        }

        foreach (var candidate in new[]
                 {
                     Path.Combine(AppContext.BaseDirectory, "appsettings.Site.json"),
                     Path.Combine(dataRoot, "appsettings.Site.json")
                 })
        {
            if (File.Exists(candidate)) return candidate;
        }

        return environment.IsDevelopment() ? null : CreatePortableSiteSettings(dataRoot);
    }

    private static string CreatePortableSiteSettings(string dataRoot)
    {
        var path = Path.Combine(dataRoot, "appsettings.Site.json");
        Directory.CreateDirectory(dataRoot);
        if (File.Exists(path)) return path;

        var payload = new Dictionary<string, object>
        {
            ["Scada"] = new Dictionary<string, object>
            {
                ["DataRoot"] = dataRoot,
                ["ConfigDbPath"] = Path.Combine(dataRoot, "scada.db"),
                ["RuntimeDbPath"] = Path.Combine(dataRoot, "runtime.db"),
                ["HistoryRoot"] = Path.Combine(dataRoot, "history"),
                ["BackupRoot"] = Path.Combine(dataRoot, "backups"),
                ["DiagnosticsRoot"] = Path.Combine(dataRoot, "diagnostics"),
                ["ReportsRoot"] = Path.Combine(dataRoot, "reports"),
                ["LogRoot"] = Path.Combine(dataRoot, "logs")
            },
            ["Jwt"] = new Dictionary<string, object>
            {
                ["Key"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48))
            }
        };
        var json = System.Text.Json.JsonSerializer.Serialize(payload, new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true,
            // 站点配置是给人看的运维文件：中文路径与 Base64 密钥保持原样，不转成 \uXXXX。
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
        // 先写临时文件再原子改名：两个进程同时首次启动时不会读到写了一半的配置。
        var temp = Path.Combine(dataRoot, $".appsettings.Site.{Environment.ProcessId}.tmp");
        File.WriteAllText(temp, json, new UTF8Encoding(false));
        try
        {
            File.Move(temp, path, overwrite: false);
            Console.WriteLine($"[startup] 已生成便携站点配置 {path}（含随机 JWT 密钥，请与数据目录一起备份）");
        }
        catch (IOException) when (File.Exists(path))
        {
            File.Delete(temp);
        }
        return path;
    }

    // 站点配置里的相对路径统一以数据根为基准，避免落到进程工作目录（服务场景是 System32）。
    // 用追加一层内存配置而不是改写已有 provider 的索引器：后者会就地篡改 JSON provider 的值，
    // 与 IOptionsMonitor 的缓存和"配置文件没变"的判断都不一致，排查时很难看出这个值是被改过的。
    public static void ApplyDataRoot(IConfigurationManager configuration, string dataRoot)
    {
        var defaults = new Dictionary<string, string>
        {
            ["ConfigDbPath"] = "scada.db",
            ["RuntimeDbPath"] = "runtime.db",
            ["HistoryRoot"] = "history",
            ["BackupRoot"] = "backups",
            ["DiagnosticsRoot"] = "diagnostics",
            ["ReportsRoot"] = "reports",
            ["LogRoot"] = "logs"
        };
        var overrides = new Dictionary<string, string?>();
        foreach (var (name, fallback) in defaults)
        {
            var key = $"Scada:{name}";
            var value = configuration[key];
            if (string.IsNullOrWhiteSpace(value)) value = fallback;
            if (!Path.IsPathRooted(value))
            {
                overrides[key] = Path.GetFullPath(Path.Combine(dataRoot, value));
            }
        }

        // 后加的 provider 优先级最高，读到的仍然是绝对路径。
        if (overrides.Count > 0) configuration.AddInMemoryCollection(overrides);
    }
}
