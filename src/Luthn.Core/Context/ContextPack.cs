using Luthn.Core.Classification;

namespace Luthn.Core.Context;

public sealed record ContextPackRequest
{
    public ContextPackRequest()
    {
    }

    public ContextPackRequest(IReadOnlyList<string>? coreTags, int maxItems = 20, string? query = null)
    {
        CoreTags = coreTags ?? [];
        MaxItems = maxItems;
        Query = query;
    }

    public string? Query { get; init; }

    public IReadOnlyList<string> CoreTags { get; init; } = [];

    public int MaxItems { get; init; } = 20;
}

public sealed record ContextPack(
    IReadOnlyList<string> CoreTags,
    IReadOnlyList<ContextPackItem> Items);

public sealed record ContextPackItem(
    string Id,
    string Title,
    string SafeSummary,
    SensitivityLevel Sensitivity,
    IReadOnlyList<string> CoreTags);

public sealed record ContextPackCandidate(
    string Id,
    string Title,
    string SafeSummary,
    SensitivityLevel Sensitivity,
    IReadOnlyList<string> CoreTags,
    bool AllowsAgentContext);
