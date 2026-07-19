using System.Linq.Expressions;
using Luthn.Core.Classification;
using Luthn.Core.Context;
using Luthn.Core.Memory;
using Luthn.Core.Persistence;
using Luthn.Core.Search;
using Microsoft.EntityFrameworkCore;

namespace Luthn.Host.Api;

public static class RetrievalCandidateLimits
{
    public const int MaxCandidatesPerCorpus = 512;
    public const int MaxCombinedCandidates = MaxCandidatesPerCorpus * 2;
}

public interface IRetrievalCandidateSelector
{
    Task<IReadOnlyList<ContextPackCandidate>> SelectAgentContextAsync(
        SafeSearchRequest request,
        string ownerUserId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ContextPackCandidate>> SelectSharedMemoryAsync(
        SafeSearchRequest request,
        string ownerUserId,
        CancellationToken cancellationToken);
}

public sealed class DbBackedRetrievalCandidateSelector(
    LuthnDbContext db,
    TimeProvider timeProvider,
    IOperationalMetrics? metrics = null) : IRetrievalCandidateSelector
{
    private readonly IOperationalMetrics _metrics = metrics ?? NullOperationalMetrics.Instance;
    public async Task<IReadOnlyList<ContextPackCandidate>> SelectAgentContextAsync(
        SafeSearchRequest request,
        string ownerUserId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var normalizedRequest = new SafeSearchRequest(
            request.Query,
            request.CoreTags,
            request.MaxItems,
            request.ProjectKey,
            request.TaskKey,
            request.TopicTags);
        var wikiCandidates = await SelectWikiAsync(normalizedRequest, ownerUserId, cancellationToken);
        var memoryCandidates = await SelectMemoryAsync(normalizedRequest, ownerUserId, cancellationToken);

        LuthnHostMetrics.SafeSearchCandidateCount.Record(
            wikiCandidates.Length,
            new KeyValuePair<string, object?>("source", "wiki_proposals"));
        _metrics.RecordSafeSearchCandidates("wiki_proposals", wikiCandidates.Length);
        LuthnHostMetrics.SafeSearchCandidateCount.Record(
            memoryCandidates.Length,
            new KeyValuePair<string, object?>("source", "shared_memory_items"));
        _metrics.RecordSafeSearchCandidates("shared_memory_items", memoryCandidates.Length);

        return wikiCandidates.Concat(memoryCandidates).ToArray();
    }

    public async Task<IReadOnlyList<ContextPackCandidate>> SelectSharedMemoryAsync(
        SafeSearchRequest request,
        string ownerUserId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var candidates = await SelectMemoryAsync(
            new SafeSearchRequest(
                request.Query,
                request.CoreTags,
                request.MaxItems,
                request.ProjectKey,
                request.TaskKey,
                request.TopicTags),
            ownerUserId,
            cancellationToken);
        LuthnHostMetrics.SafeSearchCandidateCount.Record(
            candidates.Length,
            new KeyValuePair<string, object?>("source", "shared_memory_items"));
        _metrics.RecordSafeSearchCandidates("shared_memory_items", candidates.Length);
        return candidates;
    }

    private async Task<ContextPackCandidate[]> SelectWikiAsync(
        SafeSearchRequest request,
        string ownerUserId,
        CancellationToken cancellationToken)
    {
        var query = db.WikiProposals
            .AsNoTracking()
            .Where(record => record.OwnerUserId == ownerUserId &&
                record.AllowsAgentContext &&
                record.Sensitivity == SensitivityLevel.Public);

        query = ApplySearchFilters(query, request);

        return await query
            .OrderByDescending(record => request.TaskKey != null && record.TaskKey == request.TaskKey)
            .ThenByDescending(record => record.CreatedAt)
            .ThenBy(record => record.Title.ToLower())
            .ThenBy(record => record.Id)
            .Take(RetrievalCandidateLimits.MaxCandidatesPerCorpus)
            .Select(record => new ContextPackCandidate(
                record.Id,
                record.Title,
                record.SafeSummary,
                record.Sensitivity,
                record.CoreTags,
                record.AllowsAgentContext)
            {
                ProjectKey = record.ProjectKey,
                TaskKey = record.TaskKey,
                TopicTags = record.TopicTags,
                ProjectionTimestamp = record.CreatedAt
            })
            .ToArrayAsync(cancellationToken);
    }

    private async Task<ContextPackCandidate[]> SelectMemoryAsync(
        SafeSearchRequest request,
        string ownerUserId,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var query = db.SharedMemoryItems
            .AsNoTracking()
            .Where(record => record.OwnerUserId == ownerUserId &&
                record.AllowsAgentContext &&
                record.Sensitivity == SensitivityLevel.Public &&
                (record.Visibility == MemoryVisibility.PublicSafe ||
                    record.Visibility == MemoryVisibility.SharedAcrossAgents) &&
                (record.ExpiresAt == null || record.ExpiresAt > now));

        query = ApplySearchFilters(query, request);

        return await query
            .OrderByDescending(record => request.TaskKey != null && record.TaskKey == request.TaskKey)
            .ThenByDescending(record => record.UpdatedAt)
            .ThenBy(record => record.Title.ToLower())
            .ThenBy(record => record.Id)
            .Take(RetrievalCandidateLimits.MaxCandidatesPerCorpus)
            .Select(record => new ContextPackCandidate(
                record.Id,
                record.Title,
                record.SafeSummary,
                record.Sensitivity,
                record.CoreTags,
                record.AllowsAgentContext)
            {
                ProjectKey = record.ProjectKey,
                TaskKey = record.TaskKey,
                TopicTags = record.TopicTags,
                ProjectionTimestamp = record.UpdatedAt
            })
            .ToArrayAsync(cancellationToken);
    }

    private IQueryable<WikiProposalRecord> ApplySearchFilters(
        IQueryable<WikiProposalRecord> query,
        SafeSearchRequest request)
    {
        if (request.ProjectKey is not null)
        {
            query = query.Where(record => record.ProjectKey == null || record.ProjectKey == request.ProjectKey);
        }

        var tagMarkers = request.CoreTags
            .Select(SafeSearchText.BuildTagKey)
            .Select(SafeSearchText.ToIndexMarker)
            .ToArray();
        var queryTerms = SafeSearchText.Tokenize(request.Query).ToArray();

        if (tagMarkers.Length > 0)
        {
            query = WhereContainsAny(query, record => record.SearchTagKeys, tagMarkers);
        }

        if (queryTerms.Length > 0)
        {
            query = WhereContainsAny(query, record => record.SearchTerms, queryTerms);
        }

        return query;
    }

    private IQueryable<SharedMemoryItemRecord> ApplySearchFilters(
        IQueryable<SharedMemoryItemRecord> query,
        SafeSearchRequest request)
    {
        if (request.ProjectKey is not null)
        {
            query = query.Where(record => record.ProjectKey == null || record.ProjectKey == request.ProjectKey);
        }

        var tagMarkers = request.CoreTags
            .Select(SafeSearchText.BuildTagKey)
            .Select(SafeSearchText.ToIndexMarker)
            .ToArray();
        var queryTerms = SafeSearchText.Tokenize(request.Query).ToArray();

        if (tagMarkers.Length > 0)
        {
            query = WhereContainsAny(query, record => record.SearchTagKeys, tagMarkers);
        }

        if (queryTerms.Length > 0)
        {
            query = WhereContainsAny(query, record => record.SearchTerms, queryTerms);
        }

        return query;
    }

    private static IQueryable<T> WhereContainsAny<T>(
        IQueryable<T> query,
        Expression<Func<T, string>> indexSelector,
        IReadOnlyList<string> markers)
    {
        var contains = typeof(string).GetMethod(nameof(string.Contains), [typeof(string)])!;
        var body = markers
            .Select(marker => (Expression)Expression.Call(
                indexSelector.Body,
                contains,
                Expression.Constant(marker)))
            .Aggregate(Expression.OrElse);
        return query.Where(Expression.Lambda<Func<T, bool>>(body, indexSelector.Parameters));
    }
}
