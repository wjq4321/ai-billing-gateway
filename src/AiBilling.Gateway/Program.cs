using AiBilling.Core.Billing;
using AiBilling.Core.Data;
using AiBilling.Core.Models;
using AiBilling.Core.Options;
using AiBilling.Gateway.Endpoints;
using AiBilling.Gateway.Middleware;
using AiBilling.Gateway.Services;

var builder = WebApplication.CreateBuilder(args);

// 去掉 Windows 事件日志 provider：在受限环境（无事件日志写权限）下它会抛异常，
// 而且控制台/文件日志对网关运维已经足够。
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(o =>
{
    o.SingleLine = false;
    o.TimestampFormat = "yyyy-MM-dd HH:mm:ss ";
});

// ---------------------------------------------------------------- 配置
// appsettings.json 含密码，已在 .gitignore 中忽略（仓库里只有 appsettings.example.json）。
// 因此这里额外加载可选的 appsettings.Local.json，方便本地覆盖而不影响仓库文件。
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);

builder.Services.Configure<BillingOptions>(builder.Configuration.GetSection(BillingOptions.SectionName));
var billingOptions = builder.Configuration.GetSection(BillingOptions.SectionName).Get<BillingOptions>() ?? new BillingOptions();

if (string.IsNullOrWhiteSpace(billingOptions.ConnectionString))
{
    var example = Path.Combine(AppContext.BaseDirectory, "appsettings.example.json");
    Console.Error.WriteLine();
    Console.Error.WriteLine("  错误：未配置 Billing:ConnectionString（MySQL 连接串）。");
    Console.Error.WriteLine();
    Console.Error.WriteLine("  请先创建配置文件：");
    Console.Error.WriteLine("    1) 复制 appsettings.example.json 为 appsettings.json");
    Console.Error.WriteLine("    2) 填入你的 MySQL 连接串与管理端密码");
    Console.Error.WriteLine();
    if (File.Exists(example))
        Console.Error.WriteLine($"  示例文件位置：{example}");
    Console.Error.WriteLine("  详见 README 的「快速开始」章节。");
    Console.Error.WriteLine();
    return 1;
}

// ---------------------------------------------------------------- 基础设施
builder.Services.AddSingleton(new BillingRepository(billingOptions.ConnectionString));
builder.Services.AddSingleton(sp =>
    new ApiKeyStore(
        sp.GetRequiredService<BillingRepository>(),
        TimeSpan.FromSeconds(Math.Max(0, billingOptions.KeyCacheSeconds))));

builder.Services.AddSingleton<BillingEngine>();
builder.Services.AddSingleton(new TokenEstimator(billingOptions.CharsPerToken));
builder.Services.AddSingleton(new QuotaGuard(billingOptions.RateLimitWindowSeconds));
builder.Services.AddSingleton<TokenRateLimiter>();   // token 消耗速率（每分钟/每天）
builder.Services.AddSingleton<AdminSessionService>();

builder.Services.AddSingleton<BillingQueue>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<BillingQueue>());

// 转发到 Ollama 的 HttpClient：不设整体超时，改用每次请求的取消令牌控制
builder.Services.AddHttpClient<MeteringProxy>(c =>
{
    c.Timeout = Timeout.InfiniteTimeSpan;
    c.DefaultRequestHeaders.Add("User-Agent", "AiBillingGateway/1.0");
})
.ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
{
    PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    ConnectTimeout = TimeSpan.FromSeconds(10)
});

// 管理端用的普通 HttpClient（查模型列表等短请求）
builder.Services.AddHttpClient("plain", c => c.Timeout = TimeSpan.FromSeconds(15));

builder.Services.AddHostedService<PriceRefreshService>();

var app = builder.Build();

// ---------------------------------------------------------------- 初始化
await BootstrapAsync(app);

// ---------------------------------------------------------------- 中间件
app.UseMiddleware<AdminAuthMiddleware>();     // 管理端会话
app.UseMiddleware<ApiKeyAuthMiddleware>();    // 强制 API Key

// ---------------------------------------------------------------- 路由
app.MapAdminEndpoints();

app.MapGet("/health", async (BillingRepository repo, Microsoft.Extensions.Options.IOptions<BillingOptions> opt, MeteringProxy proxy, CancellationToken ct) =>
{
    var dbOk = await DatabaseInitializer.CheckConnectionAsync(opt.Value.ConnectionString, ct);
    return Results.Ok(new
    {
        status = dbOk ? "healthy" : "degraded",
        database = dbOk ? "up" : "down",
        upstream = opt.Value.OllamaBaseUrl,
        meteringMode = opt.Value.MeteringMode,
        time = DateTime.UtcNow
    });
});

// 管理页面
app.MapGet("/admin", (HttpContext ctx) => Results.Redirect("/admin/dashboard"));
app.MapGet("/admin/login", () => Results.Content(AdminPages.Login, "text/html; charset=utf-8"));
app.MapGet("/admin/dashboard", () => Results.Content(AdminPages.Dashboard, "text/html; charset=utf-8"));

app.MapGet("/", () => Results.Content(AdminPages.Root, "text/html; charset=utf-8"));

// ---------------------------------------------------------------- 代理兜底
// 所有未被上面路由匹配的请求都交给计量代理转发给 Ollama
app.MapFallback(async (HttpContext ctx, MeteringProxy proxy) =>
{
    var path = ctx.Request.Path.Value ?? "/";
    await proxy.HandleAsync(ctx, path);
});

// ---------------------------------------------------------------- 优雅关闭
var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
lifetime.ApplicationStopping.Register(() =>
{
    try
    {
        app.Services.GetRequiredService<BillingQueue>().DrainAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
    }
    catch { /* 忽略关闭期异常 */ }
});

app.Run();
return 0;

// ---------------------------------------------------------------------------
// 启动引导：建库建表、加载单价、确保默认 Key 存在
// ---------------------------------------------------------------------------
static async Task BootstrapAsync(WebApplication app)
{
    var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Bootstrap");
    var options = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<BillingOptions>>().Value;

    // 弱密码检查：默认密码会让任何人都能登进管理后台，必须显式提醒
    WarnIfWeakCredentials(logger, options);

    try
    {
        var dbName = DatabaseInitializer.GetDatabaseName(options.ConnectionString);
        if (string.IsNullOrWhiteSpace(dbName))
        {
            logger.LogError("连接串里缺少 Database=xxx，无法建表。");
            return;
        }

        logger.LogInformation("正在初始化数据库 {Db} ...", dbName);
        await DatabaseInitializer.InitializeAsync(
            DatabaseInitializer.StripDatabase(options.ConnectionString), dbName);
        logger.LogInformation("数据库初始化完成。");

        var repo = app.Services.GetRequiredService<BillingRepository>();
        var engine = app.Services.GetRequiredService<BillingEngine>();

        // 加载模型单价
        var prices = await repo.ListPricesAsync();
        if (prices.Count == 0)
        {
            logger.LogInformation("未发现模型单价配置，写入示例单价（全部为 0，请按需修改）。");
            foreach (var m in new[] { "*" })
            {
                await repo.UpsertPriceAsync(new ModelPrice
                {
                    Model = m,
                    Mode = PricingMode.PerToken,
                    InputPricePer1K = 0m,
                    OutputPricePer1K = 0m,
                    IsWildcard = true,
                    Enabled = true
                });
            }
            prices = await repo.ListPricesAsync();
        }
        engine.SetPrices(prices);
        logger.LogInformation("已加载 {Count} 条定价规则。", prices.Count);

        logger.LogInformation("""
            
            ============================================================
              AI 计费网关已启动
              监听地址   : {Urls}
              Ollama 上游: {Upstream}
              计量模式   : {Mode}
              管理后台   : http://localhost:{Port}/admin  (账号 {User})
            ============================================================
            """,
            string.Join(", ", app.Urls.DefaultIfEmpty("(见启动日志)")),
            options.OllamaBaseUrl,
            options.MeteringMode,
            new Uri(app.Urls.FirstOrDefault() ?? "http://localhost:8080").Port,
            options.AdminUser);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "启动初始化失败。请确认 MySQL 已启动且连接串正确。");
    }
}

/// <summary>
/// 检查管理端凭据是否为默认/弱密码。
/// 网关的整个价值就在于"不认证不给用"，因此默认密码必须显式提醒。
/// 配置 AllowDefaultPassword=false 可让其直接拒绝启动（生产环境推荐）。
/// </summary>
static void WarnIfWeakCredentials(ILogger logger, BillingOptions options)
{
    var weak = new[] { "admin123", "CHANGE_ME", "123456", "password", "admin" };
    var isWeak = string.IsNullOrWhiteSpace(options.AdminPassword)
                 || weak.Contains(options.AdminPassword, StringComparer.OrdinalIgnoreCase);

    if (!isWeak)
    {
        // 非弱密码时，顺便提醒会话密钥缺失的问题
        if (string.IsNullOrWhiteSpace(options.AdminSessionSecret))
        {
            logger.LogWarning(
                "Billing:AdminSessionSecret 为空，会话签名密钥由密码派生。" +
                "生产环境建议填入一段随机字符串。");
        }
        return;
    }

    if (!options.AllowDefaultPassword)
    {
        Console.Error.WriteLine();
        Console.Error.WriteLine("  启动被拒绝：检测到默认或弱管理密码。");
        Console.Error.WriteLine();
        Console.Error.WriteLine("  请修改 appsettings.json 中的 Billing:AdminPassword 后重启。");
        Console.Error.WriteLine("  如确实要在开发环境跳过此检查，设置 Billing:AllowDefaultPassword 为 true。");
        Console.Error.WriteLine();
        throw new InvalidOperationException("检测到默认管理密码，已按配置拒绝启动。");
    }

    logger.LogWarning("""
        
        ╔════════════════════════════════════════════════════════════╗
        ║  ⚠  安全警告：管理端仍在使用默认/弱密码！                 ║
        ╠════════════════════════════════════════════════════════════╣
        ║  任何能访问本端口的人都可以登录管理后台，查看全部账单、     ║
        ║  创建 Key、修改配额。                                      ║
        ║                                                            ║
        ║  请立即处理：                                              ║
        ║    1) 登录 /admin → 「账号安全」页修改用户名和密码，或      ║
        ║    2) 修改 appsettings.json 的 Billing:AdminPassword 后重启 ║
        ║                                                            ║
        ║  若要在生产环境强制此检查，设置：                          ║
        ║    Billing:AllowDefaultPassword = false                    ║
        ╚════════════════════════════════════════════════════════════╝
        """);
}

/// <summary>定时刷新单价与缓存，保证管理端改价后及时生效。</summary>
public sealed class PriceRefreshService : BackgroundService
{
    private readonly IServiceProvider _sp;
    private readonly ILogger<PriceRefreshService> _logger;

    public PriceRefreshService(IServiceProvider sp, ILogger<PriceRefreshService> logger)
    {
        _sp = sp;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var scope = _sp.CreateScope();
                var repo = scope.ServiceProvider.GetRequiredService<BillingRepository>();
                var engine = scope.ServiceProvider.GetRequiredService<BillingEngine>();
                if (engine.NeedsRefresh)
                    engine.SetPrices(await repo.ListPricesAsync(stoppingToken));

                // 清理闲置的限速窗口，避免长期运行后内存增长
                scope.ServiceProvider.GetService<TokenRateLimiter>()?.Cleanup(DateTime.UtcNow);
                scope.ServiceProvider.GetService<QuotaGuard>()?.Cleanup(DateTime.UtcNow);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "刷新单价失败（将下轮重试）");
            }
        }
    }
}
