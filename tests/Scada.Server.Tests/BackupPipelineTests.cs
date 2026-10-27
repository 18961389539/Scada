using System.IO.Compression;
using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Quartz;
using Scada.Core.Entities;
using Scada.Core.Interfaces;
using Scada.Core.Options;
using Scada.Data;
using Scada.Engine;
using Scada.Engine.Maintenance;

namespace Scada.Server.Tests;

// 定时备份流水线：恢复演练、异地拷贝（目录与 S3 兼容）、本地与异地保留策略、失败隔离与健康告警。
public sealed class BackupPipelineTests
{
    private static async Task AddUserAsync(BackupServiceTests.Rig rig)
    {
        await using var db = new ConfigDbContext(new DbContextOptionsBuilder<ConfigDbContext>().UseSqlite($"Data Source={rig.ConfigPath};Pooling=False").Options);
        db.Users.Add(new User { UserName = "admin", PasswordHash = "x", DisplayName = "管理员" });
        await db.SaveChangesAsync();
    }

    private static BackupScheduler Scheduler(BackupServiceTests.Rig rig, FakeS3? s3 = null) =>
        new(rig.Backups, Options.Create(rig.Settings), NullLogger<BackupScheduler>.Instance, null,
            o => o.Type == "S3" ? new S3BackupTarget(o, s3) : BackupTargetFactory.Create(o));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task DisabledScheduleStillAllowsManualBackups(int intervalHours)
    {
        using var rig = new BackupServiceTests.Rig();
        await rig.PrepareAsync(settings =>
        {
            settings.FullBackupIntervalHours = intervalHours;
            settings.BackupDrillEnabled = false;
        });
        var scheduler = Scheduler(rig);

        Assert.Null(await scheduler.RunScheduledAsync(default));
        Assert.Empty(rig.Backups.List());
        Assert.Empty(scheduler.Runs());
        Assert.True((await scheduler.RunAsync("manual", default)).Ok);
        Assert.Single(rig.Backups.List());
    }

    [Fact]
    public async Task ScheduledBackupFollowsCurrentNodeRole()
    {
        using var rig = new BackupServiceTests.Rig();
        await rig.PrepareAsync(settings => settings.BackupDrillEnabled = false);
        var role = new MutableNodeRole();
        var scheduler = new BackupScheduler(rig.Backups, Options.Create(rig.Settings), NullLogger<BackupScheduler>.Instance, role);

        Assert.Null(await scheduler.RunScheduledAsync(default));
        Assert.Empty(rig.Backups.List());
        role.IsActive = true;
        var run = await scheduler.RunScheduledAsync(default);
        Assert.NotNull(run);
        Assert.True(run.Ok);
        Assert.Equal("定时", run.Trigger);
        Assert.Single(scheduler.Runs());
    }

    [Fact]
    public async Task ScheduledBackupSkipsRecentArchiveAndRunsWhenOverdue()
    {
        using var rig = new BackupServiceTests.Rig();
        await rig.PrepareAsync(settings =>
        {
            settings.FullBackupIntervalHours = 24;
            settings.BackupDrillEnabled = false;
        });
        var scheduler = Scheduler(rig);
        var backup = await rig.Backups.CreateAsync(default);

        Assert.Null(await scheduler.RunScheduledAsync(default));
        Assert.Empty(scheduler.Runs());
        File.SetLastWriteTimeUtc(backup.Path, DateTime.UtcNow.AddHours(-25));
        var run = await scheduler.RunScheduledAsync(default);
        Assert.NotNull(run);
        Assert.True(run.Ok);
        Assert.Equal(2, rig.Backups.List().Count);
        Assert.Single(scheduler.Runs());
    }

    [Fact]
    public async Task ScheduledRequestsRecheckDueTimeAfterManualRun()
    {
        using var rig = new BackupServiceTests.Rig();
        await rig.PrepareAsync(settings => settings.BackupDrillEnabled = false);
        var scheduler = Scheduler(rig);

        var manual = scheduler.RunAsync("manual", default);
        var scheduled = Enumerable.Range(0, 4).Select(_ => scheduler.RunScheduledAsync(default)).ToArray();
        Assert.True((await manual).Ok);
        Assert.All(await Task.WhenAll(scheduled), Assert.Null);
        Assert.Single(rig.Backups.List());
        Assert.Equal("manual", Assert.Single(scheduler.Runs()).Trigger);
    }

    [Fact]
    public async Task QuartzJobPropagatesCancellationWithoutRecordingFailure()
    {
        using var rig = new BackupServiceTests.Rig();
        await rig.PrepareAsync();
        var scheduler = Scheduler(rig);
        var job = new ScheduledBackupJob(scheduler, NullLogger<ScheduledBackupJob>.Instance);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => job.Execute(null!, cancellation.Token).AsTask());
        Assert.Empty(rig.Backups.List());
        Assert.Empty(scheduler.Runs());
        Assert.Empty(scheduler.Warnings());
    }

    private sealed class MutableNodeRole : INodeRole
    {
        public bool IsActive { get; set; }
        public long Generation => IsActive ? 1 : 0;
    }

    [Fact]
    public async Task QuartzRegistrationRunsBackupWithExpectedTriggerAndHostingOptions()
    {
        using var rig = new BackupServiceTests.Rig();
        await rig.PrepareAsync(settings => settings.BackupDrillEnabled = false);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScadaEngine();
        services.AddSingleton(rig.Backups);
        services.AddSingleton(Options.Create(rig.Settings));
        await using var provider = services.BuildServiceProvider();
        var quartz = await provider.GetRequiredService<ISchedulerFactory>().GetScheduler();
        try
        {
            var trigger = Assert.IsAssignableFrom<ISimpleTrigger>(await quartz.GetTrigger(new TriggerKey("full-backup", "scada")));
            Assert.Equal(TimeSpan.FromMinutes(1), trigger.RepeatInterval);
            Assert.Equal(SimpleTriggerMisfireInstruction.NextWithRemainingCount, trigger.MisfireInstruction);
            var job = await quartz.GetJobDetail(trigger.JobKey);
            Assert.NotNull(job);
            Assert.Equal(typeof(ScheduledBackupJob), job.JobType);
            Assert.True(job.ConcurrentExecutionDisallowed);
            var hosting = provider.GetRequiredService<IOptions<QuartzHostedServiceOptions>>().Value;
            Assert.Equal(TimeSpan.FromSeconds(10), hosting.StartDelay);
            Assert.True(hosting.WaitForJobsToComplete);
            var options = provider.GetRequiredService<IOptions<QuartzSchedulerOptions>>().Value;
            Assert.Equal(ShutdownJobInterruption.Always, options.ShutdownJobInterruption);

            var completed = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
            quartz.ListenerManager.AddJobListener(new BackupCompletionListener(completed));
            await quartz.Start();
            Assert.Null(await completed.Task.WaitAsync(TimeSpan.FromSeconds(30)));
            Assert.True(Assert.Single(provider.GetRequiredService<BackupScheduler>().Runs()).Ok);
            Assert.Single(rig.Backups.List());
        }
        finally { await quartz.Shutdown(waitForJobsToComplete: true); }
    }

    private sealed class BackupCompletionListener(TaskCompletionSource<Exception?> completed) : IJobListener
    {
        public string Name => nameof(BackupCompletionListener);

        public ValueTask JobToBeExecuted(IJobExecutionContext context, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask JobExecutionVetoed(IJobExecutionContext context, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask JobWasExecuted(IJobExecutionContext context, JobExecutionException? jobException, CancellationToken cancellationToken = default)
        {
            completed.TrySetResult(jobException);
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task PipelineDrillsCopiesOffsiteAndAppliesRetention()
    {
        using var rig = new BackupServiceTests.Rig();
        var offsite = Path.Combine(rig.Root, "nas");
        var blocked = Path.Combine(rig.Root, "not-a-dir");
        await File.WriteAllTextAsync(blocked, "file");
        await rig.PrepareAsync(s =>
        {
            s.BackupKeepLatest = 2; s.BackupRetentionDays = 1;
            s.BackupTargets =
            [
                new() { Name = "NAS", Type = "Directory", Path = offsite, KeepLatest = 2 },
                new() { Name = "对象存储", Type = "S3", Endpoint = "http://s3.local:9000", Bucket = "scada", Prefix = "site-1", AccessKey = "AK", SecretKey = "SK", KeepLatest = 1 },
                new() { Name = "坏目标", Type = "Directory", Path = Path.Combine(blocked, "sub") }
            ];
        });
        await AddUserAsync(rig);
        var s3 = new FakeS3("AK", "SK");
        var scheduler = Scheduler(rig, s3);

        BackupRun last = null!;
        for (var i = 0; i < 3; i++)
        {
            if (i == 2) foreach (var f in Directory.EnumerateFiles(rig.Backups.Root, "full_*.zip")) File.SetLastWriteTime(f, DateTime.Now.AddDays(-3));
            last = await scheduler.RunAsync("测试", default);
            await Task.Delay(1100); // 文件名精确到秒，保证顺序
        }

        Assert.NotNull(last.Drill); Assert.True(last.Drill!.Ok, string.Join("；", last.Drill.Problems));
        Assert.Equal(2, last.Drill.Tags);
        Assert.Equal(2, rig.Backups.List().Count);            // 本地保留最近 2 份，更旧且过期的删除
        Assert.Single(last.Pruned);
        Assert.Equal(2, Directory.GetFiles(offsite, "full_*.zip").Length);
        Assert.Single(s3.Objects);                              // S3 只保留 1 份
        Assert.StartsWith("site-1/full_", s3.Objects.Keys.Single());
        Assert.True(s3.SignaturesChecked >= 9);
        var nas = last.Targets.Single(x => x.Target == "NAS"); Assert.True(nas.Ok); Assert.Single(nas.Pruned);
        var bad = last.Targets.Single(x => x.Target == "坏目标"); Assert.False(bad.Ok); Assert.NotNull(bad.Error);
        Assert.False(last.Ok);                                  // 任一目标失败，整体标记失败，但不影响其他目标
        Assert.Contains(scheduler.Warnings(), x => x.Code == "backup_offsite_failed" && x.Message.Contains("坏目标"));
        Assert.Equal(3, scheduler.Runs().Count);
    }

    [Fact]
    public async Task CorruptBackupFailsDrillAndIsNotShippedOffsite()
    {
        using var rig = new BackupServiceTests.Rig();
        var offsite = Path.Combine(rig.Root, "nas");
        await rig.PrepareAsync(s => s.BackupTargets = [new() { Name = "NAS", Type = "Directory", Path = offsite }]);
        await AddUserAsync(rig);
        var summary = await rig.Backups.CreateAsync(default);
        Assert.True((await rig.Backups.DrillAsync(summary.FileName, default)).Ok);

        using (var zip = ZipFile.Open(summary.Path, ZipArchiveMode.Update))
        {
            var entry = zip.GetEntry("config.db")!; entry.Delete();
            using var writer = new StreamWriter(zip.CreateEntry("config.db").Open()); writer.Write("not a database");
        }
        var drill = await rig.Backups.DrillAsync(summary.FileName, default);
        Assert.False(drill.Ok); Assert.NotEmpty(drill.Problems);

        // 流水线里演练失败的备份不上传异地。
        var scheduler = Scheduler(rig);
        var run = await scheduler.RunAsync("测试", default);
        Assert.True(run.Drill!.Ok); Assert.True(run.Ok);
        Assert.Single(Directory.GetFiles(offsite, "full_*.zip"));
    }

    [Fact]
    public async Task EmptyConfigurationFailsDrill()
    {
        using var rig = new BackupServiceTests.Rig();
        await rig.PrepareAsync();
        var summary = await rig.Backups.CreateAsync(default);
        var drill = await rig.Backups.DrillAsync(summary.FileName, default);
        Assert.False(drill.Ok);
        Assert.Contains(drill.Problems, x => x.Contains("没有任何用户"));
    }

    [Fact]
    public void SignatureMatchesAwsSigV4TestVector()
    {
        // AWS SigV4 测试套件 get-vanilla。
        var headers = new SortedDictionary<string, string>(StringComparer.Ordinal) { ["host"] = "example.amazonaws.com", ["x-amz-date"] = "20150830T123600Z" };
        var auth = S3Signer.Authorization("GET", "/", "", headers, S3Signer.EmptyHash, "AKIDEXAMPLE", "wJalrXUtnFEMI/K7MDENG+bPxRfiCYEXAMPLEKEY", "us-east-1", "service",
            new DateTime(2015, 8, 30, 12, 36, 0, DateTimeKind.Utc));
        Assert.Equal("AWS4-HMAC-SHA256 Credential=AKIDEXAMPLE/20150830/us-east-1/service/aws4_request, SignedHeaders=host;x-amz-date, Signature=5fa00fa31553b73ebf1942676e86291e8372ff2a2260956d9b8aae1d763fbf31", auth);
    }

    // 内存版 S3：校验每个请求的 SigV4 签名，实现 PUT/HEAD/DELETE 与 ListObjectsV2。
    public sealed class FakeS3(string accessKey, string secretKey) : HttpMessageHandler
    {
        public readonly Dictionary<string, byte[]> Objects = new(StringComparer.Ordinal);
        public int SignaturesChecked;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri!;
            var amzDate = request.Headers.GetValues("x-amz-date").Single();
            var payloadHash = request.Headers.GetValues("x-amz-content-sha256").Single();
            var headers = new SortedDictionary<string, string>(StringComparer.Ordinal) { ["host"] = uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}", ["x-amz-content-sha256"] = payloadHash, ["x-amz-date"] = amzDate };
            var expected = S3Signer.Authorization(request.Method.Method, uri.AbsolutePath, uri.Query.TrimStart('?'), headers, payloadHash, accessKey, secretKey, "us-east-1", "s3",
                DateTime.ParseExact(amzDate, "yyyyMMdd'T'HHmmss'Z'", null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal));
            if (request.Headers.GetValues("Authorization").Single() != expected) return new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("<Error>SignatureDoesNotMatch</Error>") };
            SignaturesChecked++;
            var path = Uri.UnescapeDataString(uri.AbsolutePath).TrimStart('/');
            var slash = path.IndexOf('/');
            var key = slash < 0 ? "" : path[(slash + 1)..];
            if (request.Method == HttpMethod.Put)
            {
                var body = await request.Content!.ReadAsByteArrayAsync(ct);
                if (Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(body)).ToLowerInvariant() != payloadHash) return new HttpResponseMessage(HttpStatusCode.BadRequest);
                Objects[key] = body; return new HttpResponseMessage(HttpStatusCode.OK);
            }
            if (request.Method == HttpMethod.Head)
            {
                if (!Objects.TryGetValue(key, out var o)) return new HttpResponseMessage(HttpStatusCode.NotFound);
                var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) }; r.Content.Headers.ContentLength = o.Length; return r;
            }
            if (request.Method == HttpMethod.Delete) { Objects.Remove(key); return new HttpResponseMessage(HttpStatusCode.NoContent); }
            var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
            var prefix = query["prefix"] ?? "";
            var xml = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\"?><ListBucketResult xmlns=\"http://s3.amazonaws.com/doc/2006-03-01/\"><IsTruncated>false</IsTruncated>");
            foreach (var (k, v) in Objects.Where(x => x.Key.StartsWith(prefix, StringComparison.Ordinal)))
                xml.Append($"<Contents><Key>{k}</Key><Size>{v.Length}</Size><LastModified>2026-10-25T00:00:00.000Z</LastModified></Contents>");
            xml.Append("</ListBucketResult>");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(xml.ToString(), Encoding.UTF8, "application/xml") };
        }
    }
}
