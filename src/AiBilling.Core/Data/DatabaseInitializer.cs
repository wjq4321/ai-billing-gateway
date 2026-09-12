using MySql.Data.MySqlClient;

namespace AiBilling.Core.Data;

/// <summary>建库建表与初始化数据。可重复执行（幂等）。</summary>
public static class DatabaseInitializer
{
    public const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS api_keys (
            id                        BIGINT        NOT NULL AUTO_INCREMENT,
            key_prefix                VARCHAR(32)   NOT NULL,
            key_hash                  CHAR(64)      NOT NULL,
            name                      VARCHAR(128)  NOT NULL DEFAULT '',
            status                    TINYINT       NOT NULL DEFAULT 1,
            allowed_models            JSON          NULL,
            quota_total_tokens        BIGINT        NULL,
            quota_total_cost          DECIMAL(18,6) NULL,
            quota_total_requests      BIGINT        NULL,
            rate_limit_rpm            INT           NULL,
            max_output_tokens_per_sec DECIMAL(10,2) NULL,
            quota_tokens_per_minute   BIGINT        NULL,
            quota_tokens_per_day      BIGINT        NULL,
            used_prompt_tokens        BIGINT        NOT NULL DEFAULT 0,
            used_completion_tokens    BIGINT        NOT NULL DEFAULT 0,
            used_requests             BIGINT        NOT NULL DEFAULT 0,
            used_cost                 DECIMAL(18,6) NOT NULL DEFAULT 0,
            override_input_price_1k   DECIMAL(18,6) NULL,
            override_output_price_1k  DECIMAL(18,6) NULL,
            override_price_per_request DECIMAL(18,6) NULL,
            expires_at                DATETIME(3)   NULL,
            created_at                DATETIME(3)   NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
            last_used_at              DATETIME(3)   NULL,
            PRIMARY KEY (id),
            UNIQUE KEY ux_api_keys_hash (key_hash),
            KEY ix_api_keys_prefix (key_prefix)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

        CREATE TABLE IF NOT EXISTS model_prices (
            id                  BIGINT        NOT NULL AUTO_INCREMENT,
            model               VARCHAR(191)  NOT NULL,
            mode                TINYINT       NOT NULL DEFAULT 1,
            input_price_per_1k  DECIMAL(18,6) NOT NULL DEFAULT 0,
            output_price_per_1k DECIMAL(18,6) NOT NULL DEFAULT 0,
            price_per_request   DECIMAL(18,6) NOT NULL DEFAULT 0,
            is_wildcard         TINYINT(1)    NOT NULL DEFAULT 0,
            enabled             TINYINT(1)    NOT NULL DEFAULT 1,
            updated_at          DATETIME(3)   NOT NULL DEFAULT CURRENT_TIMESTAMP(3)
                                ON UPDATE CURRENT_TIMESTAMP(3),
            PRIMARY KEY (id),
            UNIQUE KEY ux_model_prices_model (model)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

        CREATE TABLE IF NOT EXISTS usage_records (
            id                          BIGINT        NOT NULL AUTO_INCREMENT,
            api_key_id                  BIGINT        NOT NULL,
            key_prefix                  VARCHAR(32)   NOT NULL DEFAULT '',
            model                       VARCHAR(191)  NOT NULL DEFAULT '',
            endpoint                    VARCHAR(128)  NOT NULL DEFAULT '',
            metering_mode               TINYINT       NOT NULL DEFAULT 1,
            prompt_tokens               BIGINT        NOT NULL DEFAULT 0,
            completion_tokens           BIGINT        NOT NULL DEFAULT 0,
            actual_prompt_tokens        BIGINT        NULL,
            actual_completion_tokens    BIGINT        NULL,
            estimated_completion_tokens BIGINT        NULL,
            cost                        DECIMAL(18,6) NOT NULL DEFAULT 0,
            currency                    VARCHAR(8)    NOT NULL DEFAULT 'CNY',
            outcome                     TINYINT       NOT NULL DEFAULT 1,
            http_status                 INT           NOT NULL DEFAULT 0,
            duration_ms                 INT           NOT NULL DEFAULT 0,
            first_token_ms              BIGINT        NULL,
            error_message               VARCHAR(512)  NULL,
            client_ip                   VARCHAR(64)   NOT NULL DEFAULT '',
            created_at                  DATETIME(3)   NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
            PRIMARY KEY (id),
            KEY ix_usage_key_time (api_key_id, created_at),
            KEY ix_usage_time (created_at),
            KEY ix_usage_model (model)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

        CREATE TABLE IF NOT EXISTS admin_users (
            id            BIGINT       NOT NULL AUTO_INCREMENT,
            username      VARCHAR(64)  NOT NULL,
            password_hash VARCHAR(255) NOT NULL,
            created_at    DATETIME(3)  NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
            PRIMARY KEY (id),
            UNIQUE KEY ux_admin_users_username (username)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

        CREATE TABLE IF NOT EXISTS audit_logs (
            id         BIGINT       NOT NULL AUTO_INCREMENT,
            actor      VARCHAR(64)  NOT NULL DEFAULT '',
            action     VARCHAR(64)  NOT NULL DEFAULT '',
            target     VARCHAR(191) NOT NULL DEFAULT '',
            detail     VARCHAR(1024) NULL,
            created_at DATETIME(3)  NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
            PRIMARY KEY (id),
            KEY ix_audit_time (created_at)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
        """;

    /// <summary>
    /// 创建数据库（若不存在）并建表。serverConnectionString 不含 Database，
    /// 例如 "Server=127.0.0.1;Port=3306;User ID=ai_billing;Password=你的密码;"。
    /// </summary>
    public static async Task InitializeAsync(
        string serverConnectionString,
        string databaseName,
        CancellationToken ct = default)
    {
        if (!IsValidDatabaseName(databaseName))
            throw new ArgumentException($"非法的数据库名: {databaseName}", nameof(databaseName));

        await using (var conn = new MySqlConnection(serverConnectionString))
        {
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                $"CREATE DATABASE IF NOT EXISTS `{databaseName}` " +
                "DEFAULT CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci;";
            await cmd.ExecuteNonQueryAsync(ct);
        }

        var builder = new MySqlConnectionStringBuilder(serverConnectionString) { Database = databaseName };
        await using (var conn = new MySqlConnection(builder.ConnectionString))
        {
            await conn.OpenAsync(ct);
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = SchemaSql;
                await cmd.ExecuteNonQueryAsync(ct);
            }

            // CREATE TABLE IF NOT EXISTS 不会给已存在的表补列，因此新字段需要单独迁移
            await ApplyMigrationsAsync(conn, ct);
        }
    }

    /// <summary>
    /// 增量迁移。每个升级项写成"若列不存在则添加"，可重复执行。
    /// MySQL 8.0.29+ 支持 ADD COLUMN IF NOT EXISTS，但为兼容旧版本，
    /// 这里先查 information_schema 再决定是否执行。
    /// </summary>
    private static async Task ApplyMigrationsAsync(MySqlConnection conn, CancellationToken ct)
    {
        var migrations = new (string Table, string Column, string Definition)[]
        {
            // v1.1：输出速度限速 + token 消耗速率配额
            ("api_keys", "max_output_tokens_per_sec", "DECIMAL(10,2) NULL COMMENT '输出速度上限 token/秒，NULL=不限'"),
            ("api_keys", "quota_tokens_per_minute", "BIGINT NULL COMMENT '每分钟 token 消耗上限，NULL=不限'"),
            ("api_keys", "quota_tokens_per_day", "BIGINT NULL COMMENT '每天 token 消耗上限，NULL=不限'"),
        };

        foreach (var (table, column, definition) in migrations)
        {
            await using var check = conn.CreateCommand();
            check.CommandText = """
                SELECT COUNT(*) FROM information_schema.COLUMNS
                WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @t AND COLUMN_NAME = @c;
                """;
            check.Parameters.AddWithValue("@t", table);
            check.Parameters.AddWithValue("@c", column);

            var exists = Convert.ToInt64(await check.ExecuteScalarAsync(ct)) > 0;
            if (exists) continue;

            await using var alter = conn.CreateCommand();
            alter.CommandText = $"ALTER TABLE `{table}` ADD COLUMN `{column}` {definition};";
            await alter.ExecuteNonQueryAsync(ct);
        }
    }

    /// <summary>校验库名，防止 SQL 注入（库名无法参数化）。</summary>
    public static bool IsValidDatabaseName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 64) return false;
        foreach (var ch in name)
        {
            if (!char.IsLetterOrDigit(ch) && ch != '_' && ch != '$') return false;
        }
        return true;
    }

    /// <summary>从连接串中取出数据库名。</summary>
    public static string GetDatabaseName(string connectionString)
    {
        var b = new MySqlConnectionStringBuilder(connectionString);
        return b.Database;
    }

    /// <summary>返回不含 Database 的连接串（用于先建库）。</summary>
    public static string StripDatabase(string connectionString)
    {
        var b = new MySqlConnectionStringBuilder(connectionString) { Database = "" };
        return b.ConnectionString;
    }

    public static async Task<bool> CheckConnectionAsync(string connectionString, CancellationToken ct = default)
    {
        try
        {
            await using var conn = new MySqlConnection(connectionString);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT 1;";
            await cmd.ExecuteScalarAsync(ct);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
