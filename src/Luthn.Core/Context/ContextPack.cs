using Luthn.Core.Classification;

namespace Luthn.Core.Context;

public sealed record ContextPackRequest
{
    public ContextPackRequest()
    {
    }

    public ContextPackRequest(
        IReadOnlyList<string>? coreTags,
        int maxItems = 20,
        string? query = null,
        string? projectKey = null,
        string? taskKey = null,
        IReadOnlyList<string>? topicTags = null)
    {
        CoreTags = coreTags ?? [];
        MaxItems = maxItems;
        Query = query;
        ProjectKey = projectKey;
        TaskKey = taskKey;
        TopicTags = topicTags ?? [];
    }

    public string? Query { get; init; }

    public IReadOnlyList<string> CoreTags { get; init; } = [];

    public int MaxItems { get; init; } = 20;

    public string? ProjectKey { get; init; }

    public string? TaskKey { get; init; }

    public IReadOnlyList<string> TopicTags { get; init; } = [];
}

public sealed record ContextPack(IReadOnlyList<string> CoreTags, IReadOnlyList<ContextPackItem> Items)
{
    public string? ProjectKey { get; init; }
    public string? TaskKey { get; init; }
    public IReadOnlyList<string> TopicTags { get; init; } = [];
}

public sealed record ContextPackItem(
    string Id,
    string Title,
    string SafeSummary,
    SensitivityLevel Sensitivity,
    IReadOnlyList<string> CoreTags)
{
    public string? ProjectKey { get; init; }
    public string? TaskKey { get; init; }
    public IReadOnlyList<string> TopicTags { get; init; } = [];
    public DateTimeOffset ProjectionTimestamp { get; init; }
}

public sealed record ContextPackCandidate(
    string Id,
    string Title,
    string SafeSummary,
    SensitivityLevel Sensitivity,
    IReadOnlyList<string> CoreTags,
    bool AllowsAgentContext)
{
    public string? ProjectKey { get; init; }
    public string? TaskKey { get; init; }
    public IReadOnlyList<string> TopicTags { get; init; } = [];
    public DateTimeOffset ProjectionTimestamp { get; init; }
}
