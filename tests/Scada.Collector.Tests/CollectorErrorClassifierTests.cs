using System.Security.Authentication;
using Scada.Core.Models;

namespace Scada.Collector.Tests;

// 错误分类是排障的聚合维度：类型优先、消息兜底，拿不准归 Unknown 而不是硬猜。
public sealed class CollectorErrorClassifierTests
{
    [Fact]
    public void TimeoutExceptionIsTimeout()
        => Assert.Equal(CollectorErrorKind.Timeout, CollectorErrorClassifier.Classify(new TimeoutException("读取超时")));

    [Fact]
    public void DeadlineCancellationIsTimeout()
        => Assert.Equal(CollectorErrorKind.Timeout, CollectorErrorClassifier.Classify(new OperationCanceledException()));

    [Fact]
    public void SocketExceptionIsConnection()
        => Assert.Equal(CollectorErrorKind.Connection, CollectorErrorClassifier.Classify(new System.Net.Sockets.SocketException(10061)));

    [Fact]
    public void NotConnectedMessageIsConnection()
        => Assert.Equal(CollectorErrorKind.Connection, CollectorErrorClassifier.Classify(new IOException("设备尚未连接")));

    [Fact]
    public void ConnectionRefusedMessageIsConnection()
        => Assert.Equal(CollectorErrorKind.Connection, CollectorErrorClassifier.Classify(new IOException("连接 10.0.0.5:502 被拒绝")));

    [Fact]
    public void ResponseTimeoutMessageIsTimeout()
        => Assert.Equal(CollectorErrorKind.Timeout, CollectorErrorClassifier.Classify(new IOException("等待 PLC 响应超时")));

    [Fact]
    public void DeviceEndCodeIsProtocol()
        => Assert.Equal(CollectorErrorKind.Protocol, CollectorErrorClassifier.Classify(new Mitsubishi.McEndCodeException((ushort)0xC051, "MC 响应异常码 C051")));

    [Fact]
    public void AuthenticationFailureIsAuthorization()
        => Assert.Equal(CollectorErrorKind.Authorization, CollectorErrorClassifier.Classify(new AuthenticationException("服务器证书不受信任")));

    [Fact]
    public void UnauthorizedAccessIsAuthorization()
        => Assert.Equal(CollectorErrorKind.Authorization, CollectorErrorClassifier.Classify(new UnauthorizedAccessException("访问被拒绝")));

    [Fact]
    public void BadAddressIsConfiguration()
        => Assert.Equal(CollectorErrorKind.Configuration, CollectorErrorClassifier.Classify(new FormatException("地址格式无效")));

    [Fact]
    public void UnsupportedFeatureIsConfiguration()
        => Assert.Equal(CollectorErrorKind.Configuration, CollectorErrorClassifier.Classify(new NotSupportedException("此协议不支持该数据类型")));

    [Fact]
    public void UnmappedIoFallsBackToIo()
        => Assert.Equal(CollectorErrorKind.Io, CollectorErrorClassifier.Classify(new IOException("写入失败")));

    [Fact]
    public void UnmappedExceptionFallsBackToUnknown()
        => Assert.Equal(CollectorErrorKind.Unknown, CollectorErrorClassifier.Classify(new InvalidOperationException("未知状态")));

    [Fact]
    public void InnerExceptionIsInspected()
        => Assert.Equal(CollectorErrorKind.Timeout, CollectorErrorClassifier.Classify(new IOException("采集批次失败", new TimeoutException())));

    [Fact]
    public void NullIsNone()
        => Assert.Equal(CollectorErrorKind.None, CollectorErrorClassifier.Classify(null));
}
