using AiBilling.Core.Models;

namespace AiBilling.Core.Billing;

/// <summary>计费结果。</summary>
public sealed class ChargeResult
{
    public long PromptTokens { get; init; }
    public long CompletionTokens { get; init; }
    public decimal Cost { get; init; }
    public string Currency { get; init; } = "CNY";

    /// <summary>命中的定价规则描述，便于对账。</summary>
    public string PriceSource { get; init; } = "";

    public long TotalTokens => PromptTokens + CompletionTokens;
}

/// <summary>
/// 计费引擎：根据模型单价 + Key 级覆盖，计算一次请求的费用。
/// 定价优先级：Key.Override* &gt; 模型精确匹配 &gt; 模型通配匹配 &gt; 兜底规则。
/// </summary>
public sealed class BillingEngine
{
    private readonly object _lock = new();
    private List<ModelPrice> _prices = new();
    private DateTime _loadedAt = DateTime.MinValue;
    private readonly TimeSpan _cacheTtl = TimeSpan.FromSeconds(20);

    /// <summary>未匹配到任何规则时的兜底单价（默认全 0，即免费，避免误扣费）。</summary>
    public ModelPrice Fallback { get; set; } = new()
    {
        Model = "*",
        Mode = PricingMode.PerToken,
        InputPricePer1K = 0m,
        OutputPricePer1K = 0m,
        Enabled = true,
        IsWildcard = true
    };

    public void SetPrices(IEnumerable<ModelPrice> prices)
    {
        lock (_lock)
        {
            _prices = prices.Where(p => p.Enabled).ToList();
            _loadedAt = DateTime.UtcNow;
        }
    }

    public bool NeedsRefresh => DateTime.UtcNow - _loadedAt > _cacheTtl;

    /// <summary>解析某个模型适用的定价规则。</summary>
    public ModelPrice ResolvePrice(string model)
    {
        lock (_lock)
        {
            // 1) 精确匹配
            var exact = _prices.FirstOrDefault(p =>
                !p.IsWildcard && string.Equals(p.Model, model, StringComparison.OrdinalIgnoreCase));
            if (exact is not null) return exact;

            // 2) 通配匹配（取最长模式，最具体的优先）
            var wildcard = _prices
                .Where(p => p.IsWildcard && Matches(p.Model, model))
                .OrderByDescending(p => p.Model.Length)
                .FirstOrDefault();
            if (wildcard is not null) return wildcard;

            // 3) 兜底
            return Fallback;
        }
    }

    /// <summary>计算一次请求的费用。</summary>
    public ChargeResult Calculate(
        ApiKeyAccount? key,
        string model,
        long promptTokens,
        long completionTokens,
        string currency = "CNY")
    {
        var price = ResolvePrice(model);

        if (price.Mode == PricingMode.PerRequest)
        {
            var unit = key?.OverridePricePerRequest ?? price.PricePerRequest;
            return new ChargeResult
            {
                PromptTokens = promptTokens,
                CompletionTokens = completionTokens,
                Cost = Round(unit),
                Currency = currency,
                PriceSource = $"按次 {DescribePrice(price)}"
            };
        }

        var inPrice = key?.OverrideInputPricePer1K ?? price.InputPricePer1K;
        var outPrice = key?.OverrideOutputPricePer1K ?? price.OutputPricePer1K;

        var cost = (promptTokens / 1000m) * inPrice + (completionTokens / 1000m) * outPrice;

        var source = key?.OverrideInputPricePer1K is not null || key?.OverrideOutputPricePer1K is not null
            ? $"Key覆盖 in={inPrice}/1k out={outPrice}/1k"
            : $"模型 {DescribePrice(price)}";

        return new ChargeResult
        {
            PromptTokens = promptTokens,
            CompletionTokens = completionTokens,
            Cost = Round(cost),
            Currency = currency,
            PriceSource = source
        };
    }

    /// <summary>
    /// 预估一次请求的费用上限，用于请求前做余额预检。
    /// 输出 token 未知时按 maxTokens 或默认值估算。
    /// </summary>
    public decimal EstimatePreflightCost(ApiKeyAccount? key, string model, long promptTokens, long assumedCompletion)
    {
        return Calculate(key, model, promptTokens, assumedCompletion).Cost;
    }

    private static decimal Round(decimal v) => Math.Round(v, 6, MidpointRounding.AwayFromZero);

    private static string DescribePrice(ModelPrice p) =>
        p.Mode == PricingMode.PerRequest
            ? $"{p.Model} 单次 {p.PricePerRequest}"
            : $"{p.Model} in {p.InputPricePer1K}/1k out {p.OutputPricePer1K}/1k";

    /// <summary>
    /// 通配匹配，供模型定价与 Key 白名单共用。
    /// 支持：
    ///   "*"          匹配一切
    ///   "gemma*"     前缀通配
    ///   "qwen2.5:*"  冒号分段前缀通配
    ///   "llama?-3"   ? 匹配单字符
    /// </summary>
    public static bool Matches(string pattern, string value)
    {
        if (string.IsNullOrEmpty(pattern)) return false;
        if (string.IsNullOrEmpty(value)) return false;
        if (pattern == "*") return true;

        // "qwen2.5:*" 这类前缀通配
        if (pattern.EndsWith(":*"))
        {
            var prefix = pattern[..^1];   // 保留冒号
            return value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        return WildcardMatch(pattern, value);
    }

    private static bool WildcardMatch(string pattern, string value)
    {
        int p = 0, v = 0, starIdx = -1, match = 0;
        while (v < value.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' ||
                char.ToLowerInvariant(pattern[p]) == char.ToLowerInvariant(value[v])))
            {
                p++; v++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                starIdx = p; match = v; p++;
            }
            else if (starIdx != -1)
            {
                p = starIdx + 1; match++; v = match;
            }
            else return false;
        }
        while (p < pattern.Length && pattern[p] == '*') p++;
        return p == pattern.Length;
    }
}
