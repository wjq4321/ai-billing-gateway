using AiBilling.Core.Models;

namespace AiBilling.Gateway.Middleware;

/// <summary>把已认证的 Key 放进 HttpContext.Items，供后续中间件读取。</summary>
public static class BillingContextKeys
{
    public const string ApiKey = "aibilling.apikey";
}

public static class HttpContextBillingExtensions
{
    public static ApiKeyAccount? GetApiKey(this HttpContext ctx) =>
        ctx.Items.TryGetValue(BillingContextKeys.ApiKey, out var v) ? v as ApiKeyAccount : null;

    public static void SetApiKey(this HttpContext ctx, ApiKeyAccount key) =>
        ctx.Items[BillingContextKeys.ApiKey] = key;
}
