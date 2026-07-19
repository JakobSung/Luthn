using Luthn.Core.Search;

namespace Luthn.Core.Context;

public sealed class ContextPackBuilder
{
    private readonly IRetrievalBackend retrievalBackend;

    public ContextPackBuilder()
        : this(new DeterministicRetrievalBackend(new SafeSearchIndex()))
    {
    }

    public ContextPackBuilder(IRetrievalBackend retrievalBackend)
    {
        this.retrievalBackend = retrievalBackend;
    }

    public ContextPack Build(ContextPackRequest request, IEnumerable<ContextPackCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(candidates);

        var search = retrievalBackend.Search(
            new SafeSearchRequest(
                request.Query,
                request.CoreTags,
                request.MaxItems,
                request.ProjectKey,
                request.TaskKey,
                request.TopicTags),
            candidates);

        var items = search.Results
            .Select(result => new ContextPackItem(
                result.Id,
                result.Title,
                result.SafeSummary,
                result.Sensitivity,
                result.CoreTags)
            {
                ProjectKey = result.ProjectKey,
                TaskKey = result.TaskKey,
                TopicTags = result.TopicTags,
                ProjectionTimestamp = result.ProjectionTimestamp
            })
            .ToArray();

        return new ContextPack(search.CoreTags, items)
        {
            ProjectKey = search.ProjectKey,
            TaskKey = search.TaskKey,
            TopicTags = search.TopicTags
        };
    }
}
