using System.Text.Json;
using AiBilling.Core.Billing;
using AiBilling.Core.Models;
using AiBilling.Gateway.Services;

namespace AiBilling.Gateway.Middleware;

/// <summary>
/// 强制 API Key 认证中间件。
/// 支持两种携带方式：
///   Authorization: Bearer sk-xxxx
///   X-API-Key: sk-xxxx
/// 认证通过后把账户写入 HttpContext.Items；同时做配额与限速检查。
/// </summary>
public sealed class ApiKeyAuthMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ApiKeyAuthMiddleware> _logger;

    public ApiKeyAuthMiddleware(RequestDelegate next, ILogger<ApiKeyAuthMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(
        HttpContext ctx,
        ApiKeyStore store,
        QuotaGuard quota,
        BillingEngine engine,
        TokenEstimator estimator)
    {
        var path = ctx.Request.Path.Value ?? "";

        // 放行：健康检查、管理后台、静态资源、Ollama 的公开元信息
        if (IsPublicPath(path))
        {
            await _next(ctx);
            return;
        }

        var presented = ExtractKey(ctx.Request);
        if (string.IsNullOrWhiteSpace(presented))
        {
            await WriteError(ctx, 401, "missing_api_key",
                "缺少 API Key。请在请求头中携带 Authorization: Bearer <key> 或 X-API-Key: <key>。");
            return;
        }

        var key = await store.ResolveAsync(presented, ctx.RequestAborted);
        if (key is null)
        {
            _logger.LogWarning("无效 API Key 尝试访问 {Path}，来自 {Ip}", path, ClientIp(ctx));
            await WriteError(ctx, 401, "invalid_api_key", "API Key 无效。");
            return;
        }

        if (!key.IsUsable(DateTime.UtcNow))
        {
            var why = key.Status == ApiKeyStatus.Disabled ? "该 API Key 已被禁用。" : "该 API Key 已过期。";
            await WriteError(ctx, 401, "key_unusable", why);
            return;
        }

        // 配额 + 限速
        var decision = quota.Check(key, DateTime.UtcNow);
        if (!decision.Allowed)
        {
            _logger.LogInformation("Key {Prefix} 被拦截：{Reason}", key.KeyPrefix, decision.Reason);

            // 被拦截也计一次请求数，便于发现滥用
            _ = RecordRejectedAsync(ctx, key, decision, path);

            await WriteError(ctx, decision.HttpStatus, decision.Code ?? "quota_exceeded",
                decision.Reason ?? "配额不足。", extra: new
                {
                    quota = new
                    {
                        totalTokens = key.QuotaTotalTokens,
                        usedTokens = key.TotalTokens,
                        totalCost = key.QuotaTotalCost,
                        usedCost = key.UsedCost,
                        totalRequests = key.QuotaTotalRequests,
                        usedRequests = key.UsedRequests,
                        rpm = key.RateLimitRpm
                    }
                });
            return;
        }

        ctx.SetApiKey(key);
        await _next(ctx);
    }

    private static async Task RecordRejectedAsync(HttpContext ctx, ApiKeyAccount key, QuotaDecision d, string path)
    {
        try
        {
            var repo = ctx.RequestServices.GetRequiredService<Core.Data.BillingRepository>();
            var outcome = d.Code == "rate_limit" ? RequestOutcome.RejectedRateLimit : RequestOutcome.RejectedNoQuota;
            await repo.InsertUsageAsync(new UsageRecord
            {
                ApiKeyId = key.Id,
                KeyPrefix = key.KeyPrefix,
                Model = "",
                Endpoint = path,
                Outcome = outcome,
                HttpStatus = d.HttpStatus,
                ErrorMessage = d.Reason,
                ClientIp = ClientIp(ctx),
                CreatedAt = DateTime.UtcNow
            }, CancellationToken.None);
        }
        catch { /* 记录失败不影响主流程 */ }
    }

    /// <summary>
    /// 不需要 API Key 的路径：站点首页、管理后台、健康检查、静态资源。
    /// 注意这里只放行"/"本身，不影响 /api/* 的强制认证——
    /// 网关的核心价值就是不认证不给用，别把业务路径误加进来。
    /// </summary>
    private static bool IsPublicPath(string path) =>
        path == "/" ||
        path.StartsWith("/admin", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("/health", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("/api/admin", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("/favicon", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("/css", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("/js", StringComparison.OrdinalIgnoreCase);

    /// <summary>从请求中提取 Key。</summary>
    public static string? ExtractKey(HttpRequest req)
    {
        // 1) X-API-Key
        if (req.Headers.TryGetValue("X-API-Key", out var xk) && xk.Count > 0)
        {
            var v = xk[0];
            if (!string.IsNullOrWhiteSpace(v)) return v.Trim();
        }

        // 2) Authorization: Bearer xxx
        if (req.Headers.TryGetValue("Authorization", out var auth) && auth.Count > 0)
        {
            var raw = auth[0];
            if (!string.IsNullOrWhiteSpace(raw))
            {
                var s = raw.Trim();
                const string bearer = "Bearer ";
                if (s.StartsWith(bearer, StringComparison.OrdinalIgnoreCase))
                    return s[bearer.Length..].Trim();
                return s;   // 容忍直接给 Key 的写法
            }
        }

        // 3) query string（仅用于方便调试，正式环境建议只用 header）
        if (req.Query.TryGetValue("api_key", out var qk) && qk.Count > 0)
        {
            var v = qk[0];
            if (!string.IsNullOrWhiteSpace(v)) return v.Trim();
        }

        return null;
    }

    internal static string ClientIp(HttpContext ctx)
    {
        if (ctx.Request.Headers.TryGetValue("X-Forwarded-For", out var fwd) && fwd.Count > 0)
        {
            var first = fwd[0]?.Split(',')[0].Trim();
            if (!string.IsNullOrWhiteSpace(first)) return first;
        }
        return ctx.Connection.RemoteIpAddress?.ToString() ?? "";
    }

    private static async Task WriteError(HttpContext ctx, int status, string code, string message, object? extra = null)
    {
        if (ctx.Response.HasStarted) return;
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json; charset=utf-8";

        var payload = new Dictionary<string, object?>
        {
            ["error"] = new Dictionary<string, object?>
            {
                ["code"] = code,
                ["message"] = message,
                ["type"] = "billing_gateway_error"
            }
        };
        if (extra is not null) payload["detail"] = extra;

        await ctx.Response.WriteAsync(JsonSerializer.Serialize(payload,
            new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
    }
}
