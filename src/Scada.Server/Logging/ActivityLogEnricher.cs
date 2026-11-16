using System.Diagnostics;
using Serilog.Core;
using Serilog.Events;

namespace Scada.Server.Logging;

/// <summary>
/// 把当前 <see cref="Activity"/> 的 W3C TraceId / SpanId 注入日志事件。
///
/// 接入 OpenTelemetry 之后，trace 与日志是两条独立通道：没有这个 enricher 时，
/// "这个 800ms 的 /api/history 慢在哪"能在 Jaeger 里看到，但对应的业务日志无法反查，
/// 只能靠时间戳猜。字段名沿用 OTel 语义约定（TraceId / SpanId），
/// 日志平台按这两个键即可与 trace 关联，不需要额外的映射规则。
///
/// 不引 Serilog.Enrichers.Span 包：它做的就这一件事，20 行自己实现可以少一个依赖，
/// 也避免 Serena/Serilog 版本矩阵里再多一个需要跟进的包。
/// </summary>
public sealed class ActivityLogEnricher : ILogEventEnricher
{
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        // Activity.Current 为 null 表示当前请求没有采样或不在任何 span 内。此时仍写占位符：
        // Serilog 的 outputTemplate 遇到不存在的属性会渲染出 "{TraceId}" 字面量，
        // 与其让日志里出现模板残片，不如统一成 "-"，列宽也稳定。
        logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty(
            "TraceId", Activity.Current?.TraceId.ToHexString() ?? "-"));
        logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty(
            "SpanId", Activity.Current?.SpanId.ToHexString() ?? "-"));
    }
}
