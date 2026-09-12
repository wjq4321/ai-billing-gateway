using System.Security.Cryptography;

namespace AiBilling.Core.Security;

/// <summary>
/// 管理端密码哈希。用 PBKDF2-HMAC-SHA256，格式：
///   pbkdf2$&lt;迭代次数&gt;$&lt;salt base64&gt;$&lt;hash base64&gt;
/// 与明文配置兼容：验证时若数据库没有记录，则回退到 appsettings.json 里的明文密码。
/// </summary>
public static class PasswordHasher
{
    private const int DefaultIterations = 100_000;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    public static string Hash(string password, int iterations = DefaultIterations)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            password, salt, iterations, HashAlgorithmName.SHA256, HashBytes);
        return $"pbkdf2${iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    /// <summary>校验密码。格式不合法一律返回 false，不抛异常。</summary>
    public static bool Verify(string password, string stored)
    {
        if (string.IsNullOrEmpty(stored)) return false;

        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != "pbkdf2") return false;
        if (!int.TryParse(parts[1], out var iterations) || iterations <= 0) return false;

        byte[] salt, expected;
        try
        {
            salt = Convert.FromBase64String(parts[2]);
            expected = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException) { return false; }

        // 防御：空盐或空哈希会让 PBKDF2 派生 0 字节并恒等成立，
        // 相当于"任意密码都能通过"。必须显式拒绝畸形记录。
        if (salt.Length == 0 || expected.Length == 0) return false;

        var actual = Rfc2898DeriveBytes.Pbkdf2(
            password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);

        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    /// <summary>判断字符串是否为已哈希格式（用于区分明文配置与哈希存储）。</summary>
    public static bool LooksHashed(string? value) =>
        !string.IsNullOrEmpty(value) && value.StartsWith("pbkdf2$", StringComparison.Ordinal);

    /// <summary>恒定时间字符串比较，用于明文密码回退路径。</summary>
    public static bool FixedTimeEquals(string a, string b)
    {
        var ba = System.Text.Encoding.UTF8.GetBytes(a);
        var bb = System.Text.Encoding.UTF8.GetBytes(b);
        return ba.Length == bb.Length && CryptographicOperations.FixedTimeEquals(ba, bb);
    }
}
