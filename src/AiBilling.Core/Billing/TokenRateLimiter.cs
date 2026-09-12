using System.Collections.Concurrent;

namespace AiBilling.Core.Billing;

/// <summary>
/// Token 消耗速率限流器（成本节流）。
///
/// 与 QuotaGuard 的区别：
///   - QuotaGuard 管"累计额度"（总量/金额/请求数）与"请求频率"（RPM）
///   - 本类管"单位时间能烧多少 token"（每分钟 / 每天），防止一个客户端
///     在短时间内把预算跑光或把 GPU 打满
///
/// 实现：按 Key 维护一个滑动事件列表（时间戳 + token 数），
/// 统计窗口内的消耗总和。内存中实现，单实例部署足够；
/// 多实例需换 Redis（见 README）。
/// </summary>
public sealed class TokenRateLimiter
{
    private readonly ConcurrentDictionary<long, TokenWindow> _windows = new();

    /// <summary>一分钟窗口的秒数。</summary>
    private const int MinuteWindowSeconds = 60;

    /// <summary>一天窗口的秒数。</summary>
    private const int DayWindowSeconds = 86_400;

    /// <summary>
    /// 请求前检查：加上本次预估消耗后，是否仍在每分钟/每天的 token 上限内。
    ///
    /// 注意预估口径：输出长度事先无法预知，若按"满额"保守预估，
    /// 会出现"配额还有富余、却每次都提示超限"的糟糕体验。
    /// 因此这里采用分级策略：
    ///   - 已用 + 预估 仍在限额内          → 放行
    ///   - 已用已超限                      → 拒绝（明确超了）
    ///   - 已用 + 预估 超限，但已用未超限  → 放行，靠请求完成后的实际记账兜底
    /// 这样既不误伤正常请求，又能在真正烧超额后拦下后续请求。
    /// </summary>
    public RateDecision Check(Models.ApiKeyAccount key, long estimatedTokens, DateTime nowUtc)
    {
        if (key.QuotaTokensPerMinute is { } perMin && perMin > 0)
        {
            var used = UsedIn(key.Id, MinuteWindowSeconds, nowUtc);
            if (used >= perMin)
            {
                return RateDecision.Deny("token_rate_per_minute",
                    $"已达每分钟 token 限额：本分钟已用 {used:N0} / 上限 {perMin:N0}",
                    429);
            }
        }

        if (key.QuotaTokensPerDay is { } perDay && perDay > 0)
        {
            var used = UsedIn(key.Id, DayWindowSeconds, nowUtc);
            if (used >= perDay)
            {
                return RateDecision.Deny("token_rate_per_day",
                    $"已达每天 token 限额：今日已用 {used:N0} / 上限 {perDay:N0}",
                    429);
            }
        }

        return RateDecision.Ok();
    }

    /// <summary>
    /// 严格预检：把预估消耗也算进去。用于调用方明确知道输出上限的场景
    /// （例如请求里带了 num_predict / max_tokens），能更早拒绝、避免白跑一次推理。
    /// </summary>
    public RateDecision CheckStrict(Models.ApiKeyAccount key, long estimatedTokens, DateTime nowUtc)
    {
        if (key.QuotaTokensPerMinute is { } perMin && perMin > 0)
        {
            var used = UsedIn(key.Id, MinuteWindowSeconds, nowUtc);
            if (used + estimatedTokens > perMin)
            {
                return RateDecision.Deny("token_rate_per_minute",
                    $"超出每分钟 token 限额：本分钟已用 {used:N0}，本次预估 {estimatedTokens:N0}，上限 {perMin:N0}",
                    429);
            }
        }

        if (key.QuotaTokensPerDay is { } perDay && perDay > 0)
        {
            var used = UsedIn(key.Id, DayWindowSeconds, nowUtc);
            if (used + estimatedTokens > perDay)
            {
                return RateDecision.Deny("token_rate_per_day",
                    $"超出每天 token 限额：今日已用 {used:N0}，本次预估 {estimatedTokens:N0}，上限 {perDay:N0}",
                    429);
            }
        }

        return RateDecision.Ok();
    }

    /// <summary>请求完成后记账，把实际消耗计入窗口。</summary>
    public void Record(long keyId, long tokens, DateTime nowUtc)
    {
        if (tokens <= 0) return;
        var w = _windows.GetOrAdd(keyId, _ => new TokenWindow());
        w.Add(tokens, nowUtc);
    }

    /// <summary>查询窗口内已消耗的 token 数（用于展示与预检）。</summary>
    public long UsedIn(long keyId, int windowSeconds, DateTime nowUtc)
    {
        if (!_windows.TryGetValue(keyId, out var w)) return 0;
        return w.Sum(windowSeconds, nowUtc);
    }

    /// <summary>当前每分钟已用（供管理端展示）。</summary>
    public long UsedThisMinute(long keyId, DateTime nowUtc) => UsedIn(keyId, MinuteWindowSeconds, nowUtc);

    /// <summary>当天已用（供管理端展示）。</summary>
    public long UsedToday(long keyId, DateTime nowUtc) => UsedIn(keyId, DayWindowSeconds, nowUtc);

    /// <summary>清理过期窗口，防止内存增长。由后台任务定期调用。</summary>
    public void Cleanup(DateTime nowUtc)
    {
        foreach (var kv in _windows)
        {
            if (kv.Value.IsExpired(DayWindowSeconds, nowUtc)) _windows.TryRemove(kv.Key, out _);
        }
    }

    /// <summary>滑动窗口：记录每次请求的时间与 token 数。</summary>
    private sealed class TokenWindow
    {
        private readonly List<(long Ticks, long Tokens)> _events = new();
        private readonly object _sync = new();
        private long _lastSeenTicks;

        public void Add(long tokens, DateTime nowUtc)
        {
            lock (_sync)
            {
                _lastSeenTicks = nowUtc.Ticks;
                _events.Add((nowUtc.Ticks, tokens));
                Prune(DayWindowSeconds, nowUtc);
            }
        }

        public long Sum(int windowSeconds, DateTime nowUtc)
        {
            lock (_sync)
            {
                // 注意：这里不能 prune 到 windowSeconds。
                // 同一个窗口实例要同时服务"每分钟"和"每天"两种查询，
                // 按分钟裁剪会把当天记录删掉，导致日限额失效。
                // 只做无副作用的统计，过期数据统一交给 PruneUpTo(DayWindow) 处理。
                long total = 0;
                var cutoff = nowUtc.Ticks - TimeSpan.FromSeconds(windowSeconds).Ticks;
                foreach (var (ticks, tokens) in _events)
                {
                    if (ticks >= cutoff) total += tokens;
                }
                return total;
            }
        }

        public bool IsExpired(int windowSeconds, DateTime nowUtc)
        {
            lock (_sync)
            {
                return nowUtc.Ticks - _lastSeenTicks > TimeSpan.FromSeconds(windowSeconds).Ticks;
            }
        }

        /// <summary>只清理超出最大窗口（一天）的数据，避免影响分钟统计。</summary>
        private void Prune(int windowSeconds, DateTime nowUtc)
        {
            var cutoff = nowUtc.Ticks - TimeSpan.FromSeconds(windowSeconds).Ticks;
            _events.RemoveAll(e => e.Ticks < cutoff);
        }
    }
}

/// <summary>Token 速率检查结论。</summary>
public sealed class RateDecision
{
    public bool Allowed { get; init; }
    public string? Code { get; init; }
    public string? Reason { get; init; }
    public int HttpStatus { get; init; } = 429;

    public static RateDecision Ok() => new() { Allowed = true };

    public static RateDecision Deny(string code, string reason, int status) =>
        new() { Allowed = false, Code = code, Reason = reason, HttpStatus = status };
}
