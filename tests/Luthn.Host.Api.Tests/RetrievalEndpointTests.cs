using Luthn.Core.Classification;
using Luthn.Core.Persistence;
using Luthn.Core.Search;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Luthn.Host.Api.Tests;

public sealed class RetrievalEndpointTests
{
    [Fact]
    public async Task AgentSearchUsesRetrievalBackendOverAgentAllowedWikiProjections()
    {
        await using var db = CreateDbContext();
        db.WikiProposals.AddRange(
            new WikiProposalRecord
            {
                Id = "wiki-title-match",
                SourceEventId = "source-title-match",
                Title = "Billing outage runbook",
                SafeSummary = "Queue restart steps.",
                Sensitivity = SensitivityLevel.Public,
                CoreTags = ["runbook"],
                AllowsAgentContext = true,
                CreatedAt = DateTimeOffset.UtcNow
            },
            new WikiProposalRecord
            {
                Id = "wiki-summary-match",
                SourceEventId = "source-summary-match",
                Title = "Operations fallback",
                SafeSummary = "Billing outage mitigation runbook.",
                Sensitivity = SensitivityLevel.Public,
                CoreTags = ["runbook"],
                AllowsAgentContext = true,
                CreatedAt = DateTimeOffset.UtcNow
            },
            new WikiProposalRecord
            {
                Id = "wiki-confidential",
                SourceEventId = "source-confidential",
                Title = "Billing outage private source",
                SafeSummary = "Private customer raw details.",
                Sensitivity = SensitivityLevel.Confidential,
                CoreTags = ["runbook"],
                AllowsAgentContext = false,
                CreatedAt = DateTimeOffset.UtcNow
            });
        await db.SaveChangesAsync();

        var result = await ClassificationEndpoints.SearchAgentContext(
            new SafeSearchRequest("billing outage", ["runbook"], 10),
            new DeterministicRetrievalBackend(new SafeSearchIndex()),
            new DbBackedRetrievalCandidateSelector(db, TimeProvider.System),
            CancellationToken.None);

        var ok = Assert.IsType<Ok<SafeSearchResponse>>(result.Result);
        var response = ok.Value!;

        Assert.Equal(
            ["wiki-title-match", "wiki-summary-match"],
            response.Results.Select(item => item.Id).ToArray());
        Assert.DoesNotContain(response.Results, item => item.Id == "wiki-confidential");
    }

    [Fact]
    public async Task AgentSearchFindsOlderMatchingProjectionWhenCorpusExceedsCandidateCap()
    {
        await using var db = CreateDbContext();
        var now = DateTimeOffset.UtcNow;
        db.WikiProposals.Add(new WikiProposalRecord
        {
            Id = "wiki-old-match",
            SourceEventId = "source-old-match",
            Title = "Needle recovery runbook",
            SafeSummary = "Public-safe recovery steps.",
            Sensitivity = SensitivityLevel.Public,
            CoreTags = ["needle"],
            AllowsAgentContext = true,
            CreatedAt = now.AddDays(-2)
        });
        db.WikiProposals.AddRange(Enumerable.Range(0, 1001).Select(index => new WikiProposalRecord
        {
            Id = $"wiki-newer-unmatched-{index}",
            SourceEventId = $"source-newer-unmatched-{index}",
            Title = $"General release note {index}",
            SafeSummary = "Public-safe unmatched summary.",
            Sensitivity = SensitivityLevel.Public,
            CoreTags = ["release"],
            AllowsAgentContext = true,
            CreatedAt = now.AddMinutes(index)
        }));
        await db.SaveChangesAsync();

        var result = await ClassificationEndpoints.SearchAgentContext(
            new SafeSearchRequest("needle", ["needle"], 10),
            new DeterministicRetrievalBackend(new SafeSearchIndex()),
            new DbBackedRetrievalCandidateSelector(db, TimeProvider.System),
            CancellationToken.None);

        var ok = Assert.IsType<Ok<SafeSearchResponse>>(result.Result);
        var item = Assert.Single(ok.Value!.Results);
        Assert.Equal("wiki-old-match", item.Id);
    }

    private static LuthnDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<LuthnDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        return new LuthnDbContext(options);
    }
}
