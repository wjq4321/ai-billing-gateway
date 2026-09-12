namespace AiBilling.Core.Models;

/// <summary>API Key 状态。 </summary>
public enum ApiKeyStatus
{
    Active = 1,
    Disabled = 2,
    Expired = 3
}

/// <summary>计费单价模式。 </summary>
public enum PricingMode
{
    /// <summary>按 token 计价（输入/输出分别定价）。 </summary>
    PerToken = 1,

    /// <summary>按请求次数固定价。 </summary>
    PerRequest = 2
}

/// <summary>一次请求的终态。 </summary>
public enum RequestOutcome
{
    Success = 1,
    UpstreamError = 2,
    RejectedNoQuota = 3,
    RejectedRateLimit = 4,
    RejectedAuth = 5,
    ClientAborted = 6
}

/// <summary>计量模式。</summary>
public enum MeteringMode
{
    /// <summary>边流边发，用估算值计费（首字延迟低）。 </summary>
    Realtime = 1,

    /// <summary>缓冲整个响应，用 Ollama 返回的精确值计费（首字延迟高，账目准）。 </summary>
    Exact = 2
}

/// <summary>API Key 账户。 </summary>
public sealed class ApiKeyAccount
{
    public long Id { get; set; }

    /// <summary>Key 的公开标识（可展示，如 sk-abc...xyz）。 </summary>
    public string KeyPrefix { get; set; } = "";

    /// <summary>Key 的 SHA-256 哈希（十六进制）。明文永不落库。</summary>
    public string KeyHash { get; set; } = "";

    public string Name { get; set; } = "";

    public ApiKeyStatus Status { get; set; } = ApiKeyStatus.Active;

    /// <summary>允许访问的模型白名单；空数组表示不限制。</summary>
    public List<string> AllowedModels { get; set; } = new();

    // ---- 配额（null 表示不限制）----
    public long? QuotaTotalTokens { get; set; }
    public decimal? QuotaTotalCost { get; set; }
    public long? QuotaTotalRequests { get; set; }
    public int? RateLimitRpm { get; set; }

    /// <summary>
    /// 输出速度上限（token/秒）。限制模型"吐字"快慢，用于保护 GPU、
    /// 防止单个用户独占算力。对流式输出逐块节流；非流式响应一次性返回，
    /// 无法在传输层节流，改为按整体速率校验（超速则该请求记为违规）。
    /// </summary>
    public decimal? MaxOutputTokensPerSec { get; set; }

    /// <summary>每分钟 token 消耗上限（输入+输出）。成本节流：超出则拒绝新请求。</summary>
    public long? QuotaTokensPerMinute { get; set; }

    /// <summary>每天 token 消耗上限（输入+输出）。成本节流：超出则拒绝新请求。</summary>
    public long? QuotaTokensPerDay { get; set; }

    // ---- 累计用量 ----
    public long UsedPromptTokens { get; set; }
    public long UsedCompletionTokens { get; set; }
    public long UsedRequests { get; set; }
    public decimal UsedCost { get; set; }

    /// <summary>Key 级单价覆盖；null 表示用模型默认单价。</summary>
    public decimal? OverrideInputPricePer1K { get; set; }
    public decimal? OverrideOutputPricePer1K { get; set; }
    public decimal? OverridePricePerRequest { get; set; }

    public DateTime? ExpiresAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? LastUsedAt { get; set; }

    public long TotalTokens => UsedPromptTokens + UsedCompletionTokens;

    public bool IsUsable(DateTime nowUtc) =>
        Status == ApiKeyStatus.Active && (ExpiresAt is null || ExpiresAt > nowUtc);

    /// <summary>
    /// 判断该 Key 是否允许访问某模型。
    /// 白名单为空 = 不限制；否则支持通配，如 "gemma*"、"qwen2.5:*"、"*"。
    /// </summary>
    public bool AllowsModel(string model)
    {
        if (AllowedModels.Count == 0) return true;
        return AllowedModels.Any(m => Billing.BillingEngine.Matches(m, model));
    }
}

/// <summary>单个模型的默认计费单价。</summary>
public sealed class ModelPrice
{
    public long Id { get; set; }
    public string Model { get; set; } = "";
    public PricingMode Mode { get; set; } = PricingMode.PerToken;

    /// <summary>每 1000 输入 token 的价格（币种自定，内部按 decimal 精度计算）。</summary>
    public decimal InputPricePer1K { get; set; }

    /// <summary>每 1000 输出 token 的价格。</summary>
    public decimal OutputPricePer1K { get; set; }

    /// <summary>PerRequest 模式下的单次价格。</summary>
    public decimal PricePerRequest { get; set; }

    /// <summary>模型通配匹配，如 "qwen2.5:*" 或 "*"。优先级低于精确匹配。</summary>
    public bool IsWildcard { get; set; }

    public bool Enabled { get; set; } = true;
    public DateTime UpdatedAt { get; set; }
}

/// <summary>一次计费请求的落库记录（调用明细）。</summary>
public sealed class UsageRecord
{
    public long Id { get; set; }
    public long ApiKeyId { get; set; }
    public string KeyPrefix { get; set; } = "";
    public string Model { get; set; } = "";
    public string Endpoint { get; set; } = "";
    public MeteringMode MeteringMode { get; set; }

    /// <summary>最终采信并计费的输入 token。</summary>
    public long PromptTokens { get; set; }

    /// <summary>最终采信并计费的输出 token。</summary>
    public long CompletionTokens { get; set; }

    /// <summary>Ollama 实测输入 token（精确模式或校准用）。</summary>
    public long? ActualPromptTokens { get; set; }

    /// <summary>Ollama 实测输出 token（精确模式或校准用）。</summary>
    public long? ActualCompletionTokens { get; set; }

    /// <summary>流式估算输出 token（校准对比用）。</summary>
    public long? EstimatedCompletionTokens { get; set; }

    public decimal Cost { get; set; }
    public string Currency { get; set; } = "CNY";
    public RequestOutcome Outcome { get; set; }
    public int HttpStatus { get; set; }
    public int DurationMs { get; set; }
    public long? FirstTokenMs { get; set; }
    public string? ErrorMessage { get; set; }
    public string ClientIp { get; set; } = "";
    public DateTime CreatedAt { get; set; }

    /// <summary>实测与采信值之差（正数表示实际比记账的多，说明欠费了）。</summary>
    public long TokenDrift => (ActualPromptTokens ?? PromptTokens) + (ActualCompletionTokens ?? CompletionTokens)
                              - (PromptTokens + CompletionTokens);
}

/// <summary>按天聚合的用量（由明细汇总而来）。</summary>
public sealed class DailyUsage
{
    public long ApiKeyId { get; set; }
    public string KeyPrefix { get; set; } = "";
    public string Model { get; set; } = "";
    public DateTime Day { get; set; }
    public long PromptTokens { get; set; }
    public long CompletionTokens { get; set; }
    public long Requests { get; set; }
    public decimal Cost { get; set; }
}
