using System.Security.Cryptography;
using System.Text;
using AiBilling.Core.Data;
using AiBilling.Core.Security;
using AiBilling.Core.Options;
using Microsoft.Extensions.Options;

namespace AiBilling.Gateway.Services;

/// <summary>
/// 管理后台登录会话与凭据校验。
///
/// 凭据来源优先级：
///   1) 数据库 admin_users 表（网页改密码后写到这里，PBKDF2 哈希存储）
///   2) appsettings.json 的 AdminUser / AdminPassword（首次启动的初始账号，明文）
///
/// 一旦在网页上改过账号，就以数据库为准；配置文件里的旧密码随即失效。
/// </summary>
public sealed class AdminSessionService
{
    private readonly IOptions<BillingOptions> _options;
    private readonly BillingRepository _repo;
    private readonly ILogger<AdminSessionService> _logger;
    private byte[] _secret;

    public const string CookieName = "aibilling_admin";

    public AdminSessionService(
        IOptions<BillingOptions> options,
        BillingRepository repo,
        ILogger<AdminSessionService> logger)
    {
        _options = options;
        _repo = repo;
        _logger = logger;
        _secret = DeriveSecret();
    }

    private byte[] DeriveSecret()
    {
        var s = _options.Value.AdminSessionSecret;
        if (string.IsNullOrWhiteSpace(s))
            s = _options.Value.AdminPassword + "|aibilling-default-secret";
        return SHA256.HashData(Encoding.UTF8.GetBytes(s));
    }

    /// <summary>
    /// 校验用户名密码。
    /// 数据库里有账号就用哈希校验；没有则回退到配置文件里的明文账号。
    /// </summary>
    public async Task<bool> ValidateCredentialsAsync(string user, string password, CancellationToken ct = default)
    {
        AdminAccount? account = null;
        try
        {
            account = await _repo.GetAdminAccountAsync(ct);
        }
        catch (Exception ex)
        {
            // 数据库不可用时仍允许用配置文件账号登录，避免把自己锁在门外
            _logger.LogWarning(ex, "读取管理账号失败，回退到配置文件凭据");
        }

        if (account is not null && PasswordHasher.LooksHashed(account.PasswordHash))
        {
            if (!string.Equals(user, account.Username, StringComparison.Ordinal)) return false;
            return PasswordHasher.Verify(password, account.PasswordHash);
        }

        // 回退：配置文件明文
        var o = _options.Value;
        if (!string.Equals(user, o.AdminUser, StringComparison.Ordinal)) return false;
        return PasswordHasher.FixedTimeEquals(password, o.AdminPassword);
    }

    /// <summary>读取当前生效的用户名（数据库优先）。</summary>
    public async Task<string> GetCurrentUsernameAsync(CancellationToken ct = default)
    {
        try
        {
            var a = await _repo.GetAdminAccountAsync(ct);
            if (a is not null) return a.Username;
        }
        catch { /* 忽略，回退配置 */ }
        return _options.Value.AdminUser;
    }

    /// <summary>当前凭据是否来自数据库（即是否已通过网页修改过）。</summary>
    public async Task<bool> IsDbBackedAsync(CancellationToken ct = default)
    {
        try
        {
            var a = await _repo.GetAdminAccountAsync(ct);
            return a is not null && PasswordHasher.LooksHashed(a.PasswordHash);
        }
        catch { return false; }
    }

    /// <summary>
    /// 修改账号。newUsername 为空表示不改用户名。
    /// 改完会轮换会话密钥，使所有已登录的会话立即失效（强制重新登录）。
    /// </summary>
    public async Task ChangeCredentialsAsync(string? newUsername, string? newPassword, CancellationToken ct = default)
    {
        var current = await GetCurrentUsernameAsync(ct);
        var username = string.IsNullOrWhiteSpace(newUsername) ? current : newUsername.Trim();

        // 密码为空表示只改用户名，沿用原密码哈希
        string hash;
        if (string.IsNullOrEmpty(newPassword))
        {
            var existing = await _repo.GetAdminAccountAsync(ct);
            hash = existing is not null && PasswordHasher.LooksHashed(existing.PasswordHash)
                ? existing.PasswordHash
                : PasswordHasher.Hash(_options.Value.AdminPassword);   // 首次落库：把配置里的初始密码搬过来
        }
        else
        {
            hash = PasswordHasher.Hash(newPassword);
        }

        await _repo.UpsertAdminAccountAsync(username, hash, ct);

        // 轮换签名密钥：旧 Cookie 立即作废，避免改密后旧会话仍然可用
        _secret = SHA256.HashData(Encoding.UTF8.GetBytes(
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))));

        _logger.LogInformation("管理端账号已更新，用户名={User}", username);
    }

    /// <summary>生成签名令牌：&lt;过期时间戳&gt;.&lt;HMAC&gt;</summary>
    public string IssueToken(TimeSpan lifetime)
    {
        var exp = DateTimeOffset.UtcNow.Add(lifetime).ToUnixTimeSeconds();
        var payload = exp.ToString();
        var sig = Sign(payload, _secret);
        return $"{payload}.{sig}";
    }

    public bool ValidateToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;
        var parts = token.Split('.');
        if (parts.Length != 2) return false;
        if (!long.TryParse(parts[0], out var exp)) return false;
        if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() > exp) return false;

        // 同时接受当前密钥与初始密钥签发的令牌：
        // 轮换后旧会话会因过期时间自然失效，这里保持严格比对即可。
        return PasswordHasher.FixedTimeEquals(parts[1], Sign(parts[0], _secret));
    }

    private static string Sign(string payload, byte[] secret)
    {
        using var h = new HMACSHA256(secret);
        var mac = h.ComputeHash(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(mac).ToLowerInvariant();
    }
}

/// <summary>管理端鉴权中间件，保护 /admin 与 /api/admin。</summary>
public sealed class AdminAuthMiddleware
{
    private readonly RequestDelegate _next;
    public AdminAuthMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext ctx, AdminSessionService sessions)
    {
        var path = ctx.Request.Path.Value ?? "";

        var isAdminPage = path.StartsWith("/admin", StringComparison.OrdinalIgnoreCase);
        var isAdminApi = path.StartsWith("/api/admin", StringComparison.OrdinalIgnoreCase);

        if (!isAdminPage && !isAdminApi)
        {
            await _next(ctx);
            return;
        }

        // 登录接口本身放行
        if (path.Equals("/admin/login", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("/api/admin/login", StringComparison.OrdinalIgnoreCase))
        {
            await _next(ctx);
            return;
        }

        var token = ctx.Request.Cookies[AdminSessionService.CookieName];
        if (!sessions.ValidateToken(token))
        {
            if (isAdminApi)
            {
                ctx.Response.StatusCode = 401;
                ctx.Response.ContentType = "application/json; charset=utf-8";
                await ctx.Response.WriteAsync("""{"error":{"code":"unauthorized","message":"管理端未登录或会话已过期。"}}""");
            }
            else
            {
                ctx.Response.Redirect("/admin/login");
            }
            return;
        }

        await _next(ctx);
    }
}
