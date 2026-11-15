using System.Security.Cryptography;

namespace Scada.Core.Security;

/// <summary>
/// PBKDF2-HMAC-SHA256 口令哈希。
///
/// 存储格式：<c>pbkdf2-sha256$&lt;迭代次数&gt;$&lt;base64 盐&gt;$&lt;base64 密钥&gt;</c>。
/// 算法名写进哈希串，将来换算法（例如 Argon2id）可以逐条升级而不用改库结构；
/// <see cref="Verify"/> 仍然接受历史上的 <c>迭代次数.盐.密钥</c> 三段式，并在登录成功后由
/// <see cref="NeedsRehash"/> 提示重算，不需要一次性迁移所有账号。
/// </summary>
public static class PasswordHasher
{
    /// <summary>新哈希使用的迭代次数。2026 年仍按 OWASP 的 PBKDF2-SHA256 建议取 60 万。</summary>
    public const int Iterations = 600_000;

    /// <summary>校验时允许的最低迭代数：低于此值的存量哈希一律视为需要重算，也挡住把迭代改成 1 的降级篡改。</summary>
    public const int MinIterations = 100_000;

    private const int MaxIterations = 20_000_000;
    private const int SaltSize = 16;
    private const int KeySize = 32;
    private const string Prefix = "pbkdf2-sha256";
    private const int MinSaltBytes = 8;
    private const int MaxSaltBytes = 64;
    private const int MinKeyBytes = 16;
    private const int MaxKeyBytes = 64;

    public static string Hash(string password) => Hash(password, Iterations);

    public static string Hash(string password, int iterations)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);
        ArgumentOutOfRangeException.ThrowIfLessThan(iterations, MinIterations);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(iterations, MaxIterations);
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, KeySize);
        return $"{Prefix}${iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(key)}";
    }

    /// <summary>
    /// 校验口令。任何格式损坏、参数越界、算法不认识的情况都返回 <c>false</c>——
    /// 调用方只会看到"口令错误"，不会因为一条脏数据把登录接口打成 500。
    /// </summary>
    public static bool Verify(string password, string storedHash)
    {
        if (string.IsNullOrEmpty(password) || !TryParse(storedHash, out var iterations, out var salt, out var expected)) return false;
        try
        {
            var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (ArgumentException)
        {
            // 迭代次数/盐长度在 TryParse 已限定范围，这里只兜住运行时实现层面的参数异常。
            return false;
        }
    }

    /// <summary>存量哈希（旧格式或迭代次数低于当前标准）应在登录成功后重算。</summary>
    public static bool NeedsRehash(string storedHash) =>
        !TryParse(storedHash, out var iterations, out _, out _) || iterations < Iterations;

    private static bool TryParse(string? storedHash, out int iterations, out byte[] salt, out byte[] key)
    {
        iterations = 0;
        salt = [];
        key = [];
        if (string.IsNullOrWhiteSpace(storedHash)) return false;

        string[] parts;
        if (storedHash.StartsWith(Prefix + "$", StringComparison.Ordinal))
        {
            parts = storedHash.Split('$');
            if (parts.Length != 4 || !string.Equals(parts[0], Prefix, StringComparison.Ordinal)) return false;
            parts = [parts[1], parts[2], parts[3]];
        }
        else
        {
            // 历史格式：迭代次数.盐.密钥。
            parts = storedHash.Split('.');
            if (parts.Length != 3) return false;
        }

        if (!int.TryParse(parts[0], out iterations) || iterations < MinIterations || iterations > MaxIterations) return false;
        try
        {
            salt = Convert.FromBase64String(parts[1]);
            key = Convert.FromBase64String(parts[2]);
        }
        catch (FormatException)
        {
            return false;
        }
        return salt.Length is >= MinSaltBytes and <= MaxSaltBytes && key.Length is >= MinKeyBytes and <= MaxKeyBytes;
    }
}
