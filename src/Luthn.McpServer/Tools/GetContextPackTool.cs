using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Luthn.AgentConnector.Http;
using Luthn.Sdk.Context;

namespace Luthn.McpServer.Tools;

public sealed class GetContextPackTool : ILuthnMcpTool
{
    private const int MaximumCacheKeyLength = 256;
    private const int MaximumTokenBudget = 4_000;
    private const int MaximumTimeoutMilliseconds = 5_000;
    private const int MaximumCacheTtlSeconds = 3_600;
    private const int MaximumCacheEntries = 128;

    private readonly ILuthnClient _client;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);

    public GetContextPackTool(ILuthnClient client, Func<DateTimeOffset>? utcNow = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public string Name => "get_context_pack";

    public async Task<object> InvokeAsync(
        JsonElement arguments,
        CancellationToken cancellationToken = default)
    {
        var query = ReadOptionalString(arguments, "query");
        var coreTags = ReadCoreTags(arguments);
        var maxItems = ReadBoundedInteger(arguments, "maxItems", 20, 1, 50);
        var maxTokens = ReadOptionalBoundedInteger(
            arguments,
            "maxTokens",
            1,
            MaximumTokenBudget);
        var timeoutMs = ReadOptionalBoundedInteger(
            arguments,
            "timeoutMs",
            1,
            MaximumTimeoutMilliseconds);
        var cacheTtlSeconds = ReadOptionalBoundedInteger(
            arguments,
            "cacheTtlSeconds",
            1,
            MaximumCacheTtlSeconds);
        var cacheKey = ReadOptionalString(arguments, "cacheKey");
        var failOpen = arguments.TryGetProperty("failOpen", out var failOpenElement) &&
            failOpenElement.ValueKind is JsonValueKind.True;

        if (cacheKey?.Length > MaximumCacheKeyLength)
        {
            throw new ArgumentException(
                $"cacheKey must be {MaximumCacheKeyLength} characters or fewer.");
        }

        var effectiveCacheKey = cacheKey is null || cacheTtlSeconds is null
            ? null
            : BuildCacheKey(cacheKey, query, coreTags, maxItems, maxTokens);
        var now = _utcNow();
        if (effectiveCacheKey is not null &&
            _cache.TryGetValue(effectiveCacheKey, out var cached) &&
            cached.ExpiresAt > now)
        {
            return cached.ContextPack;
        }

        using var timeoutSource = timeoutMs is null
            ? null
            : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource?.CancelAfter(TimeSpan.FromMilliseconds(timeoutMs.GetValueOrDefault()));
        var effectiveCancellationToken = timeoutSource?.Token ?? cancellationToken;

        ContextPackDto result;
        try
        {
            result = await _client.GetContextPackAsync(
                coreTags,
                maxItems,
                query,
                effectiveCancellationToken);
        }
        catch (Exception) when (failOpen && !cancellationToken.IsCancellationRequested)
        {
            return new ContextPackDto(coreTags, []);
        }

        var bounded = ApplyBounds(result, maxItems, maxTokens);
        if (effectiveCacheKey is not null)
        {
            PruneCache(now);
            _cache[effectiveCacheKey] = new CacheEntry(
                bounded,
                now.AddSeconds(cacheTtlSeconds.GetValueOrDefault()));
        }

        return bounded;
    }

    private static ContextPackDto ApplyBounds(
        ContextPackDto contextPack,
        int maxItems,
        int? maxTokens)
    {
        var candidates = contextPack.Items.Take(maxItems);
        if (maxTokens is null)
        {
            return new ContextPackDto(contextPack.CoreTags, candidates.ToArray());
        }

        var remainingTokens = maxTokens.Value;
        var items = new List<ContextPackItemDto>();
        foreach (var item in candidates)
        {
            var estimatedTokens = EstimateTokens(item);
            if (estimatedTokens > remainingTokens)
            {
                continue;
            }

            items.Add(item);
            remainingTokens -= estimatedTokens;
        }

        return new ContextPackDto(contextPack.CoreTags, items);
    }

    private static int EstimateTokens(ContextPackItemDto item)
    {
        const int JsonFieldOverheadCharacters = 80;
        var characters = JsonFieldOverheadCharacters +
            item.Id.Length +
            item.Title.Length +
            item.SafeSummary.Length +
            item.Sensitivity.Length +
            item.CoreTags.Sum(tag => tag.Length + 3);
        return Math.Max(1, (characters + 2) / 3);
    }

    private static string BuildCacheKey(
        string cacheKey,
        string? query,
        IReadOnlyList<string> coreTags,
        int maxItems,
        int? maxTokens) =>
        string.Join(
            '\u001f',
            cacheKey,
            query ?? string.Empty,
            string.Join('\u001e', coreTags),
            maxItems.ToString(CultureInfo.InvariantCulture),
            maxTokens?.ToString(CultureInfo.InvariantCulture) ?? string.Empty);

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

    private static string? ReadOptionalString(JsonElement arguments, string name) =>
        arguments.TryGetProperty(name, out var element) &&
        element.ValueKind is JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(element.GetString())
            ? element.GetString()!.Trim()
            : null;

    private static int ReadBoundedInteger(
        JsonElement arguments,
        string name,
        int defaultValue,
        int minimum,
        int maximum) =>
        ReadOptionalBoundedInteger(arguments, name, minimum, maximum) ?? defaultValue;

    private static int? ReadOptionalBoundedInteger(
        JsonElement arguments,
        string name,
        int minimum,
        int maximum)
    {
        if (!arguments.TryGetProperty(name, out var element))
        {
            return null;
        }

        if (!element.TryGetInt32(out var value) || value < minimum || value > maximum)
        {
            throw new ArgumentException($"{name} must be between {minimum} and {maximum}.");
        }

        return value;
    }

    private static IReadOnlyList<string> ReadCoreTags(JsonElement arguments)
    {
        if (!arguments.TryGetProperty("coreTags", out var tagsElement) ||
            tagsElement.ValueKind is not JsonValueKind.Array)
        {
            return [];
        }

        return tagsElement
            .EnumerateArray()
            .Where(tag => tag.ValueKind is JsonValueKind.String)
            .Select(tag => tag.GetString())
            .OfType<string>()
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .ToArray();
    }

    private sealed record CacheEntry(ContextPackDto ContextPack, DateTimeOffset ExpiresAt);
}
