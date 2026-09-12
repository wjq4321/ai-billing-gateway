using AiBilling.Core.Billing;
using AiBilling.Core.Models;

namespace AiBilling.Tests;

/// <summary>计费引擎与通配匹配的单元测试——这些逻辑直接决定收多少钱，必须锁住。</summary>
[TestClass]
public class BillingEngineTests
{
    private static BillingEngine NewEngine(params ModelPrice[] prices)
    {
        var e = new BillingEngine();
        e.SetPrices(prices);
        return e;
    }

    // ------------------------------------------------------------ 通配匹配

    [DataTestMethod]
    [DataRow("*", "gemma3:270m", true)]
    [DataRow("gemma*", "gemma3:270m", true)]
    [DataRow("gemma*", "llama3", false)]
    [DataRow("qwen2.5:*", "qwen2.5:72b", true)]
    [DataRow("qwen2.5:*", "qwen3:8b", false)]
    [DataRow("llama?-3", "llama1-3", true)]
    [DataRow("gemma3:270m", "gemma3:270m", true)]
    [DataRow("gemma3:270m", "GEMMA3:270M", true)]
    [DataRow("gemma3:270m", "gemma3:7b", false)]
    [DataRow("", "anything", false)]
    public void Matches_WorksForCommonPatterns(string pattern, string value, bool expected)
        => Assert.AreEqual(expected, BillingEngine.Matches(pattern, value));

    // ------------------------------------------------------------ 定价解析

    [TestMethod]
    public void ResolvePrice_PrefersExactOverWildcard()
    {
        var engine = NewEngine(
            new ModelPrice { Model = "*", IsWildcard = true, InputPricePer1K = 1m, OutputPricePer1K = 1m },
            new ModelPrice { Model = "gemma*", IsWildcard = true, InputPricePer1K = 2m, OutputPricePer1K = 2m },
            new ModelPrice { Model = "gemma3:270m", IsWildcard = false, InputPricePer1K = 9m, OutputPricePer1K = 9m });

        var p = engine.ResolvePrice("gemma3:270m");
        Assert.AreEqual(9m, p.InputPricePer1K, "精确匹配应优先于通配");
    }

    [TestMethod]
    public void ResolvePrice_LongerWildcardWins()
    {
        var engine = NewEngine(
            new ModelPrice { Model = "*", IsWildcard = true, InputPricePer1K = 1m },
            new ModelPrice { Model = "qwen*", IsWildcard = true, InputPricePer1K = 5m });

        Assert.AreEqual(5m, engine.ResolvePrice("qwen2.5:72b").InputPricePer1K,
            "更具体的通配应优先");
    }

    [TestMethod]
    public void ResolvePrice_FallsBackWhenNothingMatches()
    {
        var engine = NewEngine(new ModelPrice { Model = "gemma*", IsWildcard = true, InputPricePer1K = 5m });
        var p = engine.ResolvePrice("unknown-model");
        Assert.AreEqual(0m, p.InputPricePer1K, "未匹配时应走兜底规则而不是报错");
    }

    // ------------------------------------------------------------ 费用计算

    [TestMethod]
    public void Calculate_PerToken_ComputesCorrectly()
    {
        var engine = NewEngine(new ModelPrice
        {
            Model = "m", IsWildcard = false,
            Mode = PricingMode.PerToken,
            InputPricePer1K = 0.5m,
            OutputPricePer1K = 1.5m
        });

        // 1000 输入 token * 0.5/1000 = 0.5 ；2000 输出 * 1.5/1000 = 3.0 ；合计 3.5
        var r = engine.Calculate(null, "m", 1000, 2000);
        Assert.AreEqual(3.5m, r.Cost);
        Assert.AreEqual(3000, r.TotalTokens);
    }

    [TestMethod]
    public void Calculate_PerRequest_IgnoresTokenCount()
    {
        var engine = NewEngine(new ModelPrice
        {
            Model = "m", IsWildcard = false,
            Mode = PricingMode.PerRequest,
            PricePerRequest = 0.02m
        });

        Assert.AreEqual(0.02m, engine.Calculate(null, "m", 10, 10).Cost);
        Assert.AreEqual(0.02m, engine.Calculate(null, "m", 999999, 999999).Cost,
            "按次计费不应受 token 数量影响");
    }

    [TestMethod]
    public void Calculate_KeyOverrideBeatsModelPrice()
    {
        var engine = NewEngine(new ModelPrice
        {
            Model = "m", IsWildcard = false,
            InputPricePer1K = 1m, OutputPricePer1K = 1m
        });

        var key = new ApiKeyAccount { OverrideInputPricePer1K = 0.1m, OverrideOutputPricePer1K = 0.2m };

        // 1000 输入 * 0.1/1000 = 0.1 ；1000 输出 * 0.2/1000 = 0.2 ；合计 0.3
        var r = engine.Calculate(key, "m", 1000, 1000);
        Assert.AreEqual(0.3m, r.Cost, "Key 级单价应覆盖模型默认价");
    }

    [TestMethod]
    public void Calculate_ZeroTokensCostsNothing()
    {
        var engine = NewEngine(new ModelPrice { Model = "*", IsWildcard = true, InputPricePer1K = 10m, OutputPricePer1K = 10m });
        Assert.AreEqual(0m, engine.Calculate(null, "m", 0, 0).Cost);
    }

    [TestMethod]
    public void Calculate_UnmatchedModelIsFreeNotCrash()
    {
        var engine = NewEngine();   // 无任何规则
        var r = engine.Calculate(null, "mystery-model", 5000, 5000);
        Assert.AreEqual(0m, r.Cost, "无定价规则时按免费处理，不能抛异常");
    }

    // ------------------------------------------------------------ 预估

    [TestMethod]
    public void EstimatePreflightCost_UsesAssumedCompletion()
    {
        var engine = NewEngine(new ModelPrice
        {
            Model = "*", IsWildcard = true,
            InputPricePer1K = 1m, OutputPricePer1K = 2m
        });

        // 1000 输入 = 1.0 ；500 输出 * 2/1000 = 1.0 ；合计 2.0
        Assert.AreEqual(2m, engine.EstimatePreflightCost(null, "x", 1000, 500));
    }
}
