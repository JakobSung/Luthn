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
    private const int CandidatePoolMultiplier = 4;
    private const int MaximumCandidatePoolSize = 50;

    private readonly ILuthnAgentClient _client;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);

    public GetContextPackTool(ILuthnAgentClient client, Func<DateTimeOffset>? utcNow = null)
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
        var projectKey = ReadOptionalString(arguments, "projectKey");
        var taskKey = ReadOptionalString(arguments, "taskKey");
        var topicTags = ReadTags(arguments, "topicTags");
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
            : BuildCacheKey(
                cacheKey,
                query,
                coreTags,
                maxItems,
                maxTokens,
                projectKey,
                taskKey,
                topicTags);
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
            var candidateMaxItems = maxTokens is null
                ? maxItems
                : Math.Min(MaximumCandidatePoolSize, maxItems * CandidatePoolMultiplier);
            result = await _client.GetContextPackAsync(
                new ContextPackRequestDto(
                    coreTags,
                    candidateMaxItems,
                    query,
                    projectKey,
                    taskKey,
                    topicTags),
                effectiveCancellationToken);
        }
        catch (Exception) when (failOpen && !cancellationToken.IsCancellationRequested)
        {
            return new ContextPackDto(coreTags, [])
            {
                ProjectKey = projectKey,
                TaskKey = taskKey,
                TopicTags = topicTags
            };
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
        var candidates = contextPack.Items.ToArray();
        if (maxTokens is null)
        {
            return CopyPack(contextPack, candidates.Take(maxItems).ToArray());
        }

        for (var desiredItems = Math.Min(maxItems, candidates.Length); desiredItems > 0; desiredItems--)
        {
            var remainingTokens = maxTokens.Value;
            var items = new List<ContextPackItemDto>();
            foreach (var item in candidates)
            {
                if (items.Count >= desiredItems || remainingTokens <= 0)
                {
                    break;
                }

                var remainingSlots = desiredItems - items.Count;
                var itemBudget = remainingTokens / remainingSlots;
                var fitted = FitWithinTokenBudget(item, itemBudget);
                if (fitted is null)
                {
                    continue;
                }

                items.Add(fitted);
                remainingTokens -= EstimateTokens(fitted);
            }

            if (items.Count == desiredItems)
            {
                return CopyPack(contextPack, items);
            }
        }

        return CopyPack(contextPack, []);
    }

    private static ContextPackDto CopyPack(
        ContextPackDto source,
        IReadOnlyList<ContextPackItemDto> items) =>
        new(source.CoreTags, items)
        {
            ProjectKey = source.ProjectKey,
            TaskKey = source.TaskKey,
            TopicTags = source.TopicTags
        };

    private static int EstimateTokens(ContextPackItemDto item)
    {
        var characters = EstimateCharacters(item);
        return Math.Max(1, (characters + 2) / 3);
    }

    private static int EstimateCharacters(ContextPackItemDto item)
    {
        const int JsonFieldOverheadCharacters = 80;
        return JsonFieldOverheadCharacters +
            item.Id.Length +
            item.Title.Length +
            item.SafeSummary.Length +
            item.Sensitivity.Length +
            item.CoreTags.Sum(tag => tag.Length + 3) +
            (item.ProjectKey?.Length ?? 0) +
            (item.TaskKey?.Length ?? 0) +
            item.TopicTags.Sum(tag => tag.Length + 3) +
            item.ProjectionTimestamp.ToString("O", CultureInfo.InvariantCulture).Length;
    }

    private static ContextPackItemDto? FitWithinTokenBudget(
        ContextPackItemDto item,
        int tokenBudget)
    {
        if (tokenBudget <= 0)
        {
            return null;
        }

        if (EstimateTokens(item) <= tokenBudget)
        {
            return item;
        }

        var fixedCharacters = EstimateCharacters(item) - item.SafeSummary.Length;
        var maximumSummaryCharacters = (tokenBudget * 3) - 2 - fixedCharacters;
        if (maximumSummaryCharacters <= 0)
        {
            return null;
        }

        var fitted = item with
        {
            SafeSummary = TruncateSummary(item.SafeSummary, maximumSummaryCharacters)
        };
        return EstimateTokens(fitted) <= tokenBudget ? fitted : null;
    }

    private static string TruncateSummary(string summary, int maximumCharacters)
    {
        if (summary.Length <= maximumCharacters)
        {
            return summary;
        }

        if (maximumCharacters == 1)
        {
            return "…";
        }

        var contentLength = maximumCharacters - 1;
        if (contentLength < summary.Length &&
            contentLength > 0 &&
            char.IsHighSurrogate(summary[contentLength - 1]) &&
            char.IsLowSurrogate(summary[contentLength]))
        {
            contentLength--;
        }

        var prefix = summary[..contentLength].TrimEnd();
        if (prefix.Length == 0)
        {
            return "…";
        }

        var sentenceBoundary = prefix.LastIndexOfAny(['.', '!', '?', '\n']);
        if (sentenceBoundary >= prefix.Length / 2)
        {
            prefix = prefix[..(sentenceBoundary + 1)].TrimEnd();
        }

        return $"{prefix}…";
    }

    private static string BuildCacheKey(
        string cacheKey,
        string? query,
        IReadOnlyList<string> coreTags,
        int maxItems,
        int? maxTokens,
        string? projectKey,
        string? taskKey,
        IReadOnlyList<string> topicTags) =>
        string.Join(
            '\u001f',
            cacheKey,
            query ?? string.Empty,
            string.Join('\u001e', coreTags),
            maxItems.ToString(CultureInfo.InvariantCulture),
            maxTokens?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            projectKey ?? string.Empty,
            taskKey ?? string.Empty,
            string.Join('\u001e', topicTags));

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

    private static IReadOnlyList<string> ReadCoreTags(JsonElement arguments) =>
        ReadTags(arguments, "coreTags");

    private static IReadOnlyList<string> ReadTags(JsonElement arguments, string name)
    {
        if (!arguments.TryGetProperty(name, out var tagsElement) ||
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
