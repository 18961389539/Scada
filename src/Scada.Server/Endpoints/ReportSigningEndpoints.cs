using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Models;
using Scada.Core.Security;
using Scada.Data;
using Scada.Server.Auth;
using Scada.Server.Reports;
using Scada.Server.Services;

namespace Scada.Server.Endpoints;

public sealed record ReportSignRequest(string Step, string Decision, string Reason, string Password);

// 报表签名验证与电子签名审批（审核 → 批准）。
// 验证：区域 View。审核：区域 View；批准：区域 Configure 且不能与审核人相同。签名须填写理由并重新输入本人密码，结果写审计。
// 任一步驳回即终结（如需更正请重新生成新版本）；已批准后不再接受签名。
public static class ReportSigningEndpoints
{
    public const int MaxUploadBytes = 64 * 1024 * 1024;

    public static object ApprovalView(ReportApproval a) => new { a.Id, a.RunId, a.Step, a.Decision, a.UserId, a.UserName, a.DisplayName, a.Reason, a.Ts, a.SignerThumbprint };

    // 审批状态由只追加的签名记录推导：none | reviewed | approved | rejected。
    public static string Status(IReadOnlyList<ReportApproval> list) =>
        list.Any(x => x.Decision == "rejected") ? "rejected"
        : list.Any(x => x.Step == "approve") ? "approved"
        : list.Any(x => x.Step == "review") ? "reviewed" : "none";

    private sealed record FileCheck(string Format, bool Exists, bool HashOk, object Signature, bool Ok);
    private sealed record ApprovalCheck(object Approval, object Signature, bool ManifestMatches, bool Ok);

    private static object CheckView(SignatureCheck c) => new { c.Present, c.Valid, c.Error, c.Subject, c.Thumbprint, c.SigningTs, c.CurrentCertificate, c.ChainTrusted, c.SelfSigned };

    public static void Map(RouteGroupBuilder runs)
    {
        runs.MapGet("/{id:long}/verify", async (long id, HttpContext http, IDbContextFactory<ConfigDbContext> factory, ReportArchiveService archive, ReportSigner signer, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var run = await db.ReportRuns.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
            if (run is null || !(await http.GetAccessAsync()).Has(Permission.View, run.AreaId)) return Results.NotFound();
            var approvals = await db.ReportApprovals.AsNoTracking().Where(x => x.RunId == id).OrderBy(x => x.Ts).ThenBy(x => x.Id).ToListAsync(ct);
            var files = new List<FileCheck>();
            if (run.PurgedTs is null)
                foreach (var format in new[] { "xlsx", "pdf" })
                {
                    var r = await archive.ReadCheckedAsync(run, format, ct);
                    files.Add(new(format, r.Exists, r.HashOk, CheckView(r.Signature), r.Exists && r.HashOk && r.Signature.Valid));
                }
            var approvalChecks = approvals.Select(a =>
            {
                var check = signer.Verify(Encoding.UTF8.GetBytes(a.ManifestJson), a.Signature, a.SignerThumbprint);
                var matches = ManifestMatches(a, run);
                return new ApprovalCheck(ApprovalView(a), CheckView(check), matches, check.Valid && matches);
            }).ToList();
            var filesOk = files.Count > 0 && files.All(f => f.Ok);
            var approvalsOk = approvalChecks.All(x => x.Ok);
            return Results.Ok(new
            {
                runId = id, purged = run.PurgedTs is not null, signed = run.XlsxSignature is not null, run.SignerThumbprint, run.SignerSubject, run.SignedTs, run.SignatureError,
                files, approvals = approvalChecks, status = Status(approvals), ok = filesOk && approvalsOk,
                currentThumbprint = signer.Enabled ? signer.TryCurrentThumbprint() : null
            });
        });

        // 下载分离签名（.p7s，DER），可用 openssl cms -verify -binary -inform DER -content 报表文件 -in 签名 -noverify 独立验证。
        runs.MapGet("/{id:long}/signature", async (long id, string? format, HttpContext http, IDbContextFactory<ConfigDbContext> factory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var run = await db.ReportRuns.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
            if (run is null || !(await http.GetAccessAsync()).Has(Permission.View, run.AreaId)) return Results.NotFound();
            var pdf = string.Equals(format, "pdf", StringComparison.OrdinalIgnoreCase);
            var sig = pdf ? run.PdfSignature : run.XlsxSignature;
            if (sig is null) return Results.Problem("该报表未签名", statusCode: 404);
            return Results.File(Convert.FromBase64String(sig), "application/pkcs7-signature", ReportTemplateEndpoints.FileName(run, (pdf ? "pdf" : "xlsx") + ".p7s"));
        });

        // 上传一份文件（例如邮件附件或外发副本），校验它是否与该归档报表一致且签名有效。请求体为文件原始字节。
        runs.MapPost("/{id:long}/verify-file", async (long id, string? format, HttpContext http, IDbContextFactory<ConfigDbContext> factory, ReportSigner signer, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var run = await db.ReportRuns.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
            if (run is null || !(await http.GetAccessAsync()).Has(Permission.View, run.AreaId)) return Results.NotFound();
            using var buffer = new MemoryStream();
            var chunk = new byte[81920]; int n;
            while ((n = await http.Request.Body.ReadAsync(chunk, ct)) > 0)
            {
                if (buffer.Length + n > MaxUploadBytes) return EndpointHelpers.BadRequest($"文件过大：单个文件不能超过 {MaxUploadBytes / (1024 * 1024)} MB");
                buffer.Write(chunk, 0, n);
            }
            var bytes = buffer.ToArray();
            if (bytes.Length == 0) return EndpointHelpers.BadRequest("请上传文件");
            var hash = Convert.ToHexString(SHA256.HashData(bytes));
            var matched = string.Equals(hash, run.PdfSha256, StringComparison.OrdinalIgnoreCase) ? "pdf"
                : string.Equals(hash, run.XlsxSha256, StringComparison.OrdinalIgnoreCase) ? "xlsx" : null;
            // 内容与归档哈希都不一致时仍按 format 用对应签名验证，给出“签名无效/内容已被修改”的明确结论。
            var check = signer.Verify(bytes, (matched ?? format) == "pdf" ? run.PdfSignature : run.XlsxSignature, run.SignerThumbprint);
            return Results.Ok(new { sha256 = hash, matched, signature = CheckView(check), ok = matched is not null && check.Valid });
        });

        runs.MapGet("/{id:long}/approvals", async (long id, HttpContext http, IDbContextFactory<ConfigDbContext> factory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var run = await db.ReportRuns.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
            if (run is null || !(await http.GetAccessAsync()).Has(Permission.View, run.AreaId)) return Results.NotFound();
            var list = await db.ReportApprovals.AsNoTracking().Where(x => x.RunId == id).OrderBy(x => x.Ts).ThenBy(x => x.Id).ToListAsync(ct);
            return Results.Ok(new { status = Status(list), items = list.Select(ApprovalView) });
        });

        runs.MapPost("/{id:long}/approvals", async (long id, ReportSignRequest request, HttpContext http, IDbContextFactory<ConfigDbContext> factory,
            ReportArchiveService archive, ReportSigner signer, LoginThrottle throttle, CancellationToken ct) =>
        {
            var step = request.Step?.Trim().ToLowerInvariant(); var decision = request.Decision?.Trim().ToLowerInvariant();
            var action = $"报表电子签名 #{id}（{(step == "approve" ? "批准" : "审核")}{(decision == "rejected" ? "驳回" : "通过")}）";
            if (step is not ("review" or "approve")) return EndpointHelpers.BadRequest("步骤须为 review 或 approve");
            if (decision is not ("approved" or "rejected")) return EndpointHelpers.BadRequest("结论须为 approved 或 rejected");
            var reason = request.Reason?.Trim() ?? "";
            if (reason.Length is 0 or > 500) return EndpointHelpers.BadRequest("请填写签名理由（不超过 500 字）");
            if (!signer.Enabled) return Results.Problem("未启用报表签名", statusCode: 409);

            await using var db = await factory.CreateDbContextAsync(ct);
            var run = await db.ReportRuns.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
            var access = await http.GetAccessAsync();
            if (run is null || !access.Has(Permission.View, run.AreaId)) return Results.NotFound();
            if (step == "approve" && !access.Has(Permission.Configure, run.AreaId))
                return await Audit.ForbidAsync(http, action, "批准需要该区域的配置权限", "ReportRun", id);

            // 重新认证：本人密码，失败计入登录限流（防止借签名接口暴力破解）。
            if (http.User.GetUserId() is not int uid) return Results.Unauthorized();
            var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == uid, ct);
            var key = "esign:" + LoginThrottle.Key(user?.UserName ?? "?", http.Connection.RemoteIpAddress?.ToString());
            if (throttle.IsLocked(key, out var retry))
            {
                await Audit.DeniedAsync(factory, http, action, "密码错误次数过多，已锁定", ct, "ReportRun", id);
                return Results.Json(new { message = $"密码错误次数过多，请在 {(int)Math.Ceiling(retry.TotalSeconds)} 秒后重试" }, statusCode: StatusCodes.Status429TooManyRequests);
            }
            if (user is null || !user.Enabled || string.IsNullOrEmpty(request.Password) || !PasswordHasher.Verify(request.Password, user.PasswordHash))
            {
                throttle.RecordFailure(key);
                await Audit.DeniedAsync(factory, http, action, "签名密码验证失败", ct, "ReportRun", id);
                return Results.Json(new { message = "密码不正确" }, statusCode: StatusCodes.Status403Forbidden);
            }

            throttle.Reset(key);
            var existing = await db.ReportApprovals.AsNoTracking().Where(x => x.RunId == id).ToListAsync(ct);
            var status = Status(existing);
            string? conflict = status switch
            {
                "rejected" => "该报表已被驳回，请重新生成新版本",
                "approved" => "该报表已批准，不能再签名",
                "none" when step == "approve" => "须先完成审核",
                "reviewed" when step == "review" => "该报表已审核，等待批准",
                _ => null
            };
            if (conflict is null && step == "approve" && existing.Any(x => x.Step == "review" && x.UserId == uid)) conflict = "批准人不能与审核人相同";
            if (conflict is not null)
            {
                await Audit.DeniedAsync(factory, http, action, conflict, ct, "ReportRun", id);
                return Results.Problem(conflict, statusCode: 409);
            }
            // 只能对完好的归档签名：文件存在、哈希一致、归档签名有效。
            if (run.PurgedTs is not null) return Results.Problem("归档文件已删除，不能签名", statusCode: 409);
            foreach (var format in new[] { "xlsx", "pdf" })
            {
                var r = await archive.ReadCheckedAsync(run, format, ct);
                if (r.Bytes is null || !r.Signature.Valid)
                {
                    var why = !r.Exists ? "归档文件缺失" : !r.HashOk ? "SHA256 校验不一致" : r.Signature.Error ?? "未签名";
                    await Audit.FailedAsync(factory, http, action, $"{format} {why}", ct, "ReportRun", id);
                    return Results.Problem($"归档文件校验未通过（{format}：{why}），不能签名", statusCode: 409);
                }
            }

            var ts = ScadaTime.NowMs();
            var approval = new ReportApproval
            {
                RunId = id, Step = step, Decision = decision, UserId = uid, UserName = user.UserName, DisplayName = user.DisplayName, Reason = reason, Ts = ts
            };
            approval.ManifestJson = Manifest(approval, run);
            try
            {
                var (sig, thumb, _) = signer.Sign(Encoding.UTF8.GetBytes(approval.ManifestJson), DateTimeOffset.FromUnixTimeMilliseconds(ts));
                approval.Signature = sig; approval.SignerThumbprint = thumb;
            }
            catch (Exception ex)
            {
                await Audit.FailedAsync(factory, http, action, "服务器签名失败：" + ex.Message, ct, "ReportRun", id);
                return Results.Problem("服务器签名证书不可用：" + ex.Message, statusCode: 500);
            }
            db.ReportApprovals.Add(approval);
            var log = Audit.Change(http, action, "ReportRun", id, null, null);
            log.Details = JsonSerializer.Serialize(new { step, decision, reason, run.TemplateName, run.PeriodKey, run.Version, run.XlsxSha256, run.PdfSha256, approval.SignerThumbprint });
            db.OperationLogs.Add(log);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { status = Status([.. existing, approval]), approval = ApprovalView(approval) });
        });
    }

    // 签名清单：绑定报表文件哈希与签名内容；字段顺序固定，验证时按记录重新构造比对。
    public static string Manifest(ReportApproval a, ReportRun run) => JsonSerializer.Serialize(new
    {
        type = "scada.report.esign/v1", runId = run.Id, run.TemplateName, run.PeriodKey, run.Version, run.XlsxSha256, run.PdfSha256,
        step = a.Step, decision = a.Decision, userId = a.UserId, userName = a.UserName, displayName = a.DisplayName, reason = a.Reason, ts = a.Ts
    });

    public static bool ManifestMatches(ReportApproval a, ReportRun run) => string.Equals(a.ManifestJson, Manifest(a, run), StringComparison.Ordinal);
}
