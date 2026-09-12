using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AiBilling.Cli;

/// <summary>
/// AI 计费网关命令行管理工具。
/// 通过网关的管理 API 操作，因此需要网关正在运行。
/// </summary>
public static class Program
{
    private const string DefaultBase = "http://127.0.0.1:8080";

    private static string BaseUrl = DefaultBase;
    private static string User = "admin";
    private static string Password = "admin123";
    private static string? _cookie;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static async Task<int> Main(string[] args)
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch { /* 某些终端不支持 */ }

        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintHelp();
            return 0;
        }

        LoadConfigFromAppSettings();
        LoadSavedCredentials();          // 网页改密后保存的凭据优先于配置文件
        args = ParseGlobalFlags(args);

        if (args.Length == 0) { PrintHelp(); return 0; }

        try
        {
            return args[0].ToLowerInvariant() switch
            {
                "login" => await CmdLogin(),
                "keys" => await CmdKeys(args.Skip(1).ToArray()),
                "prices" => await CmdPrices(args.Skip(1).ToArray()),
                "usage" => await CmdUsage(args.Skip(1).ToArray()),
                "stats" => await CmdStats(),
                "calib" => await CmdCalibration(),
                "health" => await CmdHealth(),
                "models" => await CmdModels(),
                "billing" => await CmdBilling(args.Skip(1).ToArray()),
                "test" => await CmdTest(),
                _ => Unknown(args[0])
            };
        }
        catch (HttpRequestException ex)
        {
            Console.Error.WriteLine($"✗ 请求失败：{ex.Message}");
            return 2;
        }
    }

    // =============================================================== 命令实现

    private static async Task<int> CmdLogin()
    {
        var r = await Post("/api/admin/login", new { username = User, password = Password }, auth: false);
        if (r is null) return 1;
        Console.WriteLine($"✓ 登录成功：{User}");
        return 0;
    }

    private static async Task<int> CmdKeys(string[] a)
    {
        await EnsureAuth();
        var sub = a.Length > 0 ? a[0].ToLowerInvariant() : "list";

        switch (sub)
        {
            case "list":
                return await KeysList();

            case "create":
                return await KeysCreate(a);

            case "show":
                return await KeysShow(a);

            case "update":
            case "set":
                return await KeysUpdate(a);

            case "enable":
            case "disable":
            {
                var id = LongArg(a, 1, "id");
                await Send(HttpMethod.Put, $"/api/admin/keys/{id}",
                    new { status = sub == "enable" ? "Active" : "Disabled" });
                Console.WriteLine($"✓ 已{(sub == "enable" ? "启用" : "禁用")} Key #{id}");
                return 0;
            }

            case "reset":
            {
                var id = LongArg(a, 1, "id");
                await Post($"/api/admin/keys/{id}/reset-usage", new { });
                Console.WriteLine($"✓ 已清零 Key #{id} 的累计用量（明细记录保留）");
                return 0;
            }

            case "delete":
            {
                var id = LongArg(a, 1, "id");
                Console.Write($"确认删除 Key #{id}？使用该 Key 的客户端将立即失效 (y/N) ");
                if (Console.ReadLine()?.Trim().ToLowerInvariant() != "y") { Console.WriteLine("已取消。"); return 0; }
                await Send(HttpMethod.Delete, $"/api/admin/keys/{id}", null);
                Console.WriteLine($"✓ 已删除 Key #{id}");
                return 0;
            }

            default:
                Console.Error.WriteLine($"未知子命令：keys {sub}");
                Console.Error.WriteLine("可用：list | create | show | update | enable | disable | reset | delete");
                return 1;
        }
    }

    private static async Task<int> KeysList()
    {
        var keys = await Get<List<KeyDto>>("/api/admin/keys") ?? new();
        if (keys.Count == 0)
        {
            Console.WriteLine("（暂无 API Key，用 `keys create --name 名称` 新建）");
            return 0;
        }

        Console.WriteLine($"{"ID",-4} {"名称",-18} {"Key",-26} {"状态",-8} {"已用Token",14} {"Token配额",14} {"已用金额",10} {"金额上限",10}");
        Console.WriteLine(new string('-', 116));
        foreach (var k in keys)
        {
            Console.WriteLine($"{k.Id,-4} {Trunc(k.Name, 17),-18} {k.KeyPrefix,-26} {StatusCn(k.Status),-8} " +
                              $"{k.Used.TotalTokens,14:N0} {Fmt(k.Quota.TotalTokens),14} " +
                              $"{k.Used.Cost,10:F4} {Fmt(k.Quota.TotalCost),10}");
        }
        return 0;
    }

    private static async Task<int> KeysCreate(string[] a)
    {
        var name = Arg(a, "--name") ?? "未命名";
        var body = new
        {
            name,
            quotaTotalTokens = LongOrNull(Arg(a, "--tokens")),
            quotaTotalCost = DecOrNull(Arg(a, "--cost")),
            quotaTotalRequests = LongOrNull(Arg(a, "--requests")),
            rateLimitRpm = IntOrNull(Arg(a, "--rpm")),
            maxOutputTokensPerSec = DecOrNull(Arg(a, "--tok-per-sec")),
            quotaTokensPerMinute = LongOrNull(Arg(a, "--tok-per-min")),
            quotaTokensPerDay = LongOrNull(Arg(a, "--tok-per-day")),
            allowedModels = (Arg(a, "--models") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
                               .Select(s => s.Trim()).Where(s => s.Length > 0).ToList(),
            overrideInputPricePer1K = DecOrNull(Arg(a, "--in-price")),
            overrideOutputPricePer1K = DecOrNull(Arg(a, "--out-price")),
            overridePricePerRequest = DecOrNull(Arg(a, "--req-price")),
            expiresAt = DateTime.TryParse(Arg(a, "--expires"), out var ex) ? ex : (DateTime?)null
        };

        var r = await Post("/api/admin/keys", body);
        if (r is null) return 1;
        var created = r.Value.Deserialize<CreateResult>(Json)!;

        Console.WriteLine();
        Console.WriteLine("  ╔═══════════════════════════════════════════════════════════╗");
        Console.WriteLine("  ║  API Key 创建成功 —— 请立即保存，之后无法再次查看明文。  ║");
        Console.WriteLine("  ╚═══════════════════════════════════════════════════════════╝");
        Console.WriteLine();
        Console.WriteLine($"   名称 : {created.Key.Name}");
        Console.WriteLine($"   Key  : {created.PlainKey}");
        Console.WriteLine();
        return 0;
    }

    private static async Task<int> KeysShow(string[] a)
    {
        var id = LongArg(a, 1, "id");
        var keys = await Get<List<KeyDto>>("/api/admin/keys") ?? new();
        var k = keys.FirstOrDefault(x => x.Id == id);
        if (k is null) { Console.Error.WriteLine($"✗ 未找到 Key #{id}"); return 1; }
        PrintKeyDetail(k);
        return 0;
    }

    /// <summary>
    /// 修改 Key。后端 PUT 是整体覆盖语义，所以这里先读原值，
    /// 只覆盖显式传入的字段，避免"只改额度"却把白名单、专属价清空。
    /// </summary>
    private static async Task<int> KeysUpdate(string[] a)
    {
        var id = LongArg(a, 1, "id");

        var keys = await Get<List<KeyDto>>("/api/admin/keys") ?? new();
        var cur = keys.FirstOrDefault(x => x.Id == id);
        if (cur is null) { Console.Error.WriteLine($"✗ 未找到 Key #{id}"); return 1; }

        long? newTokens;
        if (Arg(a, "--tokens") is { } t1) newTokens = LongOrNull(t1);
        else if (Arg(a, "--clear-tokens") is not null) newTokens = null;
        else newTokens = cur.Quota.TotalTokens;

        decimal? newCost;
        if (Arg(a, "--cost") is { } c1) newCost = DecOrNull(c1);
        else if (Arg(a, "--clear-cost") is not null) newCost = null;
        else newCost = cur.Quota.TotalCost;

        long? newReqs;
        if (Arg(a, "--requests") is { } r1) newReqs = LongOrNull(r1);
        else if (Arg(a, "--clear-requests") is not null) newReqs = null;
        else newReqs = cur.Quota.TotalRequests;

        int? newRpm;
        if (Arg(a, "--rpm") is { } m1) newRpm = IntOrNull(m1);
        else if (Arg(a, "--clear-rpm") is not null) newRpm = null;
        else newRpm = cur.Quota.RateLimitRpm;

        // 输出速度上限（token/秒）
        decimal? newMaxOut;
        if (Arg(a, "--tok-per-sec") is { } ms) newMaxOut = DecOrNull(ms);
        else if (Arg(a, "--clear-tok-per-sec") is not null) newMaxOut = null;
        else newMaxOut = cur.Quota.MaxOutputTokensPerSec;

        // 每分钟 token 消耗上限
        long? newTpm;
        if (Arg(a, "--tok-per-min") is { } tm) newTpm = LongOrNull(tm);
        else if (Arg(a, "--clear-tok-per-min") is not null) newTpm = null;
        else newTpm = cur.Quota.TokensPerMinute;

        // 每天 token 消耗上限
        long? newTpd;
        if (Arg(a, "--tok-per-day") is { } td) newTpd = LongOrNull(td);
        else if (Arg(a, "--clear-tok-per-day") is not null) newTpd = null;
        else newTpd = cur.Quota.TokensPerDay;

        decimal? newIn, newOut, newPerReq;
        if (Arg(a, "--clear-prices") is not null) { newIn = newOut = newPerReq = null; }
        else
        {
            newIn = Arg(a, "--in-price") is { } ip ? DecOrNull(ip) : cur.Overrides.InputPricePer1K;
            newOut = Arg(a, "--out-price") is { } op ? DecOrNull(op) : cur.Overrides.OutputPricePer1K;
            newPerReq = Arg(a, "--req-price") is { } rp ? DecOrNull(rp) : cur.Overrides.PricePerRequest;
        }

        DateTime? newExpires;
        if (Arg(a, "--expires") is { } ex)
            newExpires = DateTime.TryParse(ex, out var exv) ? exv : null;
        else if (Arg(a, "--clear-expires") is not null) newExpires = null;
        else newExpires = cur.ExpiresAt;

        var body = new
        {
            name = Arg(a, "--name") ?? cur.Name,
            status = Arg(a, "--status") ?? cur.Status,
            allowedModels = Arg(a, "--models") is { } mm
                ? mm.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).ToList()
                : cur.AllowedModels,
            quotaTotalTokens = newTokens,
            quotaTotalCost = newCost,
            quotaTotalRequests = newReqs,
            rateLimitRpm = newRpm,
            maxOutputTokensPerSec = newMaxOut,
            quotaTokensPerMinute = newTpm,
            quotaTokensPerDay = newTpd,
            overrideInputPricePer1K = newIn,
            overrideOutputPricePer1K = newOut,
            overridePricePerRequest = newPerReq,
            expiresAt = newExpires
        };

        await Send(HttpMethod.Put, $"/api/admin/keys/{id}", body);

        var after = (await Get<List<KeyDto>>("/api/admin/keys"))?.FirstOrDefault(x => x.Id == id);
        Console.WriteLine($"✓ 已更新 Key #{id}");
        if (after is not null) PrintKeyDetail(after);
        return 0;
    }

    private static async Task<int> CmdPrices(string[] a)
    {
        await EnsureAuth();
        var sub = a.Length > 0 ? a[0].ToLowerInvariant() : "list";

        switch (sub)
        {
            case "list":
            {
                var ps = await Get<List<PriceDto>>("/api/admin/prices") ?? new();
                if (ps.Count == 0)
                {
                    Console.WriteLine("（暂无定价规则；未匹配的模型按兜底价处理，默认免费）");
                    return 0;
                }
                Console.WriteLine($"{"模型",-42} {"模式",-10} {"输入/千",12} {"输出/千",12} {"单次",12} {"启用",6}");
                Console.WriteLine(new string('-', 100));
                foreach (var p in ps)
                    Console.WriteLine($"{Trunc(p.Model, 41),-42} {(p.Mode == 2 ? "按次" : "按Token"),-10} " +
                                      $"{p.InputPricePer1K,12:F6} {p.OutputPricePer1K,12:F6} {p.PricePerRequest,12:F6} " +
                                      $"{(p.Enabled ? "是" : "否"),6}");
                return 0;
            }

            case "set":
            {
                var model = Arg(a, "--model") ?? (a.Length > 1 && !a[1].StartsWith("--") ? a[1] : null);
                if (string.IsNullOrWhiteSpace(model))
                {
                    Console.Error.WriteLine("✗ 需要 --model <模型名>（支持通配，如 \"*\" 或 \"qwen2.5:*\"）");
                    return 1;
                }

                var perReq = DecOrNull(Arg(a, "--req-price"));
                var body = new
                {
                    model,
                    mode = perReq is not null ? "PerRequest" : "PerToken",
                    inputPricePer1K = DecOrNull(Arg(a, "--in")) ?? 0m,
                    outputPricePer1K = DecOrNull(Arg(a, "--out")) ?? 0m,
                    pricePerRequest = perReq ?? 0m,
                    enabled = true
                };
                await Post("/api/admin/prices", body);
                Console.WriteLine($"✓ 已设置 {model} 的定价");
                return 0;
            }

            case "delete":
            {
                var model = a.Length > 1 ? a[1] : null;
                if (string.IsNullOrWhiteSpace(model)) { Console.Error.WriteLine("✗ 缺少模型名"); return 1; }
                await Send(HttpMethod.Delete, $"/api/admin/prices/{Uri.EscapeDataString(model)}", null);
                Console.WriteLine($"✓ 已删除 {model} 的定价");
                return 0;
            }

            default:
                Console.Error.WriteLine("可用：prices list | prices set --model <名> [--in x] [--out y] [--req-price z] | prices delete <名>");
                return 1;
        }
    }

    private static async Task<int> CmdUsage(string[] a)
    {
        await EnsureAuth();
        var keyId = Arg(a, "--key");
        var limit = Arg(a, "--limit") ?? "50";
        var q = $"?limit={limit}" + (keyId is not null ? $"&keyId={keyId}" : "");
        var rows = await Get<List<UsageDto>>("/api/admin/usage" + q) ?? new();

        if (rows.Count == 0) { Console.WriteLine("（暂无用量记录）"); return 0; }

        Console.WriteLine($"{"时间",-18} {"Key",-20} {"模型",-26} {"输入",7} {"输出",7} {"费用",10} {"结果",-12} {"耗时",8}");
        Console.WriteLine(new string('-', 118));
        foreach (var r in rows)
        {
            var t = r.CreatedAt.ToLocalTime().ToString("MM-dd HH:mm:ss");
            Console.WriteLine($"{t,-18} {Trunc(r.KeyPrefix, 19),-20} {Trunc(r.Model, 25),-26} " +
                              $"{r.PromptTokens,7:N0} {r.CompletionTokens,7:N0} {r.Cost,10:F4} " +
                              $"{Trunc(OutcomeCn(r.Outcome), 11),-12} {r.DurationMs + "ms",8}");
        }
        Console.WriteLine();
        Console.WriteLine($"共 {rows.Count} 条，合计 {rows.Sum(r => (long)r.TotalTokens):N0} token / {rows.Sum(r => r.Cost):F4}");
        return 0;
    }

    private static async Task<int> CmdStats()
    {
        await EnsureAuth();
        var ov = await Get<OverviewDto>("/api/admin/overview");
        if (ov is null) return 1;

        Console.WriteLine();
        Console.WriteLine("  ┌─────────────────────────────────────────────────┐");
        Console.WriteLine("  │            AI 计费网关 · 运行概览               │");
        Console.WriteLine("  ├─────────────────────────────────────────────────┤");
        Console.WriteLine($"  │  API Key 数    : {ov.Keys.Total,30}  │");
        Console.WriteLine($"  │  启用 / 禁用   : {ov.Keys.Active + " / " + ov.Keys.Disabled,30}  │");
        Console.WriteLine($"  │  累计 Token    : {ov.Totals.TotalTokens,30:N0}  │");
        Console.WriteLine($"  │    其中输入    : {ov.Totals.PromptTokens,30:N0}  │");
        Console.WriteLine($"  │    其中输出    : {ov.Totals.CompletionTokens,30:N0}  │");
        Console.WriteLine($"  │  累计请求      : {ov.Totals.Requests,30:N0}  │");
        Console.WriteLine($"  │  累计费用      : {ov.Totals.Cost,30:F4}  │");
        Console.WriteLine("  ├─────────────────────────────────────────────────┤");
        Console.WriteLine($"  │  今日 Token    : {ov.Today.TotalTokens,30:N0}  │");
        Console.WriteLine($"  │  今日请求      : {ov.Today.Requests,30:N0}  │");
        Console.WriteLine($"  │  今日费用      : {ov.Today.Cost,30:F4}  │");
        Console.WriteLine("  ├─────────────────────────────────────────────────┤");
        Console.WriteLine($"  │  计量模式      : {ov.Config.MeteringMode,30}  │");
        Console.WriteLine($"  │  币种          : {ov.Config.Currency,30}  │");
        Console.WriteLine("  └─────────────────────────────────────────────────┘");
        Console.WriteLine();

        var daily = await Get<List<DailyDto>>("/api/admin/usage/daily?days=7") ?? new();
        if (daily.Count > 0)
        {
            var max = daily.Max(x => x.PromptTokens + x.CompletionTokens);
            if (max <= 0) max = 1;
            Console.WriteLine("  近 7 天消耗：");
            foreach (var d in daily)
            {
                var tot = d.PromptTokens + d.CompletionTokens;
                var bar = new string('█', (int)Math.Round(tot / (double)max * 40));
                Console.WriteLine($"    {d.Day:MM-dd}  {tot,12:N0} token  {d.Cost,10:F4}  {bar}");
            }
            Console.WriteLine();
        }
        return 0;
    }

    private static async Task<int> CmdCalibration()
    {
        await EnsureAuth();
        var c = await Get<CalibDto>("/api/admin/calibration?days=7");
        if (c is null) return 1;

        Console.WriteLine();
        Console.WriteLine($"  计费精度校准（近 {c.Days} 天）");
        Console.WriteLine("  ─────────────────────────────────────────────");
        Console.WriteLine($"    有效样本数      : {c.SampleCount:N0}");
        Console.WriteLine($"    平均估算输出    : {c.AvgEstimated:N2} token/次");
        Console.WriteLine($"    平均实测输出    : {c.AvgActual:N2} token/次");
        Console.WriteLine($"    实测 / 估算     : {c.Ratio:F4}   （理想值 1.0）");
        Console.WriteLine();
        Console.WriteLine($"    {c.Hint}");
        if (c.SampleCount > 0)
            Console.WriteLine($"    建议 CharsPerToken 调整系数：× {c.SuggestedCharsPerTokenFactor:F4}");
        Console.WriteLine();
        return 0;
    }

    private static async Task<int> CmdHealth()
    {
        var h = await Get<HealthDto>("/health", auth: false);
        if (h is null) return 1;
        Console.WriteLine($"网关状态   : {h.Status}");
        Console.WriteLine($"数据库     : {(h.Database == "up" ? "✓ 已连接" : "✗ 断开")}");
        Console.WriteLine($"Ollama 上游: {h.Upstream}");
        Console.WriteLine($"计量模式   : {h.MeteringMode}");
        return h.Status == "healthy" ? 0 : 1;
    }

    private static async Task<int> CmdModels()
    {
        await EnsureAuth();
        var r = await Get<ModelsDto>("/api/admin/ollama/models");
        if (r is null) return 1;
        if (!r.Ok) { Console.Error.WriteLine($"✗ 无法获取模型列表：{r.Error}"); return 1; }

        var prices = await Get<List<PriceDto>>("/api/admin/prices") ?? new();
        Console.WriteLine($"{"模型",-54} {"当前定价",-36}");
        Console.WriteLine(new string('-', 92));
        foreach (var m in r.Models)
        {
            var p = MatchPrice(prices, m);
            var priceText = p is null
                ? "（无匹配 → 兜底免费）"
                : p.Mode == 2 ? $"按次 {p.PricePerRequest}"
                : $"in {p.InputPricePer1K}/1k  out {p.OutputPricePer1K}/1k";
            Console.WriteLine($"{Trunc(m, 53),-54} {priceText,-36}");
        }
        Console.WriteLine();
        Console.WriteLine("配置单价：aibilling prices set --model <模型名> --in <输入价> --out <输出价>");
        return 0;
    }

    /// <summary>发一次真实请求，显示计费结果。</summary>
    private static async Task<int> CmdBilling(string[] a)
    {
        var sub = a.Length > 0 ? a[0].ToLowerInvariant() : "help";
        if (sub != "estimate")
        {
            Console.Error.WriteLine("用法：billing estimate --key <Key> --model <模型> [--prompt <文本>]");
            return 1;
        }

        var key = Arg(a, "--key");
        var model = Arg(a, "--model");
        var prompt = Arg(a, "--prompt") ?? "你好";
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(model))
        {
            Console.Error.WriteLine("✗ 需要 --key 与 --model");
            return 1;
        }

        Console.WriteLine($"发送测试请求 → {BaseUrl}/api/chat  (模型 {model})");
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        using var req = new HttpRequestMessage(HttpMethod.Post, BaseUrl + "/api/chat");
        req.Headers.Add("Authorization", "Bearer " + key);
        req.Content = JsonContent.Create(new
        {
            model,
            messages = new[] { new { role = "user", content = prompt } },
            stream = false
        });

        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var resp = await http.SendAsync(req);
        var text = await resp.Content.ReadAsStringAsync();
        sw.Stop();

        Console.WriteLine($"  HTTP {(int)resp.StatusCode}   耗时 {sw.ElapsedMilliseconds}ms");
        foreach (var h in resp.Headers.Where(h => h.Key.StartsWith("X-Billing", StringComparison.OrdinalIgnoreCase)))
            Console.WriteLine($"  {h.Key}: {string.Join(",", h.Value)}");

        if (!resp.IsSuccessStatusCode)
        {
            Console.WriteLine();
            Console.WriteLine(text);
            return 1;
        }

        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.TryGetProperty("message", out var msg) &&
                msg.TryGetProperty("content", out var content))
            {
                Console.WriteLine();
                Console.WriteLine("模型回复：" + Trunc(content.GetString() ?? "", 300));
            }
        }
        catch { /* 忽略解析问题 */ }

        Console.WriteLine();
        Console.WriteLine("提示：完整计费明细见 `aibilling usage --limit 5` 或管理后台「用量明细」。");
        return 0;
    }

    /// <summary>部署自检。</summary>
    private static async Task<int> CmdTest()
    {
        Console.WriteLine();
        Console.WriteLine("  部署自检");
        Console.WriteLine("  ══════════════════════════════════════════════════");
        var ok = true;

        HealthDto? h;
        try
        {
            h = await Get<HealthDto>("/health", auth: false);
            Console.WriteLine($"  [✓] 网关可达           {BaseUrl}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  [✗] 网关不可达         {ex.Message}");
            Console.WriteLine("      请先启动网关：start-gateway.cmd");
            return 2;
        }

        if (h?.Database == "up") Console.WriteLine("  [✓] MySQL 已连接");
        else { Console.WriteLine($"  [✗] MySQL 连接失败（{h?.Database}）"); ok = false; }

        try
        {
            await EnsureAuth();
            Console.WriteLine("  [✓] 管理端登录成功");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  [✗] 管理端登录失败     {ex.Message}");
            ok = false;
        }

        ModelsDto? m = null;
        try
        {
            m = await Get<ModelsDto>("/api/admin/ollama/models");
            if (m?.Ok == true) Console.WriteLine($"  [✓] Ollama 在线        发现 {m.Models.Count} 个模型");
            else { Console.WriteLine($"  [✗] Ollama 不可用      {m?.Error}"); ok = false; }
        }
        catch (Exception ex) { Console.WriteLine($"  [✗] Ollama 检查失败    {ex.Message}"); ok = false; }

        using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) })
        {
            try
            {
                var noKey = await http.PostAsync(BaseUrl + "/api/chat",
                    JsonContent.Create(new { model = "x", messages = Array.Empty<object>(), stream = false }));
                if ((int)noKey.StatusCode == 401) Console.WriteLine("  [✓] 无 Key 请求被拒绝 (401)");
                else { Console.WriteLine($"  [✗] 无 Key 请求返回 {(int)noKey.StatusCode}，应为 401！"); ok = false; }

                using var badReq = new HttpRequestMessage(HttpMethod.Post, BaseUrl + "/api/chat");
                badReq.Headers.Add("Authorization", "Bearer sk-invalid-key-for-self-test");
                badReq.Content = JsonContent.Create(new { model = "x", messages = Array.Empty<object>(), stream = false });
                var bad = await http.SendAsync(badReq);
                if ((int)bad.StatusCode == 401) Console.WriteLine("  [✓] 无效 Key 被拒绝 (401)");
                else { Console.WriteLine($"  [✗] 无效 Key 返回 {(int)bad.StatusCode}，应为 401！"); ok = false; }
            }
            catch (Exception ex) { Console.WriteLine($"  [✗] 认证检查失败       {ex.Message}"); ok = false; }
        }

        var keys = await Get<List<KeyDto>>("/api/admin/keys") ?? new();
        var prices = await Get<List<PriceDto>>("/api/admin/prices") ?? new();
        Console.WriteLine($"  [·] 已配置 Key         {keys.Count} 个（启用 {keys.Count(k => k.Status == "Active")}）");
        Console.WriteLine($"  [·] 已配置定价规则     {prices.Count} 条");
        if (keys.Count == 0)
            Console.WriteLine("      提示：用 `aibilling keys create --name 测试 --tokens 100000` 建一个 Key");

        Console.WriteLine("  ══════════════════════════════════════════════════");
        Console.WriteLine(ok ? "  自检通过 ✓" : "  存在问题 ✗");
        Console.WriteLine();
        return ok ? 0 : 1;
    }

    // =============================================================== HTTP

    private static async Task EnsureAuth()
    {
        if (_cookie is not null) return;

        var r = await Post("/api/admin/login", new { username = User, password = Password }, auth: false);
        if (r is null)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("  提示：管理端账号可能已在网页后台改过。");
            Console.Error.WriteLine("  网页改密后，appsettings.json 里的旧账号会失效，CLI 需要手动指定新凭据：");
            Console.Error.WriteLine();
            Console.Error.WriteLine("     aibilling <命令> --user <新用户名> --password <新密码>");
            Console.Error.WriteLine();
            Console.Error.WriteLine("  指定一次后会自动记住，之后无需重复输入。");
            Console.Error.WriteLine();
            throw new HttpRequestException("管理端登录失败");
        }

        // 登录成功就记住凭据：用户在网页改密后，只需在 CLI 里指定一次新密码
        SaveCredentials(User, Password);
    }

    private static HttpClient NewClient(bool auth)
    {
        var c = new HttpClient { BaseAddress = new Uri(BaseUrl), Timeout = TimeSpan.FromSeconds(90) };
        if (auth && _cookie is not null) c.DefaultRequestHeaders.Add("Cookie", _cookie);
        return c;
    }

    private static async Task<JsonElement?> Post(string path, object body, bool auth = true)
    {
        using var c = NewClient(auth);
        var resp = await c.PostAsJsonAsync(path, body, Json);
        CaptureCookie(resp);
        var txt = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
        {
            Console.Error.WriteLine($"✗ HTTP {(int)resp.StatusCode}：{ExtractError(txt)}");
            return null;
        }
        if (string.IsNullOrWhiteSpace(txt)) return null;
        using var doc = JsonDocument.Parse(txt);
        return doc.RootElement.Clone();
    }

    private static async Task Send(HttpMethod method, string path, object? body)
    {
        using var c = NewClient(true);
        var req = new HttpRequestMessage(method, path);
        if (body is not null) req.Content = JsonContent.Create(body, options: Json);
        var resp = await c.SendAsync(req);
        var txt = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"HTTP {(int)resp.StatusCode}：{ExtractError(txt)}");
    }

    private static async Task<T?> Get<T>(string path, bool auth = true)
    {
        using var c = NewClient(auth);
        var resp = await c.GetAsync(path);
        var txt = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
        {
            if ((int)resp.StatusCode == 401) throw new HttpRequestException("管理端未登录或会话已过期");
            throw new HttpRequestException($"HTTP {(int)resp.StatusCode}：{ExtractError(txt)}");
        }
        return txt.Length == 0 ? default : JsonSerializer.Deserialize<T>(txt, Json);
    }

    private static void CaptureCookie(HttpResponseMessage resp)
    {
        if (resp.Headers.TryGetValues("Set-Cookie", out var vals))
        {
            var cookie = vals.FirstOrDefault(v => v.StartsWith("aibilling_admin=", StringComparison.Ordinal));
            if (cookie is not null) _cookie = cookie.Split(';')[0];
        }
    }

    private static string ExtractError(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("error", out var e) &&
                e.TryGetProperty("message", out var m)) return m.GetString() ?? json;
        }
        catch { /* 非 JSON */ }
        return json.Length > 300 ? json[..300] : json;
    }

    // =============================================================== 凭据持久化

    /// <summary>
    /// 从仓库里的 Gateway appsettings.json 读取网关地址与初始管理账号。
    /// </summary>
    private static void LoadConfigFromAppSettings()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "AiBilling.Gateway", "appsettings.json");
            if (File.Exists(candidate))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(candidate));
                    if (doc.RootElement.TryGetProperty("Billing", out var b))
                    {
                        if (b.TryGetProperty("AdminUser", out var u) && u.GetString() is { Length: > 0 } us) User = us;
                        if (b.TryGetProperty("AdminPassword", out var p) && p.GetString() is { Length: > 0 } ps) Password = ps;
                    }
                    if (doc.RootElement.TryGetProperty("Kestrel", out var k) &&
                        k.TryGetProperty("Endpoints", out var ep) &&
                        ep.TryGetProperty("Http", out var http) &&
                        http.TryGetProperty("Url", out var url) &&
                        url.GetString() is { Length: > 0 } uu)
                    {
                        BaseUrl = uu.Replace("0.0.0.0", "127.0.0.1").Replace("*", "127.0.0.1");
                    }
                }
                catch { /* 读取失败保留默认值 */ }
                return;
            }
            dir = dir.Parent;
        }
    }

    /// <summary>读取上次保存的凭据（若调用方没有显式传 --user/--password）。</summary>
    private static void LoadSavedCredentials()
    {
        foreach (var p in CandidateCredentialPaths())
        {
            try
            {
                if (!File.Exists(p)) continue;
                using var doc = JsonDocument.Parse(File.ReadAllText(p));
                if (doc.RootElement.TryGetProperty("username", out var u) && u.GetString() is { Length: > 0 } us) User = us;
                if (doc.RootElement.TryGetProperty("password", out var pw) && pw.GetString() is { Length: > 0 } ps) Password = ps;
                return;
            }
            catch { /* 换下一个候选路径 */ }
        }
    }

    private static void SaveCredentials(string user, string password)
    {
        var json = JsonSerializer.Serialize(new { username = user, password },
            new JsonSerializerOptions { WriteIndented = true });

        foreach (var p in CandidateCredentialPaths())
        {
            try
            {
                File.WriteAllText(p, json);
                return;
            }
            catch { /* 换下一个候选路径 */ }
        }
    }

    /// <summary>凭据候选位置：CLI 程序目录优先（一定可写），其次用户目录。</summary>
    private static IEnumerable<string> CandidateCredentialPaths()
    {
        yield return Path.Combine(AppContext.BaseDirectory, "aibilling-credentials.json");

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home))
            yield return Path.Combine(home, ".aibilling-credentials.json");
    }

    private static string[] ParseGlobalFlags(string[] args)
    {
        var list = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--url":
                    if (i + 1 < args.Length) BaseUrl = args[++i];
                    break;
                case "--user":
                    if (i + 1 < args.Length) User = args[++i];
                    break;
                case "--password":
                    if (i + 1 < args.Length) Password = args[++i];
                    break;
                default:
                    list.Add(args[i]);
                    break;
            }
        }
        return list.ToArray();
    }

    // =============================================================== 输出辅助

    private static void PrintKeyDetail(KeyDto k)
    {
        Console.WriteLine();
        Console.WriteLine($"  Key #{k.Id}  {k.Name}");
        Console.WriteLine("  ──────────────────────────────────────────────────");
        Console.WriteLine($"    Key 标识   : {k.KeyPrefix}");
        Console.WriteLine($"    状态       : {StatusCn(k.Status)}");
        Console.WriteLine($"    已用 Token : {k.Used.TotalTokens:N0}（输入 {k.Used.PromptTokens:N0} / 输出 {k.Used.CompletionTokens:N0}）");
        Console.WriteLine($"    Token 配额 : {Fmt(k.Quota.TotalTokens)}    剩余 {Fmt(k.Remaining.Tokens)}");
        Console.WriteLine($"    已用金额   : {k.Used.Cost:F4}    上限 {Fmt(k.Quota.TotalCost)}    剩余 {Fmt(k.Remaining.Cost)}");
        Console.WriteLine($"    请求数     : {k.Used.Requests:N0}    上限 {Fmt(k.Quota.TotalRequests)}");
        Console.WriteLine($"    限速       : {(k.Quota.RateLimitRpm is null ? "不限" : k.Quota.RateLimitRpm + " 次/分钟")}");
        Console.WriteLine($"    输出速度   : {(k.Quota.MaxOutputTokensPerSec is null ? "不限" : k.Quota.MaxOutputTokensPerSec + " token/秒")}");
        Console.WriteLine($"    分钟 token : {(k.Quota.TokensPerMinute is null ? "不限" : k.Quota.TokensPerMinute.Value.ToString("N0") + " /分钟")}");
        Console.WriteLine($"    每日 token : {(k.Quota.TokensPerDay is null ? "不限" : k.Quota.TokensPerDay.Value.ToString("N0") + " /天")}");
        Console.WriteLine($"    允许模型   : {(k.AllowedModels.Count == 0 ? "全部" : string.Join(", ", k.AllowedModels))}");
        Console.WriteLine($"    单独定价   : 输入 {Fmt(k.Overrides.InputPricePer1K)} / 输出 {Fmt(k.Overrides.OutputPricePer1K)} / 单次 {Fmt(k.Overrides.PricePerRequest)}");
        Console.WriteLine($"    过期时间   : {(k.ExpiresAt is null ? "永不过期" : k.ExpiresAt.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm"))}");
        Console.WriteLine($"    创建时间   : {k.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm}");
        Console.WriteLine($"    最后使用   : {(k.LastUsedAt is null ? "从未使用" : k.LastUsedAt.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm"))}");
        Console.WriteLine();
    }

    private static PriceDto? MatchPrice(List<PriceDto> prices, string model)
    {
        var exact = prices.FirstOrDefault(p => !p.IsWildcard &&
            string.Equals(p.Model, model, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact;

        return prices.Where(p => p.IsWildcard && WildMatch(p.Model, model))
                     .OrderByDescending(p => p.Model.Length)
                     .FirstOrDefault()
               ?? prices.FirstOrDefault(p => p.Model == "*");
    }

    private static bool WildMatch(string pattern, string value)
    {
        if (pattern == "*") return true;
        if (pattern.EndsWith(":*")) return value.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase);

        var parts = pattern.Split('*');
        int pos = 0;
        for (int i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            if (part.Length == 0) continue;
            var found = value.IndexOf(part, pos, StringComparison.OrdinalIgnoreCase);
            if (found < 0) return false;
            if (i == 0 && found != 0) return false;
            pos = found + part.Length;
        }
        return parts[^1].Length == 0 || value.EndsWith(parts[^1], StringComparison.OrdinalIgnoreCase);
    }

    private static string StatusCn(string s) => s switch
    {
        "Active" => "启用",
        "Disabled" => "禁用",
        "Expired" => "过期",
        _ => s
    };

    private static string OutcomeCn(string s) => s switch
    {
        "Success" => "成功",
        "UpstreamError" => "上游错误",
        "RejectedNoQuota" => "配额不足",
        "RejectedRateLimit" => "超限速",
        "RejectedAuth" => "认证失败",
        "ClientAborted" => "客户端中断",
        _ => s
    };

    private static string Trunc(string? s, int max)
    {
        s ??= "";
        return s.Length <= max ? s : s[..(max - 1)] + "…";
    }

    private static string Fmt(long? v) => v is null ? "不限" : v.Value.ToString("N0");
    private static string Fmt(decimal? v) => v is null ? "不限" : v.Value.ToString("F4");

    private static string? Arg(string[] a, string name)
    {
        for (int i = 0; i < a.Length - 1; i++)
            if (a[i].Equals(name, StringComparison.OrdinalIgnoreCase)) return a[i + 1];
        return null;
    }

    private static long LongArg(string[] a, int index, string what)
    {
        if (a.Length > index && long.TryParse(a[index], out var v)) return v;
        throw new ArgumentException($"缺少或非法的 {what} 参数");
    }

    private static long? LongOrNull(string? s) => long.TryParse(s, out var v) ? v : null;
    private static int? IntOrNull(string? s) => int.TryParse(s, out var v) ? v : null;
    private static decimal? DecOrNull(string? s) =>
        decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static int Unknown(string cmd)
    {
        Console.Error.WriteLine($"未知命令：{cmd}");
        Console.Error.WriteLine();
        PrintHelp();
        return 1;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
        AI 计费网关 · 命令行管理工具

        用法：dotnet aibilling.dll <命令> [子命令] [选项]

        命令：
          test                          部署自检（逐项验证强制 Key、上游、数据库）
          health                        查看网关 / 数据库 / Ollama 状态
          stats                         运行概览 + 近 7 天消耗趋势
          models                        列出 Ollama 模型及其当前定价

          keys list                     列出所有 API Key
          keys create --name <名称>     新建 Key
               [--tokens N]              Token 总配额
               [--cost N]                金额上限
               [--requests N]            请求数上限
               [--rpm N]                 每分钟请求限速
               [--tok-per-sec N]         输出速度上限（token/秒），限制模型吐字快慢
               [--tok-per-min N]         每分钟 token 消耗上限
               [--tok-per-day N]         每天 token 消耗上限
               [--models a,b]            允许的模型（支持通配）
               [--in-price N]            该 Key 专属输入价 / 千 token
               [--out-price N]           该 Key 专属输出价 / 千 token
               [--req-price N]           该 Key 专属单次价
               [--expires <时间>]        过期时间
          keys show <id>                查看 Key 详情
          keys update <id>              修改额度 / 定价 / 名称（只改传入的字段）
               [--tokens N]              Token 总配额（用 --clear-tokens 取消限制）
               [--cost N]                金额上限（--clear-cost 取消限制）
               [--requests N]            请求数上限（--clear-requests 取消限制）
               [--rpm N]                 限速（--clear-rpm 取消限制）
               [--tok-per-sec N]         输出速度上限（--clear-tok-per-sec 取消）
               [--tok-per-min N]         每分钟 token 上限（--clear-tok-per-min 取消）
               [--tok-per-day N]         每天 token 上限（--clear-tok-per-day 取消）
               [--models a,b]            重设模型白名单
               [--in-price N]            专属输入价（--clear-prices 清除专属价）
               [--out-price N]           专属输出价
               [--req-price N]           专属单次价
               [--name 名称]             改名
               [--status Active|Disabled] 改状态
               [--expires <时间>]        改过期时间（--clear-expires 取消）
          keys enable|disable <id>      启用 / 禁用
          keys reset <id>               清零累计用量
          keys delete <id>              删除 Key

          prices list                   列出定价规则
          prices set --model <模型名>   设置定价（支持 * 与 qwen2.5:* 通配）
               [--in N] [--out N]       按 token 计价（元 / 千 token）
               [--req-price N]          按次计价
          prices delete <模型名>        删除定价

          usage [--key <id>] [--limit N]   用量明细
          calib                         计费精度校准报告
          billing estimate --key <Key> --model <模型> [--prompt <文本>]
                                        发一次真实请求查看计费结果

        全局选项：
          --url <地址>                  网关地址（默认 http://127.0.0.1:8080）
          --user <用户名>               管理端账号
          --password <密码>             管理端密码

        常用示例：
          dotnet aibilling.dll test
          dotnet aibilling.dll keys create --name 张三 --tokens 1000000 --cost 100 --rpm 60
          dotnet aibilling.dll keys update 1 --tokens 5000000        # 提高配额
          dotnet aibilling.dll keys update 1 --clear-cost            # 取消金额上限
          dotnet aibilling.dll prices set --model gemma3:270m --in 0.5 --out 1.5
          dotnet aibilling.dll prices set --model "*" --in 1 --out 2
          dotnet aibilling.dll stats

        说明：若在网页后台修改过管理端账号，请用 --user/--password 指定新凭据，
              CLI 会自动记住，之后无需重复输入。
        """);
    }

    // =============================================================== DTO

    private sealed class KeyDto
    {
        [JsonPropertyName("id")] public long Id { get; set; }
        [JsonPropertyName("keyPrefix")] public string KeyPrefix { get; set; } = "";
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("status")] public string Status { get; set; } = "";
        [JsonPropertyName("allowedModels")] public List<string> AllowedModels { get; set; } = new();
        [JsonPropertyName("quota")] public QuotaDto Quota { get; set; } = new();
        [JsonPropertyName("used")] public UsedDto Used { get; set; } = new();
        [JsonPropertyName("remaining")] public RemainDto Remaining { get; set; } = new();
        [JsonPropertyName("overrides")] public OverrideDto Overrides { get; set; } = new();
        [JsonPropertyName("expiresAt")] public DateTime? ExpiresAt { get; set; }
        [JsonPropertyName("createdAt")] public DateTime CreatedAt { get; set; }
        [JsonPropertyName("lastUsedAt")] public DateTime? LastUsedAt { get; set; }
    }

    private sealed class QuotaDto
    {
        [JsonPropertyName("totalTokens")] public long? TotalTokens { get; set; }
        [JsonPropertyName("totalCost")] public decimal? TotalCost { get; set; }
        [JsonPropertyName("totalRequests")] public long? TotalRequests { get; set; }
        [JsonPropertyName("rateLimitRpm")] public int? RateLimitRpm { get; set; }
        [JsonPropertyName("maxOutputTokensPerSec")] public decimal? MaxOutputTokensPerSec { get; set; }
        [JsonPropertyName("tokensPerMinute")] public long? TokensPerMinute { get; set; }
        [JsonPropertyName("tokensPerDay")] public long? TokensPerDay { get; set; }
    }

    private sealed class UsedDto
    {
        [JsonPropertyName("promptTokens")] public long PromptTokens { get; set; }
        [JsonPropertyName("completionTokens")] public long CompletionTokens { get; set; }
        [JsonPropertyName("totalTokens")] public long TotalTokens { get; set; }
        [JsonPropertyName("requests")] public long Requests { get; set; }
        [JsonPropertyName("cost")] public decimal Cost { get; set; }
    }

    private sealed class RemainDto
    {
        [JsonPropertyName("tokens")] public long? Tokens { get; set; }
        [JsonPropertyName("cost")] public decimal? Cost { get; set; }
        [JsonPropertyName("requests")] public long? Requests { get; set; }
    }

    private sealed class OverrideDto
    {
        [JsonPropertyName("inputPricePer1K")] public decimal? InputPricePer1K { get; set; }
        [JsonPropertyName("outputPricePer1K")] public decimal? OutputPricePer1K { get; set; }
        [JsonPropertyName("pricePerRequest")] public decimal? PricePerRequest { get; set; }
    }

    private sealed class CreateResult
    {
        [JsonPropertyName("key")] public KeyDto Key { get; set; } = new();
        [JsonPropertyName("plainKey")] public string PlainKey { get; set; } = "";
    }

    private sealed class PriceDto
    {
        [JsonPropertyName("model")] public string Model { get; set; } = "";
        [JsonPropertyName("mode")] public int Mode { get; set; }
        [JsonPropertyName("inputPricePer1K")] public decimal InputPricePer1K { get; set; }
        [JsonPropertyName("outputPricePer1K")] public decimal OutputPricePer1K { get; set; }
        [JsonPropertyName("pricePerRequest")] public decimal PricePerRequest { get; set; }
        [JsonPropertyName("isWildcard")] public bool IsWildcard { get; set; }
        [JsonPropertyName("enabled")] public bool Enabled { get; set; }
    }

    private sealed class UsageDto
    {
        [JsonPropertyName("keyPrefix")] public string KeyPrefix { get; set; } = "";
        [JsonPropertyName("model")] public string Model { get; set; } = "";
        [JsonPropertyName("promptTokens")] public long PromptTokens { get; set; }
        [JsonPropertyName("completionTokens")] public long CompletionTokens { get; set; }
        [JsonPropertyName("totalTokens")] public long TotalTokens { get; set; }
        [JsonPropertyName("cost")] public decimal Cost { get; set; }
        [JsonPropertyName("outcome")] public string Outcome { get; set; } = "";
        [JsonPropertyName("durationMs")] public int DurationMs { get; set; }
        [JsonPropertyName("createdAt")] public DateTime CreatedAt { get; set; }
    }

    private sealed class OverviewDto
    {
        [JsonPropertyName("keys")] public KeysStat Keys { get; set; } = new();
        [JsonPropertyName("totals")] public TotalsStat Totals { get; set; } = new();
        [JsonPropertyName("today")] public TodayStat Today { get; set; } = new();
        [JsonPropertyName("config")] public ConfigStat Config { get; set; } = new();
    }

    private sealed class KeysStat
    {
        [JsonPropertyName("total")] public int Total { get; set; }
        [JsonPropertyName("active")] public int Active { get; set; }
        [JsonPropertyName("disabled")] public int Disabled { get; set; }
    }

    private sealed class TotalsStat
    {
        [JsonPropertyName("promptTokens")] public long PromptTokens { get; set; }
        [JsonPropertyName("completionTokens")] public long CompletionTokens { get; set; }
        [JsonPropertyName("totalTokens")] public long TotalTokens { get; set; }
        [JsonPropertyName("requests")] public long Requests { get; set; }
        [JsonPropertyName("cost")] public decimal Cost { get; set; }
    }

    private sealed class TodayStat
    {
        [JsonPropertyName("totalTokens")] public long TotalTokens { get; set; }
        [JsonPropertyName("requests")] public long Requests { get; set; }
        [JsonPropertyName("cost")] public decimal Cost { get; set; }
    }

    private sealed class ConfigStat
    {
        [JsonPropertyName("meteringMode")] public string MeteringMode { get; set; } = "";
        [JsonPropertyName("ollamaBaseUrl")] public string OllamaBaseUrl { get; set; } = "";
        [JsonPropertyName("currency")] public string Currency { get; set; } = "";
    }

    private sealed class DailyDto
    {
        [JsonPropertyName("day")] public DateTime Day { get; set; }
        [JsonPropertyName("promptTokens")] public long PromptTokens { get; set; }
        [JsonPropertyName("completionTokens")] public long CompletionTokens { get; set; }
        [JsonPropertyName("cost")] public decimal Cost { get; set; }
    }

    private sealed class CalibDto
    {
        [JsonPropertyName("days")] public int Days { get; set; }
        [JsonPropertyName("sampleCount")] public long SampleCount { get; set; }
        [JsonPropertyName("avgEstimated")] public double AvgEstimated { get; set; }
        [JsonPropertyName("avgActual")] public double AvgActual { get; set; }
        [JsonPropertyName("ratio")] public double Ratio { get; set; }
        [JsonPropertyName("suggestedCharsPerTokenFactor")] public double SuggestedCharsPerTokenFactor { get; set; }
        [JsonPropertyName("hint")] public string Hint { get; set; } = "";
    }

    private sealed class HealthDto
    {
        [JsonPropertyName("status")] public string Status { get; set; } = "";
        [JsonPropertyName("database")] public string Database { get; set; } = "";
        [JsonPropertyName("upstream")] public string Upstream { get; set; } = "";
        [JsonPropertyName("meteringMode")] public string MeteringMode { get; set; } = "";
    }

    private sealed class ModelsDto
    {
        [JsonPropertyName("ok")] public bool Ok { get; set; }
        [JsonPropertyName("error")] public string? Error { get; set; }
        [JsonPropertyName("models")] public List<string> Models { get; set; } = new();
    }
}
