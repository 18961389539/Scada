using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Scada.Core.Options;
using Scada.Engine.Maintenance;

// 离线备份工具：在停机窗口内校验与还原完整备份，不依赖正在运行的服务。
// 用法：
//   Scada.BackupTool verify  --backup <full_*.zip | full_*.zip.enc> [--password <口令>]
//   Scada.BackupTool restore --backup <full_*.zip | full_*.zip.enc> --target <空目录> [--password <口令>]
//   Scada.BackupTool reencrypt --backup <full_*.zip.enc> [--target <新包路径>] [--password <旧口令>] --new-password <新口令>

// Windows 控制台默认不是 UTF-8，不显式设置会把中文输出成乱码。
Console.OutputEncoding = System.Text.Encoding.UTF8;

if (args.Length == 0 || args[0] is "-h" or "--help")
{
    PrintUsage();
    return 2;
}

var command = args[0];
var promptPassword = false;
var promptNewPassword = false;
string? backupPath = null, targetPath = null, password = Environment.GetEnvironmentVariable("SCADA_BACKUP_PASSWORD"), newPassword = null;
for (var i = 1; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--prompt-password": promptPassword = true; break;
        case "--prompt-new-password": promptNewPassword = true; break;
        case "--new-password" when i + 1 < args.Length: newPassword = args[++i]; break;
        case "--backup" when i + 1 < args.Length: backupPath = args[++i]; break;
        case "--target" when i + 1 < args.Length: targetPath = args[++i]; break;
        // 口令也可以从 SCADA_BACKUP_PASSWORD 读：命令行参数会进进程列表与运维记录。
        case "--password" when i + 1 < args.Length: password = args[++i]; break;
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

if (promptPassword)
{
    if (Console.IsInputRedirected) { Console.Error.WriteLine("Password prompt requires an interactive console."); return 2; }
    var entered = ReadHidden("Backup password (hidden): ");
    if (entered is null) return 2;
    password = entered;
}

if (promptNewPassword)
{
    if (Console.IsInputRedirected) { Console.Error.WriteLine("New password prompt requires an interactive console."); return 2; }
    var first = ReadHidden("New backup password (hidden): ");
    var second = first is null ? null : ReadHidden("Repeat new backup password: ");
    if (first is null || second is null) return 2;
    if (!string.Equals(first, second, StringComparison.Ordinal))
    {
        Console.Error.WriteLine("两次输入的新口令不一致，未做任何改动。");
        return 2;
    }
    newPassword = first;
}

var full = Path.GetFullPath(backupPath);
if (!File.Exists(full))
{
    Console.Error.WriteLine($"备份不存在：{full}");
    return 1;
}

var service = new BackupService(
    Options.Create(new ScadaSettings
    {
        BackupRoot = Path.GetDirectoryName(full)!,
        ConfigDbPath = Path.Combine(Path.GetDirectoryName(full)!, "unused.db"),
        BackupPassword = password
    }),
    NullLogger<BackupService>.Instance);

var encrypted = BackupArchive.IsEncrypted(full);
if (encrypted && string.IsNullOrWhiteSpace(password))
{
    Console.Error.WriteLine("这是加密备份包，请用 --password 或环境变量 SCADA_BACKUP_PASSWORD 提供口令。");
    return 2;
}

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
                Console.WriteLine($"生成时间：{manifest.CreatedAt:yyyy-MM-dd HH:mm:ss}Z，应用版本 {manifest.AppVersion}，{(manifest.Encrypted ? "已加密" : "明文")}");
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
        case "reencrypt":
        {
            if (string.IsNullOrWhiteSpace(password))
            {
                Console.Error.WriteLine("重新加密需要旧口令：--password、SCADA_BACKUP_PASSWORD 或 --prompt-password。");
                return 2;
            }

            if (string.IsNullOrWhiteSpace(newPassword))
            {
                Console.Error.WriteLine("重新加密需要新口令：--new-password 或 --prompt-new-password。");
                return 2;
            }

            var target = string.IsNullOrWhiteSpace(targetPath)
                ? Path.Combine(Path.GetDirectoryName(full)!, Path.GetFileNameWithoutExtension(full) + ".reenc" + BackupService.EncryptedSuffix)
                : Path.GetFullPath(targetPath);
            var result = await BackupReencrypt.RunAsync(full, target, password, newPassword, default);
            Console.WriteLine($"重新加密完成：{result.TargetPath}（{result.Bytes} 字节）");
            Console.WriteLine($"旧包保持原样：{result.SourcePath}");
            Console.WriteLine("请先用 verify 校验新包并确认能还原，再手动删除旧包；保留策略不会替你判断口令是否已交接。");
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
        Scada.BackupTool —— 离线校验、还原与轮换完整备份

          Scada.BackupTool verify  --backup <full_*.zip | full_*.zip.enc> [--password <口令>]
          Scada.BackupTool restore --backup <full_*.zip | full_*.zip.enc> --target <空目录> [--password <口令>]
          Scada.BackupTool reencrypt --backup <full_*.zip.enc> [--target <新包路径>] [--password <旧口令>] --new-password <新口令>

        加密包（.zip.enc）必须提供口令；也可用环境变量 SCADA_BACKUP_PASSWORD，避免口令进命令历史。使用 --prompt-password 可在交互控制台隐藏输入口令。
        还原只写入空目录，不会覆盖现场数据；执行前请先停止服务。
        reencrypt 用新口令重写一份同目录新包，旧包保持不变；新包必须通过回读比对才会留下，确认无误后再手动删除旧包。
        reencrypt 的新口令用 --prompt-new-password 隐藏输入并要求二次确认，避免打错后新包永久不可解。
        """);
}

// 交互式隐藏输入；Esc 取消返回 null，由调用方决定退出码。
static string? ReadHidden(string label)
{
    Console.Write(label);
    var input = new System.Text.StringBuilder();
    while (true)
    {
        var key = Console.ReadKey(intercept: true);
        if (key.Key == ConsoleKey.Enter) { Console.WriteLine(); return input.ToString(); }
        if (key.Key == ConsoleKey.Escape) { Console.WriteLine(); return null; }
        if (key.Key == ConsoleKey.Backspace) { if (input.Length > 0) input.Length--; }
        else if (!char.IsControl(key.KeyChar)) input.Append(key.KeyChar);
    }
}
