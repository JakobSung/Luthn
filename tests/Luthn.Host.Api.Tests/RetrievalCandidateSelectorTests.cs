using Luthn.Core.Classification;
using Luthn.Core.Memory;
using Luthn.Core.Persistence;
using Luthn.Core.Search;
using Microsoft.EntityFrameworkCore;

namespace Luthn.Host.Api.Tests;

public sealed class RetrievalCandidateSelectorTests
{
    [Fact]
    public async Task CombinedCandidateSelectionIsBoundedPerSafeCorpus()
    {
        await using var db = CreateDbContext();
        var now = DateTimeOffset.UtcNow;
        db.WikiProposals.AddRange(Enumerable.Range(0, 600).Select(index => new WikiProposalRecord
        {
            Id = $"wiki-common-{index:D4}",
            SourceEventId = $"source-common-{index:D4}",
            Title = $"Common wiki {index:D4}",
            SafeSummary = "Common public-safe projection.",
            Sensitivity = SensitivityLevel.Public,
            CoreTags = ["common"],
            AllowsAgentContext = true,
            CreatedAt = now
        }));
        db.SharedMemoryItems.AddRange(Enumerable.Range(0, 600).Select(index => new SharedMemoryItemRecord
        {
            Id = $"memory-common-{index:D4}",
            Title = $"Common memory {index:D4}",
            SafeSummary = "Common public-safe memory.",
            Sensitivity = SensitivityLevel.Public,
            CoreTags = ["common"],
            Visibility = MemoryVisibility.PublicSafe,
            RetentionKind = MemoryRetentionKind.Durable,
            AllowsAgentContext = true,
            CreatedAt = now,
            UpdatedAt = now,
            CreatedBy = "test"
        }));
        await db.SaveChangesAsync();

        var selector = new DbBackedRetrievalCandidateSelector(db, TimeProvider.System);
        var candidates = await selector.SelectAgentContextAsync(
            new SafeSearchRequest("common", ["common"], 100),
            "local-owner",
            CancellationToken.None);

        Assert.Equal(RetrievalCandidateLimits.MaxCombinedCandidates, candidates.Count);
        Assert.Equal(
            RetrievalCandidateLimits.MaxCandidatesPerCorpus,
            candidates.Count(candidate => candidate.Id.StartsWith("wiki-", StringComparison.Ordinal)));
        Assert.Equal(
            RetrievalCandidateLimits.MaxCandidatesPerCorpus,
            candidates.Count(candidate => candidate.Id.StartsWith("memory-", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task CandidateSelectionExcludesUnsafePrivateAndExpiredRecords()
    {
        await using var db = CreateDbContext();
        var now = DateTimeOffset.UtcNow;
        db.WikiProposals.AddRange(
            Wiki("wiki-safe", SensitivityLevel.Public, allowsAgentContext: true),
            Wiki("wiki-confidential", SensitivityLevel.Confidential, allowsAgentContext: false),
            Wiki("wiki-agent-blocked", SensitivityLevel.Public, allowsAgentContext: false));
        db.SharedMemoryItems.AddRange(
            Memory("memory-safe", MemoryVisibility.SharedAcrossAgents, now.AddHours(1), allowsAgentContext: true),
            Memory("memory-private", MemoryVisibility.PrivateToOwner, now.AddHours(1), allowsAgentContext: true),
            Memory("memory-expired", MemoryVisibility.PublicSafe, now.AddHours(-1), allowsAgentContext: true),
            Memory("memory-agent-blocked", MemoryVisibility.PublicSafe, now.AddHours(1), allowsAgentContext: false));
        await db.SaveChangesAsync();

        var selector = new DbBackedRetrievalCandidateSelector(db, TimeProvider.System);
        var candidates = await selector.SelectAgentContextAsync(
            new SafeSearchRequest("needle", ["needle"], 20),
            "local-owner",
            CancellationToken.None);

        Assert.Equal(["wiki-safe", "memory-safe"], candidates.Select(candidate => candidate.Id).ToArray());
    }

    [Fact]
    public async Task CandidateSelectionPreservesSubstringQueryMatching()
    {
        await using var db = CreateDbContext();
        db.WikiProposals.Add(Wiki("wiki-needle", SensitivityLevel.Public, allowsAgentContext: true));
        await db.SaveChangesAsync();

        var selector = new DbBackedRetrievalCandidateSelector(db, TimeProvider.System);
        var candidates = await selector.SelectAgentContextAsync(
            new SafeSearchRequest("need", [], 20),
            "local-owner",
            CancellationToken.None);

        Assert.Equal(["wiki-needle"], candidates.Select(candidate => candidate.Id).ToArray());
    }

    [Fact]
    public async Task ProjectScopeIncludesMatchingAndGlobalButExcludesOtherProjects()
    {
        await using var db = CreateDbContext();
        db.WikiProposals.AddRange(
            Wiki("wiki-matching", SensitivityLevel.Public, true, projectKey: "luthn"),
            Wiki("wiki-global", SensitivityLevel.Public, true),
            Wiki("wiki-other", SensitivityLevel.Public, true, projectKey: "other"));
        await db.SaveChangesAsync();

        var selector = new DbBackedRetrievalCandidateSelector(db, TimeProvider.System);
        var candidates = await selector.SelectAgentContextAsync(
            new SafeSearchRequest("needle", ["needle"], 20, "luthn"),
            "local-owner",
            CancellationToken.None);

        Assert.Equal(
            ["wiki-global", "wiki-matching"],
            candidates.Select(candidate => candidate.Id).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task NewestFirstPreselectionKeepsRecentCandidateBeyondAlphabeticalLimit()
    {
        await using var db = CreateDbContext();
        var now = DateTimeOffset.Parse("2026-07-19T12:00:00Z");
        db.WikiProposals.AddRange(Enumerable.Range(0, RetrievalCandidateLimits.MaxCandidatesPerCorpus)
            .Select(index => new WikiProposalRecord
            {
                Id = $"wiki-old-{index:D4}",
                SourceEventId = $"source-old-{index:D4}",
                Title = $"A old recall {index:D4}",
                SafeSummary = "Common recall projection.",
                Sensitivity = SensitivityLevel.Public,
                CoreTags = ["recall"],
                AllowsAgentContext = true,
                CreatedAt = now.AddDays(-30)
            }));
        db.WikiProposals.Add(new WikiProposalRecord
        {
            Id = "wiki-recent-z",
            SourceEventId = "source-recent-z",
            Title = "Z recent recall",
            SafeSummary = "Common recall projection.",
            Sensitivity = SensitivityLevel.Public,
            CoreTags = ["recall"],
            AllowsAgentContext = true,
            CreatedAt = now
        });
        await db.SaveChangesAsync();

        var selector = new DbBackedRetrievalCandidateSelector(db, new FixedTimeProvider(now));
        var candidates = await selector.SelectAgentContextAsync(
            new SafeSearchRequest("recall", ["recall"], 20),
            "local-owner",
            CancellationToken.None);

        Assert.Equal(RetrievalCandidateLimits.MaxCandidatesPerCorpus, candidates.Count);
        Assert.Contains(candidates, candidate => candidate.Id == "wiki-recent-z");
    }

    private static WikiProposalRecord Wiki(
        string id,
        SensitivityLevel sensitivity,
        bool allowsAgentContext,
        string? projectKey = null) => new()
        {
            Id = id,
            SourceEventId = $"source-{id}",
            Title = "Needle wiki",
            SafeSummary = "Needle public-safe projection.",
            Sensitivity = sensitivity,
            CoreTags = ["needle"],
            ProjectKey = projectKey,
            AllowsAgentContext = allowsAgentContext,
            CreatedAt = DateTimeOffset.UtcNow
        };

    private static SharedMemoryItemRecord Memory(
        string id,
        MemoryVisibility visibility,
        DateTimeOffset expiresAt,
        bool allowsAgentContext) => new()
        {
            Id = id,
            Title = "Needle memory",
            SafeSummary = "Needle public-safe memory.",
            Sensitivity = SensitivityLevel.Public,
            CoreTags = ["needle"],
            Visibility = visibility,
            RetentionKind = MemoryRetentionKind.Session,
            ExpiresAt = expiresAt,
            AllowsAgentContext = allowsAgentContext,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            CreatedBy = "test"
        };

    private static LuthnDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<LuthnDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        return new LuthnDbContext(options);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
