using System.Security.Cryptography;
using Scada.Core.Licensing;

// 厂商侧离线签发工具：私钥永远不进入客户现场，也不提交到仓库。
// keygen 生成 P-256 密钥对；issue 根据客户机器码签发；inspect 校验已签发的激活码。

const string usage = """
用法：
  Scada.LicenseTool keygen --out <目录> [--kid <密钥编号>]
  Scada.LicenseTool issue  --key <私钥.pem> --machine <机器码> [--customer <客户>] [--license-id <编号>]
                           [--days <天数>] [--expires <yyyy-MM-dd>] [--devices <上限>] [--tags <上限>]
                           [--features <逗号分隔>] [--kid <密钥编号>]
  Scada.LicenseTool inspect --public <公钥.pem> --code <激活码>
  Scada.LicenseTool inspect --public <公钥.pem> --file <含激活码的文件>

不带 --machine 时签发不绑定机器的授权（仅演示，客户现场会被拒绝绑定校验）。
""";

var command = args.Length > 0 ? args[0].ToLowerInvariant() : "";
var options = ParseOptions(args.Skip(1).ToArray());

try
{
    return command switch
    {
        "keygen" => Keygen(),
        "issue" => Issue(),
        "inspect" => Inspect(),
        _ => Fail(usage)
    };
}
catch (Exception ex) when (ex is IOException or CryptographicException or FormatException or ArgumentException)
{
    Console.Error.WriteLine($"错误：{ex.Message}");
    return 2;
}

int Keygen()
{
    var outDir = Required("--out");
    var kid = options.GetValueOrDefault("--kid") ?? "k1";
    Directory.CreateDirectory(outDir);
    using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    var privatePath = Path.Combine(outDir, "private.pem");
    var publicPath = Path.Combine(outDir, "public.pem");
    File.WriteAllText(privatePath, key.ExportPkcs8PrivateKeyPem());
    File.WriteAllText(publicPath, key.ExportSubjectPublicKeyInfoPem());
    Console.WriteLine($"私钥：{privatePath}（离线保管，切勿随软件分发）");
    Console.WriteLine($"公钥：{publicPath}（内嵌到 Scada.Server）");
    Console.WriteLine($"kid ：{kid}");
    Console.WriteLine();
    Console.WriteLine("公钥内容：");
    Console.WriteLine(key.ExportSubjectPublicKeyInfoPem());
    return 0;
}

int Issue()
{
    var keyPath = Required("--key");
    using var signingKey = ECDsa.Create();
    signingKey.ImportFromPem(File.ReadAllText(keyPath));

    var now = DateTimeOffset.UtcNow;
    var expires = options.TryGetValue("--expires", out var dateText)
        ? DateTimeOffset.Parse(dateText + "T23:59:59Z")
        : now.AddDays(int.Parse(options.GetValueOrDefault("--days") ?? "365"));
    if (expires <= now)
    {
        return Fail("到期时间必须晚于当前时间");
    }

    var payload = new LicensePayload
    {
        KeyId = options.GetValueOrDefault("--kid") ?? "k1",
        LicenseId = options.GetValueOrDefault("--license-id") ?? $"SC-{now:yyyyMMdd}-{Random.Shared.Next(1000, 9999)}",
        Customer = options.GetValueOrDefault("--customer") ?? "",
        MachineId = NormalizeMachine(options.GetValueOrDefault("--machine") ?? ""),
        IssuedAt = now.ToUnixTimeSeconds(),
        ExpiresAt = expires.ToUnixTimeSeconds(),
        MaxDevices = int.Parse(options.GetValueOrDefault("--devices") ?? "0"),
        MaxTags = int.Parse(options.GetValueOrDefault("--tags") ?? "0"),
        Features = (options.GetValueOrDefault("--features") ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    };

    var code = LicenseToken.Create(payload, signingKey);
    Console.WriteLine($"授权编号：{payload.LicenseId}");
    Console.WriteLine($"客户    ：{payload.Customer}");
    Console.WriteLine($"机器码  ：{payload.MachineId}");
    Console.WriteLine($"有效期至：{expires:yyyy-MM-dd HH:mm:ss} UTC（{Math.Max(0, (int)(expires - now).TotalDays)} 天）");
    Console.WriteLine($"上限    ：设备 {(payload.MaxDevices == 0 ? "不限" : payload.MaxDevices)}，测点 {(payload.MaxTags == 0 ? "不限" : payload.MaxTags)}");
    Console.WriteLine($"功能    ：{(payload.Features.Length == 0 ? "基础功能" : string.Join(", ", payload.Features))}");
    Console.WriteLine();
    Console.WriteLine(code);
    return 0;
}

int Inspect()
{
    var publicPath = Required("--public");
    using var verificationKey = ECDsa.Create();
    verificationKey.ImportFromPem(File.ReadAllText(publicPath));
    var code = options.TryGetValue("--code", out var inline)
        ? inline
        : File.ReadAllText(Required("--file")).Trim();
    var result = LicenseToken.Parse(code, verificationKey);
    if (!result.Valid)
    {
        Console.Error.WriteLine($"无效：{result.Error}");
        return 1;
    }

    var p = result.Payload!;
    Console.WriteLine($"有效：{p.LicenseId} / {p.Customer}");
    Console.WriteLine($"机器码：{p.MachineId}");
    Console.WriteLine($"签发：{DateTimeOffset.FromUnixTimeSeconds(p.IssuedAt):yyyy-MM-dd HH:mm:ss} UTC");
    Console.WriteLine($"到期：{DateTimeOffset.FromUnixTimeSeconds(p.ExpiresAt):yyyy-MM-dd HH:mm:ss} UTC");
    return 0;
}

string Required(string name) =>
    options.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
        ? value
        : throw new ArgumentException($"缺少参数 {name}\n\n{usage}");

static int Fail(string message)
{
    Console.Error.WriteLine(message);
    return 1;
}

// 机器码按 5 字符分组展示，粘贴时允许有空格/短横线。
static string NormalizeMachine(string machine) =>
    new(machine.Where(char.IsLetterOrDigit).ToArray());

static Dictionary<string, string> ParseOptions(string[] remainder)
{
    var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (var i = 0; i < remainder.Length; i++)
    {
        var item = remainder[i];
        if (!item.StartsWith("--", StringComparison.Ordinal))
        {
            throw new ArgumentException($"无法识别的参数 {item}");
        }
        if (i + 1 >= remainder.Length || remainder[i + 1].StartsWith("--", StringComparison.Ordinal))
        {
            throw new ArgumentException($"参数 {item} 缺少值");
        }
        map[item] = remainder[++i];
    }
    return map;
}