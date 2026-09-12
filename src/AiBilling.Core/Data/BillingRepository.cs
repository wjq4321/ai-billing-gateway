using AiBilling.Core.Models;
using AiBilling.Core.Security;
using MySql.Data.MySqlClient;

namespace AiBilling.Core.Data;

/// <summary>MySQL 数据访问层。</summary>
public sealed class BillingRepository
{
    private readonly string _connectionString;

    public BillingRepository(string connectionString) => _connectionString = connectionString;

    private async Task<MySqlConnection> OpenAsync(CancellationToken ct)
    {
        var conn = new MySqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        return conn;
    }

    // ------------------------------------------------------------------ Key

    public async Task<ApiKeyAccount?> FindKeyByHashAsync(string keyHash, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM api_keys WHERE key_hash = @h LIMIT 1;";
        cmd.Parameters.AddWithValue("@h", keyHash);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? MapKey(reader) : null;
    }

    public async Task<ApiKeyAccount?> GetKeyAsync(long id, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM api_keys WHERE id = @id LIMIT 1;";
        cmd.Parameters.AddWithValue("@id", id);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? MapKey(reader) : null;
    }

    public async Task<List<ApiKeyAccount>> ListKeysAsync(CancellationToken ct = default)
    {
        var list = new List<ApiKeyAccount>();
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM api_keys ORDER BY id DESC;";
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) list.Add(MapKey(reader));
        return list;
    }

    /// <summary>创建 Key，返回 (账户, 明文 Key)。明文只在此刻可见。</summary>
    public async Task<(ApiKeyAccount Account, string PlainKey)> CreateKeyAsync(
        ApiKeyAccount account, CancellationToken ct = default)
    {
        var plain = ApiKeyHasher.GenerateKey();
        account.KeyHash = ApiKeyHasher.Hash(plain);
        account.KeyPrefix = ApiKeyHasher.DisplayPrefix(plain);

        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO api_keys
                (key_prefix, key_hash, name, status, allowed_models,
                 quota_total_tokens, quota_total_cost, quota_total_requests, rate_limit_rpm,
                 max_output_tokens_per_sec, quota_tokens_per_minute, quota_tokens_per_day,
                 override_input_price_1k, override_output_price_1k, override_price_per_request,
                 expires_at)
            VALUES
                (@prefix, @hash, @name, @status, @models,
                 @qtok, @qcost, @qreq, @rpm,
                 @maxout, @tpm, @tpd,
                 @oip, @oop, @opr,
                 @expires);
            SELECT LAST_INSERT_ID();
            """;
        cmd.Parameters.AddWithValue("@prefix", account.KeyPrefix);
        cmd.Parameters.AddWithValue("@hash", account.KeyHash);
        cmd.Parameters.AddWithValue("@name", account.Name);
        cmd.Parameters.AddWithValue("@status", (int)account.Status);
        cmd.Parameters.AddWithValue("@models", System.Text.Json.JsonSerializer.Serialize(account.AllowedModels));
        cmd.Parameters.AddWithValue("@qtok", (object?)account.QuotaTotalTokens ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@qcost", (object?)account.QuotaTotalCost ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@qreq", (object?)account.QuotaTotalRequests ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@rpm", (object?)account.RateLimitRpm ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@maxout", (object?)account.MaxOutputTokensPerSec ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@tpm", (object?)account.QuotaTokensPerMinute ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@tpd", (object?)account.QuotaTokensPerDay ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@oip", (object?)account.OverrideInputPricePer1K ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@oop", (object?)account.OverrideOutputPricePer1K ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@opr", (object?)account.OverridePricePerRequest ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@expires", (object?)account.ExpiresAt ?? DBNull.Value);

        var id = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
        account.Id = id;
        account.CreatedAt = DateTime.UtcNow;
        return (account, plain);
    }

    /// <summary>更新 Key 的可配置字段（不含用量）。</summary>
    public async Task UpdateKeyAsync(ApiKeyAccount a, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE api_keys SET
                name = @name,
                status = @status,
                allowed_models = @models,
                quota_total_tokens = @qtok,
                quota_total_cost = @qcost,
                quota_total_requests = @qreq,
                rate_limit_rpm = @rpm,
                max_output_tokens_per_sec = @maxout,
                quota_tokens_per_minute = @tpm,
                quota_tokens_per_day = @tpd,
                override_input_price_1k = @oip,
                override_output_price_1k = @oop,
                override_price_per_request = @opr,
                expires_at = @expires
            WHERE id = @id;
            """;
        cmd.Parameters.AddWithValue("@name", a.Name);
        cmd.Parameters.AddWithValue("@status", (int)a.Status);
        cmd.Parameters.AddWithValue("@models", System.Text.Json.JsonSerializer.Serialize(a.AllowedModels));
        cmd.Parameters.AddWithValue("@qtok", (object?)a.QuotaTotalTokens ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@qcost", (object?)a.QuotaTotalCost ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@qreq", (object?)a.QuotaTotalRequests ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@rpm", (object?)a.RateLimitRpm ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@maxout", (object?)a.MaxOutputTokensPerSec ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@tpm", (object?)a.QuotaTokensPerMinute ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@tpd", (object?)a.QuotaTokensPerDay ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@oip", (object?)a.OverrideInputPricePer1K ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@oop", (object?)a.OverrideOutputPricePer1K ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@opr", (object?)a.OverridePricePerRequest ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@expires", (object?)a.ExpiresAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@id", a.Id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> DeleteKeyAsync(long id, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM api_keys WHERE id = @id;";
        cmd.Parameters.AddWithValue("@id", id);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    /// <summary>原子累加用量。返回受影响行数。</summary>
    public async Task AccumulateUsageAsync(
        long keyId, long promptTokens, long completionTokens, long requests,
        decimal cost, DateTime usedAtUtc, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE api_keys SET
                used_prompt_tokens     = used_prompt_tokens + @pt,
                used_completion_tokens = used_completion_tokens + @ct,
                used_requests          = used_requests + @rq,
                used_cost              = used_cost + @cost,
                last_used_at           = @at
            WHERE id = @id;
            """;
        cmd.Parameters.AddWithValue("@pt", promptTokens);
        cmd.Parameters.AddWithValue("@ct", completionTokens);
        cmd.Parameters.AddWithValue("@rq", requests);
        cmd.Parameters.AddWithValue("@cost", cost);
        cmd.Parameters.AddWithValue("@at", usedAtUtc);
        cmd.Parameters.AddWithValue("@id", keyId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>清零某个 Key 的累计用量（用于月结重置）。</summary>
    public async Task ResetUsageAsync(long keyId, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE api_keys SET
                used_prompt_tokens = 0, used_completion_tokens = 0,
                used_requests = 0, used_cost = 0
            WHERE id = @id;
            """;
        cmd.Parameters.AddWithValue("@id", keyId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ------------------------------------------------------------- 模型单价

    public async Task<List<ModelPrice>> ListPricesAsync(CancellationToken ct = default)
    {
        var list = new List<ModelPrice>();
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM model_prices ORDER BY is_wildcard, model;";
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) list.Add(MapPrice(reader));
        return list;
    }

    public async Task UpsertPriceAsync(ModelPrice p, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO model_prices
                (model, mode, input_price_per_1k, output_price_per_1k, price_per_request, is_wildcard, enabled)
            VALUES (@model, @mode, @ip, @op, @pr, @wc, @en)
            ON DUPLICATE KEY UPDATE
                mode = VALUES(mode),
                input_price_per_1k = VALUES(input_price_per_1k),
                output_price_per_1k = VALUES(output_price_per_1k),
                price_per_request = VALUES(price_per_request),
                is_wildcard = VALUES(is_wildcard),
                enabled = VALUES(enabled);
            """;
        cmd.Parameters.AddWithValue("@model", p.Model);
        cmd.Parameters.AddWithValue("@mode", (int)p.Mode);
        cmd.Parameters.AddWithValue("@ip", p.InputPricePer1K);
        cmd.Parameters.AddWithValue("@op", p.OutputPricePer1K);
        cmd.Parameters.AddWithValue("@pr", p.PricePerRequest);
        cmd.Parameters.AddWithValue("@wc", p.IsWildcard);
        cmd.Parameters.AddWithValue("@en", p.Enabled);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> DeletePriceAsync(string model, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM model_prices WHERE model = @model;";
        cmd.Parameters.AddWithValue("@model", model);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    // ------------------------------------------------------------- 用量明细

    public async Task<long> InsertUsageAsync(UsageRecord r, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO usage_records
                (api_key_id, key_prefix, model, endpoint, metering_mode,
                 prompt_tokens, completion_tokens,
                 actual_prompt_tokens, actual_completion_tokens, estimated_completion_tokens,
                 cost, currency, outcome, http_status, duration_ms, first_token_ms,
                 error_message, client_ip, created_at)
            VALUES
                (@kid, @kp, @model, @ep, @mm,
                 @pt, @ct,
                 @apt, @act, @ect,
                 @cost, @cur, @outcome, @status, @dur, @ftm,
                 @err, @ip, @created);
            SELECT LAST_INSERT_ID();
            """;
        cmd.Parameters.AddWithValue("@kid", r.ApiKeyId);
        cmd.Parameters.AddWithValue("@kp", r.KeyPrefix);
        cmd.Parameters.AddWithValue("@model", r.Model);
        cmd.Parameters.AddWithValue("@ep", r.Endpoint);
        cmd.Parameters.AddWithValue("@mm", (int)r.MeteringMode);
        cmd.Parameters.AddWithValue("@pt", r.PromptTokens);
        cmd.Parameters.AddWithValue("@ct", r.CompletionTokens);
        cmd.Parameters.AddWithValue("@apt", (object?)r.ActualPromptTokens ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@act", (object?)r.ActualCompletionTokens ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ect", (object?)r.EstimatedCompletionTokens ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@cost", r.Cost);
        cmd.Parameters.AddWithValue("@cur", r.Currency);
        cmd.Parameters.AddWithValue("@outcome", (int)r.Outcome);
        cmd.Parameters.AddWithValue("@status", r.HttpStatus);
        cmd.Parameters.AddWithValue("@dur", r.DurationMs);
        cmd.Parameters.AddWithValue("@ftm", (object?)r.FirstTokenMs ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@err", (object?)r.ErrorMessage ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ip", r.ClientIp);
        cmd.Parameters.AddWithValue("@created", r.CreatedAt);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
    }

    public async Task<List<UsageRecord>> QueryUsageAsync(
        long? keyId, DateTime? fromUtc, DateTime? toUtc, string? model,
        int limit = 200, CancellationToken ct = default)
    {
        var list = new List<UsageRecord>();
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        var where = new List<string>();
        if (keyId is not null) { where.Add("api_key_id = @kid"); cmd.Parameters.AddWithValue("@kid", keyId); }
        if (fromUtc is not null) { where.Add("created_at >= @from"); cmd.Parameters.AddWithValue("@from", fromUtc); }
        if (toUtc is not null) { where.Add("created_at < @to"); cmd.Parameters.AddWithValue("@to", toUtc); }
        if (!string.IsNullOrWhiteSpace(model)) { where.Add("model = @model"); cmd.Parameters.AddWithValue("@model", model); }

        var clause = where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "";
        cmd.CommandText = $"SELECT * FROM usage_records {clause} ORDER BY id DESC LIMIT {Math.Clamp(limit, 1, 5000)};";
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) list.Add(MapUsage(reader));
        return list;
    }

    /// <summary>汇总统计。</summary>
    public async Task<UsageSummary> SummarizeAsync(
        long? keyId, DateTime? fromUtc, DateTime? toUtc, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        var where = new List<string>();
        if (keyId is not null) { where.Add("api_key_id = @kid"); cmd.Parameters.AddWithValue("@kid", keyId); }
        if (fromUtc is not null) { where.Add("created_at >= @from"); cmd.Parameters.AddWithValue("@from", fromUtc); }
        if (toUtc is not null) { where.Add("created_at < @to"); cmd.Parameters.AddWithValue("@to", toUtc); }
        var clause = where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "";

        cmd.CommandText = $"""
            SELECT
                COALESCE(SUM(prompt_tokens), 0),
                COALESCE(SUM(completion_tokens), 0),
                COUNT(*),
                COALESCE(SUM(cost), 0),
                COALESCE(SUM(CASE WHEN outcome = 1 THEN 1 ELSE 0 END), 0)
            FROM usage_records {clause};
            """;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var s = new UsageSummary();
        if (await reader.ReadAsync(ct))
        {
            s.PromptTokens = reader.GetInt64(0);
            s.CompletionTokens = reader.GetInt64(1);
            s.Requests = reader.GetInt64(2);
            s.Cost = reader.GetDecimal(3);
            s.SuccessRequests = reader.GetInt64(4);
        }
        return s;
    }

    /// <summary>按天聚合，用于趋势图。</summary>
    public async Task<List<DailyUsage>> DailyAsync(
        long? keyId, DateTime? fromUtc, DateTime? toUtc, CancellationToken ct = default)
    {
        var list = new List<DailyUsage>();
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        var where = new List<string>();
        if (keyId is not null) { where.Add("api_key_id = @kid"); cmd.Parameters.AddWithValue("@kid", keyId); }
        if (fromUtc is not null) { where.Add("created_at >= @from"); cmd.Parameters.AddWithValue("@from", fromUtc); }
        if (toUtc is not null) { where.Add("created_at < @to"); cmd.Parameters.AddWithValue("@to", toUtc); }
        var clause = where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "";

        cmd.CommandText = $"""
            SELECT DATE(created_at) AS d,
                   SUM(prompt_tokens), SUM(completion_tokens), COUNT(*), SUM(cost)
            FROM usage_records {clause}
            GROUP BY d ORDER BY d;
            """;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(new DailyUsage
            {
                Day = reader.GetDateTime(0),
                PromptTokens = reader.GetInt64(1),
                CompletionTokens = reader.GetInt64(2),
                Requests = reader.GetInt64(3),
                Cost = reader.GetDecimal(4)
            });
        }
        return list;
    }

    /// <summary>各模型的用量分布。</summary>
    public async Task<List<DailyUsage>> ByModelAsync(
        long? keyId, DateTime? fromUtc, DateTime? toUtc, CancellationToken ct = default)
    {
        var list = new List<DailyUsage>();
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        var where = new List<string>();
        if (keyId is not null) { where.Add("api_key_id = @kid"); cmd.Parameters.AddWithValue("@kid", keyId); }
        if (fromUtc is not null) { where.Add("created_at >= @from"); cmd.Parameters.AddWithValue("@from", fromUtc); }
        if (toUtc is not null) { where.Add("created_at < @to"); cmd.Parameters.AddWithValue("@to", toUtc); }
        var clause = where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "";

        cmd.CommandText = $"""
            SELECT model, SUM(prompt_tokens), SUM(completion_tokens), COUNT(*), SUM(cost)
            FROM usage_records {clause}
            GROUP BY model ORDER BY SUM(cost) DESC;
            """;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(new DailyUsage
            {
                Model = reader.GetString(0),
                PromptTokens = reader.GetInt64(1),
                CompletionTokens = reader.GetInt64(2),
                Requests = reader.GetInt64(3),
                Cost = reader.GetDecimal(4)
            });
        }
        return list;
    }

    /// <summary>计费校准：估算值与实测值的偏差统计。</summary>
    public async Task<CalibrationReport> CalibrationAsync(int days = 7, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
                COUNT(*),
                COALESCE(AVG(estimated_completion_tokens), 0),
                COALESCE(AVG(actual_completion_tokens), 0),
                COALESCE(SUM(estimated_completion_tokens), 0),
                COALESCE(SUM(actual_completion_tokens), 0)
            FROM usage_records
            WHERE created_at >= @from
              AND estimated_completion_tokens IS NOT NULL
              AND actual_completion_tokens IS NOT NULL;
            """;
        cmd.Parameters.AddWithValue("@from", DateTime.UtcNow.AddDays(-days));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var r = new CalibrationReport { Days = days };
        if (await reader.ReadAsync(ct))
        {
            r.SampleCount = reader.GetInt64(0);
            r.AvgEstimated = reader.GetDouble(1);
            r.AvgActual = reader.GetDouble(2);
            r.SumEstimated = reader.GetInt64(3);
            r.SumActual = reader.GetInt64(4);
        }
        return r;
    }

    // --------------------------------------------------------------- 管理账号

    /// <summary>读取管理账号（单账号模式，取 id=1）。不存在返回 null。</summary>
    public async Task<AdminAccount?> GetAdminAccountAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, username, password_hash FROM admin_users ORDER BY id LIMIT 1;";
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;

        return new AdminAccount
        {
            Id = reader.GetInt64(0),
            Username = reader.GetString(1),
            PasswordHash = reader.GetString(2)
        };
    }

    /// <summary>新建或覆盖管理账号。密码应为已哈希值。</summary>
    public async Task UpsertAdminAccountAsync(string username, string passwordHash, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();

        // 单账号模式：先清掉旧记录，保证表里只有一行，避免用户名冲突导致登录歧义
        cmd.CommandText = """
            DELETE FROM admin_users;
            INSERT INTO admin_users (username, password_hash) VALUES (@u, @p);
            """;
        cmd.Parameters.AddWithValue("@u", username);
        cmd.Parameters.AddWithValue("@p", passwordHash);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // --------------------------------------------------------------- 审计日志

    public async Task WriteAuditAsync(string actor, string action, string target, string? detail,
        CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO audit_logs (actor, action, target, detail) VALUES (@a, @ac, @t, @d);";
        cmd.Parameters.AddWithValue("@a", actor);
        cmd.Parameters.AddWithValue("@ac", action);
        cmd.Parameters.AddWithValue("@t", target);
        cmd.Parameters.AddWithValue("@d", (object?)detail ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ------------------------------------------------------------------ 映射

    private static ApiKeyAccount MapKey(System.Data.Common.DbDataReader r)
    {
        var models = new List<string>();
        if (r.Str("allowed_models") is { Length: > 0 } json)
        {
            try { models = System.Text.Json.JsonSerializer.Deserialize<List<string>>(json) ?? new(); }
            catch { models = new(); }
        }

        return new ApiKeyAccount
        {
            Id = r.I64("id"),
            KeyPrefix = r.Str("key_prefix"),
            KeyHash = r.Str("key_hash"),
            Name = r.Str("name"),
            Status = (ApiKeyStatus)r.I32("status"),
            AllowedModels = models,
            QuotaTotalTokens = r.I64N("quota_total_tokens"),
            QuotaTotalCost = r.DecN("quota_total_cost"),
            QuotaTotalRequests = r.I64N("quota_total_requests"),
            RateLimitRpm = r.I32N("rate_limit_rpm"),
            MaxOutputTokensPerSec = r.DecN("max_output_tokens_per_sec"),
            QuotaTokensPerMinute = r.I64N("quota_tokens_per_minute"),
            QuotaTokensPerDay = r.I64N("quota_tokens_per_day"),
            UsedPromptTokens = r.I64("used_prompt_tokens"),
            UsedCompletionTokens = r.I64("used_completion_tokens"),
            UsedRequests = r.I64("used_requests"),
            UsedCost = r.Dec("used_cost"),
            OverrideInputPricePer1K = r.DecN("override_input_price_1k"),
            OverrideOutputPricePer1K = r.DecN("override_output_price_1k"),
            OverridePricePerRequest = r.DecN("override_price_per_request"),
            ExpiresAt = r.DtN("expires_at"),
            CreatedAt = r.Dt("created_at"),
            LastUsedAt = r.DtN("last_used_at")
        };
    }

    private static ModelPrice MapPrice(System.Data.Common.DbDataReader r) => new()
    {
        Id = r.I64("id"),
        Model = r.Str("model"),
        Mode = (PricingMode)r.I32("mode"),
        InputPricePer1K = r.Dec("input_price_per_1k"),
        OutputPricePer1K = r.Dec("output_price_per_1k"),
        PricePerRequest = r.Dec("price_per_request"),
        IsWildcard = r.Bool("is_wildcard"),
        Enabled = r.Bool("enabled"),
        UpdatedAt = r.Dt("updated_at")
    };

    private static UsageRecord MapUsage(System.Data.Common.DbDataReader r) => new()
    {
        Id = r.I64("id"),
        ApiKeyId = r.I64("api_key_id"),
        KeyPrefix = r.Str("key_prefix"),
        Model = r.Str("model"),
        Endpoint = r.Str("endpoint"),
        MeteringMode = (MeteringMode)r.I32("metering_mode"),
        PromptTokens = r.I64("prompt_tokens"),
        CompletionTokens = r.I64("completion_tokens"),
        ActualPromptTokens = r.I64N("actual_prompt_tokens"),
        ActualCompletionTokens = r.I64N("actual_completion_tokens"),
        EstimatedCompletionTokens = r.I64N("estimated_completion_tokens"),
        Cost = r.Dec("cost"),
        Currency = r.Str("currency"),
        Outcome = (RequestOutcome)r.I32("outcome"),
        HttpStatus = r.I32("http_status"),
        DurationMs = r.I32("duration_ms"),
        FirstTokenMs = r.I64N("first_token_ms"),
        ErrorMessage = r.StrN("error_message"),
        ClientIp = r.Str("client_ip"),
        CreatedAt = r.Dt("created_at")
    };
}

/// <summary>
/// DbDataReader 的按列名读取扩展。
/// 用 DbDataReader（而非驱动专有类型）以便兼容 MySqlConnector / MySql.Data 等不同实现。
/// </summary>
internal static class DbReaderExtensions
{
    private static int Ord(this System.Data.Common.DbDataReader r, string name) => r.GetOrdinal(name);

    public static long I64(this System.Data.Common.DbDataReader r, string n) => r.GetInt64(r.Ord(n));
    public static int I32(this System.Data.Common.DbDataReader r, string n) => r.GetInt32(r.Ord(n));
    public static decimal Dec(this System.Data.Common.DbDataReader r, string n) => r.GetDecimal(r.Ord(n));
    public static string Str(this System.Data.Common.DbDataReader r, string n) => r.GetString(r.Ord(n));
    public static DateTime Dt(this System.Data.Common.DbDataReader r, string n) => r.GetDateTime(r.Ord(n));
    public static bool Bool(this System.Data.Common.DbDataReader r, string n) => Convert.ToBoolean(r.GetValue(r.Ord(n)));

    public static long? I64N(this System.Data.Common.DbDataReader r, string n)
    {
        var o = r.Ord(n);
        return r.IsDBNull(o) ? null : r.GetInt64(o);
    }

    public static int? I32N(this System.Data.Common.DbDataReader r, string n)
    {
        var o = r.Ord(n);
        return r.IsDBNull(o) ? null : r.GetInt32(o);
    }

    public static decimal? DecN(this System.Data.Common.DbDataReader r, string n)
    {
        var o = r.Ord(n);
        return r.IsDBNull(o) ? null : r.GetDecimal(o);
    }

    public static DateTime? DtN(this System.Data.Common.DbDataReader r, string n)
    {
        var o = r.Ord(n);
        return r.IsDBNull(o) ? null : r.GetDateTime(o);
    }

    public static string? StrN(this System.Data.Common.DbDataReader r, string n)
    {
        var o = r.Ord(n);
        return r.IsDBNull(o) ? null : r.GetString(o);
    }
}

/// <summary>管理端账号。</summary>
public sealed class AdminAccount
{
    public long Id { get; set; }
    public string Username { get; set; } = "";
    public string PasswordHash { get; set; } = "";
}

/// <summary>用量汇总。</summary>
public sealed class UsageSummary
{
    public long PromptTokens { get; set; }
    public long CompletionTokens { get; set; }
    public long Requests { get; set; }
    public long SuccessRequests { get; set; }
    public decimal Cost { get; set; }
    public long TotalTokens => PromptTokens + CompletionTokens;
}

/// <summary>估算 vs 实测的校准报告。</summary>
public sealed class CalibrationReport
{
    public int Days { get; set; }
    public long SampleCount { get; set; }
    public double AvgEstimated { get; set; }
    public double AvgActual { get; set; }
    public long SumEstimated { get; set; }
    public long SumActual { get; set; }

    /// <summary>实测 / 估算 的比值。理想值 1.0；大于 1 说明估算偏低、在亏损。</summary>
    public double Ratio => SumEstimated == 0 ? 0 : (double)SumActual / SumEstimated;

    /// <summary>建议的 CharsPerToken 修正系数（乘到当前配置上）。</summary>
    public double SuggestedCharsPerTokenFactor => Ratio <= 0 ? 1.0 : 1.0 / Ratio;
}
