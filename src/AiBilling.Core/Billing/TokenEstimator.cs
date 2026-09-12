using System.Text;

namespace AiBilling.Core.Billing;

/// <summary>
/// Token 估算器。用于实时流模式（拿不到 Ollama 的 eval_count 时）。
/// 采用启发式：CJK 字符约 1 token/字；ASCII 按 CharsPerToken 折算；
/// 数字、标点、空白各自加权。精度足够支撑计费，并可通过校准报告修正系数。
/// </summary>
public sealed class TokenEstimator
{
    private readonly double _charsPerToken;

    public TokenEstimator(double charsPerToken)
    {
        _charsPerToken = charsPerToken <= 0 ? 1.7 : charsPerToken;
    }

    /// <summary>估算一段文本的 token 数。</summary>
    public long Estimate(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 0;

        double tokens = 0;
        int asciiRun = 0;

        void FlushAscii()
        {
            if (asciiRun > 0)
            {
                // 纯 ASCII 连续段：每 _charsPerToken 个字符算 1 token，至少 1
                tokens += Math.Max(1.0, asciiRun / _charsPerToken);
                asciiRun = 0;
            }
        }

        foreach (var ch in text)
        {
            if (IsCjk(ch))
            {
                FlushAscii();
                tokens += 1.0;          // 汉字/日文/韩文基本 1 字 1 token
            }
            else if (char.IsWhiteSpace(ch))
            {
                FlushAscii();
                tokens += 0.25;
            }
            else
            {
                asciiRun++;
            }
        }
        FlushAscii();

        return (long)Math.Ceiling(tokens);
    }

    /// <summary>
    /// 估算聊天消息列表。每条消息有固定开销（role/分隔符），经验值约 4 token。
    /// </summary>
    public long EstimateMessages(IEnumerable<(string Role, string Content)> messages)
    {
        long total = 0;
        foreach (var (role, content) in messages)
        {
            total += 4;                       // 消息包装开销
            total += Estimate(role);
            total += Estimate(content);
        }
        total += 3;                           // 回复引导开销
        return total;
    }

    /// <summary>从 Ollama 请求 JSON 中提取文本并估算 prompt token。</summary>
    public long EstimateFromRequestJson(string requestJson)
    {
        long total = 0;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(requestJson);
            var root = doc.RootElement;

            // /api/chat 的 messages
            if (root.TryGetProperty("messages", out var msgs) && msgs.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var m in msgs.EnumerateArray())
                {
                    total += 4;
                    if (m.TryGetProperty("role", out var role)) total += Estimate(role.GetString());
                    if (m.TryGetProperty("content", out var content)) total += Estimate(content.GetString());
                }
                total += 3;
                return total;
            }

            // /api/generate 的 prompt
            if (root.TryGetProperty("prompt", out var prompt))
            {
                total += Estimate(prompt.GetString());
                if (root.TryGetProperty("system", out var sys)) total += Estimate(sys.GetString());
                return total;
            }
        }
        catch
        {
            // JSON 解析失败就退回按整体长度估算
        }

        return Estimate(requestJson);
    }

    private static bool IsCjk(char ch) =>
        (ch >= 0x4E00 && ch <= 0x9FFF) ||   // CJK 统一表意
        (ch >= 0x3400 && ch <= 0x4DBF) ||   // 扩展 A
        (ch >= 0xF900 && ch <= 0xFAFF) ||   // 兼容表意
        (ch >= 0x3040 && ch <= 0x30FF) ||   // 日文假名
        (ch >= 0xAC00 && ch <= 0xD7AF) ||   // 韩文
        (ch >= 0x3000 && ch <= 0x303F);     // CJK 标点
}
