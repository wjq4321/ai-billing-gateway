using AiBilling.Core.Billing;

namespace AiBilling.Tests;

/// <summary>Token 估算与 Ollama 响应解析测试。</summary>
[TestClass]
public class EstimationAndParsingTests
{
    // ------------------------------------------------------------ 估算器

    [TestMethod]
    public void Estimate_EmptyIsZero()
    {
        var e = new TokenEstimator(1.7);
        Assert.AreEqual(0, e.Estimate(""));
        Assert.AreEqual(0, e.Estimate(null));
    }

    [TestMethod]
    public void Estimate_ChineseRoughlyOneTokenPerChar()
    {
        var e = new TokenEstimator(1.7);
        var n = e.Estimate("你好世界这是一段中文测试");
        // 13 个汉字 → 应在 13 附近
        Assert.IsTrue(n is >= 10 and <= 20, $"中文估算应接近字数，实际 {n}");
    }

    [TestMethod]
    public void Estimate_EnglishUsesCharsPerToken()
    {
        var e = new TokenEstimator(4.0);
        var n = e.Estimate("hello world this is a test");
        // 26 字符 / 4 ≈ 6.5 → 允许较大区间（空白另计）
        Assert.IsTrue(n is >= 5 and <= 15, $"英文估算偏离过大：{n}");
    }

    [TestMethod]
    public void Estimate_MonotonicWithLength()
    {
        var e = new TokenEstimator(1.7);
        var shortText = e.Estimate("abc");
        var longText = e.Estimate(new string('a', 400));
        Assert.IsTrue(longText > shortText, "更长的文本应估算出更多 token");
    }

    [TestMethod]
    public void EstimateFromRequestJson_ReadsChatMessages()
    {
        var e = new TokenEstimator(1.7);
        var json = """
            {"model":"m","messages":[
              {"role":"user","content":"你好，请介绍一下你自己"},
              {"role":"assistant","content":"好的"},
              {"role":"user","content":"谢谢"}
            ],"stream":false}
            """;
        var n = e.EstimateFromRequestJson(json);
        Assert.IsTrue(n > 10, $"应把 messages 内容都算进去，实际 {n}");
    }

    [TestMethod]
    public void EstimateFromRequestJson_ReadsGeneratePrompt()
    {
        var e = new TokenEstimator(1.7);
        var n = e.EstimateFromRequestJson("""{"model":"m","prompt":"写一首关于秋天的诗"}""");
        Assert.IsTrue(n > 5, $"应识别 prompt 字段，实际 {n}");
    }

    [TestMethod]
    public void EstimateFromRequestJson_SurvivesBadJson()
    {
        var e = new TokenEstimator(1.7);
        var n = e.EstimateFromRequestJson("not json at all");
        Assert.IsTrue(n >= 0, "非法 JSON 不应抛异常");
    }

    [TestMethod]
    public void EstimateMessages_AddsPerMessageOverhead()
    {
        var e = new TokenEstimator(1.7);
        var one = e.EstimateMessages(new[] { ("user", "hi") });
        var three = e.EstimateMessages(new[] { ("user", "hi"), ("user", "hi"), ("user", "hi") });
        Assert.IsTrue(three > one, "每条消息都应带来额外开销");
    }

    // ------------------------------------------------------------ Ollama 解析

    [TestMethod]
    public void Parse_ReadsTokenCounts()
    {
        var json = """
            {"model":"m","done":true,"prompt_eval_count":11,"eval_count":17,
             "total_duration":203508100,"eval_duration":50948000}
            """;
        var u = OllamaUsageParser.Parse(json);
        Assert.IsNotNull(u);
        Assert.AreEqual(11, u!.PromptEvalCount);
        Assert.AreEqual(17, u.EvalCount);
        Assert.IsTrue(u.HasTokenCounts);
    }

    [TestMethod]
    public void Parse_HandlesMissingFields()
    {
        var u = OllamaUsageParser.Parse("""{"model":"m","done":true}""");
        Assert.IsNotNull(u);
        Assert.IsNull(u!.PromptEvalCount);
        Assert.IsFalse(u.HasTokenCounts);
    }

    [TestMethod]
    public void Parse_BadJsonReturnsNull()
    {
        Assert.IsNull(OllamaUsageParser.Parse("{not json"));
        Assert.IsNull(OllamaUsageParser.Parse(""));
    }

    [TestMethod]
    public void ParseNdjson_FindsFinalCounts()
    {
        // Ollama 的流式响应是 NDJSON：每行一个独立的完整 JSON 对象。
        // 计数只出现在最后一个 done:true 的分块里。
        var ndjson = string.Join("\n",
            """{"model":"m","message":{"role":"assistant","content":"你"},"done":false}""",
            """{"model":"m","message":{"role":"assistant","content":"好"},"done":false}""",
            """{"model":"m","message":{"role":"assistant","content":""},"done":true,"prompt_eval_count":25,"eval_count":8}""");

        var u = OllamaUsageParser.ParseNdjson(ndjson);
        Assert.IsNotNull(u);
        Assert.AreEqual(25, u!.PromptEvalCount, "应取到末块的输入计数");
        Assert.AreEqual(8, u.EvalCount, "应取到末块的输出计数");
    }

    [TestMethod]
    public void ParseNdjson_MergesCountsAcrossChunks()
    {
        // 有的实现会在不同分块里分别给出输入/输出计数，合并时不能丢值
        var ndjson = string.Join("\n",
            """{"model":"m","prompt_eval_count":30}""",
            """{"model":"m","eval_count":12,"done":true}""");

        var u = OllamaUsageParser.ParseNdjson(ndjson);
        Assert.IsNotNull(u);
        Assert.AreEqual(30, u!.PromptEvalCount, "先出现的前置计数应被保留");
        Assert.AreEqual(12, u.EvalCount, "后出现的输出计数应被采用");
    }

    [TestMethod]
    public void ParsedNdjson_IgnoresBlankLines()
    {
        var ndjson = "\n\n{\"eval_count\":5,\"prompt_eval_count\":2}\n\n";
        var u = OllamaUsageParser.ParseNdjson(ndjson);
        Assert.IsNotNull(u);
        Assert.AreEqual(5, u!.EvalCount);
    }

    [TestMethod]
    public void ExtractModel_Works()
        => Assert.AreEqual("gemma3:270m", OllamaUsageParser.ExtractModel("""{"model":"gemma3:270m"}"""));

    [TestMethod]
    public void IsStreaming_DefaultsToTrue()
    {
        // Ollama 未指定 stream 时默认流式
        Assert.IsTrue(OllamaUsageParser.IsStreaming("""{"model":"m"}"""));
        Assert.IsTrue(OllamaUsageParser.IsStreaming("""{"model":"m","stream":true}"""));
        Assert.IsFalse(OllamaUsageParser.IsStreaming("""{"model":"m","stream":false}"""));
    }

    [TestMethod]
    public void AccumulateResponseText_CollectsChatContent()
    {
        var sb = new System.Text.StringBuilder();
        OllamaUsageParser.AccumulateResponseText(
            """{"message":{"role":"assistant","content":"你好"}}""", sb);
        OllamaUsageParser.AccumulateResponseText(
            """{"message":{"role":"assistant","content":"世界"}}""", sb);
        Assert.AreEqual("你好世界", sb.ToString());
    }

    [TestMethod]
    public void AccumulateResponseText_CollectsGenerateResponse()
    {
        var sb = new System.Text.StringBuilder();
        OllamaUsageParser.AccumulateResponseText("""{"response":"abc"}""", sb);
        Assert.AreEqual("abc", sb.ToString());
    }

    [TestMethod]
    public void AccumulateResponseText_CountsThinkingTokens()
    {
        // 思考型模型的思维链同样消耗 token，必须计入
        var sb = new System.Text.StringBuilder();
        OllamaUsageParser.AccumulateResponseText("""{"thinking":"推理过程"}""", sb);
        Assert.AreEqual("推理过程", sb.ToString());
    }

    [TestMethod]
    public void ParseModelList_ReadsNames()
    {
        var json = """{"models":[{"name":"a:1"},{"name":"b:2"}]}""";
        var list = OllamaUsageParser.ParseModelList(json);
        CollectionAssert.AreEqual(new[] { "a:1", "b:2" }, list);
    }

    [TestMethod]
    public void Merge_PrefersNewNonNullValues()
    {
        var a = new OllamaUsageParser.OllamaUsage { PromptEvalCount = 5, Model = "x" };
        var b = new OllamaUsageParser.OllamaUsage { EvalCount = 9 };
        var m = OllamaUsageParser.Merge(a, b);
        Assert.AreEqual(5, m.PromptEvalCount, "旧值应保留");
        Assert.AreEqual(9, m.EvalCount, "新值应补上");
        Assert.AreEqual("x", m.Model);
    }

    // ------------------------------------------- OpenAI 兼容（/v1/chat/completions）

    [TestMethod]
    public void Parse_ReadsOpenAiUsageBlock()
    {
        // Ollama 的 /v1/chat/completions 返回 OpenAI 风格的 usage 字段
        var json = """
            {"id":"chatcmpl-1","object":"chat.completion","model":"m",
             "choices":[{"index":0,"message":{"role":"assistant","content":"hi"},
                         "finish_reason":"stop"}],
             "usage":{"prompt_tokens":10,"completion_tokens":12,"total_tokens":22}}
            """;
        var u = OllamaUsageParser.Parse(json);
        Assert.IsNotNull(u);
        Assert.AreEqual(10, u!.PromptEvalCount, "应识别 usage.prompt_tokens");
        Assert.AreEqual(12, u.EvalCount, "应识别 usage.completion_tokens");
        Assert.IsTrue(u.HasTokenCounts);
    }

    [TestMethod]
    public void Parse_OpenAiStreamChunkWithoutUsageHasNoCounts()
    {
        // OpenAI 流式分块通常不含 usage，此时应回退到估算
        var chunk = """
            {"id":"c1","object":"chat.completion.chunk","model":"m",
             "choices":[{"index":0,"delta":{"content":"你"},"finish_reason":null}]}
            """;
        var u = OllamaUsageParser.Parse(chunk);
        Assert.IsNotNull(u);
        Assert.IsFalse(u!.HasTokenCounts, "无 usage 时不应误报计数");
    }

    [TestMethod]
    public void Parse_OpenAiFinishReasonImpliesDone()
    {
        var chunk = """
            {"model":"m","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}
            """;
        var u = OllamaUsageParser.Parse(chunk);
        Assert.IsTrue(u!.Done, "finish_reason 非空应视为已结束");
    }

    [TestMethod]
    public void ParseNdjson_PicksUpOpenAiFinalUsage()
    {
        // Ollama 在 OpenAI 流式的最后一块会带上 usage
        var ndjson = string.Join("\n",
            """{"model":"m","choices":[{"index":0,"delta":{"content":"你"},"finish_reason":null}]}""",
            """{"model":"m","choices":[{"index":0,"delta":{"content":"好"},"finish_reason":"stop"}],"usage":{"prompt_tokens":7,"completion_tokens":3}}""");

        var u = OllamaUsageParser.ParseNdjson(ndjson);
        Assert.IsNotNull(u);
        Assert.AreEqual(7, u!.PromptEvalCount);
        Assert.AreEqual(3, u.EvalCount);
    }

    [TestMethod]
    public void AccumulateResponseText_CollectsOpenAiDelta()
    {
        var sb = new System.Text.StringBuilder();
        OllamaUsageParser.AccumulateResponseText(
            """{"choices":[{"index":0,"delta":{"content":"你"},"finish_reason":null}]}""", sb);
        OllamaUsageParser.AccumulateResponseText(
            """{"choices":[{"index":0,"delta":{"content":"好"},"finish_reason":"stop"}]}""", sb);
        Assert.AreEqual("你好", sb.ToString());
    }

    [TestMethod]
    public void AccumulateResponseText_CollectsOpenAiNonStreamingMessage()
    {
        var sb = new System.Text.StringBuilder();
        OllamaUsageParser.AccumulateResponseText(
            """{"choices":[{"index":0,"message":{"role":"assistant","content":"回答"},"finish_reason":"stop"}]}""",
            sb);
        Assert.AreEqual("回答", sb.ToString());
    }
}
