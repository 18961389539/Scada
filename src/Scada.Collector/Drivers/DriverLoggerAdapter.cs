using Microsoft.Extensions.Logging;
using Scada.Core.Interfaces;

namespace Scada.Collector.Drivers;

/// <summary>把宿主 ILogger 收成插件可依赖的窄日志面，避免把 Microsoft.Extensions 类型泄漏给插件。</summary>
internal sealed class DriverLoggerAdapter(ILogger logger) : IDriverLogger
{
    public void Debug(string message) => logger.LogDebug("{Message}", message);
    public void Info(string message) => logger.LogInformation("{Message}", message);
    public void Warn(string message, Exception? exception = null)
    {
        if (exception is null) logger.LogWarning("{Message}", message);
        else logger.LogWarning(exception, "{Message}", message);
    }
    public void Error(string message, Exception? exception = null)
    {
        if (exception is null) logger.LogError("{Message}", message);
        else logger.LogError(exception, "{Message}", message);
    }
}
