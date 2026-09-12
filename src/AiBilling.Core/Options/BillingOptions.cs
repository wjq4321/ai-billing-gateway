namespace AiBilling.Core.Options;

/// <summary>网关运行配置，绑定 appsettings.json 的 "Billing" 节。</summary>
public sealed class BillingOptions
{
    public const string SectionName = "Billing";

    /// <summary>Ollama 上游地址。</summary>
    public string OllamaBaseUrl { get; set; } = "http://127.0.0.1:11434";

    /// <summary>MySQL 连接串。</summary>
    public string ConnectionString { get; set; } = "";

    /// <summary>
    /// 计量模式：
    /// Exact    = 缓冲上游响应，用 Ollama 返回的 eval_count 精确计费（首字延迟变高）
    /// Realtime = 边流边发，用累积文本估算 token 计费（首字延迟低，有少量误差）
    /// </summary>
    public string MeteringMode { get; set; } = "Realtime";

    /// <summary>估算 token 时使用的每 token 字符数（中文场景约 1.5，英文约 4）。</summary>
    public double CharsPerToken { get; set; } = 1.7;

    /// <summary>是否把请求明细写库。</summary>
    public bool LogUsageRecords { get; set; } = true;

    /// <summary>是否启用估算值与实测值的对比校准记录。</summary>
    public bool EnableCalibration { get; set; } = true;

    /// <summary>管理后台用户名。</summary>
    public string AdminUser { get; set; } = "admin";

    /// <summary>
    /// 管理后台初始密码。
    /// 默认值仅为让"刚 clone 下来就能跑"，启动时会检测并在日志中警告。
    /// 生产环境请务必通过 appsettings.json 或网页「账号安全」页修改。
    /// </summary>
    public string AdminPassword { get; set; } = "admin123";

    /// <summary>是否允许使用默认的弱密码启动（false 时检测到默认密码会直接拒绝启动）。</summary>
    public bool AllowDefaultPassword { get; set; } = true;

    /// <summary>管理后台 / 管理 API 的会话密钥。</summary>
    public string AdminSessionSecret { get; set; } = "";

    /// <summary>币种展示符号。</summary>
    public string Currency { get; set; } = "CNY";

    /// <summary>上游单次请求超时（秒）。</summary>
    public int UpstreamTimeoutSeconds { get; set; } = 600;

    /// <summary>Key 缓存秒数，避免每个请求都查库。</summary>
    public int KeyCacheSeconds { get; set; } = 15;

    /// <summary>限速计数器的时间窗口秒数。</summary>
    public int RateLimitWindowSeconds { get; set; } = 60;

    /// <summary>
    /// 全局默认的输出速度上限（token/秒）。0 = 不限速。
    /// 单个 Key 的 MaxOutputTokensPerSec 优先于此值。
    /// 用作"整台机器不被某个客户端跑满"的兜底保护。
    /// </summary>
    public decimal DefaultMaxOutputTokensPerSec { get; set; } = 0;

    /// <summary>
    /// 是否对非流式响应校验输出速度。非流式一次性返回、无法节流，
    /// 只能事后判断是否超速并写入记录，供对账与告警。
    /// </summary>
    public bool EnforceRateOnNonStreaming { get; set; } = true;
}
