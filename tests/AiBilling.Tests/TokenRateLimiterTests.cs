using AiBilling.Core.Billing;
using AiBilling.Core.Models;

namespace AiBilling.Tests;

/// <summary>Token 消耗速率限流器测试。</summary>
[TestClass]
public class TokenRateLimiterTests
{
    private static readonly DateTime Now = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private static ApiKeyAccount Key(long id = 1) => new()
    {
        Id = id,
        KeyPrefix = "sk-t",
        Name = "t",
        Status = ApiKeyStatus.Active
    };

    [TestMethod]
    public void Check_NoLimits_AlwaysAllows()
    {
        var l = new TokenRateLimiter();
        var d = l.Check(Key(), 999_999_999, Now);
        Assert.IsTrue(d.Allowed);
    }

    [TestMethod]
    public void Check_AllowsWhenWithinPerMinuteLimit()
    {
        var l = new TokenRateLimiter();
        var k = Key();
        k.QuotaTokensPerMinute = 10_000;

        Assert.IsTrue(l.Check(k, 5_000, Now).Allowed);
    }

    [TestMethod]
    public void Check_RejectsWhenAlreadyOverPerMinuteLimit()
    {
        var l = new TokenRateLimiter();
        var k = Key();
        k.QuotaTokensPerMinute = 1_000;

        l.Record(k.Id, 1_000, Now);   // 已用满

        var d = l.Check(k, 300, Now);
        Assert.IsFalse(d.Allowed);
        Assert.AreEqual("token_rate_per_minute", d.Code);
        Assert.AreEqual(429, d.HttpStatus);
    }

    [TestMethod]
    public void Check_DoesNotRejectOnEstimateAlone()
    {
        // 关键行为：输出长度事先未知，若仅凭预估就拒绝，会出现
        // "配额还有富余却每次都被拦"的糟糕体验。因此预估超限也应放行，
        // 由请求完成后的实际记账兜底。
        var l = new TokenRateLimiter();
        var k = Key();
        k.QuotaTokensPerMinute = 200;

        // 已用 0，本次预估 524 → 远超 200，但仍应放行
        Assert.IsTrue(l.Check(k, 524, Now).Allowed,
            "仅有预估超限时不应拒绝，否则会误伤正常请求");
    }

    [TestMethod]
    public void CheckStrict_RejectsBasedOnEstimate()
    {
        // 严格模式：明确知道输出上限时，可以更早拒绝、避免白跑推理
        var l = new TokenRateLimiter();
        var k = Key();
        k.QuotaTokensPerMinute = 200;

        var d = l.CheckStrict(k, 524, Now);
        Assert.IsFalse(d.Allowed);
        Assert.AreEqual("token_rate_per_minute", d.Code);
    }

    [TestMethod]
    public void CheckStrict_AllowsWhenWithinEstimate()
    {
        var l = new TokenRateLimiter();
        var k = Key();
        k.QuotaTokensPerMinute = 1_000;

        l.Record(k.Id, 100, Now);
        Assert.IsTrue(l.CheckStrict(k, 500, Now).Allowed);
    }

    [TestMethod]
    public void Check_AllowsExactlyUpToLimit()
    {
        var l = new TokenRateLimiter();
        var k = Key();
        k.QuotaTokensPerMinute = 1_000;

        l.Record(k.Id, 999, Now);

        // 已用 999 < 1000 → 放行（第 1000 个 token 还能用）
        Assert.IsTrue(l.Check(k, 1, Now).Allowed);
    }

    [TestMethod]
    public void Check_RejectsAtExactlyLimit()
    {
        var l = new TokenRateLimiter();
        var k = Key();
        k.QuotaTokensPerMinute = 1_000;

        l.Record(k.Id, 1_000, Now);
        Assert.IsFalse(l.Check(k, 1, Now).Allowed, "已达上限应拒绝");
    }

    [TestMethod]
    public void PerMinuteWindow_SlidesAndRecovers()
    {
        var l = new TokenRateLimiter();
        var k = Key();
        k.QuotaTokensPerMinute = 1_000;

        l.Record(k.Id, 1_000, Now);
        Assert.IsFalse(l.Check(k, 1, Now).Allowed, "本分钟已满，应拒绝");

        // 61 秒后窗口滑过，配额恢复
        Assert.IsTrue(l.Check(k, 1, Now.AddSeconds(61)).Allowed, "窗口滑过后应恢复");
    }

    [TestMethod]
    public void Window_PartiallySlidesOverTime()
    {
        var l = new TokenRateLimiter();
        var k = Key();
        k.QuotaTokensPerMinute = 1_000;

        l.Record(k.Id, 600, Now);
        l.Record(k.Id, 500, Now.AddSeconds(30));

        // 到 61 秒时：第一次的 600 已滑出分钟窗口，只剩 500
        Assert.AreEqual(500, l.UsedThisMinute(k.Id, Now.AddSeconds(61)),
            "较早的记录应滑出分钟窗口");

        // 但两次都还在当天窗口内
        Assert.AreEqual(1_100, l.UsedToday(k.Id, Now.AddSeconds(61)),
            "当天窗口应仍保留两条记录");

        // 500 < 1000 → 恢复正常使用
        Assert.IsTrue(l.Check(k, 1, Now.AddSeconds(61)).Allowed);
    }

    [TestMethod]
    public void Check_RejectsWhenPerDayExhausted()
    {
        var l = new TokenRateLimiter();
        var k = Key();
        k.QuotaTokensPerDay = 100_000;

        l.Record(k.Id, 100_000, Now.AddHours(-2));

        var d = l.Check(k, 2_000, Now);
        Assert.IsFalse(d.Allowed);
        Assert.AreEqual("token_rate_per_day", d.Code);
    }

    [TestMethod]
    public void PerDay_LimitTakesPrecedenceAfterMinutePasses()
    {
        var l = new TokenRateLimiter();
        var k = Key();
        k.QuotaTokensPerMinute = 100_000;   // 分钟额度很宽
        k.QuotaTokensPerDay = 1_000;        // 但当天额度很小

        l.Record(k.Id, 1_000, Now.AddMinutes(-10));

        var d = l.Check(k, 200, Now);
        Assert.IsFalse(d.Allowed, "分钟额度够用，但当天额度已满");
        Assert.AreEqual("token_rate_per_day", d.Code);
    }

    [TestMethod]
    public void KeysAreIsolated()
    {
        var l = new TokenRateLimiter();
        var k1 = Key(1);
        var k2 = Key(2);
        k1.QuotaTokensPerMinute = 1_000;

        l.Record(k1.Id, 1_000, Now);

        Assert.IsFalse(l.Check(k1, 1, Now).Allowed, "Key 1 已满");
        Assert.IsTrue(l.Check(k2, 999_999, Now).Allowed, "Key 2 不受影响");
    }

    [TestMethod]
    public void Record_IgnoresZeroOrNegative()
    {
        var l = new TokenRateLimiter();
        l.Record(1, 0, Now);
        l.Record(1, -5, Now);
        Assert.AreEqual(0, l.UsedThisMinute(1, Now));
    }

    [TestMethod]
    public void UsedToday_CountsAcrossHours()
    {
        var l = new TokenRateLimiter();
        l.Record(1, 100, Now.AddHours(-5));
        l.Record(1, 200, Now.AddHours(-1));

        Assert.AreEqual(300, l.UsedToday(1, Now));
    }

    [TestMethod]
    public void Cleanup_RemovesIdleKeys()
    {
        var l = new TokenRateLimiter();
        l.Record(1, 100, Now);

        // 两天后清理，该 Key 的记录应被移除
        l.Cleanup(Now.AddDays(2));
        Assert.AreEqual(0, l.UsedToday(1, Now.AddDays(2)));
    }

    [TestMethod]
    public void Cleanup_KeepsActiveKeys()
    {
        var l = new TokenRateLimiter();
        l.Record(1, 100, Now);

        l.Cleanup(Now.AddSeconds(30));
        Assert.AreEqual(100, l.UsedToday(1, Now.AddSeconds(30)), "活跃 Key 不应被清理");
    }
}
