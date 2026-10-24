using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Scada.Core.Options;

namespace Scada.Engine.Maintenance;

public sealed record RemoteBackup(string FileName, long Bytes, DateTimeOffset? Modified);

// 异地备份目标（可插拔）：上传后必须能列出并核对大小，保留策略按文件名（含时间戳）排序删除最旧的。
public interface IBackupTarget
{
    string Name { get; }
    string Kind { get; }
    int KeepLatest { get; }
    Task UploadAsync(string localPath, string fileName, CancellationToken ct);
    Task<IReadOnlyList<RemoteBackup>> ListAsync(CancellationToken ct);
    Task DeleteAsync(string fileName, CancellationToken ct);
}

public static class BackupTargetFactory
{
    public static IBackupTarget Create(BackupTargetOptions o, HttpMessageHandler? handler = null) => o.Type.Trim().ToLowerInvariant() switch
    {
        "directory" => new DirectoryBackupTarget(o),
        "s3" => new S3BackupTarget(o, handler),
        _ => throw new InvalidOperationException($"未知的备份目标类型：{o.Type}")
    };
}

// 目录目标：本机另一块盘、挂载的 NAS/SMB 共享。先写临时名再改名，避免对方看到半个文件。
public sealed class DirectoryBackupTarget(BackupTargetOptions o) : IBackupTarget
{
    private readonly string _root = System.IO.Path.GetFullPath(o.Path ?? throw new InvalidOperationException($"备份目标 {o.Name} 缺少 Path"));
    public string Name => string.IsNullOrWhiteSpace(o.Name) ? _root : o.Name;
    public string Kind => "Directory";
    public int KeepLatest => Math.Max(o.KeepLatest, 1);

    public async Task UploadAsync(string localPath, string fileName, CancellationToken ct)
    {
        Directory.CreateDirectory(_root);
        var target = System.IO.Path.Combine(_root, System.IO.Path.GetFileName(fileName));
        var temp = target + ".partial";
        await using (var from = File.OpenRead(localPath))
        await using (var to = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await from.CopyToAsync(to, ct);
            to.Flush(true);
        }
        File.Move(temp, target, true);
        if (new FileInfo(target).Length != new FileInfo(localPath).Length) throw new IOException($"异地副本大小不符：{target}");
    }

    public Task<IReadOnlyList<RemoteBackup>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<RemoteBackup>>(!Directory.Exists(_root) ? [] :
        Directory.EnumerateFiles(_root, "full_*.zip").Select(x => new FileInfo(x)).Select(x => new RemoteBackup(x.Name, x.Length, x.LastWriteTimeUtc)).ToList());

    public Task DeleteAsync(string fileName, CancellationToken ct)
    {
        File.Delete(System.IO.Path.Combine(_root, System.IO.Path.GetFileName(fileName)));
        return Task.CompletedTask;
    }
}

// S3 兼容目标：路径式寻址（endpoint/bucket/key），AWS Signature V4 签名，单次 PUT（备份包通常小于 5GB）。
public sealed class S3BackupTarget : IBackupTarget, IDisposable
{
    private readonly BackupTargetOptions _o;
    private readonly HttpClient _http;
    private readonly Uri _endpoint;
    public S3BackupTarget(BackupTargetOptions o, HttpMessageHandler? handler = null)
    {
        _o = o;
        if (!Uri.TryCreate(o.Endpoint, UriKind.Absolute, out var endpoint) || string.IsNullOrWhiteSpace(o.Bucket) || string.IsNullOrEmpty(o.AccessKey) || string.IsNullOrEmpty(o.SecretKey))
            throw new InvalidOperationException($"S3 备份目标 {o.Name} 需要 Endpoint、Bucket、AccessKey、SecretKey");
        _endpoint = endpoint;
        _http = new HttpClient(handler ?? new SocketsHttpHandler()) { Timeout = TimeSpan.FromMinutes(30) };
    }
    public string Name => string.IsNullOrWhiteSpace(_o.Name) ? $"{_endpoint.Host}/{_o.Bucket}" : _o.Name;
    public string Kind => "S3";
    public int KeepLatest => Math.Max(_o.KeepLatest, 1);
    private string Prefix => string.IsNullOrEmpty(_o.Prefix) ? "" : _o.Prefix.Trim('/') + "/";
    private string ObjectPath(string fileName) => $"/{_o.Bucket}/{Prefix}{System.IO.Path.GetFileName(fileName)}";

    public async Task UploadAsync(string localPath, string fileName, CancellationToken ct)
    {
        string hash;
        await using (var s = File.OpenRead(localPath)) hash = Convert.ToHexString(await SHA256.HashDataAsync(s, ct)).ToLowerInvariant();
        await using var body = File.OpenRead(localPath);
        var content = new StreamContent(body); content.Headers.ContentLength = body.Length;
        content.Headers.ContentType = new("application/zip");
        using var response = await SendAsync(HttpMethod.Put, ObjectPath(fileName), "", hash, content, ct);
        await EnsureAsync(response, "上传", ct);
        using var head = await SendAsync(HttpMethod.Head, ObjectPath(fileName), "", S3Signer.EmptyHash, null, ct);
        await EnsureAsync(head, "核对", ct);
        if (head.Content.Headers.ContentLength is long length && length != new FileInfo(localPath).Length) throw new IOException($"S3 副本大小不符：{length}");
    }

    public async Task<IReadOnlyList<RemoteBackup>> ListAsync(CancellationToken ct)
    {
        var result = new List<RemoteBackup>(); string? token = null;
        do
        {
            var query = new SortedDictionary<string, string>(StringComparer.Ordinal) { ["list-type"] = "2", ["prefix"] = Prefix + "full_" };
            if (token is not null) query["continuation-token"] = token;
            using var response = await SendAsync(HttpMethod.Get, $"/{_o.Bucket}", S3Signer.CanonicalQuery(query), S3Signer.EmptyHash, null, ct);
            await EnsureAsync(response, "列出", ct);
            var xml = XDocument.Parse(await response.Content.ReadAsStringAsync(ct)); var ns = xml.Root!.Name.Namespace;
            foreach (var item in xml.Root.Elements(ns + "Contents"))
            {
                var key = item.Element(ns + "Key")!.Value;
                var name = key[Prefix.Length..];
                if (name.Contains('/') || !name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;
                result.Add(new(name, long.Parse(item.Element(ns + "Size")?.Value ?? "0", CultureInfo.InvariantCulture),
                    DateTimeOffset.TryParse(item.Element(ns + "LastModified")?.Value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t) ? t : null));
            }
            token = xml.Root.Element(ns + "IsTruncated")?.Value == "true" ? xml.Root.Element(ns + "NextContinuationToken")?.Value : null;
        } while (token is not null);
        return result;
    }

    public async Task DeleteAsync(string fileName, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Delete, ObjectPath(fileName), "", S3Signer.EmptyHash, null, ct);
        if (response.StatusCode != HttpStatusCode.NotFound) await EnsureAsync(response, "删除", ct);
    }

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string query, string payloadHash, HttpContent? content, CancellationToken ct)
    {
        var encodedPath = S3Signer.EncodePath(path);
        var uri = new UriBuilder(_endpoint) { Path = _endpoint.AbsolutePath.TrimEnd('/') + encodedPath, Query = query }.Uri;
        var request = new HttpRequestMessage(method, uri) { Content = content };
        var now = DateTime.UtcNow;
        var headers = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["host"] = uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}",
            ["x-amz-content-sha256"] = payloadHash,
            ["x-amz-date"] = now.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture)
        };
        var authorization = S3Signer.Authorization(method.Method, uri.AbsolutePath, query, headers, payloadHash, _o.AccessKey!, _o.SecretKey!, _o.Region, "s3", now);
        request.Headers.TryAddWithoutValidation("x-amz-content-sha256", payloadHash);
        request.Headers.TryAddWithoutValidation("x-amz-date", headers["x-amz-date"]);
        request.Headers.TryAddWithoutValidation("Authorization", authorization);
        return _http.SendAsync(request, ct);
    }

    private static async Task EnsureAsync(HttpResponseMessage response, string what, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        var body = response.Content is null ? "" : await response.Content.ReadAsStringAsync(ct);
        throw new IOException($"S3 {what}失败：HTTP {(int)response.StatusCode} {(body.Length > 300 ? body[..300] : body)}");
    }

    public void Dispose() => _http.Dispose();
}

public static class S3Signer
{
    public const string EmptyHash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    public static string Encode(string value, bool keepSlash = false)
    {
        var sb = new StringBuilder();
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            var c = (char)b;
            if (char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '~' || keepSlash && c == '/') sb.Append(c);
            else sb.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    public static string EncodePath(string path) => Encode(path, keepSlash: true);

    public static string CanonicalQuery(IDictionary<string, string> query) =>
        string.Join('&', query.Select(x => (Key: Encode(x.Key), Value: Encode(x.Value))).OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => $"{x.Key}={x.Value}"));

    // canonicalPath 须已编码；headers 键为小写。
    public static string Authorization(string method, string canonicalPath, string canonicalQuery, IDictionary<string, string> headers, string payloadHash,
        string accessKey, string secretKey, string region, string service, DateTime utcNow)
    {
        var amzDate = utcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var date = utcNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var sorted = headers.OrderBy(x => x.Key, StringComparer.Ordinal).ToList();
        var canonicalHeaders = string.Concat(sorted.Select(x => $"{x.Key}:{x.Value.Trim()}\n"));
        var signedHeaders = string.Join(';', sorted.Select(x => x.Key));
        var canonical = $"{method}\n{(canonicalPath.Length == 0 ? "/" : canonicalPath)}\n{canonicalQuery}\n{canonicalHeaders}\n{signedHeaders}\n{payloadHash}";
        var scope = $"{date}/{region}/{service}/aws4_request";
        var toSign = $"AWS4-HMAC-SHA256\n{amzDate}\n{scope}\n{Hex(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))}";
        var key = Hmac(Hmac(Hmac(Hmac(Encoding.UTF8.GetBytes("AWS4" + secretKey), date), region), service), "aws4_request");
        return $"AWS4-HMAC-SHA256 Credential={accessKey}/{scope}, SignedHeaders={signedHeaders}, Signature={Hex(Hmac(key, toSign))}";
    }

    private static byte[] Hmac(byte[] key, string data) => HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(data));
    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();
}
