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
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ContextPackCandidate>> SelectSharedMemoryAsync(
        SafeSearchRequest request,
        CancellationToken cancellationToken);
}

public sealed class DbBackedRetrievalCandidateSelector(
    LuthnDbContext db,
    TimeProvider timeProvider) : IRetrievalCandidateSelector
{
    public async Task<IReadOnlyList<ContextPackCandidate>> SelectAgentContextAsync(
        SafeSearchRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var normalizedRequest = new SafeSearchRequest(request.Query, request.CoreTags, request.MaxItems);
        var wikiCandidates = await SelectWikiAsync(normalizedRequest, cancellationToken);
        var memoryCandidates = await SelectMemoryAsync(normalizedRequest, cancellationToken);

        LuthnHostMetrics.SafeSearchCandidateCount.Record(
            wikiCandidates.Length,
            new KeyValuePair<string, object?>("source", "wiki_proposals"));
        LuthnHostMetrics.SafeSearchCandidateCount.Record(
            memoryCandidates.Length,
            new KeyValuePair<string, object?>("source", "shared_memory_items"));

        return wikiCandidates.Concat(memoryCandidates).ToArray();
    }

    public async Task<IReadOnlyList<ContextPackCandidate>> SelectSharedMemoryAsync(
        SafeSearchRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var candidates = await SelectMemoryAsync(
            new SafeSearchRequest(request.Query, request.CoreTags, request.MaxItems),
            cancellationToken);
        LuthnHostMetrics.SafeSearchCandidateCount.Record(
            candidates.Length,
            new KeyValuePair<string, object?>("source", "shared_memory_items"));
        return candidates;
    }

    private async Task<ContextPackCandidate[]> SelectWikiAsync(
        SafeSearchRequest request,
        CancellationToken cancellationToken)
    {
        var query = db.WikiProposals
            .AsNoTracking()
            .Where(record => record.AllowsAgentContext &&
                record.Sensitivity == SensitivityLevel.Public);

        if (!db.Database.IsNpgsql())
        {
            var records = await query.ToArrayAsync(cancellationToken);
            return records
                .Where(record => MatchesInMemory(
                    record.Title,
                    record.SafeSummary,
                    record.CoreTags,
                    request))
                .OrderBy(record => record.Title, StringComparer.OrdinalIgnoreCase)
                .ThenBy(record => record.Id, StringComparer.OrdinalIgnoreCase)
                .Take(RetrievalCandidateLimits.MaxCandidatesPerCorpus)
                .Select(record => new ContextPackCandidate(
                    record.Id,
                    record.Title,
                    record.SafeSummary,
                    record.Sensitivity,
                    record.CoreTags,
                    record.AllowsAgentContext))
                .ToArray();
        }

        query = ApplySearchFilters(query, request);

        return await query
            .OrderBy(record => record.Title.ToLower())
            .ThenBy(record => record.Id)
            .Take(RetrievalCandidateLimits.MaxCandidatesPerCorpus)
            .Select(record => new ContextPackCandidate(
                record.Id,
                record.Title,
                record.SafeSummary,
                record.Sensitivity,
                record.CoreTags,
                record.AllowsAgentContext))
            .ToArrayAsync(cancellationToken);
    }

    private async Task<ContextPackCandidate[]> SelectMemoryAsync(
        SafeSearchRequest request,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var query = db.SharedMemoryItems
            .AsNoTracking()
            .Where(record => record.AllowsAgentContext &&
                record.Sensitivity == SensitivityLevel.Public &&
                (record.Visibility == MemoryVisibility.PublicSafe ||
                    record.Visibility == MemoryVisibility.SharedAcrossAgents) &&
                (record.ExpiresAt == null || record.ExpiresAt > now));

        if (!db.Database.IsNpgsql())
        {
            var records = await query.ToArrayAsync(cancellationToken);
            return records
                .Where(record => MatchesInMemory(
                    record.Title,
                    record.SafeSummary,
                    record.CoreTags,
                    request))
                .OrderBy(record => record.Title, StringComparer.OrdinalIgnoreCase)
                .ThenBy(record => record.Id, StringComparer.OrdinalIgnoreCase)
                .Take(RetrievalCandidateLimits.MaxCandidatesPerCorpus)
                .Select(record => new ContextPackCandidate(
                    record.Id,
                    record.Title,
                    record.SafeSummary,
                    record.Sensitivity,
                    record.CoreTags,
                    record.AllowsAgentContext))
                .ToArray();
        }

        query = ApplySearchFilters(query, request);

        return await query
            .OrderBy(record => record.Title.ToLower())
            .ThenBy(record => record.Id)
            .Take(RetrievalCandidateLimits.MaxCandidatesPerCorpus)
            .Select(record => new ContextPackCandidate(
                record.Id,
                record.Title,
                record.SafeSummary,
                record.Sensitivity,
                record.CoreTags,
                record.AllowsAgentContext))
            .ToArrayAsync(cancellationToken);
    }

    private IQueryable<WikiProposalRecord> ApplySearchFilters(
        IQueryable<WikiProposalRecord> query,
        SafeSearchRequest request)
    {
        var normalizedTags = request.CoreTags
            .Select(tag => tag.ToLowerInvariant())
            .ToArray();
        var tokens = SafeSearchText.Tokenize(request.Query).ToArray();

        if (normalizedTags.Length > 0)
        {
            query = query.Where(record =>
                record.CoreTags.Any(tag => normalizedTags.Contains(tag.ToLower())));
        }

        if (tokens.Length > 0)
        {
            var patterns = tokens.Select(token => $"%{token}%").ToArray();
            query = query.Where(record => patterns.Any(pattern =>
                EF.Functions.ILike(record.Title, pattern) ||
                EF.Functions.ILike(record.SafeSummary, pattern) ||
                record.CoreTags.Any(tag => EF.Functions.ILike(tag, pattern))));
        }

        return query;
    }

    private IQueryable<SharedMemoryItemRecord> ApplySearchFilters(
        IQueryable<SharedMemoryItemRecord> query,
        SafeSearchRequest request)
    {
        var normalizedTags = request.CoreTags
            .Select(tag => tag.ToLowerInvariant())
            .ToArray();
        var tokens = SafeSearchText.Tokenize(request.Query).ToArray();

        if (normalizedTags.Length > 0)
        {
            query = query.Where(record =>
                record.CoreTags.Any(tag => normalizedTags.Contains(tag.ToLower())));
        }

        if (tokens.Length > 0)
        {
            var patterns = tokens.Select(token => $"%{token}%").ToArray();
            query = query.Where(record => patterns.Any(pattern =>
                EF.Functions.ILike(record.Title, pattern) ||
                EF.Functions.ILike(record.SafeSummary, pattern) ||
                record.CoreTags.Any(tag => EF.Functions.ILike(tag, pattern))));
        }

        return query;
    }

    private static bool MatchesInMemory(
        string title,
        string safeSummary,
        IReadOnlyList<string> coreTags,
        SafeSearchRequest request)
    {
        if (request.CoreTags.Count > 0 &&
            !coreTags.Any(tag => request.CoreTags.Contains(tag, StringComparer.OrdinalIgnoreCase)))
        {
            return false;
        }

        var tokens = SafeSearchText.Tokenize(request.Query);
        return tokens.Count == 0 || tokens.Any(token =>
            title.Contains(token, StringComparison.OrdinalIgnoreCase) ||
            safeSummary.Contains(token, StringComparison.OrdinalIgnoreCase) ||
            coreTags.Any(tag => tag.Contains(token, StringComparison.OrdinalIgnoreCase)));
    }
}
