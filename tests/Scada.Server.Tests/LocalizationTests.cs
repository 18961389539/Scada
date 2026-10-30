using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Scada.Server.Localization;

namespace Scada.Server.Tests;

// 多语言：个人语言偏好、后端提示翻译（精确/插值/前缀）、报表表头、后端提示对照表覆盖率。
public sealed partial class LocalizationTests : IClassFixture<ControlTests.ControlSite>
{
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
    [Fact]
    public void EveryUserFacingBackendMessageHasAnEnglishTranslation()
    {
        var root = FindRoot();
        string[] dirs = ["src/Scada.Server/Endpoints", "src/Scada.Server/Auth", "src/Scada.Engine/Control", "src/Scada.Core/Models", "src/Scada.Server/Projects"];
        var files = dirs.SelectMany(d => Directory.GetFiles(Path.Combine(root, d), "*.cs", SearchOption.AllDirectories)).Append(Path.Combine(root, "src/Scada.Server/Program.cs"));
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
        Assert.True(missing.Count == 0, "Untranslated backend messages (add to src/Scada.Server/Localization/messages.en-US.json):\n" + string.Join("\n", missing));
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
