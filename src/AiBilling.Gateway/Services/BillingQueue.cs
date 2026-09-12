using AiBilling.Core.Data;
using AiBilling.Core.Models;

namespace AiBilling.Gateway.Services;

/// <summary>一条待落库的计费记录。</summary>
public sealed record UsageEnvelope(
    long ApiKeyId,
    long PromptTokens,
    long CompletionTokens,
    decimal Cost,
    UsageRecord Record);

/// <summary>
/// 计费写入队列。响应已经在客户端手里，因此落库放到后台串行执行，
/// 既不拖慢响应，也避免每个请求都开一次数据库连接。
/// 进程退出时会尽量把队列排空（见 DrainAsync）。
/// </summary>
public sealed class BillingQueue : BackgroundService
{
    private readonly System.Threading.Channels.Channel<UsageEnvelope> _channel =
        System.Threading.Channels.Channel.CreateUnbounded<UsageEnvelope>(new System.Threading.Channels.UnboundedChannelOptions
        {
            SingleReader = true
        });

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<BillingQueue> _logger;
    private long _dropped;

    public BillingQueue(IServiceScopeFactory scopes, ILogger<BillingQueue> logger)
    {
        _scopes = scopes;
        _logger = logger;
    }

    public long DroppedCount => Interlocked.Read(ref _dropped);

    public ValueTask EnqueueAsync(UsageEnvelope env, CancellationToken ct = default)
        => _channel.Writer.WriteAsync(env, ct);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var env in _channel.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                using var scope = _scopes.CreateScope();
                var repo = scope.ServiceProvider.GetRequiredService<BillingRepository>();

                if (env.Record.ApiKeyId > 0)
                {
                    await repo.InsertUsageAsync(env.Record, stoppingToken);
                    await repo.AccumulateUsageAsync(
                        env.ApiKeyId,
                        env.PromptTokens,
                        env.CompletionTokens,
                        requests: 1,
                        env.Cost,
                        DateTime.UtcNow,
                        stoppingToken);
                }

                // 释放配额守卫里已闲置的限速窗口
                scope.ServiceProvider.GetService<Core.Billing.QuotaGuard>()?.Cleanup(DateTime.UtcNow);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _dropped);
                _logger.LogError(ex, "计费记录落库失败：key={KeyId} model={Model}", env.ApiKeyId, env.Record.Model);
            }
        }
    }

    /// <summary>等待队列排空，用于优雅关闭。</summary>
    public async Task DrainAsync(TimeSpan timeout)
    {
        _channel.Writer.TryComplete();
        var deadline = DateTime.UtcNow + timeout;
        while (_channel.Reader.Count > 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }
    }
}
