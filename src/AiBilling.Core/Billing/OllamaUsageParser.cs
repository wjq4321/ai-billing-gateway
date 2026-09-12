using AiBilling.Core.Models;

namespace AiBilling.Core.Billing;

/// <summary>
/// 从 Ollama 响应中解析用量元数据。
/// /api/chat 与 /api/generate 在最终块返回：
///   prompt_eval_count  —— 输入 token
///   eval_count         —— 输出 token
/// 非流式响应中同样包含这两个字段。
/// </summary>
public static class OllamaUsageParser
{
    public sealed class OllamaUsage
    {
        public long? PromptEvalCount { get; set; }
        public long? EvalCount { get; set; }
        public long? TotalDurationNs { get; set; }
        public long? EvalDurationNs { get; set; }
        public string? Model { get; set; }
        public bool Done { get; set; }

        /// <summary>是否拿到了可用于计费的完整数据。</summary>
        public bool HasTokenCounts => PromptEvalCount is not null || EvalCount is not null;
    }

    /// <summary>解析一段 JSON（可能是单个对象，也可能是 NDJSON 中的一行）。</summary>
    public static OllamaUsage? Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            return ParseElement(doc.RootElement);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>解析 NDJSON 流：逐行找带 eval_count 的最后一行。</summary>
    public static OllamaUsage? ParseNdjson(string ndjson)
    {
        OllamaUsage? best = null;
        foreach (var line in ndjson.Split('\n'))
        {
            var t = line.Trim();
            if (t.Length == 0) continue;
            var u = Parse(t);
            if (u is null) continue;
            if (u.EvalCount is not null || u.PromptEvalCount is not null) best = Merge(best, u);
            else if (best is null) best = u;
        }
        return best;
    }

    /// <summary>合并两次解析结果（后到的非空值优先）。</summary>
    public static OllamaUsage Merge(OllamaUsage? a, OllamaUsage b)
    {
        if (a is null) return b;
        return new OllamaUsage
        {
            PromptEvalCount = b.PromptEvalCount ?? a.PromptEvalCount,
            EvalCount = b.EvalCount ?? a.EvalCount,
            TotalDurationNs = b.TotalDurationNs ?? a.TotalDurationNs,
            EvalDurationNs = b.EvalDurationNs ?? a.EvalDurationNs,
            Model = b.Model ?? a.Model,
            Done = b.Done || a.Done
        };
    }

    /// <summary>从 /api/show 的响应里读取模型信息（用于自动发现模型）。</summary>
    public static List<string> ParseModelList(string json)
    {
        var list = new List<string>();
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("models", out var models) &&
                models.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var m in models.EnumerateArray())
                {
                    if (m.TryGetProperty("name", out var name) && name.GetString() is { } n)
                        list.Add(n);
                }
            }
        }
        catch { /* 忽略解析错误 */ }
        return list;
    }

    /// <summary>从请求体中提取模型名。</summary>
    public static string? ExtractModel(string requestJson)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(requestJson);
            if (doc.RootElement.TryGetProperty("model", out var m)) return m.GetString();
        }
        catch { /* 忽略 */ }
        return null;
    }

    /// <summary>判断请求是否要求流式输出。</summary>
    public static bool IsStreaming(string requestJson)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(requestJson);
            if (doc.RootElement.TryGetProperty("stream", out var s))
            {
                return s.ValueKind switch
                {
                    System.Text.Json.JsonValueKind.True => true,
                    System.Text.Json.JsonValueKind.False => false,
                    System.Text.Json.JsonValueKind.String => bool.TryParse(s.GetString(), out var b) && b,
                    _ => true
                };
            }
        }
        catch { /* 忽略 */ }
        return true;   // Ollama 默认 stream=true
    }

    /// <summary>从 NDJSON 流中累积助手回复的正文，用于实时估算。</summary>
    public static void AccumulateResponseText(string line, System.Text.StringBuilder sink)
    {
        var u = Parse(line);
        if (u is null) return;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(line);
            var root = doc.RootElement;
            // /api/chat -> message.content ； /api/generate -> response
            if (root.TryGetProperty("message", out var msg) &&
                msg.TryGetProperty("content", out var mc) && mc.GetString() is { } c1)
            {
                sink.Append(c1);
            }
            else if (root.TryGetProperty("response", out var resp) && resp.GetString() is { } c2)
            {
                sink.Append(c2);
            }
            else if (root.TryGetProperty("thinking", out var th) && th.GetString() is { } c3)
            {
                sink.Append(c3);   // 思考型模型的思维链同样消耗 token
            }
            // OpenAI 兼容：choices[0].delta.content（流式）或 choices[0].message.content
            else if (root.TryGetProperty("choices", out var choices) &&
                     choices.ValueKind == System.Text.Json.JsonValueKind.Array &&
                     choices.GetArrayLength() > 0)
            {
                var first = choices[0];
                if (first.TryGetProperty("delta", out var delta) &&
                    delta.TryGetProperty("content", out var dc) && dc.GetString() is { } d1)
                {
                    sink.Append(d1);
                }
                else if (first.TryGetProperty("message", out var om) &&
                         om.TryGetProperty("content", out var omc) && omc.GetString() is { } d2)
                {
                    sink.Append(d2);
                }
                else if (first.TryGetProperty("text", out var tx) && tx.GetString() is { } d3)
                {
                    sink.Append(d3);   // /v1/completions
                }
            }
        }
        catch { /* 忽略单行解析失败 */ }
    }

    private static OllamaUsage ParseElement(System.Text.Json.JsonElement root)
    {
        var u = new OllamaUsage();

        // ---- Ollama 原生字段 ----
        if (root.TryGetProperty("prompt_eval_count", out var pec) && pec.TryGetInt64(out var p)) u.PromptEvalCount = p;
        if (root.TryGetProperty("eval_count", out var ec) && ec.TryGetInt64(out var e)) u.EvalCount = e;
        if (root.TryGetProperty("total_duration", out var td) && td.TryGetInt64(out var t)) u.TotalDurationNs = t;
        if (root.TryGetProperty("eval_duration", out var ed) && ed.TryGetInt64(out var d)) u.EvalDurationNs = d;
        if (root.TryGetProperty("model", out var m)) u.Model = m.GetString();
        if (root.TryGetProperty("done", out var done) && done.ValueKind == System.Text.Json.JsonValueKind.True) u.Done = true;

        // ---- OpenAI 兼容字段（/v1/chat/completions 返回 usage.prompt_tokens 等）----
        if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            if (u.PromptEvalCount is null &&
                usage.TryGetProperty("prompt_tokens", out var pt) && pt.TryGetInt64(out var ptv))
                u.PromptEvalCount = ptv;

            if (u.EvalCount is null &&
                usage.TryGetProperty("completion_tokens", out var ct) && ct.TryGetInt64(out var ctv))
                u.EvalCount = ctv;
        }

        // OpenAI 流式响应的结束标记是 finish_reason 而非 done
        if (!u.Done &&
            root.TryGetProperty("choices", out var choices) &&
            choices.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (var ch in choices.EnumerateArray())
            {
                if (ch.TryGetProperty("finish_reason", out var fr) &&
                    fr.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    u.Done = true;
                    break;
                }
            }
        }

        return u;
    }
}
