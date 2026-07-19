using System.Collections.Concurrent;
using System.Text.Json;
using Luthn.AgentConnector.Http;
using Luthn.Sdk.Access;

namespace Luthn.McpServer.Tools;

public sealed class GetSensitiveAccessRequestTool : ILuthnMcpTool
{
    private const int MaximumCacheEntries = 128;
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(1);
    private readonly ILuthnAgentClient _client;
    private readonly string _principalCachePartition;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);

    public GetSensitiveAccessRequestTool(
        ILuthnAgentClient client,
        string principalCachePartition = "single-owner-local",
        Func<DateTimeOffset>? utcNow = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _principalCachePartition = string.IsNullOrWhiteSpace(principalCachePartition)
            ? throw new ArgumentException("Principal cache partition is required.", nameof(principalCachePartition))
            : principalCachePartition.Trim();
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public string Name => "get_sensitive_access_request";

    public async Task<object> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        var id = McpToolArguments.ReadRequiredString(arguments, "id");
        var cacheKey = BuildCacheKey(_principalCachePartition, id);
        var now = _utcNow();
        if (_cache.TryGetValue(cacheKey, out var cached) && cached.ExpiresAt > now)
        {
            return cached.Value;
        }

        if (cached is not null)
        {
            _cache.TryRemove(cacheKey, out _);
        }

        var value = await _client.GetSensitiveAccessRequestAsync(id, cancellationToken);
        PruneCache(now);
        _cache[cacheKey] = new CacheEntry(value, now.Add(CacheLifetime));
        return value;
    }

    internal static string BuildCacheKey(string principalCachePartition, string id) =>
        $"{principalCachePartition}\u001f{id}";

    private void PruneCache(DateTimeOffset now)
    {
        foreach (var entry in _cache.Where(entry => entry.Value.ExpiresAt <= now))
        {
            _cache.TryRemove(entry.Key, out _);
        }

        while (_cache.Count >= MaximumCacheEntries)
        {
            var oldestKey = _cache.MinBy(entry => entry.Value.ExpiresAt).Key;
            if (!_cache.TryRemove(oldestKey, out _))
            {
                break;
            }
        }
    }

    private sealed record CacheEntry(SensitiveAccessRequestDto Value, DateTimeOffset ExpiresAt);
}
