using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ModelContextProtocol;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Interfaces;
using Scada.Core.Models;
using Scada.Data;
using Scada.Engine.Reports;
using Scada.Server.Mcp;
using HistoryValue = Scada.Core.Models.HistoryValue;
using static Scada.Server.Tests.McpApiTests;

namespace Scada.Server.Tests;

public sealed class McpExtendedTests : IClassFixture<McpExtendedTests.Site>
{
    private readonly Site _site;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public McpExtendedTests(Site site) => _site = site;

    public sealed class Site : McpApiTests.Site
    {
        public long From { get; private set; }
        public int LineId, ProductId, ShiftId, JobId;
        public long RunId, BrokenRunId, LargeRunId;

        protected override void ConfigureServices(IServiceCollection services)
        {
            services.RemoveAll<ICollectorDiagnostics>();
            services.AddSingleton<ReadOnlyDiagnostics>();
            services.AddSingleton<ICollectorDiagnostics>(sp => sp.GetRequiredService<ReadOnlyDiagnostics>());
        }

        public override async Task InitializeAsync()
        {
            await base.InitializeAsync();
            From = new DateTimeOffset(DateTime.UtcNow.Date.AddDays(-2), TimeSpan.Zero).ToUnixTimeMilliseconds() + 3_600_000;
            Services.GetRequiredService<ReadOnlyDiagnostics>().DeviceId = DeviceA;
            var factory = Services.GetRequiredService<IDbContextFactory<ConfigDbContext>>();
            await using var db = await factory.CreateDbContextAsync();
            var product = new ProductionProduct { Name = "MCP product", AreaId = AreaA, IdealCycleSec = 60 };
            var shift = new ProductionShift { Name = "MCP all-day shift", AreaId = AreaA, StartMinute = 0, EndMinute = 0, Days = 127 };
            var line = new ProductionLine { Name = "MCP line", AreaId = AreaA, IdealCycleSec = 60, Enabled = false };
            var job = new TraceabilityJob { Name = "MCP inspection", TriggerTagId = TagA, Enabled = false };
            db.ProductionProducts.Add(product); db.ProductionShifts.Add(shift); db.ProductionLines.Add(line); db.TraceabilityJobs.Add(job);
            await db.SaveChangesAsync();
            ProductId = product.Id; ShiftId = shift.Id; LineId = line.Id; JobId = job.Id;
            db.DowntimeEvents.Add(new DowntimeEvent { LineId = LineId, StartTs = From + 45 * 60_000, EndTs = From + 55 * 60_000, State = LineState.Fault, Confirmed = true });
            db.ProductionCountCorrections.Add(new ProductionCountCorrection
            {
                LineId = LineId, ProductId = ProductId, PeriodFrom = From, PeriodTo = From + 3_600_000,
                GoodDelta = 50, RejectDelta = 5, Reason = "fixture"
            });
            db.AlarmEvents.AddRange(
                new AlarmEvent { TagId = TagA, DeviceId = DeviceA, StartTs = From + 1000, EndTs = From + 5000, Message = "MCP A", Level = AlarmLevel.Warning },
                new AlarmEvent { TagId = TagA, DeviceId = DeviceA, StartTs = From + 10000, EndTs = From + 15000, Message = "MCP A", Level = AlarmLevel.Warning },
                new AlarmEvent { TagId = TagB, DeviceId = DeviceB, StartTs = From + 20000, EndTs = From + 25000, Message = "MCP B", Level = AlarmLevel.Critical });
            db.TraceabilityRecords.AddRange(
                new TraceabilityRecord
                {
                    CycleId = Guid.NewGuid(), JobId = JobId, JobName = job.Name, AreaId = AreaA, Sn = "MCP-SN",
                    Ts = From + 1000, Outcome = TraceabilityOutcome.Failed, Acknowledged = true,
                    ValuesJson = JsonSerializer.Serialize(new[] { new TraceabilityValue("inspection", TagA, 0, null, TraceabilityOutcome.Failed) }, Json)
                },
                new TraceabilityRecord
                {
                    CycleId = Guid.NewGuid(), JobId = JobId, JobName = job.Name, AreaId = AreaB, Sn = "MCP-SN",
                    Ts = From + 2000, Outcome = TraceabilityOutcome.Passed, Acknowledged = false,
                    ValuesJson = JsonSerializer.Serialize(new[] { new TraceabilityValue("inspection", TagB, 1, null, TraceabilityOutcome.Passed) }, Json)
                });
            var table = new ReportTable("MCP report", "test-day", ReportPeriod.Day, From, From + 3_600_000,
                [new ReportColumnHeader("t", "Temperature", "C", 2, ReportColumnKind.TagAvg)],
                Enumerable.Range(1, 3).Select(i => new ReportRow($"row-{i}", From + i, From + i + 1, [i * 10d])).ToArray(),
                new ReportRow("Summary", From, From + 3_600_000, [20]), From + 3_600_000);
            var report = new ReportRun
            {
                TemplateId = 987, TemplateName = "MCP archive", AreaId = AreaA, FromTs = From, ToTs = From + 3_600_000,
                GeneratedTs = From + 3_600_000, PeriodKey = "test-day", Version = 1,
                DataJson = JsonSerializer.Serialize(table, Json), XlsxPath = "private-path-must-not-appear", PdfPath = "private-path-must-not-appear"
            };
            var broken = new ReportRun { TemplateName = "broken", PeriodKey = "broken", DataJson = "{}", GeneratedTs = From };
            var large = new ReportRun { TemplateName = "large", PeriodKey = "large", DataJson = new string('x', 2 * 1024 * 1024 + 1), GeneratedTs = From };
            db.ReportRuns.AddRange(report, broken, large);
            await db.SaveChangesAsync();
            RunId = report.Id; BrokenRunId = broken.Id; LargeRunId = large.Id;
            var history = Services.GetRequiredService<IHistoryStore>();
            await history.WriteRawAsync(
            [
                new HistoryValue(TagA, From, 10, TagQuality.Good),
                new HistoryValue(TagA, From + 10000, 30, TagQuality.Good),
                new HistoryValue(TagA, From + 20000, 999, TagQuality.Bad),
                new HistoryValue(TagA, From + 30000, 50, TagQuality.Good)
            ], default);
            await history.WriteAggregateHourAsync(
            [
                new AggregateValue(TagA, From, 20, 10, 30, 4),
                new AggregateValue(TagA, From + 3_600_000, 40, 30, 50, 2)
            ], default);
            await history.WriteAggregate5MinAsync(
            [
                new AggregateValue(TagA, From + 300_000, 25, 20, 30, 3)
            ], default);
        }
    }

    public sealed class ReadOnlyDiagnostics : ICollectorDiagnostics
    {
        public int DeviceId { get; set; }
        public int Reconnects { get; private set; }
        public bool RequestReconnect(int deviceId) { Reconnects++; throw new InvalidOperationException("MCP must not reconnect."); }
        public CollectorDeviceDiagnostics? GetDiagnostics(int deviceId)
        {
            if (deviceId != DeviceId) return null;
            return new(deviceId, "Diagnostic device", "ModbusTcp", "private-connection-must-not-appear", "Running",
                "Idle", null, null, true, false, 1, 100000, 0, 1000, 500, 3000, 0, 120,
                new CollectorQualityCounts(60, 60, 0, 0, 0, 60),
                new CollectorDiagnosticMetrics(1, 0, 20, 10, 10, 0, 1, 10, 9, 10, null, "timeout", 0, 0, 0, 0, null, 20, 15, 30, 25, 20),
                [],
                Enumerable.Range(1, 120).Select(i => new CollectorTagDiagnostic(i, $"Point {i}", i.ToString(), "Float",
                    i, i, i % 2 == 0 ? TagQuality.Good : TagQuality.Bad, 1000, null, 500, 100, i % 2 == 1, null)).ToArray(),
                Enumerable.Range(1, 60).Select(i => new CollectorDiagnosticEvent(i, i, "warning", "read", $"event {i}")).ToArray(),
                Enumerable.Range(1, 60).Select(i => new CollectorPollSample(i, i, true, 1, 0, null)).ToArray());
        }
    }

    [Fact]
    public async Task Alarm_Analysis_Filters_Areas_And_Devices_And_Returns_Real_Statistics()
    {
        using var client = _site.Server.CreateClient();
        var all = McpApiTests.Data(await CallAsync(client, "analyze_alarms", new { from = _site.From, to = _site.From + 60000, top = 1 }));
        Assert.Equal(3, all.GetProperty("total").GetInt32());
        Assert.False(all.GetProperty("truncated").GetBoolean());
        Assert.Equal(2, Assert.Single(all.GetProperty("top").EnumerateArray()).GetProperty("count").GetInt32());
        Assert.Equal(3, all.GetProperty("trend").EnumerateArray().Sum(x => x.GetProperty("total").GetInt32()));
        var area = McpApiTests.Data(await CallAsync(client, "analyze_alarms", new { from = _site.From, to = _site.From + 60000, areaId = _site.AreaA }));
        Assert.Equal(2, area.GetProperty("total").GetInt32());
        var device = McpApiTests.Data(await CallAsync(client, "analyze_alarms", new { from = _site.From, to = _site.From + 60000, deviceId = _site.DeviceB }));
        Assert.Equal(1, device.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Exact_History_Statistics_Preserve_Quality_And_Threshold_Durations()
    {
        using var client = _site.Server.CreateClient();
        var data = McpApiTests.Data(await CallAsync(client, "analyze_history", new
        {
            tagIds = new[] { _site.TagA, _site.TagB }, from = _site.From, to = _site.From + 40000, lowerLimit = 20, upperLimit = 20
        }));
        var tag = data.GetProperty("tags")[0];
        var stats = tag.GetProperty("statistics");
        Assert.Equal(30, stats.GetProperty("timeWeightedAvg").GetDouble());
        Assert.Equal(10, stats.GetProperty("min").GetDouble());
        Assert.Equal(50, stats.GetProperty("max").GetDouble());
        Assert.Equal(75, stats.GetProperty("coverage").GetDouble());
        Assert.Equal(10000, tag.GetProperty("belowLimitMs").GetInt64());
        Assert.Equal(20000, tag.GetProperty("aboveLimitMs").GetInt64());
        Assert.Equal(JsonValueKind.Null, data.GetProperty("tags")[1].GetProperty("statistics").GetProperty("timeWeightedAvg").ValueKind);
    }

    [Fact]
    public async Task Stored_Aggregates_Use_Explicit_Resolution_Stats_And_Budgets()
    {
        using var client = _site.Server.CreateClient();
        var hourly = McpApiTests.Data(await CallAsync(client, "query_aggregates", new
        {
            tagIds = new[] { _site.TagA }, from = _site.From, to = _site.From + 3_600_000, resolution = "Hour", maxPoints = 100
        }));
        Assert.Equal("Hour", hourly.GetProperty("resolution").GetString());
        var points = hourly.GetProperty("series")[0].GetProperty("points").EnumerateArray().ToArray();
        Assert.Equal(2, points.Length);
        Assert.Equal(20, points[0].GetProperty("avg").GetDouble());
        Assert.Equal(10, points[0].GetProperty("min").GetDouble());
        Assert.Equal(30, points[0].GetProperty("max").GetDouble());
        Assert.Equal(4, points[0].GetProperty("count").GetInt32());
        Assert.Equal(40, points[1].GetProperty("avg").GetDouble());
        Assert.Empty(hourly.GetProperty("missingTagIds").EnumerateArray());
        Assert.False(hourly.GetProperty("truncated").GetBoolean());
        Assert.True(hourly.GetProperty("dataAsOf").GetInt64() > 0);

        var five = McpApiTests.Data(await CallAsync(client, "query_aggregates", new
        {
            tagIds = new[] { _site.TagA }, from = _site.From, to = _site.From + 3_600_000, resolution = "FiveMinute", maxPoints = 100
        }));
        var fivePoint = Assert.Single(five.GetProperty("series")[0].GetProperty("points").EnumerateArray());
        Assert.Equal(25, fivePoint.GetProperty("avg").GetDouble());
        Assert.Equal(3, fivePoint.GetProperty("count").GetInt32());

        var missing = McpApiTests.Data(await CallAsync(client, "query_aggregates", new
        {
            tagIds = new[] { _site.TagB }, from = _site.From, to = _site.From + 3_600_000, resolution = "Hour", maxPoints = 100
        }));
        Assert.Equal(_site.TagB, missing.GetProperty("missingTagIds")[0].GetInt32());
        Assert.Empty(missing.GetProperty("series").EnumerateArray());

        foreach (var args in new object[]
        {
            new { tagIds = new[] { _site.TagA }, from = _site.From, to = _site.From + 1000, resolution = "Raw" },
            new { tagIds = new[] { _site.TagA }, from = _site.From, to = _site.From + 8L * 86_400_000, resolution = "FiveMinute", maxPoints = 2000 },
            new { tagIds = new[] { _site.TagA }, from = _site.From, to = _site.From + 3_600_000, resolution = "Hour", maxPoints = 19 },
            new { tagIds = new[] { _site.TagA }, from = _site.From, to = _site.From + 30L * 86_400_000, resolution = "FiveMinute", maxPoints = 2000 },
            new { tagIds = new[] { int.MaxValue }, from = _site.From, to = _site.From + 1000, resolution = "Hour" }
        })
            Assert.True((await CallAsync(client, "query_aggregates", args)).GetProperty("isError").GetBoolean());
    }

    [Fact]
    public async Task Collector_Diagnostics_Are_Paged_Bounded_And_Never_Reconnect()
    {
        using var client = _site.Server.CreateClient();
        var result = McpApiTests.Data(await CallAsync(client, "get_collector_diagnostics", new { deviceId = _site.DeviceA, page = 2, limit = 10, staleOnly = true }));
        Assert.Equal(60, result.GetProperty("totalPoints").GetInt32());
        Assert.Equal(10, result.GetProperty("points").GetArrayLength());
        Assert.Equal(21, result.GetProperty("points")[0].GetProperty("tagId").GetInt32());
        Assert.Equal(50, result.GetProperty("events").GetArrayLength());
        Assert.Equal(60, result.GetProperty("events")[0].GetProperty("id").GetInt64());
        Assert.True(result.GetProperty("truncated").GetBoolean());
        Assert.True(result.GetProperty("dataAsOf").GetInt64() > 0);
        Assert.DoesNotContain("private-connection", result.ToString());
        Assert.Equal(0, _site.Services.GetRequiredService<ReadOnlyDiagnostics>().Reconnects);
        Assert.True((await CallAsync(client, "get_collector_diagnostics", new { deviceId = _site.DeviceB })).GetProperty("isError").GetBoolean());
        var tags = McpApiTests.Data(await CallAsync(client, "search_tags", new { deviceId = _site.DeviceA }));
        Assert.All(tags.GetProperty("items").EnumerateArray(), tag => Assert.Equal(_site.DeviceA, tag.GetProperty("deviceId").GetInt32()));
    }

    [Theory]
    [InlineData("lines")]
    [InlineData("products")]
    [InlineData("shifts")]
    public async Task Production_Catalog_Is_Discoverable_Without_Login(string kind)
    {
        using var client = _site.Server.CreateClient();
        var result = McpApiTests.Data(await CallAsync(client, "list_production_catalog", new { kind }));
        Assert.NotEmpty(result.GetProperty("items").EnumerateArray());
        Assert.All(result.GetProperty("items").EnumerateArray(), x => Assert.Equal(_site.AreaA, x.GetProperty("areaId").GetInt32()));
    }

    [Fact]
    public async Task Oee_And_Downtime_Query_Use_Existing_Math_Without_Confirming_Events()
    {
        using var client = _site.Server.CreateClient();
        var result = McpApiTests.Data(await CallAsync(client, "query_oee", new
        {
            lineIds = new[] { _site.LineId }, from = _site.From, to = _site.From + 3_600_000,
            productId = _site.ProductId, shiftId = _site.ShiftId
        }));
        var kpi = result.GetProperty("report").GetProperty("kpi");
        Assert.Equal(50, kpi.GetProperty("good").GetDouble());
        Assert.Equal(5, kpi.GetProperty("reject").GetDouble());
        Assert.Equal(600000, kpi.GetProperty("faultMs").GetInt64());
        Assert.InRange(kpi.GetProperty("availability").GetDouble(), 0.83, 0.84);
        var down = McpApiTests.Data(await CallAsync(client, "query_downtimes", new { lineIds = new[] { _site.LineId }, from = _site.From, to = _site.From + 3_600_000 }));
        Assert.Single(down.GetProperty("items").EnumerateArray());
        var pending = McpApiTests.Data(await CallAsync(client, "query_downtimes", new { lineIds = new[] { _site.LineId }, from = _site.From, to = _site.From + 3_600_000, confirmed = false }));
        Assert.Empty(pending.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task Traceability_Uses_Saved_Outcome_Independent_Of_Plc_Acknowledgement_And_Pages()
    {
        using var client = _site.Server.CreateClient();
        var first = McpApiTests.Data(await CallAsync(client, "query_traceability", new { from = _site.From, to = _site.From + 60000, sn = "MCP-SN", limit = 1 }));
        var record = Assert.Single(first.GetProperty("items").EnumerateArray());
        Assert.Equal("Passed", record.GetProperty("outcome").GetString());
        Assert.False(record.GetProperty("acknowledged").GetBoolean());
        var second = McpApiTests.Data(await CallAsync(client, "query_traceability", new
        {
            from = _site.From, to = _site.From + 60000, sn = "MCP-SN", limit = 1, beforeId = first.GetProperty("nextBeforeId").GetInt64()
        }));
        Assert.Equal("Failed", second.GetProperty("items")[0].GetProperty("outcome").GetString());
        Assert.True(second.GetProperty("items")[0].GetProperty("acknowledged").GetBoolean());
        var filtered = McpApiTests.Data(await CallAsync(client, "query_traceability", new
        {
            from = _site.From, to = _site.From + 60000, jobId = _site.JobId, outcome = "Failed"
        }));
        Assert.Single(filtered.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task Daily_And_Monthly_Reports_Read_Stored_Aggregates_Not_Raw_Or_Generated_Files()
    {
        using var client = _site.Server.CreateClient();
        var daily = McpApiTests.Data(await CallAsync(client, "query_report", new { tagIds = new[] { _site.TagA, _site.TagB }, from = _site.From, to = _site.From + 7_200_000 }));
        Assert.Equal("stored_hourly_aggregates", daily.GetProperty("source").GetString());
        Assert.False(daily.GetProperty("recentRawIncluded").GetBoolean());
        Assert.Equal(_site.TagB, daily.GetProperty("missingTagIds")[0].GetInt32());
        Assert.Equal(2, daily.GetProperty("report").GetProperty("series")[0].GetProperty("points").GetArrayLength());
        var monthly = McpApiTests.Data(await CallAsync(client, "query_report", new { type = "monthly", tagIds = new[] { _site.TagA }, from = _site.From, to = _site.From + 7_200_000 }));
        var point = Assert.Single(monthly.GetProperty("report").GetProperty("series")[0].GetProperty("points").EnumerateArray());
        Assert.Equal(6, point.GetProperty("count").GetInt32());
        Assert.Equal(160d / 6, point.GetProperty("avg").GetDouble(), 5);
    }

    [Fact]
    public async Task Archived_Report_Metadata_And_Tables_Are_ReadOnly_And_Invalid_Snapshots_Fail()
    {
        using var client = _site.Server.CreateClient();
        var listed = McpApiTests.Data(await CallAsync(client, "list_report_runs", new { from = _site.From, to = _site.From + 7_200_000, templateId = 987 }));
        Assert.Equal(_site.RunId, Assert.Single(listed.GetProperty("items").EnumerateArray()).GetProperty("id").GetInt64());
        Assert.DoesNotContain("private-path", listed.ToString());
        var page = McpApiTests.Data(await CallAsync(client, "read_report_run", new { runId = _site.RunId, offset = 1, limit = 1 }));
        Assert.Equal(3, page.GetProperty("totalRows").GetInt32());
        Assert.Equal("row-2", page.GetProperty("rows")[0].GetProperty("label").GetString());
        Assert.Equal(2, page.GetProperty("nextOffset").GetInt32());
        Assert.Equal(20, page.GetProperty("summary").GetProperty("values")[0].GetDouble());
        foreach (var id in new[] { _site.BrokenRunId, _site.LargeRunId, long.MaxValue })
            Assert.True((await CallAsync(client, "read_report_run", new { runId = id })).GetProperty("isError").GetBoolean());
        await using var db = await _site.Services.GetRequiredService<IDbContextFactory<ConfigDbContext>>().CreateDbContextAsync();
        Assert.Equal(3, await db.ReportRuns.CountAsync());
        Assert.Empty(await db.ReportApprovals.ToListAsync());
    }

    [Fact]
    public async Task Resources_And_Prompts_Are_Discoverable_Readable_And_Audited()
    {
        using var client = _site.Server.CreateClient();
        var resources = await RpcAsync(client, "resources/list");
        Assert.Contains(resources.GetProperty("resources").EnumerateArray(), x => x.GetProperty("uri").GetString() == "scada://guide");
        var templates = await RpcAsync(client, "resources/templates/list");
        Assert.Equal(2, templates.GetProperty("resourceTemplates").GetArrayLength());
        var guide = await RpcAsync(client, "resources/read", new { uri = "scada://guide" });
        Assert.Contains("read-only", guide.GetProperty("contents")[0].GetProperty("text").GetString());
        var tag = await RpcAsync(client, "resources/read", new { uri = $"scada://tags/{_site.TagA}" });
        using var tagJson = JsonDocument.Parse(tag.GetProperty("contents")[0].GetProperty("text").GetString()!);
        Assert.Equal(_site.TagA, tagJson.RootElement.GetProperty("id").GetInt32());
        var device = await RpcAsync(client, "resources/read", new { uri = $"scada://devices/{_site.DeviceA}" });
        Assert.DoesNotContain("password", device.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("127.0.0.1", device.ToString());
        var prompts = await RpcAsync(client, "prompts/list");
        Assert.Equal(2, prompts.GetProperty("prompts").GetArrayLength());
        var prompt = await RpcAsync(client, "prompts/get", new { name = "inspect_device", arguments = new { deviceId = _site.DeviceA.ToString() } });
        Assert.Contains("get_collector_diagnostics", prompt.GetProperty("messages")[0].GetProperty("content").GetProperty("text").GetString());
        var period = await RpcAsync(client, "prompts/get", new { name = "analyze_period", arguments = new { from = _site.From.ToString(), to = (_site.From + 60000).ToString() } });
        Assert.Contains("analyze_alarms", period.GetProperty("messages")[0].GetProperty("content").GetProperty("text").GetString());
        Assert.NotNull(await _site.LastLogAsync(x => x.TargetType == "McpResource" && x.Action == "MCP scada://guide"));
        Assert.NotNull(await _site.LastLogAsync(x => x.TargetType == "McpPrompt" && x.Action == "MCP inspect_device"));
        var invalid = await EnvelopeAsync(client, "prompts/get", new { name = "inspect_device", arguments = new { deviceId = "1; write_tag" } });
        Assert.True(invalid.TryGetProperty("error", out _));
        var missing = await EnvelopeAsync(client, "resources/read", new { uri = "scada://tags/2147483647" });
        Assert.True(missing.TryGetProperty("error", out _));
    }

    [Fact]
    public async Task Result_Size_Limit_Rejects_Oversized_Tools_And_Resources_With_Failed_Audits()
    {
        await using var db = await _site.Services.GetRequiredService<IDbContextFactory<ConfigDbContext>>().CreateDbContextAsync();
        var tag = new Tag { Name = "MCP oversized fixture", DeviceId = _site.DeviceA, Address = 100, Description = new string('x', 600000) };
        db.Tags.Add(tag);
        await db.SaveChangesAsync();
        using var client = _site.Server.CreateClient();
        try
        {
            var allowed = await EnvelopeAsync(client, "resources/read", new { uri = $"scada://tags/{tag.Id}" });
            Assert.True(allowed.TryGetProperty("result", out _));
            var tool = await EnvelopeAsync(client, "tools/call", new { name = "search_tags", arguments = new { keyword = tag.Name } });
            Assert.True(tool.GetProperty("result").GetProperty("isError").GetBoolean());
            Assert.Contains("result_too_large", tool.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString());
            Assert.Equal("失败：result_too_large",
                (await _site.LastLogAsync(x => x.TargetType == "McpTool" && x.Action == "MCP search_tags"))!.Result);
            tag.Description = new string('x', 1024 * 1024 + 1);
            await db.SaveChangesAsync();
            var resource = await EnvelopeAsync(client, "resources/read", new { uri = $"scada://tags/{tag.Id}" });
            Assert.True(resource.TryGetProperty("error", out _));
            Assert.Equal("失败", (await _site.LastLogAsync(x => x.TargetType == "McpResource" && x.Action == $"MCP scada://tags/{tag.Id}"))!.Result);
        }
        finally
        {
            db.Tags.Remove(tag);
            await db.SaveChangesAsync();
        }
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{broken")]
    [InlineData("[null]")]
    public async Task Traceability_Invalid_Stored_Values_Do_Not_Become_Empty_Success(string values)
    {
        await using var db = await _site.Services.GetRequiredService<IDbContextFactory<ConfigDbContext>>().CreateDbContextAsync();
        var sn = "invalid-" + Guid.NewGuid().ToString("N");
        var record = new TraceabilityRecord
        {
            CycleId = Guid.NewGuid(), JobId = _site.JobId, JobName = "MCP invalid values", AreaId = _site.AreaA,
            Sn = sn, Ts = _site.From, ValuesJson = values
        };
        db.TraceabilityRecords.Add(record);
        await db.SaveChangesAsync();
        using var client = _site.Server.CreateClient();
        var result = await CallAsync(client, "query_traceability", new { from = _site.From, to = _site.From + 1000, sn });
        Assert.True(result.GetProperty("isError").GetBoolean());
        Assert.False((await db.TraceabilityRecords.AsNoTracking().SingleAsync(x => x.Id == record.Id)).Acknowledged);
    }

    [Theory]
    [InlineData("analyze_alarms", "{\"from\":0,\"to\":2764800000}")]
    [InlineData("analyze_alarms", "{\"from\":0,\"to\":1000,\"top\":21}")]
    [InlineData("analyze_history", "{\"tagIds\":[1],\"from\":0,\"to\":86400001}")]
    [InlineData("analyze_history", "{\"tagIds\":[1],\"from\":0,\"to\":1000,\"lowerLimit\":20,\"upperLimit\":10}")]
    [InlineData("get_collector_diagnostics", "{\"deviceId\":1,\"page\":0}")]
    [InlineData("list_production_catalog", "{\"kind\":\"secrets\"}")]
    [InlineData("query_oee", "{\"lineIds\":[],\"from\":0,\"to\":1000}")]
    [InlineData("query_oee", "{\"lineIds\":[1],\"from\":0,\"to\":0}")]
    [InlineData("query_downtimes", "{\"lineIds\":[1],\"from\":0,\"to\":1000,\"beforeId\":0}")]
    [InlineData("query_traceability", "{\"from\":0,\"to\":1000,\"limit\":51}")]
    [InlineData("query_report", "{\"tagIds\":[1],\"from\":0,\"to\":86400001,\"type\":\"daily\"}")]
    [InlineData("query_report", "{\"tagIds\":[1],\"from\":0,\"to\":1000,\"type\":\"execute\"}")]
    [InlineData("list_report_runs", "{\"from\":0,\"to\":1000,\"templateId\":0}")]
    [InlineData("read_report_run", "{\"runId\":1,\"limit\":101}")]
    public async Task Extended_Query_Budgets_Reject_Invalid_Inputs(string name, string json)
    {
        using var client = _site.Server.CreateClient();
        using var args = JsonDocument.Parse(json);
        Assert.True((await CallAsync(client, name, args.RootElement)).GetProperty("isError").GetBoolean());
    }

    [Fact]
    public async Task Query_Budget_Rejects_Fifth_Concurrent_Query_And_Honors_Cancellation()
    {
        using var budget = new McpQueryBudget();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = Enumerable.Range(0, 4).Select(_ => budget.RunAsync(async ct =>
        {
            await release.Task.WaitAsync(ct);
            return 1;
        }, default)).ToArray();
        try
        {
            var busy = await Assert.ThrowsAsync<McpToolException>(() => budget.RunAsync(_ => Task.FromResult(1), default));
            Assert.Equal(McpErrorCodes.QueryBusy, busy.Code);
            Assert.True(busy.Retryable);
        }
        finally { release.SetResult(); await Task.WhenAll(running); }
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => budget.RunAsync(_ => Task.FromResult(1), cancelled.Token));
        Assert.Equal(1, await budget.RunAsync(_ => Task.FromResult(1), default));
    }
}
