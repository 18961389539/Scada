using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Interfaces;
using Scada.Core.Models;
using Scada.Core.Protocols;

namespace Scada.Collector;

/// <summary>
/// 写命令的回读校验：写入原始值 → 按单测点计划回读 → 质量/工程量/持久记录校验 → 发布。
/// 与协议无关；驱动只负责 Write/Read，失败分类与结果未知口径统一在这里。
/// </summary>
internal sealed class DeviceWriteVerifier(
    Device device,
    IDeviceDriver driver,
    IReadPlanBuilder planBuilder,
    IRealtimeCache cache,
    ICollectorJournal? journal,
    DeviceDiagnostics diagnostics,
    Action<TagValue> publish)
{
    public async Task<double> WriteAndVerifyAsync(Tag tag, double value, CancellationToken cancellationToken)
    {
        if (!CommandValuePolicy.TryGetRawValue(tag, value, out var expectedRaw, out var error))
            throw CommandFailure.Tag(new InvalidOperationException(error), GatewayCommandOutcome.NotSent);
        await driver.WriteAsync(device, tag, expectedRaw, cancellationToken);
        var verifyPlan = planBuilder.Build(device, new[] { tag });
        var readBack = await driver.ReadAsync(device, verifyPlan, cancellationToken);
        var feedback = readBack.FirstOrDefault(x => x.TagId == tag.Id) ?? throw CommandFailure.Tag(new IOException("回读失败"), GatewayCommandOutcome.Unknown);
        if (feedback.Quality != TagQuality.Good || !double.IsFinite(feedback.Value))
            throw CommandFailure.Tag(new IOException($"写入已发送，回读质量无效，结果未知：{feedback.Error ?? feedback.Quality.ToString()}"), GatewayCommandOutcome.Unknown);
        var raw = feedback.Value;

        var engineering = tag.DataType == TagDataType.Bool
            ? raw
            : raw * tag.Scale + tag.Offset;
        if (!double.IsFinite(engineering)) throw CommandFailure.Tag(new IOException("写入已发送，回读工程量无效，结果未知"), GatewayCommandOutcome.Unknown);

        var ts = ScadaTime.NowMs();
        var tagValue = new TagValue(tag.Id, engineering, TagQuality.Good, ts, feedback.SourceTs, DefinitionRevision: tag.CalculationRevision);
        if (journal is not null)
        {
            try { await journal.AppendAsync(device.Id, new[] { tagValue }, cancellationToken); }
            catch (Exception ex) when (ex is not OperationCanceledException) { throw CommandFailure.Tag(new IOException($"写入已发送，回读已取得，但持久记录失败；请检查现场反馈：{ex.Message}", ex), GatewayCommandOutcome.Unknown); }
        }
        cache.Set(tagValue);
        diagnostics.Point(tagValue, raw);
        publish(tagValue);

        if (!CommandValuePolicy.Matches(tag, expectedRaw, raw))
            throw CommandFailure.Tag(new IOException($"回读不匹配：目标 {value:0.######}，实际 {engineering:0.######}，请检查设备"), GatewayCommandOutcome.Failed);
        return engineering;
    }

    public async Task<string> WriteTextAndVerifyAsync(Tag tag, string text, CancellationToken cancellationToken)
    {
        var bytes = System.Text.Encoding.UTF8.GetByteCount(text);
        if (bytes > tag.TextLength)
            throw CommandFailure.Tag(new InvalidOperationException($"文本为 {bytes} 字节，超过测点长度 {tag.TextLength} 字节"), GatewayCommandOutcome.NotSent);
        await driver.WriteTextAsync(device, tag, text, cancellationToken);
        var verifyPlan = planBuilder.Build(device, new[] { tag });
        var readBack = await driver.ReadAsync(device, verifyPlan, cancellationToken);
        var feedback = readBack.FirstOrDefault(x => x.TagId == tag.Id) ?? throw CommandFailure.Tag(new IOException("回读失败"), GatewayCommandOutcome.Unknown);
        if (feedback.Quality != TagQuality.Good || feedback.Text is null)
            throw CommandFailure.Tag(new IOException($"写入已发送，回读质量无效，结果未知：{feedback.Error ?? feedback.Quality.ToString()}"), GatewayCommandOutcome.Unknown);

        var ts = ScadaTime.NowMs();
        var tagValue = new TagValue(tag.Id, 0, TagQuality.Good, ts, feedback.SourceTs, DefinitionRevision: tag.CalculationRevision, Text: feedback.Text);
        if (journal is not null)
        {
            try { await journal.AppendAsync(device.Id, new[] { tagValue }, cancellationToken); }
            catch (Exception ex) when (ex is not OperationCanceledException) { throw CommandFailure.Tag(new IOException($"写入已发送，回读已取得，但持久记录失败；请检查现场反馈：{ex.Message}", ex), GatewayCommandOutcome.Unknown); }
        }
        cache.Set(tagValue);
        diagnostics.Point(tagValue, null);
        publish(tagValue);

        if (!string.Equals(feedback.Text, text, StringComparison.Ordinal))
            throw CommandFailure.Tag(new IOException($"回读不匹配：目标 \"{text}\"，实际 \"{feedback.Text}\"，请检查设备"), GatewayCommandOutcome.Failed);
        return feedback.Text;
    }
}