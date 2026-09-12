using System.Text.Json;
using AiBilling.Core.Billing;
using AiBilling.Core.Data;
using AiBilling.Core.Models;
using AiBilling.Core.Options;
using AiBilling.Gateway.Services;
using Microsoft.Extensions.Options;

namespace AiBilling.Gateway.Endpoints;

/// <summary>管理端 REST API（/api/admin/*），供 Web 后台与 CLI 共用。</summary>
public static class AdminEndpoints
{
    public static void MapAdminEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/api/admin");

        // ---------------------------------------------------------- 登录
        g.MapPost("/login", async (HttpContext ctx, AdminSessionService sessions, LoginRequest req, CancellationToken ct) =>
        {
            if (!await sessions.ValidateCredentialsAsync(req.Username ?? "", req.Password ?? "", ct))
                return Results.Json(new { error = new { code = "bad_credentials", message = "用户名或密码错误。" } },
                    statusCode: 401);

            var token = sessions.IssueToken(TimeSpan.FromHours(12));
            ctx.Response.Cookies.Append(AdminSessionService.CookieName, token, new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.Lax,
                Expires = DateTimeOffset.UtcNow.AddHours(12)
            });
            return Results.Ok(new { ok = true, username = req.Username });
        });

        // ---------------------------------------------------------- 管理账号
        g.MapGet("/account", async (AdminSessionService sessions, CancellationToken ct) =>
        {
            var dbBacked = await sessions.IsDbBackedAsync(ct);
            return Results.Ok(new
            {
                username = await sessions.GetCurrentUsernameAsync(ct),
                source = dbBacked ? "database" : "config",
                hint = dbBacked
                    ? "账号保存在数据库中（密码为 PBKDF2 哈希），修改后立即生效。"
                    : "当前使用 appsettings.json 中的初始账号；首次在此修改后将转为数据库存储。"
            });
        });

        g.MapPost("/account", async (HttpContext ctx, AdminSessionService sessions, BillingRepository repo,
            ChangeAccountRequest req, CancellationToken ct) =>
        {
            var newUser = req.NewUsername?.Trim();
            var newPass = req.NewPassword;

            if (string.IsNullOrWhiteSpace(newUser) && string.IsNullOrEmpty(newPass))
                return Results.BadRequest(new { error = new { code = "nothing_to_change", message = "用户名和新密码至少要填一项。" } });

            // 改密码或改用户名都必须验证当前密码，避免会话被劫持后直接改掉凭据
            if (!string.IsNullOrEmpty(req.CurrentPassword))
            {
                var currentUser = await sessions.GetCurrentUsernameAsync(ct);
                if (!await sessions.ValidateCredentialsAsync(currentUser, req.CurrentPassword, ct))
                    return Results.BadRequest(new { error = new { code = "bad_current_password", message = "当前密码不正确。" } });
            }
            else
            {
                return Results.BadRequest(new { error = new { code = "current_password_required", message = "请输入当前密码以确认身份。" } });
            }

            if (!string.IsNullOrEmpty(newPass))
            {
                if (newPass.Length < 8)
                    return Results.BadRequest(new { error = new { code = "password_too_short", message = "新密码至少 8 位。" } });
                if (newPass != req.ConfirmPassword)
                    return Results.BadRequest(new { error = new { code = "password_mismatch", message = "两次输入的新密码不一致。" } });
            }

            if (!string.IsNullOrWhiteSpace(newUser) && newUser.Length > 64)
                return Results.BadRequest(new { error = new { code = "username_too_long", message = "用户名不能超过 64 个字符。" } });

            await sessions.ChangeCredentialsAsync(newUser, newPass, ct);
            await repo.WriteAuditAsync("admin", "change_credentials",
                string.IsNullOrWhiteSpace(newUser) ? "(仅改密码)" : newUser, "管理端账号已修改", ct);

            // 会话密钥已轮换，当前 Cookie 立即失效，主动清掉让前端跳回登录页
            ctx.Response.Cookies.Delete(AdminSessionService.CookieName);

            return Results.Ok(new
            {
                ok = true,
                username = await sessions.GetCurrentUsernameAsync(ct),
                message = "账号已更新，请使用新凭据重新登录。"
            });
        });

        g.MapPost("/logout", (HttpContext ctx) =>
        {
            ctx.Response.Cookies.Delete(AdminSessionService.CookieName);
            return Results.Ok(new { ok = true });
        });

        // ---------------------------------------------------------- 概览
        g.MapGet("/overview", async (BillingRepository repo, BillingEngine engine,
            IOptions<BillingOptions> opt, BillingQueue queue, CancellationToken ct) =>
        {
            var keys = await repo.ListKeysAsync(ct);
            var all = await repo.SummarizeAsync(null, null, null, ct);
            var todayUtc = DateTime.UtcNow.Date;
            var today = await repo.SummarizeAsync(null, todayUtc, null, ct);

            return Results.Ok(new
            {
                keys = new
                {
                    total = keys.Count,
                    active = keys.Count(k => k.Status == ApiKeyStatus.Active),
                    disabled = keys.Count(k => k.Status == ApiKeyStatus.Disabled)
                },
                totals = new
                {
                    promptTokens = all.PromptTokens,
                    completionTokens = all.CompletionTokens,
                    totalTokens = all.TotalTokens,
                    requests = all.Requests,
                    successRequests = all.SuccessRequests,
                    cost = all.Cost
                },
                today = new
                {
                    totalTokens = today.TotalTokens,
                    requests = today.Requests,
                    cost = today.Cost
                },
                config = new
                {
                    meteringMode = opt.Value.MeteringMode,
                    ollamaBaseUrl = opt.Value.OllamaBaseUrl,
                    currency = opt.Value.Currency,
                    charsPerToken = opt.Value.CharsPerToken,
                    logUsageRecords = opt.Value.LogUsageRecords,
                    calibration = opt.Value.EnableCalibration
                },
                health = new { droppedRecords = queue.DroppedCount }
            });
        });

        // ---------------------------------------------------------- API Keys
        g.MapGet("/keys", async (BillingRepository repo, CancellationToken ct) =>
            Results.Ok((await repo.ListKeysAsync(ct)).Select(ToKeyDto)));

        g.MapPost("/keys", async (BillingRepository repo, ApiKeyStore store, BillingRepository audit,
            CreateKeyRequest req, CancellationToken ct) =>
        {
            var account = new ApiKeyAccount
            {
                Name = req.Name ?? "未命名",
                Status = ApiKeyStatus.Active,
                AllowedModels = req.AllowedModels ?? new List<string>(),
                QuotaTotalTokens = req.QuotaTotalTokens,
                QuotaTotalCost = req.QuotaTotalCost,
                QuotaTotalRequests = req.QuotaTotalRequests,
                RateLimitRpm = req.RateLimitRpm,
                MaxOutputTokensPerSec = req.MaxOutputTokensPerSec,
                QuotaTokensPerMinute = req.QuotaTokensPerMinute,
                QuotaTokensPerDay = req.QuotaTokensPerDay,
                OverrideInputPricePer1K = req.OverrideInputPricePer1K,
                OverrideOutputPricePer1K = req.OverrideOutputPricePer1K,
                OverridePricePerRequest = req.OverridePricePerRequest,
                ExpiresAt = req.ExpiresAt
            };

            var (created, plain) = await repo.CreateKeyAsync(account, ct);
            store.Invalidate();
            await audit.WriteAuditAsync("admin", "create_key", created.KeyPrefix, created.Name, ct);

            // 明文 Key 仅此一次返回
            return Results.Ok(new
            {
                key = ToKeyDto(created),
                plainKey = plain,
                warning = "请立即保存该 Key，之后无法再次查看。"
            });
        });

        g.MapPut("/keys/{id:long}", async (long id, BillingRepository repo, ApiKeyStore store,
            UpdateKeyRequest req, CancellationToken ct) =>
        {
            var existing = await repo.GetKeyAsync(id, ct);
            if (existing is null) return Results.NotFound(new { error = new { code = "not_found", message = "Key 不存在。" } });

            existing.Name = req.Name ?? existing.Name;
            if (req.Status is not null && Enum.TryParse<ApiKeyStatus>(req.Status, true, out var st)) existing.Status = st;
            if (req.AllowedModels is not null) existing.AllowedModels = req.AllowedModels;
            existing.QuotaTotalTokens = req.QuotaTotalTokens;
            existing.QuotaTotalCost = req.QuotaTotalCost;
            existing.QuotaTotalRequests = req.QuotaTotalRequests;
            existing.RateLimitRpm = req.RateLimitRpm;
            existing.MaxOutputTokensPerSec = req.MaxOutputTokensPerSec;
            existing.QuotaTokensPerMinute = req.QuotaTokensPerMinute;
            existing.QuotaTokensPerDay = req.QuotaTokensPerDay;
            existing.OverrideInputPricePer1K = req.OverrideInputPricePer1K;
            existing.OverrideOutputPricePer1K = req.OverrideOutputPricePer1K;
            existing.OverridePricePerRequest = req.OverridePricePerRequest;
            existing.ExpiresAt = req.ExpiresAt;

            await repo.UpdateKeyAsync(existing, ct);
            store.Invalidate();
            return Results.Ok(ToKeyDto(existing));
        });

        g.MapDelete("/keys/{id:long}", async (long id, BillingRepository repo, ApiKeyStore store, CancellationToken ct) =>
        {
            var ok = await repo.DeleteKeyAsync(id, ct);
            store.Invalidate();
            return ok ? Results.Ok(new { ok = true }) : Results.NotFound();
        });

        g.MapPost("/keys/{id:long}/reset-usage", async (long id, BillingRepository repo, ApiKeyStore store, CancellationToken ct) =>
        {
            await repo.ResetUsageAsync(id, ct);
            store.Invalidate();
            return Results.Ok(new { ok = true });
        });

        // ---------------------------------------------------------- 模型定价
        g.MapGet("/prices", async (BillingRepository repo, CancellationToken ct) =>
            Results.Ok(await repo.ListPricesAsync(ct)));

        g.MapPost("/prices", async (BillingRepository repo, BillingEngine engine, ModelPriceDto req, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Model))
                return Results.BadRequest(new { error = new { code = "invalid_model", message = "模型名不能为空。" } });

            var p = new ModelPrice
            {
                Model = req.Model.Trim(),
                Mode = Enum.TryParse<PricingMode>(req.Mode, true, out var m) ? m : PricingMode.PerToken,
                InputPricePer1K = req.InputPricePer1K,
                OutputPricePer1K = req.OutputPricePer1K,
                PricePerRequest = req.PricePerRequest,
                IsWildcard = req.Model.Contains('*') || req.Model.Contains('?'),
                Enabled = req.Enabled
            };
            await repo.UpsertPriceAsync(p, ct);
            engine.SetPrices(await repo.ListPricesAsync(ct));
            return Results.Ok(p);
        });

        g.MapDelete("/prices/{model}", async (string model, BillingRepository repo, BillingEngine engine, CancellationToken ct) =>
        {
            var ok = await repo.DeletePriceAsync(model, ct);
            engine.SetPrices(await repo.ListPricesAsync(ct));
            return ok ? Results.Ok(new { ok = true }) : Results.NotFound();
        });

        g.MapPost("/prices/reload", async (BillingRepository repo, BillingEngine engine, CancellationToken ct) =>
        {
            engine.SetPrices(await repo.ListPricesAsync(ct));
            return Results.Ok(new { ok = true, count = (await repo.ListPricesAsync(ct)).Count });
        });

        // ---------------------------------------------------------- 用量与账单
        g.MapGet("/usage", async (BillingRepository repo, long? keyId, DateTime? from, DateTime? to,
            string? model, int? limit, CancellationToken ct) =>
        {
            var records = await repo.QueryUsageAsync(keyId, from, to, model, limit ?? 200, ct);
            return Results.Ok(records.Select(ToUsageDto));
        });

        g.MapGet("/usage/summary", async (BillingRepository repo, long? keyId, DateTime? from, DateTime? to, CancellationToken ct) =>
        {
            var s = await repo.SummarizeAsync(keyId, from, to, ct);
            return Results.Ok(new
            {
                promptTokens = s.PromptTokens,
                completionTokens = s.CompletionTokens,
                totalTokens = s.TotalTokens,
                requests = s.Requests,
                successRequests = s.SuccessRequests,
                cost = s.Cost
            });
        });

        g.MapGet("/usage/daily", async (BillingRepository repo, long? keyId, int? days, CancellationToken ct) =>
        {
            var from = DateTime.UtcNow.Date.AddDays(-(Math.Clamp(days ?? 14, 1, 365)));
            return Results.Ok(await repo.DailyAsync(keyId, from, null, ct));
        });

        g.MapGet("/usage/by-model", async (BillingRepository repo, long? keyId, int? days, CancellationToken ct) =>
        {
            var from = DateTime.UtcNow.Date.AddDays(-(Math.Clamp(days ?? 30, 1, 365)));
            return Results.Ok(await repo.ByModelAsync(keyId, from, null, ct));
        });

        // ---------------------------------------------------------- 校准
        g.MapGet("/calibration", async (BillingRepository repo, int? days, CancellationToken ct) =>
        {
            var r = await repo.CalibrationAsync(Math.Clamp(days ?? 7, 1, 365), ct);
            return Results.Ok(new
            {
                days = r.Days,
                sampleCount = r.SampleCount,
                avgEstimated = Math.Round(r.AvgEstimated, 2),
                avgActual = Math.Round(r.AvgActual, 2),
                sumEstimated = r.SumEstimated,
                sumActual = r.SumActual,
                ratio = Math.Round(r.Ratio, 4),
                suggestedCharsPerTokenFactor = Math.Round(r.SuggestedCharsPerTokenFactor, 4),
                hint = r.SampleCount == 0
                    ? "样本不足，请先跑一些流式请求。"
                    : (r.Ratio > 1.05
                        ? "估算偏低（实际 token 更多），建议调小 Billing:CharsPerToken 或按建议系数修正。"
                        : r.Ratio < 0.95
                            ? "估算偏高，建议调大 Billing:CharsPerToken。"
                            : "估算与实测接近，无需调整。")
            });
        });

        // ---------------------------------------------------------- Ollama 上游
        g.MapGet("/ollama/models", async (HttpClient http, IOptions<BillingOptions> opt, CancellationToken ct) =>
        {
            try
            {
                var url = opt.Value.OllamaBaseUrl.TrimEnd('/') + "/api/tags";
                var json = await http.GetStringAsync(url, ct);
                return Results.Ok(new { ok = true, models = OllamaUsageParser.ParseModelList(json), raw = JsonDocument.Parse(json).RootElement });
            }
            catch (Exception ex)
            {
                return Results.Ok(new { ok = false, error = ex.Message, models = Array.Empty<string>() });
            }
        });

        g.MapGet("/ollama/health", async (HttpClient http, IOptions<BillingOptions> opt, CancellationToken ct) =>
        {
            try
            {
                var url = opt.Value.OllamaBaseUrl.TrimEnd('/') + "/api/tags";
                using var res = await http.GetAsync(url, ct);
                return Results.Ok(new { ok = res.IsSuccessStatusCode, status = (int)res.StatusCode, url = opt.Value.OllamaBaseUrl });
            }
            catch (Exception ex)
            {
                return Results.Ok(new { ok = false, error = ex.Message, url = opt.Value.OllamaBaseUrl });
            }
        });

        // ---------------------------------------------------------- 审计日志
        g.MapGet("/audit", async (BillingRepository repo, CancellationToken ct) =>
            Results.Ok(await repo.QueryUsageAsync(null, null, null, null, 1, ct)));
    }

    // --------------------------------------------------------------- DTO 映射

    private static object ToKeyDto(ApiKeyAccount k) => new
    {
        id = k.Id,
        keyPrefix = k.KeyPrefix,
        name = k.Name,
        status = k.Status.ToString(),
        allowedModels = k.AllowedModels,
        quota = new
        {
            totalTokens = k.QuotaTotalTokens,
            totalCost = k.QuotaTotalCost,
            totalRequests = k.QuotaTotalRequests,
            rateLimitRpm = k.RateLimitRpm,
            maxOutputTokensPerSec = k.MaxOutputTokensPerSec,
            tokensPerMinute = k.QuotaTokensPerMinute,
            tokensPerDay = k.QuotaTokensPerDay
        },
        used = new
        {
            promptTokens = k.UsedPromptTokens,
            completionTokens = k.UsedCompletionTokens,
            totalTokens = k.TotalTokens,
            requests = k.UsedRequests,
            cost = k.UsedCost
        },
        remaining = new
        {
            tokens = k.QuotaTotalTokens is null ? (long?)null : Math.Max(0, k.QuotaTotalTokens.Value - k.TotalTokens),
            cost = k.QuotaTotalCost is null ? (decimal?)null : Math.Max(0, k.QuotaTotalCost.Value - k.UsedCost),
            requests = k.QuotaTotalRequests is null ? (long?)null : Math.Max(0, k.QuotaTotalRequests.Value - k.UsedRequests)
        },
        overrides = new
        {
            inputPricePer1K = k.OverrideInputPricePer1K,
            outputPricePer1K = k.OverrideOutputPricePer1K,
            pricePerRequest = k.OverridePricePerRequest
        },
        expiresAt = k.ExpiresAt,
        createdAt = k.CreatedAt,
        lastUsedAt = k.LastUsedAt
    };

    private static object ToUsageDto(UsageRecord r) => new
    {
        id = r.Id,
        apiKeyId = r.ApiKeyId,
        keyPrefix = r.KeyPrefix,
        model = r.Model,
        endpoint = r.Endpoint,
        meteringMode = r.MeteringMode.ToString(),
        promptTokens = r.PromptTokens,
        completionTokens = r.CompletionTokens,
        totalTokens = r.PromptTokens + r.CompletionTokens,
        actualPromptTokens = r.ActualPromptTokens,
        actualCompletionTokens = r.ActualCompletionTokens,
        estimatedCompletionTokens = r.EstimatedCompletionTokens,
        tokenDrift = r.TokenDrift,
        cost = r.Cost,
        currency = r.Currency,
        outcome = r.Outcome.ToString(),
        httpStatus = r.HttpStatus,
        durationMs = r.DurationMs,
        firstTokenMs = r.FirstTokenMs,
        errorMessage = r.ErrorMessage,
        clientIp = r.ClientIp,
        createdAt = r.CreatedAt
    };

    // --------------------------------------------------------------- 请求 DTO

    public sealed record LoginRequest(string? Username, string? Password);

    public sealed record ChangeAccountRequest(
        string? CurrentPassword,
        string? NewUsername,
        string? NewPassword,
        string? ConfirmPassword);

    public sealed record CreateKeyRequest(
        string? Name,
        List<string>? AllowedModels,
        long? QuotaTotalTokens,
        decimal? QuotaTotalCost,
        long? QuotaTotalRequests,
        int? RateLimitRpm,
        decimal? MaxOutputTokensPerSec,
        long? QuotaTokensPerMinute,
        long? QuotaTokensPerDay,
        decimal? OverrideInputPricePer1K,
        decimal? OverrideOutputPricePer1K,
        decimal? OverridePricePerRequest,
        DateTime? ExpiresAt);

    public sealed record UpdateKeyRequest(
        string? Name,
        string? Status,
        List<string>? AllowedModels,
        long? QuotaTotalTokens,
        decimal? QuotaTotalCost,
        long? QuotaTotalRequests,
        int? RateLimitRpm,
        decimal? MaxOutputTokensPerSec,
        long? QuotaTokensPerMinute,
        long? QuotaTokensPerDay,
        decimal? OverrideInputPricePer1K,
        decimal? OverrideOutputPricePer1K,
        decimal? OverridePricePerRequest,
        DateTime? ExpiresAt);

    public sealed record ModelPriceDto(
        string? Model,
        string? Mode,
        decimal InputPricePer1K,
        decimal OutputPricePer1K,
        decimal PricePerRequest,
        bool Enabled = true);
}
