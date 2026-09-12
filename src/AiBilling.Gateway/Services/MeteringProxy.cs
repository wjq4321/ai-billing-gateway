using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AiBilling.Core.Billing;
using AiBilling.Core.Models;
using AiBilling.Core.Options;
using AiBilling.Gateway.Middleware;
using Microsoft.Extensions.Options;

namespace AiBilling.Gateway.Services;

/// <summary>
/// 计量代理：把请求转发给 Ollama，解析 token 用量，计算费用并入队落库。
///
/// 两种计量模式（appsettings 的 Billing:MeteringMode）：
///   Exact    —— 缓冲整个上游响应，读 prompt_eval_count / eval_count 精确计费。
///               代价：流式场景首字延迟升高（要等全量生成完）。
///   Realtime —— 逐块透传，保持秒出字；同时用累积文本估算 token 计费。
///               代价：与真实值有偏差，偏差量可在校准报告里看到。
///
/// 无论哪种模式，只要上游返回了真实计数，都会写进 actual_* 列用于校准。
/// </summary>
public sealed class MeteringProxy
{
    private readonly HttpClient _http;
    private readonly BillingEngine _engine;
    private readonly TokenEstimator _estimator;
    private readonly BillingQueue _queue;
    private readonly TokenRateLimiter _tokenRates;
    private readonly BillingOptions _options;
    private readonly ILogger<MeteringProxy> _logger;

    /// <summary>
    /// 需要计量计费的端点。
    /// 兼容两套 API：Ollama 原生（/api/*）与 OpenAI 风格（/v1/*，Ollama 也原生支持）。
    /// 漏配会导致该路径"能用但不计费"，等于漏收钱，所以新增端点务必同步这里。
    /// </summary>
    private static readonly HashSet<string> MeteredPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        // Ollama 原生
        "/api/chat", "/api/generate", "/api/embed", "/api/embeddings",
        // OpenAI 兼容（Ollama 原生提供）
        "/v1/chat/completions", "/v1/completions", "/v1/embeddings"
    };

    public MeteringProxy(
        HttpClient http,
        BillingEngine engine,
        TokenEstimator estimator,
        BillingQueue queue,
        TokenRateLimiter tokenRates,
        IOptions<BillingOptions> options,
        ILogger<MeteringProxy> logger)
    {
        _http = http;
        _engine = engine;
        _estimator = estimator;
        _queue = queue;
        _tokenRates = tokenRates;
        _options = options.Value;
        _logger = logger;
    }

    public bool IsMeteredPath(string path) => MeteredPaths.Contains(path);

    /// <summary>处理一次代理请求。</summary>
    public async Task HandleAsync(HttpContext ctx, string path)
    {
        var sw = Stopwatch.StartNew();
        var key = ctx.GetApiKey()!;
        var ct = ctx.RequestAborted;

        // ---- 1. 读请求体 ----
        string body;
        using (var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8, leaveOpen: true))
        {
            body = await reader.ReadToEndAsync(ct);
        }

        var model = OllamaUsageParser.ExtractModel(body) ?? "";
        var streaming = OllamaUsageParser.IsStreaming(body);
        var mode = ParseMode(_options.MeteringMode);

        // ---- 2. 模型白名单 ----
        if (!string.IsNullOrEmpty(model) && !key.AllowsModel(model))
        {
            await WriteJsonError(ctx, 403, "model_not_allowed",
                $"该 API Key 无权访问模型 \"{model}\"。");
            return;
        }

        // ---- 3. 预估输入 token + 预检配额 ----
        var estimatedPrompt = _estimator.EstimateFromRequestJson(body);
        var assumedCompletion = _estimator.Estimate("") + 512;   // 输出未知，按 512 保守预估
        var preflightCost = _engine.EstimatePreflightCost(key, model, estimatedPrompt, assumedCompletion);

        var quota = ctx.RequestServices.GetRequiredService<QuotaGuard>();
        var pre = quota.Preflight(key, estimatedPrompt + assumedCompletion, preflightCost, DateTime.UtcNow);
        if (!pre.Allowed)
        {
            await WriteJsonError(ctx, pre.HttpStatus, pre.Code ?? "quota_exceeded", pre.Reason ?? "配额不足。");
            return;
        }

        // ---- 3b. token 消耗速率检查（每分钟/每天）----
        // 与累计配额不同：这是"单位时间能烧多少 token"的节流，
        // 防止一个客户端短时间内把预算或算力跑光。
        var rateOk = _tokenRates.Check(key, estimatedPrompt + assumedCompletion, DateTime.UtcNow);
        if (!rateOk.Allowed)
        {
            await WriteJsonError(ctx, rateOk.HttpStatus, rateOk.Code ?? "token_rate_exceeded",
                rateOk.Reason ?? "超出 token 消耗速率限制。");
            return;
        }

        // ---- 4. 转发 ----
        using var upstreamReq = BuildUpstreamRequest(ctx, path, body);
        HttpResponseMessage upstream;
        try
        {
            upstream = await _http.SendAsync(upstreamReq, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (Exception ex)
        {
            sw.Stop();
            await RecordAndRespondError(ctx, key, model, path, mode, estimatedPrompt, ex.Message, 502, sw.ElapsedMilliseconds, streaming);
            return;
        }

        using (upstream)
        {
            // 响应头提交前可自由写入；一旦开始写正文（流式第一块）就锁定了。
            // 这里先把回调挂上，值随解析进展被填充；必须在 CopyResponseHeaders
            // 之前完成注册，否则 Headers 已进入只读状态，再注册会抛异常。
            var billingHeaders = new Dictionary<string, string>();

            ctx.Response.OnStarting(() =>
            {
                foreach (var kv in billingHeaders)
                {
                    ctx.Response.Headers[kv.Key] = kv.Value;
                }
                return Task.CompletedTask;
            });

            ctx.Response.StatusCode = (int)upstream.StatusCode;
            CopyResponseHeaders(upstream, ctx.Response);

            if (!upstream.IsSuccessStatusCode)
            {
                var errBody = await upstream.Content.ReadAsStringAsync(ct);
                sw.Stop();
                await SafeWriteAsync(ctx.Response, errBody, ct);
                await RecordAndRespondError(ctx, key, model, path, mode, estimatedPrompt,
                    Truncate(errBody, 500), (int)upstream.StatusCode, sw.ElapsedMilliseconds, streaming);
                return;
            }

            // 非计量端点（如 /api/tags）直接透传
            if (!IsMeteredPath(path))
            {
                await using var s = await upstream.Content.ReadAsStreamAsync(ct);
                await s.CopyToAsync(ctx.Response.Body, ct);
                return;
            }

            // ---- 5. 按模式处理计量 ----
            var result = streaming
                ? (mode == MeteringMode.Exact
                    ? await HandleStreamExactAsync(ctx, upstream, estimatedPrompt, ct, billingHeaders)
                    : await HandleStreamRealtimeAsync(ctx, upstream, estimatedPrompt, ct, billingHeaders, model, key))
                : await HandleNonStreamAsync(ctx, upstream, model, estimatedPrompt, ct, billingHeaders, key);

            sw.Stop();

            // ---- 6. 计费 ----
            var charge = _engine.Calculate(key, model, result.PromptTokens, result.CompletionTokens, _options.Currency);

            // 尽力补一次：若响应头尚未提交（精确模式 / 非流式）即可带上最终值；
            // 实时流模式下已在首块提交过头，此处只更新字典供日志使用。
            SetBillingHeaders(ctx, billingHeaders, result.PromptTokens, result.CompletionTokens, charge.Cost);

            var record = new UsageRecord
            {
                ApiKeyId = key.Id,
                KeyPrefix = key.KeyPrefix,
                Model = model,
                Endpoint = path,
                MeteringMode = mode,
                PromptTokens = result.PromptTokens,
                CompletionTokens = result.CompletionTokens,
                ActualPromptTokens = result.ActualPrompt,
                ActualCompletionTokens = result.ActualCompletion,
                EstimatedCompletionTokens = _options.EnableCalibration ? result.EstimatedCompletion : null,
                Cost = charge.Cost,
                Currency = _options.Currency,
                Outcome = result.ClientAborted ? RequestOutcome.ClientAborted : RequestOutcome.Success,
                HttpStatus = (int)upstream.StatusCode,
                DurationMs = (int)sw.ElapsedMilliseconds,
                FirstTokenMs = result.FirstTokenMs,
                ClientIp = ApiKeyAuthMiddleware.ClientIp(ctx),
                CreatedAt = DateTime.UtcNow
            };

            if (_options.LogUsageRecords)
            {
                await _queue.EnqueueAsync(new UsageEnvelope(
                    key.Id, result.PromptTokens, result.CompletionTokens, charge.Cost, record), CancellationToken.None);
            }

            // 把本次实际消耗计入速率窗口，供后续请求做分钟/天级节流
            var totalTokens = result.PromptTokens + result.CompletionTokens;
            _tokenRates.Record(key.Id, totalTokens, DateTime.UtcNow);

            // 非流式响应无法在传输层节流，这里事后校验是否超出输出速度上限，
            // 超出只做记录与告警，不追溯拒绝（请求已经完成了）。
            string? rateNote = null;
            if (_options.EnforceRateOnNonStreaming && !streaming)
            {
                var limit = key.MaxOutputTokensPerSec ?? _options.DefaultMaxOutputTokensPerSec;
                var elapsedSec = sw.Elapsed.TotalSeconds;
                if (OutputThrottle.ExceedsRate(result.CompletionTokens, elapsedSec, limit, out var actualRate))
                {
                    rateNote = $"非流式输出速率 {actualRate:F1} tok/s 超过上限 {limit} tok/s";
                    _logger.LogWarning("Key {Prefix} {Note}", key.KeyPrefix, rateNote);
                }
            }

            // 计费头在各自的处理分支里、趁着响应头还能写时就已经落定
            // （见 SetBillingHeaders 的说明），这里只做日志。
            _logger.LogInformation(
                "计费 key={Prefix} model={Model} in={In} out={Out} mode={Mode} cost={Cost} 耗时={Ms}ms 首字={First}ms{Note}",
                key.KeyPrefix, model, result.PromptTokens, result.CompletionTokens, mode, charge.Cost,
                sw.ElapsedMilliseconds, result.FirstTokenMs?.ToString() ?? "-",
                rateNote is null ? "" : " ⚠ " + rateNote);
        }
    }

    // ------------------------------------------------------------ 处理模式

    private sealed class MeterResult
    {
        public long PromptTokens { get; set; }
        public long CompletionTokens { get; set; }
        public long? ActualPrompt { get; set; }
        public long? ActualCompletion { get; set; }
        public long? EstimatedCompletion { get; set; }
        public long? FirstTokenMs { get; set; }
        public bool ClientAborted { get; set; }
    }

    /// <summary>非流式：上游直接返回完整 JSON，计数就在里面。</summary>
    private async Task<MeterResult> HandleNonStreamAsync(
        HttpContext ctx, HttpResponseMessage upstream, string model, long estimatedPrompt, CancellationToken ct,
        Dictionary<string, string> billingHeaders, ApiKeyAccount key)
    {
        var text = await upstream.Content.ReadAsStringAsync(ct);
        SetContentType(ctx.Response, upstream, "application/json");

        var usage = OllamaUsageParser.Parse(text);
        var responseText = ExtractResponseText(text);
        var r = new MeterResult
        {
            ActualPrompt = usage?.PromptEvalCount,
            ActualCompletion = usage?.EvalCount
        };

        // 优先用上游精确值，缺失时退回估算
        r.PromptTokens = usage?.PromptEvalCount ?? estimatedPrompt;
        r.CompletionTokens = usage?.EvalCount ?? _estimator.Estimate(responseText);
        r.EstimatedCompletion = _estimator.Estimate(responseText);

        // 关键：必须在写入正文之前把计费头落定。
        // 非流式响应一次性写出，写完后响应头即锁定，再补就会失败。
        var charge = _engine.Calculate(key, model, r.PromptTokens, r.CompletionTokens, _options.Currency);
        SetBillingHeaders(ctx, billingHeaders, r.PromptTokens, r.CompletionTokens, charge.Cost);

        await SafeWriteAsync(ctx.Response, text, ct);
        return r;
    }

    /// <summary>把计费结果写进响应头（仅在响应尚未提交时真正生效）。</summary>
    private void SetBillingHeaders(
        HttpContext ctx, Dictionary<string, string> sink,
        long promptTokens, long completionTokens, decimal cost)
    {
        // 更新共享字典：即使响应已提交（流式），OnStarting 回调也能带上这些值
        sink["X-Billing-Tokens-Prompt"] = promptTokens.ToString();
        sink["X-Billing-Tokens-Completion"] = completionTokens.ToString();
        sink["X-Billing-Cost"] = cost.ToString("0.######");
        sink["X-Billing-Currency"] = _options.Currency;

        if (ctx.Response.HasStarted) return;

        foreach (var kv in sink)
        {
            ctx.Response.Headers[kv.Key] = kv.Value;
        }
    }

    /// <summary>流式 · 精确模式：先把上游读完，拿到计数，再一次性回传给客户端。</summary>
    private async Task<MeterResult> HandleStreamExactAsync(
        HttpContext ctx, HttpResponseMessage upstream, long estimatedPrompt, CancellationToken ct,
        Dictionary<string, string> billingHeaders)
    {
        var sb = new StringBuilder();
        var textSink = new StringBuilder();
        long? firstTokenMs = null;
        var sw = Stopwatch.StartNew();
        var aborted = false;

        await using (var stream = await upstream.Content.ReadAsStreamAsync(ct))
        using (var reader = new StreamReader(stream, Encoding.UTF8))
        {
            while (true)
            {
                string? line;
                try
                {
                    line = await reader.ReadLineAsync(ct);
                }
                catch (OperationCanceledException) { aborted = true; break; }

                if (line is null) break;
                if (line.Length == 0) continue;

                if (firstTokenMs is null) firstTokenMs = sw.ElapsedMilliseconds;
                sb.Append(line).Append('\n');
                OllamaUsageParser.AccumulateResponseText(line, textSink);
            }
        }

        var payload = sb.ToString();
        var usage = OllamaUsageParser.ParseNdjson(payload);
        var estimated = _estimator.Estimate(textSink.ToString());

        var r = new MeterResult
        {
            PromptTokens = usage?.PromptEvalCount ?? estimatedPrompt,
            CompletionTokens = usage?.EvalCount ?? estimated,
            ActualPrompt = usage?.PromptEvalCount,
            ActualCompletion = usage?.EvalCount,
            EstimatedCompletion = estimated,
            FirstTokenMs = firstTokenMs,
            ClientAborted = aborted
        };

        // 精确模式下响应体最后一次写出，因此可以在此刻填入准确的计费头
        billingHeaders["X-Billing-Tokens-Prompt"] = r.PromptTokens.ToString();
        billingHeaders["X-Billing-Tokens-Completion"] = r.CompletionTokens.ToString();
        billingHeaders["X-Billing-Mode"] = "Exact";

        SetContentType(ctx.Response, upstream, "application/x-ndjson");
        if (!ctx.Response.HasStarted)
            await SafeWriteAsync(ctx.Response, payload, ct);

        return r;
    }

    /// <summary>流式 · 实时模式：逐块透传，边发边估算。</summary>
    private async Task<MeterResult> HandleStreamRealtimeAsync(
        HttpContext ctx, HttpResponseMessage upstream, long estimatedPrompt, CancellationToken ct,
        Dictionary<string, string> billingHeaders, string model, ApiKeyAccount key)
    {
        var textSink = new StringBuilder();
        var lastUsage = (OllamaUsageParser.OllamaUsage?)null;
        var upstreamBuffer = new StringBuilder();      // 保留原始内容，流式结束后若含计数则用于校准
        long? firstTokenMs = null;
        var sw = Stopwatch.StartNew();
        var aborted = false;

        // 输出速度限速：优先用 Key 配置（token/秒），未配置则用全局默认（0=不限）
        var throttle = new OutputThrottle(key.MaxOutputTokensPerSec ?? _options.DefaultMaxOutputTokensPerSec);
        var lastThrottleTextLen = 0;                   // 上次节流时已累积的文本长度
        long throttledWaitMs = 0;

        SetContentType(ctx.Response, upstream, "application/x-ndjson");

        // 实时模式第一块就会提交响应头，彼时输出 token 还未知。
        // 因此先写入已知的输入 token 与模式，输出 token 以流式结束后的
        // 计费记录为准（管理后台明细 + /usage 接口可查完整数值）。
        billingHeaders["X-Billing-Tokens-Prompt"] = estimatedPrompt.ToString();
        billingHeaders["X-Billing-Tokens-Completion"] = "streaming";
        billingHeaders["X-Billing-Mode"] = "Realtime";
        if (throttle.Enabled)
            billingHeaders["X-Billing-Throttled-To"] = $"{key.MaxOutputTokensPerSec ?? _options.DefaultMaxOutputTokensPerSec} tok/s";

        await using var stream = await upstream.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        while (true)
        {
            string? line;
            try
            {
                line = await reader.ReadLineAsync(ct);
            }
            catch (OperationCanceledException) { aborted = true; break; }

            if (line is null) break;
            if (line.Length == 0) continue;

            if (firstTokenMs is null) firstTokenMs = sw.ElapsedMilliseconds;

            // 解析这一行：累积正文用于估算，抓取末尾的计数用于校准
            OllamaUsageParser.AccumulateResponseText(line, textSink);
            // ---- 输出速度限速 ----
            // 先按本块新增的文本估算 token，若超出速率额度就等待，
            // 再把这一块下发给客户端。等待发生在"上游已产出、客户端未收到"之间，
            // 因此表现为客户端看到匀速吐字。
            if (throttle.Enabled)
            {
                var currentLen = textSink.Length;
                var deltaChars = currentLen - lastThrottleTextLen;
                if (deltaChars > 0)
                {
                    var deltaTokens = Math.Max(1, _estimator.Estimate(
                        textSink.ToString(lastThrottleTextLen, deltaChars)));
                    throttledWaitMs += await throttle.ThrottleAsync(deltaTokens, ct);
                    lastThrottleTextLen = currentLen;
                }
            }

            // 转发给客户端
            try
            {
                await ctx.Response.WriteAsync(line + "\n", ct);
                await ctx.Response.Body.FlushAsync(ct);
            }
            catch (OperationCanceledException) { aborted = true; break; }
            catch (IOException) { aborted = true; break; }

            // 解析这一行：累积正文用于估算，抓取末尾的计数用于校准
            var u = OllamaUsageParser.Parse(line);
            if (u is not null)
            {
                if (u.HasTokenCounts) lastUsage = OllamaUsageParser.Merge(lastUsage, u);
                if (_options.EnableCalibration && upstreamBuffer.Length < 200_000)
                    upstreamBuffer.Append(line).Append('\n');
            }
        }

        var estimated = _estimator.Estimate(textSink.ToString());

        // 关键决策：如果流尾拿到了 Ollama 的真实计数，就用真实值计费（更准）；
        // 否则退回估算值。这样"实时模式"在网络允许时精度也接近精确模式。
        var promptTokens = lastUsage?.PromptEvalCount ?? estimatedPrompt;
        var completionTokens = lastUsage?.EvalCount ?? estimated;

        return new MeterResult
        {
            PromptTokens = promptTokens,
            CompletionTokens = completionTokens,
            ActualPrompt = lastUsage?.PromptEvalCount,
            ActualCompletion = lastUsage?.EvalCount,
            EstimatedCompletion = estimated,
            FirstTokenMs = firstTokenMs,
            ClientAborted = aborted
        };
    }

    // -------------------------------------------------------------- 工具方法

    private HttpRequestMessage BuildUpstreamRequest(HttpContext ctx, string path, string body)
    {
        var baseUrl = _options.OllamaBaseUrl.TrimEnd('/');
        var query = ctx.Request.QueryString.HasValue ? ctx.Request.QueryString.Value : "";
        var req = new HttpRequestMessage(new HttpMethod(ctx.Request.Method), baseUrl + path + query)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        return req;
    }

    private static void CopyResponseHeaders(HttpResponseMessage from, HttpResponse to)
    {
        foreach (var h in from.Headers)
        {
            if (IsHopByHop(h.Key)) continue;
            to.Headers[h.Key] = h.Value.ToArray();
        }
        foreach (var h in from.Content.Headers)
        {
            if (IsHopByHop(h.Key)) continue;
            to.Headers[h.Key] = h.Value.ToArray();
        }
    }

    private static bool IsHopByHop(string name) =>
        name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Connection", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Keep-Alive", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase);

    private static void SetContentType(HttpResponse res, HttpResponseMessage upstream, string fallback)
    {
        var ct = upstream.Content.Headers.ContentType?.ToString();
        res.ContentType = string.IsNullOrWhiteSpace(ct) ? fallback : ct;
    }

    private async Task RecordAndRespondError(
        HttpContext ctx, ApiKeyAccount key, string model, string path, MeteringMode mode,
        long estimatedPrompt, string error, int status, long elapsedMs, bool streaming)
    {
        if (!ctx.Response.HasStarted)
            await WriteJsonError(ctx, status, "upstream_error", $"上游 Ollama 调用失败：{error}");

        if (!_options.LogUsageRecords) return;

        var record = new UsageRecord
        {
            ApiKeyId = key.Id,
            KeyPrefix = key.KeyPrefix,
            Model = model,
            Endpoint = path,
            MeteringMode = mode,
            PromptTokens = estimatedPrompt,     // 上游失败不计输出 token
            CompletionTokens = 0,
            Cost = 0m,                          // 失败请求不收费
            Currency = _options.Currency,
            Outcome = RequestOutcome.UpstreamError,
            HttpStatus = status,
            DurationMs = (int)elapsedMs,
            ErrorMessage = Truncate(error, 500),
            ClientIp = ApiKeyAuthMiddleware.ClientIp(ctx),
            CreatedAt = DateTime.UtcNow
        };

        await _queue.EnqueueAsync(new UsageEnvelope(key.Id, estimatedPrompt, 0, 0m, record), CancellationToken.None);
    }

    private static async Task SafeWriteAsync(HttpResponse res, string text, CancellationToken ct)
    {
        try
        {
            if (!res.HasStarted) await res.WriteAsync(text, ct);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException) { /* 客户端断开 */ }
    }

    private static async Task WriteJsonError(HttpContext ctx, int status, string code, string message)
    {
        if (ctx.Response.HasStarted) return;
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        await ctx.Response.WriteAsync(JsonSerializer.Serialize(new
        {
            error = new { code, message, type = "billing_gateway_error" }
        }, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
    }

    private static MeteringMode ParseMode(string? s) =>
        Enum.TryParse<MeteringMode>(s, ignoreCase: true, out var m) ? m : MeteringMode.Realtime;

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    /// <summary>从完整响应 JSON 里抽取助手正文，用于估算（仅在拿不到上游计数时兜底）。</summary>
    private static string ExtractResponseText(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Ollama 原生
            if (root.TryGetProperty("message", out var msg) &&
                msg.TryGetProperty("content", out var c))
                return c.GetString() ?? "";

            if (root.TryGetProperty("response", out var r))
                return r.GetString() ?? "";

            // OpenAI 兼容：choices[0].message.content 或 choices[0].text
            if (root.TryGetProperty("choices", out var choices) &&
                choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0)
            {
                var first = choices[0];
                if (first.TryGetProperty("message", out var om) &&
                    om.TryGetProperty("content", out var omc))
                    return omc.GetString() ?? "";
                if (first.TryGetProperty("text", out var tx))
                    return tx.GetString() ?? "";
            }
        }
        catch { /* 忽略 */ }
        return "";
    }
}
