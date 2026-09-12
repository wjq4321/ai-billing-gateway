using AiBilling.Core.Billing;
using AiBilling.Core.Models;

namespace AiBilling.Tests;

/// <summary>配额守卫测试：确保超额请求会被拦下，且不会误拦正常请求。</summary>
[TestClass]
public class QuotaGuardTests
{
    private static readonly DateTime Now = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static ApiKeyAccount Key() => new()
    {
        Id = 1,
        KeyPrefix = "sk-test",
        Name = "t",
        Status = ApiKeyStatus.Active
    };

    [TestMethod]
    public void Check_AllowsHealthyKey()
    {
        var g = new QuotaGuard();
        var d = g.Check(Key(), Now);
        Assert.IsTrue(d.Allowed);
    }

    [TestMethod]
    public void Check_RejectsDisabledKey()
    {
        var g = new QuotaGuard();
        var k = Key();
        k.Status = ApiKeyStatus.Disabled;
        var d = g.Check(k, Now);
        Assert.IsFalse(d.Allowed);
        Assert.AreEqual(401, d.HttpStatus);
    }

    [TestMethod]
    public void Check_RejectsExpiredKey()
    {
        var g = new QuotaGuard();
        var k = Key();
        k.ExpiresAt = Now.AddMinutes(-1);
        var d = g.Check(k, Now);
        Assert.IsFalse(d.Allowed);
        Assert.AreEqual(401, d.HttpStatus);
    }

    [TestMethod]
    public void Check_RejectsWhenTokenQuotaExhausted()
    {
        var g = new QuotaGuard();
        var k = Key();
        k.QuotaTotalTokens = 1000;
        k.UsedPromptTokens = 600;
        k.UsedCompletionTokens = 500;   // 合计 1100 > 1000

        var d = g.Check(k, Now);
        Assert.IsFalse(d.Allowed);
        Assert.AreEqual("quota_tokens", d.Code);
        Assert.AreEqual(402, d.HttpStatus);
    }

    [TestMethod]
    public void Check_RejectsWhenCostQuotaExhausted()
    {
        var g = new QuotaGuard();
        var k = Key();
        k.QuotaTotalCost = 10m;
        k.UsedCost = 10m;

        var d = g.Check(k, Now);
        Assert.IsFalse(d.Allowed);
        Assert.AreEqual("quota_cost", d.Code);
    }

    [TestMethod]
    public void Check_RejectsWhenRequestQuotaExhausted()
    {
        var g = new QuotaGuard();
        var k = Key();
        k.QuotaTotalRequests = 5;
        k.UsedRequests = 5;

        var d = g.Check(k, Now);
        Assert.IsFalse(d.Allowed);
        Assert.AreEqual("quota_requests", d.Code);
    }

    [TestMethod]
    public void Check_EnforcesRateLimit()
    {
        var g = new QuotaGuard(windowSeconds: 60);
        var k = Key();
        k.RateLimitRpm = 3;

        for (int i = 0; i < 3; i++)
            Assert.IsTrue(g.Check(k, Now).Allowed, $"第 {i + 1} 次应放行");

        var d = g.Check(k, Now);
        Assert.IsFalse(d.Allowed, "超出 RPM 后应被拦截");
        Assert.AreEqual("rate_limit", d.Code);
        Assert.AreEqual(429, d.HttpStatus);
    }

    [TestMethod]
    public void Check_RateLimitWindowSlides()
    {
        var g = new QuotaGuard(windowSeconds: 60);
        var k = Key();
        k.RateLimitRpm = 1;

        Assert.IsTrue(g.Check(k, Now).Allowed);
        Assert.IsFalse(g.Check(k, Now.AddSeconds(30)).Allowed, "窗口内第二次应被拦");

        // 过了一个完整窗口后应恢复
        Assert.IsTrue(g.Check(k, Now.AddSeconds(61)).Allowed, "窗口滑过后应恢复配额");
    }

    [TestMethod]
    public void Preflight_RejectsRequestThatWouldOverflow()
    {
        var g = new QuotaGuard();
        var k = Key();
        k.QuotaTotalTokens = 1000;
        k.UsedPromptTokens = 900;    // 只剩 100

        var d = g.Preflight(k, estimatedTokens: 500, estimatedCost: 0m, Now);
        Assert.IsFalse(d.Allowed, "预估就超额时应提前拒绝，避免白跑一次推理");
        Assert.AreEqual("quota_tokens", d.Code);
    }

    [TestMethod]
    public void Preflight_AllowsRequestWithinQuota()
    {
        var g = new QuotaGuard();
        var k = Key();
        k.QuotaTotalTokens = 1000;

        Assert.IsTrue(g.Preflight(k, 100, 0m, Now).Allowed);
    }

    [TestMethod]
    public void Preflight_IgnoresNullQuotas()
    {
        var g = new QuotaGuard();
        var k = Key();   // 所有配额为 null = 不限

        Assert.IsTrue(g.Preflight(k, long.MaxValue / 2, 999999m, Now).Allowed);
    }
}
