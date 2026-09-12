using AiBilling.Core.Models;
using AiBilling.Core.Security;

namespace AiBilling.Tests;

/// <summary>API Key 生成、哈希与模型白名单测试。</summary>
[TestClass]
public class ApiKeyTests
{
    [TestMethod]
    public void GenerateKey_HasPrefixAndIsUnique()
    {
        var a = ApiKeyHasher.GenerateKey();
        var b = ApiKeyHasher.GenerateKey();

        Assert.IsTrue(a.StartsWith("sk-"), "Key 应以 sk- 开头");
        Assert.AreNotEqual(a, b, "两次生成不应重复");
        Assert.IsTrue(a.Length > 20, "Key 长度应足够");
    }

    [TestMethod]
    public void Hash_IsDeterministicAndCaseInsensitiveOnTrim()
    {
        var k = "sk-abc123";
        Assert.AreEqual(ApiKeyHasher.Hash(k), ApiKeyHasher.Hash(k));
        Assert.AreEqual(ApiKeyHasher.Hash(k), ApiKeyHasher.Hash("  sk-abc123  "),
            "应忽略首尾空白，避免客户端复制粘贴带空格导致失败");
    }

    [TestMethod]
    public void Hash_DifferentKeysDifferentHashes()
        => Assert.AreNotEqual(ApiKeyHasher.Hash("sk-a"), ApiKeyHasher.Hash("sk-b"));

    [TestMethod]
    public void Hash_ProducesSha256Hex()
    {
        var h = ApiKeyHasher.Hash("sk-x");
        Assert.AreEqual(64, h.Length, "SHA-256 十六进制应为 64 字符");
        Assert.IsTrue(h.All(c => "0123456789abcdef".Contains(c)), "应为小写十六进制");
    }

    [TestMethod]
    public void DisplayPrefix_ShortensLongKey()
    {
        var key = ApiKeyHasher.GenerateKey();
        var p = ApiKeyHasher.DisplayPrefix(key);

        Assert.IsTrue(p.Contains("..."), "长 Key 应缩略展示");
        Assert.IsTrue(p.StartsWith("sk-"), "应保留可识别前缀");
        Assert.AreNotEqual(key, p, "不应展示完整 Key");
    }

    [TestMethod]
    public void DisplayPrefix_KeepsShortKeyIntact()
        => Assert.AreEqual("sk-short", ApiKeyHasher.DisplayPrefix("sk-short"));

    [TestMethod]
    public void FixedTimeEquals_BehavesLikeEquality()
    {
        Assert.IsTrue(ApiKeyHasher.FixedTimeEquals("abc", "abc"));
        Assert.IsFalse(ApiKeyHasher.FixedTimeEquals("abc", "abd"));
        Assert.IsFalse(ApiKeyHasher.FixedTimeEquals("abc", "abcd"));
    }

    // ------------------------------------------------------------ 模型白名单

    [TestMethod]
    public void AllowsModel_EmptyListMeansAll()
    {
        var k = new ApiKeyAccount();
        Assert.IsTrue(k.AllowsModel("anything:1b"));
    }

    [TestMethod]
    public void AllowsModel_ExactMatch()
    {
        var k = new ApiKeyAccount { AllowedModels = { "gemma3:270m" } };
        Assert.IsTrue(k.AllowsModel("gemma3:270m"));
        Assert.IsFalse(k.AllowsModel("gemma3:7b"));
    }

    [TestMethod]
    public void AllowsModel_PrefixWildcard()
    {
        var k = new ApiKeyAccount { AllowedModels = { "gemma*" } };
        Assert.IsTrue(k.AllowsModel("gemma3:270m"), "gemma* 应允许 gemma3:270m");
        Assert.IsTrue(k.AllowsModel("gemma3:7b"));
        Assert.IsFalse(k.AllowsModel("llama3"));
    }

    [TestMethod]
    public void AllowsModel_ColonWildcard()
    {
        var k = new ApiKeyAccount { AllowedModels = { "qwen2.5:*" } };
        Assert.IsTrue(k.AllowsModel("qwen2.5:72b"));
        Assert.IsFalse(k.AllowsModel("qwen3:8b"));
    }

    [TestMethod]
    public void AllowsModel_StarAllowsAll()
    {
        var k = new ApiKeyAccount { AllowedModels = { "*" } };
        Assert.IsTrue(k.AllowsModel("literally-anything"));
    }

    [TestMethod]
    public void AllowsModel_MultipleEntriesAnyMatches()
    {
        var k = new ApiKeyAccount { AllowedModels = { "gemma*", "qwen2.5:*" } };
        Assert.IsTrue(k.AllowsModel("gemma3:270m"));
        Assert.IsTrue(k.AllowsModel("qwen2.5:72b"));
        Assert.IsFalse(k.AllowsModel("llama3"));
    }

    // ------------------------------------------------------------ 账户状态

    [TestMethod]
    public void IsUsable_RespectsStatusAndExpiry()
    {
        var now = DateTime.UtcNow;

        var active = new ApiKeyAccount();
        Assert.IsTrue(active.IsUsable(now));

        var disabled = new ApiKeyAccount { Status = ApiKeyStatus.Disabled };
        Assert.IsFalse(disabled.IsUsable(now));

        var expired = new ApiKeyAccount { ExpiresAt = now.AddMinutes(-1) };
        Assert.IsFalse(expired.IsUsable(now));

        var future = new ApiKeyAccount { ExpiresAt = now.AddDays(1) };
        Assert.IsTrue(future.IsUsable(now));
    }

    [TestMethod]
    public void TotalTokens_SumsBothDirections()
    {
        var k = new ApiKeyAccount { UsedPromptTokens = 100, UsedCompletionTokens = 250 };
        Assert.AreEqual(350, k.TotalTokens);
    }
}
