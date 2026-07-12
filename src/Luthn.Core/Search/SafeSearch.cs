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

    public SafeSearchRequest(string? query, IReadOnlyList<string>? coreTags, int maxItems = SafeSearchLimits.DefaultMaxItems)
    {
        Query = string.IsNullOrWhiteSpace(query) ? null : query.Trim();
        CoreTags = NormalizeCoreTags(coreTags);
        MaxItems = maxItems;
    }

    public string? Query { get; init; }

    public IReadOnlyList<string> CoreTags { get; init; } = [];

    public int MaxItems { get; init; } = SafeSearchLimits.DefaultMaxItems;

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
    IReadOnlyList<SafeSearchResult> Results);

public sealed record SafeSearchResult(
    string Id,
    string Title,
    string SafeSummary,
    SensitivityLevel Sensitivity,
    IReadOnlyList<string> CoreTags,
    int Score);
