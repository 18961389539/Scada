using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Scada.Core.Options;
using Scada.Engine.Maintenance;

// 离线备份工具：在停机窗口内校验与还原完整备份，不依赖正在运行的服务。
// 用法：
//   Scada.BackupTool verify  --backup <full_*.zip>
//   Scada.BackupTool restore --backup <full_*.zip> --target <空目录>

// Windows 控制台默认不是 UTF-8，不显式设置会把中文输出成乱码。
Console.OutputEncoding = System.Text.Encoding.UTF8;

if (args.Length == 0 || args[0] is "-h" or "--help")
{
    PrintUsage();
    return 2;
}

var command = args[0];
string? backupPath = null, targetPath = null;
for (var i = 1; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--backup" when i + 1 < args.Length: backupPath = args[++i]; break;
        case "--target" when i + 1 < args.Length: targetPath = args[++i]; break;
        default:
            Console.Error.WriteLine($"无法识别的参数：{args[i]}");
            PrintUsage();
            return 2;
    }
}

if (string.IsNullOrWhiteSpace(backupPath))
{
    Console.Error.WriteLine("缺少 --backup 参数");
    return 2;
}

var full = Path.GetFullPath(backupPath);
if (!File.Exists(full))
{
    Console.Error.WriteLine($"备份不存在：{full}");
    return 1;
}

var service = new BackupService(
    Options.Create(new ScadaSettings { BackupRoot = Path.GetDirectoryName(full)!, ConfigDbPath = Path.Combine(Path.GetDirectoryName(full)!, "unused.db") }),
    NullLogger<BackupService>.Instance);

try
{
    switch (command)
    {
        case "verify":
        {
            var verification = await service.VerifyAsync(Path.GetFileName(full), default);
            if (verification.Valid)
            {
                var manifest = verification.Manifest!;
                Console.WriteLine($"校验通过：{manifest.Devices} 台设备 / {manifest.Tags} 个测点 / {manifest.AlarmRules} 条报警规则 / {manifest.HistoryFiles} 个历史库");
                Console.WriteLine($"生成时间：{manifest.CreatedAt:yyyy-MM-dd HH:mm:ss}Z，应用版本 {manifest.AppVersion}");
                return 0;
            }

            Console.Error.WriteLine("校验失败：");
            foreach (var problem in verification.Problems) Console.Error.WriteLine($"  - {problem}");
            return 1;
        }
        case "restore":
        {
            if (string.IsNullOrWhiteSpace(targetPath))
            {
                Console.Error.WriteLine("还原需要 --target 参数");
                return 2;
            }

            var report = await service.RestoreAsync(Path.GetFileName(full), targetPath, default);
            Console.WriteLine($"还原完成：{report.TargetRoot}");
            Console.WriteLine($"文件 {report.Files} 个，设备 {report.Devices} 台，测点 {report.Tags} 个，报警规则 {report.AlarmRules} 条");
            return 0;
        }
        default:
            Console.Error.WriteLine($"未知命令：{command}");
            PrintUsage();
            return 2;
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine($"执行失败：{ex.Message}");
    return 1;
}

static void PrintUsage()
{
    Console.WriteLine("""
        Scada.BackupTool —— 离线校验与还原完整备份

          Scada.BackupTool verify  --backup <full_*.zip>
          Scada.BackupTool restore --backup <full_*.zip> --target <空目录>

        还原只写入空目录，不会覆盖现场数据；执行前请先停止服务。
        """);
}
