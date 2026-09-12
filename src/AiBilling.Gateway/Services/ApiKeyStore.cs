using System.Collections.Concurrent;
using AiBilling.Core.Data;
using AiBilling.Core.Models;
using AiBilling.Core.Security;

namespace AiBilling.Gateway.Services;

/// <summary>
/// Key 查询缓存。每个请求都查库会成为瓶颈，这里按哈希缓存若干秒。
/// 缓存命中时返回的是快照副本，因此不会污染实时用量；
/// 配额校验用的是缓存快照 + 内存增量，精度足够，落库时以数据库为准。
/// </summary>
public sealed class ApiKeyStore
{
    private readonly BillingRepository _repo;
    private readonly TimeSpan _ttl;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new();

    public ApiKeyStore(BillingRepository repo, TimeSpan ttl)
    {
        _repo = repo;
        _ttl = ttl;
    }

    private sealed record CacheEntry(ApiKeyAccount Key, DateTime CachedAt);

    /// <summary>按明文 Key 查找账户。未找到返回 null。</summary>
    public async Task<ApiKeyAccount?> ResolveAsync(string plainKey, CancellationToken ct = default)
    {
        var hash = ApiKeyHasher.Hash(plainKey);

        if (_cache.TryGetValue(hash, out var hit) && DateTime.UtcNow - hit.CachedAt < _ttl)
            return hit.Key;

        var account = await _repo.FindKeyByHashAsync(hash, ct);
        if (account is null)
        {
            // 缓存"不存在"以避免对无效 Key 的暴力探测打满数据库
            _cache[hash] = new CacheEntry(null!, DateTime.UtcNow);
            return null;
        }

        _cache[hash] = new CacheEntry(account, DateTime.UtcNow);
        return account;
    }

    /// <summary>管理端修改 Key 后调用，使缓存立即失效。</summary>
    public void Invalidate(string? keyHash = null)
    {
        if (keyHash is null) _cache.Clear();
        else _cache.TryRemove(keyHash, out _);
    }

    public void Cleanup()
    {
        var now = DateTime.UtcNow;
        foreach (var kv in _cache)
        {
            if (now - kv.Value.CachedAt > _ttl * 4) _cache.TryRemove(kv.Key, out _);
        }
    }
}
