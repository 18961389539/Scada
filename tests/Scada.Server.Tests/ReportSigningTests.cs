using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Scada.Core.Options;
using Scada.Data;
using Scada.Server.Reports;

namespace Scada.Server.Tests;

// 报表签名：归档即 CMS 分离签名；下载/验证接口校验签名；电子签名审批（审核→批准）须理由+密码复核，并写审计。
public sealed class ReportSigningTests : IClassFixture<AuthorizationTests.Site>
{
    private readonly AuthorizationTests.Site _site;
    public ReportSigningTests(AuthorizationTests.Site site) => _site = site;
    private IDbContextFactory<ConfigDbContext> Factory() => _site.Services.GetRequiredService<IDbContextFactory<ConfigDbContext>>();

    private static async Task<JsonElement> Json(HttpResponseMessage r)
    {
        Assert.True(r.IsSuccessStatusCode, $"{r.StatusCode}: {await r.Content.ReadAsStringAsync()}");
        return await r.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<long> GenerateAsync()
    {
        var cfg = await _site.ClientAsync("cfgA");
        var body = new
        {
            name = "签名" + Guid.NewGuid().ToString("N")[..6], period = "Day", areaId = _site.AreaA, scheduleEnabled = false,
            columns = new object[] { new { key = "avg", title = "平均", kind = "TagAvg", tagId = _site.TagA, decimals = 2 } }
        };
        var template = await Json(await cfg.PostAsJsonAsync("/api/report-templates", body));
        var run = await Json(await cfg.PostAsJsonAsync($"/api/report-templates/{template.GetProperty("id").GetInt32()}/generate", new { }));
        Assert.True(run.GetProperty("signed").GetBoolean());
        return run.GetProperty("id").GetInt64();
    }

    private static object Sign(string step, string decision = "approved", string password = "secret123", string reason = "数据核对无误") => new { step, decision, reason, password };

    [Fact]
    public async Task ArchivedFilesAreSignedVerifiedOnDownloadAndTamperRejected()
    {
        var runId = await GenerateAsync();
        var op = await _site.ClientAsync("opA");
        var signer = _site.Services.GetRequiredService<ReportSigner>();
        Assert.True(File.Exists(signer.CertificatePath));
        Assert.EndsWith(Path.Combine("keys", ReportSigner.DefaultFileName), signer.CertificatePath);

        var verify = await op.GetFromJsonAsync<JsonElement>($"/api/report-runs/{runId}/verify");
        Assert.True(verify.GetProperty("ok").GetBoolean());
        Assert.Equal(signer.Certificate.Thumbprint, verify.GetProperty("signerThumbprint").GetString());
        var file = verify.GetProperty("files")[0].GetProperty("signature");
        Assert.True(file.GetProperty("selfSigned").GetBoolean());
        Assert.True(file.GetProperty("currentCertificate").GetBoolean());
        Assert.True(file.GetProperty("signingTs").GetInt64() > 0);

        var pdf = await op.GetAsync($"/api/report-runs/{runId}/download?format=pdf");
        Assert.Equal("valid", pdf.Headers.GetValues("X-Report-Signature").Single());
        var pdfBytes = await pdf.Content.ReadAsByteArrayAsync();
        // 下载的 .p7s 可对下载文件独立验证。
        var p7s = await op.GetByteArrayAsync($"/api/report-runs/{runId}/signature?format=pdf");
        Assert.True(signer.Verify(pdfBytes, Convert.ToBase64String(p7s), null).Valid);
        Assert.False(signer.Verify([.. pdfBytes, 0], Convert.ToBase64String(p7s), null).Valid);

        // 上传副本验证。
        var good = await Json(await op.PostAsync($"/api/report-runs/{runId}/verify-file", new ByteArrayContent(pdfBytes)));
        Assert.True(good.GetProperty("ok").GetBoolean()); Assert.Equal("pdf", good.GetProperty("matched").GetString());
        var bad = await Json(await op.PostAsync($"/api/report-runs/{runId}/verify-file?format=pdf", new ByteArrayContent([.. pdfBytes, 1])));
        Assert.False(bad.GetProperty("ok").GetBoolean()); Assert.False(bad.GetProperty("signature").GetProperty("valid").GetBoolean());

        // 同时改文件与哈希（绕过 SHA256）仍被签名发现。
        await using (var db = await Factory().CreateDbContextAsync())
        {
            var run = await db.ReportRuns.SingleAsync(x => x.Id == runId);
            File.SetAttributes(run.XlsxPath, FileAttributes.Normal);
            var tampered = (await File.ReadAllBytesAsync(run.XlsxPath)).Concat(new byte[] { 0 }).ToArray();
            await File.WriteAllBytesAsync(run.XlsxPath, tampered);
            run.XlsxSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(tampered));
            await db.SaveChangesAsync();
        }
        var denied = await op.GetAsync($"/api/report-runs/{runId}/download?format=xlsx");
        Assert.Equal(HttpStatusCode.Conflict, denied.StatusCode);
        Assert.Contains("数字签名无效", await denied.Content.ReadAsStringAsync());
        Assert.NotNull(await _site.LastLogAsync(x => x.Action == $"下载报表 #{runId}" && x.Result.Contains("数字签名无效")));
        verify = await op.GetFromJsonAsync<JsonElement>($"/api/report-runs/{runId}/verify");
        Assert.False(verify.GetProperty("ok").GetBoolean());
        // 已损坏的报表不能电子签名。
        Assert.Equal(HttpStatusCode.Conflict, (await op.PostAsJsonAsync($"/api/report-runs/{runId}/approvals", Sign("review"))).StatusCode);
    }

    [Fact]
    public async Task UnsignedLegacyRunsDownloadButAreFlagged()
    {
        var runId = await GenerateAsync();
        await using (var db = await Factory().CreateDbContextAsync())
        {
            var run = await db.ReportRuns.SingleAsync(x => x.Id == runId);
            run.XlsxSignature = null; run.PdfSignature = null; run.SignerThumbprint = null;
            await db.SaveChangesAsync();
        }
        var op = await _site.ClientAsync("opA");
        var r = await op.GetAsync($"/api/report-runs/{runId}/download?format=xlsx");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("unsigned", r.Headers.GetValues("X-Report-Signature").Single());
        var verify = await op.GetFromJsonAsync<JsonElement>($"/api/report-runs/{runId}/verify");
        Assert.False(verify.GetProperty("signed").GetBoolean()); Assert.False(verify.GetProperty("ok").GetBoolean());
        Assert.Equal(HttpStatusCode.NotFound, (await op.GetAsync($"/api/report-runs/{runId}/signature")).StatusCode);
    }

    [Fact]
    public async Task ReviewThenApproveRequiresReasonPasswordAndSeparatePeople()
    {
        var runId = await GenerateAsync();
        var op = await _site.ClientAsync("opA"); var cfg = await _site.ClientAsync("cfgA");
        var url = $"/api/report-runs/{runId}/approvals";

        Assert.Equal(HttpStatusCode.BadRequest, (await op.PostAsJsonAsync(url, Sign("review", reason: " "))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await op.PostAsJsonAsync(url, Sign("review", password: "wrong"))).StatusCode);
        Assert.NotNull(await _site.LastLogAsync(x => x.UserName == "opA" && x.Result == "拒绝：签名密码验证失败"));
        Assert.Equal(HttpStatusCode.Conflict, (await cfg.PostAsJsonAsync(url, Sign("approve"))).StatusCode); // 须先审核

        var reviewed = await Json(await op.PostAsJsonAsync(url, Sign("review")));
        Assert.Equal("reviewed", reviewed.GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await cfg.PostAsJsonAsync(url, Sign("review"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await op.PostAsJsonAsync(url, Sign("approve"))).StatusCode); // 批准需配置权限
        var approved = await Json(await cfg.PostAsJsonAsync(url, Sign("approve", reason: "同意发布")));
        Assert.Equal("approved", approved.GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await cfg.PostAsJsonAsync(url, Sign("approve"))).StatusCode);

        var log = await _site.LastLogAsync(x => x.UserName == "cfgA" && x.Action == $"报表电子签名 #{runId}（批准通过）" && x.Result == "成功");
        Assert.Equal("同意发布", JsonDocument.Parse(log!.Details!).RootElement.GetProperty("reason").GetString());
        var verify = await op.GetFromJsonAsync<JsonElement>($"/api/report-runs/{runId}/verify");
        Assert.True(verify.GetProperty("ok").GetBoolean());
        Assert.Equal("approved", verify.GetProperty("status").GetString());
        Assert.Equal(2, verify.GetProperty("approvals").GetArrayLength());

        // 篡改签名记录（改理由）后验证失败。
        await using (var db = await Factory().CreateDbContextAsync())
        {
            var a = await db.ReportApprovals.SingleAsync(x => x.RunId == runId && x.Step == "approve");
            a.Reason = "被改过";
            await db.SaveChangesAsync();
        }
        verify = await op.GetFromJsonAsync<JsonElement>($"/api/report-runs/{runId}/verify");
        Assert.False(verify.GetProperty("ok").GetBoolean());

        // 同一人不能既审核又批准；驳回即终结。
        var second = await GenerateAsync();
        var url2 = $"/api/report-runs/{second}/approvals";
        await Json(await cfg.PostAsJsonAsync(url2, Sign("review")));
        var self = await cfg.PostAsJsonAsync(url2, Sign("approve"));
        Assert.Equal(HttpStatusCode.Conflict, self.StatusCode);
        Assert.Contains("批准人不能与审核人相同", await self.Content.ReadAsStringAsync());
        var third = await GenerateAsync();
        var rejected = await Json(await op.PostAsJsonAsync($"/api/report-runs/{third}/approvals", Sign("review", "rejected", reason: "数据缺失")));
        Assert.Equal("rejected", rejected.GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await cfg.PostAsJsonAsync($"/api/report-runs/{third}/approvals", Sign("approve"))).StatusCode);
    }

    [Fact]
    public void ConfiguredCertificateMustExistAndRealPfxIsUsed()
    {
        var dir = Path.Combine(Path.GetTempPath(), "scada-sign-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var missing = new ReportSigner(Options.Create(new ScadaSettings { ConfigDbPath = Path.Combine(dir, "c.db"), ReportSigningCertPath = Path.Combine(dir, "none.pfx") }), NullLogger<ReportSigner>.Instance);
            Assert.Throws<FileNotFoundException>(() => missing.Certificate);
            Assert.False(File.Exists(Path.Combine(dir, "keys", ReportSigner.DefaultFileName)));

            // 自动生成（带口令）后，作为“外部证书”配置使用同一 PFX。
            var auto = new ReportSigner(Options.Create(new ScadaSettings { ConfigDbPath = Path.Combine(dir, "c.db"), ReportSigningCertPassword = "pw" }), NullLogger<ReportSigner>.Instance);
            var thumb = auto.Certificate.Thumbprint;
            if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(auto.CertificatePath));
            var configured = new ReportSigner(Options.Create(new ScadaSettings { ConfigDbPath = Path.Combine(dir, "x.db"), ReportSigningCertPath = auto.CertificatePath, ReportSigningCertPassword = "pw" }), NullLogger<ReportSigner>.Instance);
            var (sig, t, _) = configured.Sign([1, 2, 3], DateTimeOffset.UtcNow);
            Assert.Equal(thumb, t);
            Assert.True(auto.Verify([1, 2, 3], sig, thumb).Valid);
            Assert.False(auto.Verify([1, 2, 3], sig, "00").Valid);
        }
        finally { Directory.Delete(dir, true); }
    }
}
