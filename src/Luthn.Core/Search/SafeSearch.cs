using Luthn.Core.Classification;

namespace Luthn.Core.Search;

public static class SafeSearchLimits
{
    public const int DefaultMaxItems = 20;
    public const int MaxItemsUpperBound = 100;

    public static int ClampMaxItems(int maxItems) =>
        Math.Clamp(maxItems, 1, MaxItemsUpperBound);
}

public sealed record SafeSearchRequest
{
    public SafeSearchRequest()
    {
    }

    public SafeSearchRequest(
        string? query,
        IReadOnlyList<string>? coreTags,
        int maxItems = SafeSearchLimits.DefaultMaxItems,
        string? projectKey = null,
        string? taskKey = null,
        IReadOnlyList<string>? topicTags = null)
    {
        Query = string.IsNullOrWhiteSpace(query) ? null : query.Trim();
        CoreTags = NormalizeCoreTags(coreTags);
        MaxItems = maxItems;
        ProjectKey = RecallMetadata.NormalizeKey(projectKey);
        TaskKey = RecallMetadata.NormalizeKey(taskKey);
        TopicTags = RecallMetadata.NormalizeTopicTags(topicTags);
    }

    public string? Query { get; init; }

    public IReadOnlyList<string> CoreTags { get; init; } = [];

    public int MaxItems { get; init; } = SafeSearchLimits.DefaultMaxItems;

    public string? ProjectKey { get; init; }
    public string? TaskKey { get; init; }
    public IReadOnlyList<string> TopicTags { get; init; } = [];

    public static IReadOnlyList<string> NormalizeCoreTags(IEnumerable<string>? coreTags) =>
        coreTags is null
            ? []
            : coreTags
                .Where(tag => !string.IsNullOrWhiteSpace(tag))
                .Select(tag => tag.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
}

public sealed record SafeSearchResponse(
    string? Query,
    IReadOnlyList<string> CoreTags,
    IReadOnlyList<SafeSearchResult> Results)
{
    public string? ProjectKey { get; init; }
    public string? TaskKey { get; init; }
    public IReadOnlyList<string> TopicTags { get; init; } = [];
}

public sealed record SafeSearchResult(
    string Id,
    string Title,
    string SafeSummary,
    SensitivityLevel Sensitivity,
    IReadOnlyList<string> CoreTags,
    int Score)
{
    public string? ProjectKey { get; init; }
    public string? TaskKey { get; init; }
    public IReadOnlyList<string> TopicTags { get; init; } = [];
    public DateTimeOffset ProjectionTimestamp { get; init; }
}
