using System.Buffers.Binary;

namespace Scada.Core.Scripting;

/// <summary>
/// 长度前缀帧（4 字节小端长度 + UTF-8 JSON），协议 v3 起取代按行分隔的 stdout 文本。
/// 好处是脚本或运行库往通道里写字符也不会切断一次回复，且不再需要把换行转义后传输。
/// 通道本身是宿主创建的匿名管道，stdout 只做为脚本杂散输出的排水口。
/// </summary>
public static class ScriptFrames
{
    private const int HeaderBytes = 4;

    public static async Task<byte[]?> ReadAsync(Stream input, CancellationToken cancellationToken)
    {
        var header = new byte[HeaderBytes];
        if (!await ReadExactlyAsync(input, header, cancellationToken).ConfigureAwait(false)) return null;
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is <= 0 or > CSharpScriptLimits.MaxFrameBytes)
            throw new InvalidDataException($"Script frame length {length} is outside 1..{CSharpScriptLimits.MaxFrameBytes}.");
        var payload = new byte[length];
        return await ReadExactlyAsync(input, payload, cancellationToken).ConfigureAwait(false) ? payload : null;
    }

    public static async Task WriteAsync(Stream output, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        if (payload.Length is 0 or > CSharpScriptLimits.MaxFrameBytes)
            throw new ArgumentOutOfRangeException(nameof(payload), $"Script frame must contain 1..{CSharpScriptLimits.MaxFrameBytes} bytes.");
        var header = new byte[HeaderBytes];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await output.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await output.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> ReadExactlyAsync(Stream input, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await input.ReadAsync(buffer[offset..], cancellationToken).ConfigureAwait(false);
            if (read == 0) return false;
            offset += read;
        }
        return true;
    }
}
