using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Scada.Server.Tests;

// 主备集成测试：在本机以两个独立进程启动两个中心实例（各自的数据目录、共享仲裁目录），
// 验证选主、备机只读/不接网关、配置与历史复制、手动切换、主机被杀后自动接管、原主机重启后回到备机，且全程无双主。
public sealed class HaClusterTests : IAsyncLifetime
{
    private const string Key = "ha-cluster-tests-shared-key-0123456789abcdef";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "scada-ha-" + Guid.NewGuid().ToString("N"));
    private Node _a = null!, _b = null!;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private CancellationTokenSource _monitor = new();
    private Task? _monitorTask;
    private volatile string? _dualPrimary;

    private sealed class Node(string id, int port, int peerPort, string root, string witness, bool prefer)
    {
        public readonly string Id = id, Root = root;
        public readonly int Port = port;
        public string Url => $"http://127.0.0.1:{Port}/";
        public Process? Process;
        public readonly StringBuilder Log = new();

        public void Start()
        {
            var bin = ServerBin();
            var info = new ProcessStartInfo("dotnet") { WorkingDirectory = Path.GetDirectoryName(bin)!, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            info.ArgumentList.Add(bin);
            foreach (var arg in new[]
            {
                $"--Kestrel:Endpoints:Http:Url=http://127.0.0.1:{Port}", $"--Scada:ConfigDbPath={Path.Combine(Root, "config.db")}", $"--Scada:HistoryRoot={Path.Combine(Root, "history")}",
                $"--Scada:BackupRoot={Path.Combine(Root, "backups")}", $"--Scada:DiagnosticsRoot={Path.Combine(Root, "diag")}", $"--Scada:ReportsRoot={Path.Combine(Root, "reports")}",
                $"--Scada:LogRoot={Path.Combine(Root, "logs")}", "--Scada:SeedDemoData=false", "--Scada:FullBackupIntervalHours=0", "--Scada:ReportSchedulerEnabled=false",
                "--Jwt:Key=ha-cluster-tests-jwt-signing-key-0123456789", "--Ha:Enabled=true", $"--Ha:NodeId={Id}", $"--Ha:PeerUrl=http://127.0.0.1:{peerPort}/",
                $"--Ha:SelfUrl={Url}", $"--Ha:SharedKey={Key}", $"--Ha:WitnessPath={witness}", $"--Ha:PreferPrimary={prefer}", "--Ha:HeartbeatMs=200", "--Ha:LeaseMs=1500",
                "--Ha:ClockSkewMs=200", "--Ha:PeerTimeoutMs=500", "--Ha:ConfigSyncMs=500", "--Ha:HistorySyncMs=300"
            }) info.ArgumentList.Add(arg);
            info.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
            Process = System.Diagnostics.Process.Start(info)!;
            Process.OutputDataReceived += (_, e) => { lock (Log) Log.AppendLine(e.Data); };
            Process.ErrorDataReceived += (_, e) => { lock (Log) Log.AppendLine(e.Data); };
            Process.BeginOutputReadLine(); Process.BeginErrorReadLine();
        }

        public void Kill() { try { Process?.Kill(entireProcessTree: true); Process?.WaitForExit(10000); } catch (InvalidOperationException) { } }
        public string Tail() { lock (Log) { var s = Log.ToString(); return s.Length > 4000 ? s[^4000..] : s; } }

        private static string ServerBin()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory); // tests/Scada.Server.Tests/bin/<cfg>/net10.0/
            var framework = dir.Name; var configuration = dir.Parent!.Name;
            var repo = dir.Parent!.Parent!.Parent!.Parent!.Parent!.FullName;
            return Path.Combine(repo, "src", "Scada.Server", "bin", configuration, framework, "Scada.Server.dll");
        }
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); var p = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return p;
    }

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        var witness = Path.Combine(_root, "witness");
        int pa = FreePort(), pb = FreePort();
        _a = new Node("node-a", pa, pb, Path.Combine(_root, "a"), witness, true);
        _b = new Node("node-b", pb, pa, Path.Combine(_root, "b"), witness, false);
        _a.Start(); _b.Start();
        _monitorTask = Task.Run(MonitorAsync);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _monitor.Cancel();
        try { if (_monitorTask is not null) await _monitorTask; } catch { }
        _a.Kill(); _b.Kill();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, true); } catch { }
        _http.Dispose();
    }

    private async Task<string?> RoleAsync(Node node)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, node.Url + "api/ha/peer/status");
            request.Headers.Add("X-Scada-Ha-Key", Key);
            using var cts = new CancellationTokenSource(1000);
            using var response = await _http.SendAsync(request, cts.Token);
            if (!response.IsSuccessStatusCode) return null;
            return (await response.Content.ReadFromJsonAsync<JsonElement>(cts.Token)).GetProperty("role").GetString();
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException) { return null; }
    }

    private async Task MonitorAsync()
    {
        while (!_monitor.IsCancellationRequested)
        {
            var a = RoleAsync(_a); var b = RoleAsync(_b);
            if (await a == "Primary" && await b == "Primary") _dualPrimary = $"双主出现于 {DateTime.Now:HH:mm:ss.fff}";
            await Task.Delay(100);
        }
    }

    private async Task WaitAsync(Func<Task<bool>> condition, string what, int seconds = 30)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition()) return;
            await Task.Delay(150);
        }
        Assert.Fail($"等待超时：{what}\n--- A ---\n{_a.Tail()}\n--- B ---\n{_b.Tail()}");
    }

    private async Task<string> LoginAsync(Node node)
    {
        var response = await _http.PostAsJsonAsync(node.Url + "api/auth/login", new { userName = "admin", password = "admin123" });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, Node node, string path, string token, object? body = null)
    {
        using var request = new HttpRequestMessage(method, node.Url + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null) request.Content = JsonContent.Create(body);
        return await _http.SendAsync(request);
    }

    private static List<string> Audit(Node node, string like)
    {
        using var c = new SqliteConnection($"Data Source={Path.Combine(node.Root, "config.db")};Mode=ReadOnly;Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT Action || '｜' || Result FROM OperationLog WHERE Action LIKE $p ORDER BY Id";
        cmd.Parameters.AddWithValue("$p", like);
        using var r = cmd.ExecuteReader(); var list = new List<string>(); while (r.Read()) list.Add(r.GetString(0)); return list;
    }

    private static string RawFile(Node node) => Path.Combine(node.Root, "history", $"raw_{DateTime.UtcNow:yyyyMM}.db");

    private static long CountHistory(Node node, int tagId)
    {
        if (!File.Exists(RawFile(node))) return 0;
        using var c = new SqliteConnection($"Data Source={RawFile(node)};Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT COUNT(*) FROM HistoryValue WHERE TagId = $t"; cmd.Parameters.AddWithValue("$t", tagId);
        try { return Convert.ToInt64(cmd.ExecuteScalar()); } catch (SqliteException) { return 0; }
    }

    [Fact]
    public async Task TwoInstancesElectReplicateSwitchOverAndFailOverWithoutDualPrimary()
    {
        await WaitAsync(async () => await RoleAsync(_a) == "Primary" && await RoleAsync(_b) == "Standby", "A 为主机、B 为备机");

        // 配置复制：主机上的变更出现在备机；两节点共用 Jwt 密钥，主机签发的令牌在备机可用。
        var token = await LoginAsync(_a);
        var area = "复制区域" + Guid.NewGuid().ToString("N")[..4];
        Assert.True((await SendAsync(HttpMethod.Post, _a, "api/areas", token, new { name = area })).IsSuccessStatusCode);
        await WaitAsync(async () => (await (await SendAsync(HttpMethod.Get, _b, "api/areas", token)).Content.ReadAsStringAsync()).Contains(area), "配置复制到备机");

        // 备机只读，不接网关，并告知主机地址。
        var refused = await SendAsync(HttpMethod.Post, _b, "api/areas", token, new { name = "备机写入" });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
        Assert.Equal("standby", refused.Headers.GetValues("X-Scada-Ha-Role").Single());
        var gateway = await _http.PostAsJsonAsync(_b.Url + "api/gateway-agent/hello", new { });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, gateway.StatusCode);
        Assert.Equal(_a.Url, gateway.Headers.GetValues("X-Scada-Primary").Single());
        var control = await SendAsync(HttpMethod.Post, _b, "api/control/check", token, new { tagId = 1, value = 1 });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, control.StatusCode);

        // 历史复制：写入主机的原始历史库，备机按 rowid 增量追上。
        Directory.CreateDirectory(Path.GetDirectoryName(RawFile(_a))!);
        using (var c = new SqliteConnection($"Data Source={RawFile(_a)};Pooling=False"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            cmd.CommandText = "CREATE TABLE IF NOT EXISTS HistoryValue (TagId INTEGER NOT NULL, Ts INTEGER NOT NULL, Value REAL NOT NULL, Quality INTEGER NOT NULL, CONSTRAINT PK_HistoryValue PRIMARY KEY (TagId, Ts));"
                + $"INSERT INTO HistoryValue VALUES (9001,{ts},1.5,0),(9001,{ts + 1000},2.5,0),(9001,{ts + 2000},3.5,0);";
            cmd.ExecuteNonQuery();
        }
        await WaitAsync(() => Task.FromResult(CountHistory(_b, 9001) == 3), "历史复制到备机");

        // 手动切换：A 交给 B；B 接管并记审计，A 成为备机。
        var bad = await SendAsync(HttpMethod.Post, _a, "api/ha/switchover", token, new { reason = "" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        var switched = await SendAsync(HttpMethod.Post, _a, "api/ha/switchover", token, new { reason = "计划检修" });
        Assert.True(switched.IsSuccessStatusCode, await switched.Content.ReadAsStringAsync());
        await WaitAsync(async () => await RoleAsync(_b) == "Primary" && await RoleAsync(_a) == "Standby", "手动切换后 B 为主机");
        Assert.Contains(Audit(_b, "主备手动切换%"), x => x.Contains("计划检修"));
        Assert.Contains(Audit(_b, "主备切换：本机成为主机%"), x => x.Contains("手动切换") && x.Contains("admin"));
        Assert.Equal(3, CountHistory(_b, 9001));

        // 新主机上的变更反向复制到 A。
        var area2 = "切换后区域" + Guid.NewGuid().ToString("N")[..4];
        Assert.True((await SendAsync(HttpMethod.Post, _b, "api/areas", token, new { name = area2 })).IsSuccessStatusCode);
        await WaitAsync(async () => (await (await SendAsync(HttpMethod.Get, _a, "api/areas", token)).Content.ReadAsStringAsync()).Contains(area2), "配置反向复制到 A");

        // 主机进程被强杀：租约过期后 A 自动接管并记审计。
        _b.Kill();
        await WaitAsync(async () => await RoleAsync(_a) == "Primary", "B 被杀后 A 自动接管", 20);
        Assert.Contains(Audit(_a, "主备切换：本机成为主机%"), x => x.Contains("自动切换"));
        Assert.True((await SendAsync(HttpMethod.Post, _a, "api/areas", token, new { name = "接管后可写" + Guid.NewGuid().ToString("N")[..4] })).IsSuccessStatusCode);

        // 原主机重启：租约在 A 手里，B 回到备机并继续复制，不抢主。
        _b.Start();
        await WaitAsync(async () => await RoleAsync(_b) == "Standby", "B 重启后为备机");
        await Task.Delay(3000);
        Assert.Equal("Primary", await RoleAsync(_a));
        Assert.Equal("Standby", await RoleAsync(_b));

        // 状态接口（系统权限）与指标。
        var status = await (await SendAsync(HttpMethod.Get, _a, "api/ha/status", token)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Primary", status.GetProperty("role").GetString());
        Assert.True(status.GetProperty("events").GetArrayLength() > 0);
        var metrics = await (await SendAsync(HttpMethod.Get, _a, "metrics", token)).Content.ReadAsStringAsync();
        Assert.Contains("scada_ha_primary{node=\"node-a\"} 1", metrics);
        Assert.Null(_dualPrimary);
    }
}
