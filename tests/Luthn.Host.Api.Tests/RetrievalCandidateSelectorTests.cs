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
            CancellationToken.None);

        Assert.Equal(["wiki-safe", "memory-safe"], candidates.Select(candidate => candidate.Id).ToArray());
    }

    private static WikiProposalRecord Wiki(
        string id,
        SensitivityLevel sensitivity,
        bool allowsAgentContext) => new()
    {
        Id = id,
        SourceEventId = $"source-{id}",
        Title = "Needle wiki",
        SafeSummary = "Needle public-safe projection.",
        Sensitivity = sensitivity,
        CoreTags = ["needle"],
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
}
