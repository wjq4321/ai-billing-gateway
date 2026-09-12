using AiBilling.Core.Security;

namespace AiBilling.Tests;

/// <summary>管理端密码哈希测试。</summary>
[TestClass]
public class PasswordHasherTests
{
    [TestMethod]
    public void Hash_ThenVerify_Succeeds()
    {
        var hash = PasswordHasher.Hash("my-secret-password");
        Assert.IsTrue(PasswordHasher.Verify("my-secret-password", hash));
    }

    [TestMethod]
    public void Verify_WrongPasswordFails()
    {
        var hash = PasswordHasher.Hash("correct");
        Assert.IsFalse(PasswordHasher.Verify("wrong", hash));
        Assert.IsFalse(PasswordHasher.Verify("", hash));
        Assert.IsFalse(PasswordHasher.Verify("Correct", hash), "应区分大小写");
    }

    [TestMethod]
    public void Hash_IsSaltedSoSamePasswordDiffers()
    {
        var a = PasswordHasher.Hash("same");
        var b = PasswordHasher.Hash("same");
        Assert.AreNotEqual(a, b, "相同密码每次哈希应不同（随机盐）");
        Assert.IsTrue(PasswordHasher.Verify("same", a));
        Assert.IsTrue(PasswordHasher.Verify("same", b));
    }

    [TestMethod]
    public void Hash_HasExpectedFormat()
    {
        var h = PasswordHasher.Hash("x");
        var parts = h.Split('$');
        Assert.AreEqual(4, parts.Length, "格式应为 pbkdf2$iterations$salt$hash");
        Assert.AreEqual("pbkdf2", parts[0]);
        Assert.IsTrue(int.Parse(parts[1]) >= 100_000, "迭代次数应足够高");
    }

    [TestMethod]
    public void LooksHashed_DistinguishesHashFromPlaintext()
    {
        Assert.IsTrue(PasswordHasher.LooksHashed(PasswordHasher.Hash("x")));
        Assert.IsFalse(PasswordHasher.LooksHashed("admin123"), "明文密码不应被当成哈希");
        Assert.IsFalse(PasswordHasher.LooksHashed(""));
        Assert.IsFalse(PasswordHasher.LooksHashed(null));
    }

    [DataTestMethod]
    [DataRow("")]                                  // 空字符串
    [DataRow("not-a-hash")]                        // 完全不是哈希
    [DataRow("pbkdf2$abc$xx$yy")]                  // 非法迭代次数
    [DataRow("pbkdf2$100000$!!!$yy")]              // 非法 base64（盐）
    [DataRow("pbkdf2$100000$c2FsdA==$")]           // 空哈希 —— 曾在旧实现中被误认为通过
    [DataRow("pbkdf2$100000$$aGFzaA==")]           // 空盐
    [DataRow("$100000$c2FsdA==$aGFzaA==")]         // 缺算法名
    [DataRow("pbkdf2$0$c2FsdA==$aGFzaA==")]        // 迭代次数为 0
    [DataRow("pbkdf2$100000$c2FsdA==")]            // 字段数不足
    public void Verify_MalformedInputReturnsFalseWithoutThrowing(string stored)
    {
        // 关键：畸形数据既不能抛异常（会变 500），也不能被误认为通过（安全漏洞）
        Assert.IsFalse(PasswordHasher.Verify("anything", stored));
    }

    [TestMethod]
    public void Verify_EmptyHashNeverAcceptsEmptyPassword()
    {
        // 曾经的漏洞：空哈希 → PBKDF2 派生 0 字节 → 恒等成立 → 空密码即可登录
        Assert.IsFalse(PasswordHasher.Verify("", "pbkdf2$100000$c2FsdA==$"));
        Assert.IsFalse(PasswordHasher.Verify("anything", "pbkdf2$100000$c2FsdA==$"));
        Assert.IsFalse(PasswordHasher.Verify("anything", "pbkdf2$100000$$aGFzaA=="));
    }

    [TestMethod]
    public void Verify_EmptyStoredReturnsFalse()
    {
        Assert.IsFalse(PasswordHasher.Verify("x", ""));
    }

    [TestMethod]
    public void Verify_UnicodePasswordWorks()
    {
        var hash = PasswordHasher.Hash("中文密码测试🔒");
        Assert.IsTrue(PasswordHasher.Verify("中文密码测试🔒", hash));
        Assert.IsFalse(PasswordHasher.Verify("中文密码测试", hash));
    }

    [TestMethod]
    public void Verify_LongPasswordWorks()
    {
        var pw = new string('a', 500);
        Assert.IsTrue(PasswordHasher.Verify(pw, PasswordHasher.Hash(pw)));
    }

    [TestMethod]
    public void FixedTimeEquals_BehavesCorrectly()
    {
        Assert.IsTrue(PasswordHasher.FixedTimeEquals("abc", "abc"));
        Assert.IsFalse(PasswordHasher.FixedTimeEquals("abc", "abd"));
        Assert.IsFalse(PasswordHasher.FixedTimeEquals("abc", "ab"));
    }
}
