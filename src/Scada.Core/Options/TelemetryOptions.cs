namespace Scada.Core.Options;

/// <summary>
/// OpenTelemetry 导出配置。默认不启用：只有显式配置 <see cref="Endpoint"/>（或设置标准环境变量
/// <c>OTEL_EXPORTER_OTLP_ENDPOINT</c>）才会注册导出器。
///
/// 为什么不默认开：OTLP 导出器的默认目标是 <c>localhost:4317</c>，没有收集器时每次批量导出都会失败并刷日志，
/// 现场排查会把真正的问题淹掉。需要追踪的环境显式配一个接收端即可，其余部署保持零开销。
/// </summary>
public sealed class TelemetryOptions
{
    public bool Enabled { get; set; } = true;
    // OTLP/gRPC 接收端，如 http://collector:4317。留空则读 OTEL_EXPORTER_OTLP_ENDPOINT。
    public string? Endpoint { get; set; }
    // 服务名，出现在 trace 的 service.name 资源属性上。
    public string ServiceName { get; set; } = "scada-server";
    // 采样比例（0~1）。实时推送与看板轮询会产生大量重复请求，默认只留 20%。
    public double SamplingRatio { get; set; } = 0.2;
    // 记录每条 SQL 的耗时 span。EF Core 自带 Microsoft.EntityFrameworkCore ActivitySource，
    // 只要 tracer provider 订阅它就会生成 span，无需引入仍是 beta 的官方 instrumentation 包。
    // SQL 文本只含参数占位符（不含值），但会暴露表名；极敏感的现场可关掉。
    public bool EntityFrameworkCore { get; set; } = true;
}
