using System.Security.Cryptography;
using System.Text;

namespace AiBilling.Core.Security;

/// <summary>API Key 的生成与哈希。</summary>
public static class ApiKeyHasher
{
    public const string KeyPrefixLiteral = "sk-";

    /// <summary>生成一个新的明文 Key，形如 sk-3f9a...（48 位随机字符）。</summary>
    public static string GenerateKey(int randomBytes = 24)
    {
        var raw = RandomNumberGenerator.GetBytes(randomBytes);
        var body = Base64Url(raw);
        return KeyPrefixLiteral + body;
    }

    /// <summary>计算 Key 的 SHA-256 十六进制哈希。明文不落库。</summary>
    public static string Hash(string key)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(key.Trim()));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>取出用于展示的前缀片段，如 sk-3f9a…b21c。</summary>
    public static string DisplayPrefix(string key)
    {
        var k = key.Trim();
        if (k.Length <= 14) return k;
        return k[..10] + "..." + k[^4..];
    }

    /// <summary>恒定时间比较，避免时序侧信道。</summary>
    public static bool FixedTimeEquals(string a, string b)
    {
        var ba = Encoding.UTF8.GetBytes(a);
        var bb = Encoding.UTF8.GetBytes(b);
        return ba.Length == bb.Length && CryptographicOperations.FixedTimeEquals(ba, bb);
    }

    private static string Base64Url(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
