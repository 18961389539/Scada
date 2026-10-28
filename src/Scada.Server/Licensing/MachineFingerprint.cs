using System.Management;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace Scada.Server.Licensing;

/// <summary>
/// 机器指纹：由硬件/系统标识派生，用于把激活码绑定到这台工控机。
/// 只取不易频繁变化的标识（主板 UUID、系统卷序列号、Linux machine-id），
/// 刻意不掺网卡 MAC——工控机换网卡很常见，掺进去会造成授权误伤。
/// </summary>
public static class MachineFingerprint
{
    private const string Salt = "scada-machine:v1:";

    public static string Compute()
    {
        var parts = new List<string>();
        if (OperatingSystem.IsWindows())
        {
            parts.Add(QueryWmi("Win32_ComputerSystemProduct", "UUID"));
            parts.Add(QueryWmi("Win32_LogicalDisk", "VolumeSerialNumber", "DeviceID='C:'"));
            parts.Add(QueryWmi("Win32_OperatingSystem", "SerialNumber"));
        }
        else
        {
            parts.Add(ReadFirstLine("/sys/class/dmi/id/product_uuid"));
            parts.Add(ReadFirstLine("/etc/machine-id"));
            parts.Add(ReadFirstLine("/sys/class/dmi/id/board_serial"));
        }

        var raw = string.Join('|', parts.Where(x => !string.IsNullOrWhiteSpace(x) && x != "None"));
        if (raw.Length == 0)
        {
            // 极端情况（容器/裁剪系统）：仍给出稳定值，但同样受机器名影响，需人工核对。
            raw = "hostname:" + Environment.MachineName;
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(Salt + raw));
        return Format(Base32(hash.AsSpan(0, 16)));
    }

    public static string Normalize(string machineId) =>
        new(machineId.Where(char.IsLetterOrDigit).ToArray());

    [SupportedOSPlatform("windows")]
    private static string QueryWmi(string className, string property, string? condition = null)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                $"SELECT {property} FROM {className}" + (condition is null ? "" : $" WHERE {condition}"));
            foreach (ManagementObject item in searcher.Get())
            {
                var value = item[property]?.ToString()?.Trim();
                if (!string.IsNullOrWhiteSpace(value)) return value;
            }
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or InvalidOperationException)
        {
            // WMI 被禁用或拒绝时降级：其余标识仍参与指纹，不影响可用性。
        }
        return "";
    }

    private static string ReadFirstLine(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadLines(path).FirstOrDefault()?.Trim() ?? "" : "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }

    // Base32（RFC 4648，无填充），避免激活码里出现易混淆的 0/1。
    private static string Base32(ReadOnlySpan<byte> data)
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var output = new StringBuilder((data.Length * 8 + 4) / 5);
        var buffer = 0;
        var bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                output.Append(alphabet[(buffer >> (bits - 5)) & 0x1F]);
                bits -= 5;
            }
        }
        if (bits > 0)
        {
            output.Append(alphabet[(buffer << (5 - bits)) & 0x1F]);
        }
        return output.ToString();
    }

    private static string Format(string value) =>
        string.Join('-', value.Chunk(5).Select(c => new string(c)));
}