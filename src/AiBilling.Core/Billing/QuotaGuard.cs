using System.Collections.Concurrent;
using AiBilling.Core.Models;

namespace AiBilling.Core.Billing;

/// <summary>配额校验结论。</summary>
public sealed class QuotaDecision
{
    public bool Allowed { get; init; }
    public string? Reason { get; init; }
    public int HttpStatus { get; init; } = 402;
    public string? Code { get; init; }

    public static QuotaDecision Ok() => new() { Allowed = true };

    public static QuotaDecision Deny(string code, string reason, int status) =>
        new() { Allowed = false, Code = code, Reason = reason, HttpStatus = status };
}

/// <summary>
/// 配额守卫：检查 Token 总量、金额上限、请求数、RPM 限速。
/// RPM 用内存滑动窗口实现，单实例部署足够；
/// 多实例部署需换成 Redis 计数（见 README 说明）。
/// </summary>
public sealed class QuotaGuard
{
    private readonly ConcurrentDictionary<long, RateWindow> _windows = new();
    private readonly int _windowSeconds;

    public QuotaGuard(int windowSeconds = 60)
    {
        _windowSeconds = windowSeconds <= 0 ? 60 : windowSeconds;
    }

    /// <summary>请求前检查：累计配额 + 限速。</summary>
    public QuotaDecision Check(ApiKeyAccount key, DateTime nowUtc)
    {
        if (!key.IsUsable(nowUtc))
        {
            var why = key.Status == ApiKeyStatus.Disabled ? "API Key 已被禁用" : "API Key 已过期";
            return QuotaDecision.Deny("key_unusable", why, 401);
        }

        if (key.QuotaTotalRequests is { } maxReq && key.UsedRequests >= maxReq)
            return QuotaDecision.Deny("quota_requests", $"请求数配额已用尽（{key.UsedRequests}/{maxReq}）", 402);

        if (key.QuotaTotalTokens is { } maxTok && key.TotalTokens >= maxTok)
            return QuotaDecision.Deny("quota_tokens", $"Token 配额已用尽（{key.TotalTokens}/{maxTok}）", 402);

        if (key.QuotaTotalCost is { } maxCost && key.UsedCost >= maxCost)
            return QuotaDecision.Deny("quota_cost", $"金额配额已用尽（{key.UsedCost:F4}/{maxCost:F4}）", 402);

        if (key.RateLimitRpm is { } rpm && rpm > 0 && !TryConsumeRate(key.Id, rpm, nowUtc))
            return QuotaDecision.Deny("rate_limit", $"超出速率限制 {rpm} 次/分钟", 429);

        return QuotaDecision.Ok();
    }

    /// <summary>预估本次请求是否会冲破配额（请求前预检，避免跑完才发现超额）。</summary>
    public QuotaDecision Preflight(ApiKeyAccount key, long estimatedTokens, decimal estimatedCost, DateTime nowUtc)
    {
        if (key.QuotaTotalTokens is { } maxTok && key.TotalTokens + estimatedTokens > maxTok)
            return QuotaDecision.Deny("quota_tokens",
                $"本次请求约需 {estimatedTokens} token，将超出剩余配额 {maxTok - key.TotalTokens}", 402);

        if (key.QuotaTotalCost is { } maxCost && key.UsedCost + estimatedCost > maxCost)
            return QuotaDecision.Deny("quota_cost",
                $"本次请求约需 {estimatedCost:F6}，将超出剩余额度 {maxCost - key.UsedCost:F6}", 402);

        return QuotaDecision.Ok();
    }

    private bool TryConsumeRate(long keyId, int limit, DateTime nowUtc)
    {
        var w = _windows.GetOrAdd(keyId, _ => new RateWindow(_windowSeconds));
        return w.TryAcquire(limit, nowUtc);
    }

    /// <summary>清理过期窗口，防止内存增长。建议由后台定时任务调用。</summary>
    public void Cleanup(DateTime nowUtc)
    {
        foreach (var kv in _windows)
        {
            if (kv.Value.IsExpired(nowUtc)) _windows.TryRemove(kv.Key, out _);
        }
    }

    private sealed class RateWindow
    {
        private readonly int _seconds;
        private readonly Queue<long> _hits = new();
        private readonly object _sync = new();
        private long _lastSeenTicks;

        public RateWindow(int seconds) => _seconds = seconds;

        public bool TryAcquire(int limit, DateTime nowUtc)
        {
            var now = nowUtc.Ticks;
            var cutoff = now - TimeSpan.FromSeconds(_seconds).Ticks;
            lock (_sync)
            {
                _lastSeenTicks = now;
                while (_hits.Count > 0 && _hits.Peek() < cutoff) _hits.Dequeue();
                if (_hits.Count >= limit) return false;
                _hits.Enqueue(now);
                return true;
            }
        }

        public bool IsExpired(DateTime nowUtc)
        {
            lock (_sync)
            {
                return nowUtc.Ticks - _lastSeenTicks > TimeSpan.FromSeconds(_seconds * 2).Ticks;
            }
        }
    }
}
