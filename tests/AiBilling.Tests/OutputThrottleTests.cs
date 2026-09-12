using System.Diagnostics;
using AiBilling.Core.Billing;

namespace AiBilling.Tests;

/// <summary>输出速度限速器测试。</summary>
[TestClass]
public class OutputThrottleTests
{
    [TestMethod]
    public void Disabled_WhenLimitIsNullOrZero()
    {
        Assert.IsFalse(new OutputThrottle(null).Enabled, "null 应表示不限速");
        Assert.IsFalse(new OutputThrottle(0).Enabled, "0 应表示不限速");
        Assert.IsFalse(new OutputThrottle(-1).Enabled, "负数应表示不限速");
    }

    [TestMethod]
    public void Enabled_WhenLimitPositive()
    {
        Assert.IsTrue(new OutputThrottle(10).Enabled);
        Assert.IsTrue(new OutputThrottle(0.5m).Enabled);
    }

    [TestMethod]
    public async Task Disabled_NeverWaits()
    {
        var t = new OutputThrottle(null);
        var sw = Stopwatch.StartNew();
        await t.ThrottleAsync(100_000);
        sw.Stop();

        Assert.IsTrue(sw.ElapsedMilliseconds < 100, $"不限速时不应等待，实际 {sw.ElapsedMilliseconds}ms");
        Assert.AreEqual(0, t.TotalWaitMs);
    }

    [TestMethod]
    public async Task FirstBurst_IsNotDelayed()
    {
        // 限 5 token/秒，第一次报 5 个 token —— 额度刚好够，不该等待
        var t = new OutputThrottle(5);
        var ms = await t.ThrottleAsync(5);
        Assert.AreEqual(0, ms, "首次在额度内的产出不应被延迟");
    }

    [TestMethod]
    public async Task ExceedingRate_CausesWait()
    {
        // 限 2 token/秒：一次性报 10 个 token 相当于提前用了 5 秒的量
        var t = new OutputThrottle(2);
        var ms = await t.ThrottleAsync(10);
        Assert.IsTrue(ms > 0, "超出速率应产生等待");
        Assert.IsTrue(t.TotalWaitMs > 0);
    }

    [TestMethod]
    public async Task Throttle_KeepsTotalTimeNearBudget()
    {
        // 限 20 token/秒，总共 40 个 token → 理论耗时约 2 秒（首次额度免费，故略少）
        var t = new OutputThrottle(20);
        var sw = Stopwatch.StartNew();

        await t.ThrottleAsync(20);   // 第一秒的量
        await t.ThrottleAsync(20);   // 第二秒的量

        sw.Stop();
        // 允许较大误差：下限 0.8s（确实被限速了），上限 4s（没睡过头）
        Assert.IsTrue(sw.ElapsedMilliseconds >= 800,
            $"应被限速到接近理论时长，实际仅 {sw.ElapsedMilliseconds}ms");
        Assert.IsTrue(sw.ElapsedMilliseconds < 4000,
            $"不应等待过久，实际 {sw.ElapsedMilliseconds}ms");
    }

    [TestMethod]
    public async Task Throttle_ZeroOrNegativeTokens_NoWait()
    {
        var t = new OutputThrottle(1);
        Assert.AreEqual(0, await t.ThrottleAsync(0));
        Assert.AreEqual(0, await t.ThrottleAsync(-10));
    }

    [TestMethod]
    public async Task Throttle_RespectsCancellation()
    {
        var t = new OutputThrottle(1);   // 极慢
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(50);

        var sw = Stopwatch.StartNew();
        await t.ThrottleAsync(1000, cts.Token);   // 需要等很久，但会被取消
        sw.Stop();

        Assert.IsTrue(sw.ElapsedMilliseconds < 2000, "取消后应尽快返回，不能一直睡");
    }

    // ------------------------------------------------ 非流式速率校验

    [TestMethod]
    public void ExceedsRate_DetectsViolation()
    {
        // 100 token 用了 1 秒 = 100 token/s，超过 10 的限制
        Assert.IsTrue(OutputThrottle.ExceedsRate(100, 1.0, 10, out var rate));
        Assert.AreEqual(100, rate, 0.01);
    }

    [TestMethod]
    public void ExceedsRate_WithinLimitIsFine()
    {
        // 5 token 用了 1 秒 = 5 token/s，未超过 10
        Assert.IsFalse(OutputThrottle.ExceedsRate(5, 1.0, 10, out var rate));
        Assert.AreEqual(5, rate, 0.01);
    }

    [TestMethod]
    public void ExceedsRate_NoLimitNeverViolates()
    {
        Assert.IsFalse(OutputThrottle.ExceedsRate(1_000_000, 0.001, null, out _));
        Assert.IsFalse(OutputThrottle.ExceedsRate(1_000_000, 0.001, 0, out _));
    }

    [TestMethod]
    public void ExceedsRate_HandlesZeroElapsed()
    {
        // 耗时为 0 是异常输入，不能除零抛异常。
        // 语义：有产出却零耗时 = 速率无限大 → 视为超速；无产出则不算超速。
        Assert.IsFalse(OutputThrottle.ExceedsRate(0, 0, 10, out _), "零产出不应算超速");
        Assert.IsTrue(OutputThrottle.ExceedsRate(100, 0, 10, out _), "零耗时有产出即视为超速");
    }
}
