using System.Diagnostics;

namespace AiBilling.Core.Billing;

/// <summary>
/// 输出速度限速器：让模型的输出以不超过指定 token/秒 的速率下发。
///
/// 原理（令牌桶/节流）：
///   维护"已发送 token 数"与"允许的最早完成时刻"。
///   每发送 N 个 token，检查实际用时是否快于额度；
///   若快了，就 sleep 到应有的时刻再继续。
/// 这样把"瞬间喷完"变成"匀速吐字"，达到限制单用户独占算力的目的。
///
/// 适用场景：流式响应（逐块下发）。非流式响应是一次性返回的，
/// 无法在传输层节流，需另行处理（见 MeteringProxy 的说明）。
/// </summary>
public sealed class OutputThrottle
{
    private readonly double _tokensPerSecond;
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    /// <summary>截至目前已放行的 token 估算量。</summary>
    private double _allowed;

    /// <summary>已等待的总时长（毫秒），用于观测与日志。</summary>
    public long TotalWaitMs { get; private set; }

    /// <summary>构造。tokensPerSecond &lt;= 0 时表示不限速，此时 ThrottleAsync 直接返回。</summary>
    public OutputThrottle(decimal? tokensPerSecond)
    {
        _tokensPerSecond = tokensPerSecond is > 0 ? (double)tokensPerSecond.Value : 0;
        _enabled = _tokensPerSecond > 0;
    }

    private readonly bool _enabled;

    public bool Enabled => _enabled;

    /// <summary>
    /// 报告已经产出 <paramref name="tokens"/> 个 token，必要时等待以维持速率。
    /// 返回本次等待的毫秒数。
    /// </summary>
    public async Task<long> ThrottleAsync(long tokens, CancellationToken ct = default)
    {
        if (!_enabled || tokens <= 0) return 0;

        _allowed += tokens;

        // 这些 token 按额度"应该"花掉的时间
        var expectedMs = _allowed / _tokensPerSecond * 1000.0;
        var actualMs = _clock.Elapsed.TotalMilliseconds;
        var delayMs = expectedMs - actualMs;

        if (delayMs <= 1) return 0;   // 1ms 以内不值得 sleep

        // 单次不要睡太久，避免客户端超时、也便于及时响应取消
        var sleepMs = (int)Math.Min(delayMs, 1000);
        try
        {
            await Task.Delay(sleepMs, ct);
        }
        catch (OperationCanceledException)
        {
            // 客户端断开：不再等待，但保留已计算的账
            return 0;
        }

        TotalWaitMs += sleepMs;
        return sleepMs;
    }

    /// <summary>
    /// 校验一次"已全部完成"的输出是否超过速率上限。
    /// 用于非流式响应：无法节流，但可以判断是否违规，便于记录/告警。
    /// </summary>
    public static bool ExceedsRate(long tokens, double elapsedSeconds, decimal? maxTokensPerSec, out double actualRate)
    {
        // 无产出时速率视为 0（不是无限大），否则会把"空响应"误判为超速
        if (tokens <= 0)
        {
            actualRate = 0;
            return false;
        }

        // 有产出但零耗时 = 速率无限大，视为超速
        actualRate = elapsedSeconds > 0 ? tokens / elapsedSeconds : double.MaxValue;

        if (maxTokensPerSec is not > 0) return false;
        return actualRate > (double)maxTokensPerSec.Value;
    }
}
