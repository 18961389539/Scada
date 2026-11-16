using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Scada.Server.Localization;

namespace Scada.Server.Tests;

// 多语言：个人语言偏好、后端提示翻译（精确/插值/前缀）、报表表头、后端提示对照表覆盖率。
public sealed partial class LocalizationTests : IClassFixture<ControlTests.ControlSite>
{
    // 历史欠账上限：只允许下降。翻译一批就从基线删除并同步下调本常量；新增中文提示必须翻译，不能塞进基线。
    private const int BaselineCeiling = 848;
    private readonly ControlTests.ControlSite _site;
    public LocalizationTests(ControlTests.ControlSite site) => _site = site;

    [Fact]
    public async Task UserLocaleIsStoredServerSideAndMessagesFollowRequestLocale()
    {
        var admin = await _site.ClientAsync("admin");
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/api/auth/me/locale", new { locale = "fr-FR" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync("/api/auth/me/locale", new { locale = "en-US" })).StatusCode);
        Assert.Equal("en-US", (await admin.GetFromJsonAsync<JsonElement>("/api/auth/me")).GetProperty("locale").GetString());
        try
        {
            var login = await _site.CreateClient().PostAsJsonAsync("/api/auth/login", new { userName = "admin", password = "admin123" });
            Assert.Equal("en-US", (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("user").GetProperty("locale").GetString());

            var anonymous = _site.CreateClient();
            HttpRequestMessage Bad(string? locale, string? accept = null)
            {
                var r = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login") { Content = JsonContent.Create(new { userName = "admin", password = "wrong-" + Guid.NewGuid() }) };
                if (locale is not null) r.Headers.Add(Localizer.LocaleHeader, locale);
                if (accept is not null) r.Headers.Add("Accept-Language", accept);
                return r;
            }
            var en = await anonymous.SendAsync(Bad("en-US"));
            Assert.Equal(HttpStatusCode.Unauthorized, en.StatusCode);
            Assert.Equal("Incorrect user name or password", (await en.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("message").GetString());
            Assert.Contains("Incorrect", await (await anonymous.SendAsync(Bad(null, "en-GB,en;q=0.9"))).Content.ReadAsStringAsync());
            Assert.Contains("用户名或密码错误", await (await anonymous.SendAsync(Bad(null))).Content.ReadAsStringAsync());
            Assert.Contains("用户名或密码错误", await (await anonymous.SendAsync(Bad("zh-CN", "en-US"))).Content.ReadAsStringAsync());
        }
        finally { await admin.PutAsJsonAsync("/api/auth/me/locale", new { locale = "zh-CN" }); }
    }

    [Fact]
    public void InterpolatedPrefixedAndJoinedMessagesAreTranslated()
    {
        Assert.Equal("Change 5 exceeds the limit 2 (current 1)", Localizer.T("单次变化 5 超过限制 2（当前 1）", "en-US"));
        Assert.Equal("No permission to access tags: 泵1,泵2", Localizer.T("无权访问测点：泵1,泵2", "en-US"));
        Assert.Equal("Pre-check failed, no parameters downloaded: Tag is not writable", Localizer.T("预检查未通过，未下发任何参数：测点不可写", "en-US"));
        Assert.Equal("Tag is not writable; Invalid command value", Localizer.T("测点不可写；命令值无效", "en-US"));
        Assert.Equal("v3 · template revision 2 · manual: admin", Localizer.T("v3 · 模板版本 2 · 手动生成：admin", "en-US"));
        Assert.Equal("测点不可写", Localizer.T("测点不可写", "zh-CN"));
        Assert.Equal("某条尚未翻译的提示", Localizer.T("某条尚未翻译的提示", "en-US"));
    }

    [Fact]
    public async Task NestedJobStatusErrorsFollowRequestLocale()
    {
        const string payload = """[{"id":1,"status":{"state":"error","error":"SN 123 在本任务近 10 天内已采集过（重码），本次采集被拒绝并应答 3。"},"scannerStatus":{"error":"无权配置该追溯任务"}}]""";
        var http = new DefaultHttpContext();
        http.Request.Path = "/api/traceability/jobs";
        http.Request.Headers[Localizer.LocaleHeader] = Localizer.EnUs;
        http.Response.Body = new MemoryStream();
        await new ResponseLocalization(async context =>
        {
            context.Response.ContentType = "application/json";
            await context.Response.Body.WriteAsync(Encoding.UTF8.GetBytes(payload));
        }).InvokeAsync(http);
        http.Response.Body.Position = 0;
        using var json = JsonDocument.Parse(http.Response.Body);
        var job = json.RootElement[0];
        var statusError = job.GetProperty("status").GetProperty("error").GetString();
        Assert.Contains("duplicate SN", statusError);
        Assert.DoesNotContain("重码", statusError);
        Assert.Equal("No permission to configure this traceability job", job.GetProperty("scannerStatus").GetProperty("error").GetString());
    }

    [Fact]
    public async Task AdHocReportHeadersFollowLocale()
    {
        var admin = await _site.ClientAsync("admin");
        var tag = (await (await admin.PostAsJsonAsync("/api/tags", _site.TagBody(_site.DeviceA, "报表" + Guid.NewGuid().ToString("N")[..5]))).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        async Task<string> Header(string locale)
        {
            var r = new HttpRequestMessage(HttpMethod.Get, $"/api/reports/export?type=daily&tagIds={tag}");
            r.Headers.Add(Localizer.LocaleHeader, locale);
            var response = await admin.SendAsync(r); Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
            using var book = new ClosedXML.Excel.XLWorkbook(await response.Content.ReadAsStreamAsync());
            var sheet = book.Worksheets.First();
            return sheet.Name + "|" + string.Join(",", Enumerable.Range(1, 6).Select(i => sheet.Cell(1, i).GetString()));
        }
        Assert.Equal("Report|Time,Tag,Average,Minimum,Maximum,Samples", await Header("en-US"));
        Assert.Equal("报表|时间,测点,平均值,最小值,最大值,样本数", await Header("zh-CN"));
    }

    // 未翻译检查：服务端直接返回给用户的提示（与 tools/backend-i18n-extract.py 同一抽取规则）都必须在英文对照表中。
    // 以前只扫 5 个写死的目录，Services / Reports / External / Mcp / Ha 里的几百条中文提示
    // 完全不在门禁里；现在扫全部产品程序集，历史欠账记在 i18n-baseline.json（只能减不能增），
    // 任何**新增**中文提示都会立刻让这条用例变红。
    [Fact]
    public void EveryUserFacingBackendMessageHasAnEnglishTranslation()
    {
        var root = FindRoot();
        string[] dirs = ["src/Scada.Server", "src/Scada.Engine", "src/Scada.Core", "src/Scada.Data",
            "src/Scada.Collector", "src/Scada.Gateway", "src/Scada.Gateway.Host", "src/Scada.Protocols"];
        var files = dirs
            .Where(d => Directory.Exists(Path.Combine(root, d)))
            .SelectMany(d => Directory.GetFiles(Path.Combine(root, d), "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
        var missing = new SortedSet<string>();
        foreach (var file in files)
        {
            var source = Regex.Replace(File.ReadAllText(file), "//.*", "");
            foreach (Match m in MessagePattern().Matches(source))
            {
                var text = m.Groups[3].Value.Replace("\\\"", "\"").Replace("\\n", "\n");
                var key = m.Groups[2].Value == "$" ? Holes(text) : text;
                if (!Localizer.Messages.ContainsKey(key)) missing.Add(key);
            }
        }
        var baselinePath = Path.Combine(root, "tests", "Scada.Server.Tests", "i18n-baseline.json");
        if (Environment.GetEnvironmentVariable("SCADA_I18N_GENERATE_BASELINE") == "1")
        {
            // 维护入口：只有显式带这个环境变量才会重写基线，CI 与日常测试永远不会自动放宽门禁。
            File.WriteAllText(baselinePath, JsonSerializer.Serialize(missing.ToArray(), new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            }) + "\n");
            return;
        }
        Assert.True(File.Exists(baselinePath), "缺少 i18n 基线文件：tests/Scada.Server.Tests/i18n-baseline.json");
        var baseline = JsonSerializer.Deserialize<HashSet<string>>(File.ReadAllText(baselinePath))!;
        // 基线只降不升：手工把新欠账塞进基线（绕过下面的 added 断言）会先在这里失败。
        Assert.True(baseline.Count <= BaselineCeiling,
            $"i18n 基线从 {BaselineCeiling} 条涨到 {baseline.Count} 条；新增提示必须翻译，不能加进基线。");
        // 已翻译的旧条目必须从基线删除，否则基线会一直虚高，"清零"永远做不到。
        var stale = baseline.Except(missing).Order(StringComparer.Ordinal).ToArray();
        Assert.True(stale.Length == 0,
            "以下条目已有英文翻译，请从 i18n-baseline.json 删除并下调 BaselineCeiling：\n" + string.Join("\n", stale));
        var added = missing.Except(baseline).ToArray();
        Assert.True(added.Length == 0,
            "新增了未翻译的后端提示（请加到 src/Scada.Server/Localization/messages.en-US.json）：\n" + string.Join("\n", added));
        foreach (var (zh, en) in Localizer.Messages)
            Assert.True(Placeholders(zh) == Placeholders(en), $"Placeholder mismatch: {zh} => {en}");
    }

    private static string Placeholders(string s) => string.Join(",", Regex.Matches(s, @"\{\w+\}").Select(m => m.Value).Order());
    private static string Holes(string s)
    {
        var sb = new System.Text.StringBuilder(); var n = 0;
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '{' && i + 1 < s.Length && s[i + 1] == '{') { sb.Append('{'); i++; continue; }
            if (s[i] == '}' && i + 1 < s.Length && s[i + 1] == '}') { sb.Append('}'); i++; continue; }
            if (s[i] == '{')
            {
                var depth = 1; var j = i + 1;
                while (j < s.Length && depth > 0) { depth += s[j] == '{' ? 1 : s[j] == '}' ? -1 : 0; j++; }
                sb.Append('{').Append(n++).Append('}'); i = j - 1; continue;
            }
            sb.Append(s[i]);
        }
        return sb.ToString();
    }
    // 仓库根：优先用编译期记录的源文件位置，其次从输出目录向上找。
    // 单独依赖输出目录时，任何自定义 OutDir（CI 汇总目录、共享产物目录）都会让用例假失败。
    private static string FindRoot([System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        for (var dir = new DirectoryInfo(Path.GetDirectoryName(source) ?? "."); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Scada.sln"))) return dir.FullName;
        var probe = new DirectoryInfo(AppContext.BaseDirectory);
        while (probe is not null && !File.Exists(Path.Combine(probe.FullName, "Scada.sln"))) probe = probe.Parent;
        return probe?.FullName ?? throw new InvalidOperationException("repo root not found");
    }

    [GeneratedRegex(@"(BadRequest|Conflict|NotFound|Problem|ForbidAsync|Fail|Error|Issue|Add|Reject|Deny|message\s*[=:]|Message\s*=|throw new \w+Exception)\s*\(?[^;]*?(\$?)""((?:[^""\\\n]|\\.)*[\u4e00-\u9fff](?:[^""\\\n]|\\.)*)""")]
    private static partial Regex MessagePattern();
}
