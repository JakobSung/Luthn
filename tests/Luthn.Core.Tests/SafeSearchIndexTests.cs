using Luthn.Core.Classification;
using Luthn.Core.Context;
using Luthn.Core.Search;

namespace Luthn.Core.Tests;

public sealed class SafeSearchIndexTests
{
    [Fact]
    public void SearchRanksExactPhraseTokenAndTagMatchesDeterministically()
    {
        var index = new SafeSearchIndex();
        var candidates = new[]
        {
            Candidate("wiki-summary-phrase", "Operations fallback", "Billing outage mitigation runbook.", ["runbook"]),
            Candidate("wiki-title-tokens", "Billing recovery", "Outage mitigation steps.", ["runbook"]),
            Candidate("wiki-tag-exact", "Support workflow", "Escalation steps.", ["billing-outage"]),
            Candidate("wiki-title-phrase", "Billing outage runbook", "Queue restart steps.", ["runbook"]),
            Candidate("wiki-title-exact", "Billing outage", "Primary incident response.", ["runbook"]),
            Candidate("wiki-unrelated", "Release checklist", "Deployment smoke tests.", ["runbook"])
        };

        var response = index.Search(new SafeSearchRequest("billing outage", ["runbook", "billing-outage"], 10), candidates);

        Assert.Equal(
            [
                "wiki-title-exact",
                "wiki-title-phrase",
                "wiki-tag-exact",
                "wiki-summary-phrase",
                "wiki-title-tokens"
            ],
            response.Results.Select(result => result.Id).ToArray());
        Assert.All(response.Results, result => Assert.True(result.Score > 0));
    }

    [Fact]
    public void SearchFiltersByCoreTagsAndClampsMaxItems()
    {
        var index = new SafeSearchIndex();
        var candidates = new[]
        {
            Candidate("wiki-a", "Alpha runbook", "Public alpha summary.", ["runbook"]),
            Candidate("wiki-b", "Beta runbook", "Public beta summary.", ["runbook"]),
            Candidate("wiki-c", "Gamma guide", "Public gamma summary.", ["guide"])
        };

        var response = index.Search(new SafeSearchRequest("public", ["runbook"], 0), candidates);

        var result = Assert.Single(response.Results);
        Assert.Equal("wiki-a", result.Id);
    }

    [Fact]
    public void SearchNeverReturnsConfidentialOrAgentBlockedCandidates()
    {
        var index = new SafeSearchIndex();
        var candidates = new[]
        {
            Candidate("wiki-public", "Contract renewal runbook", "Public-safe summary.", ["contract"]),
            Candidate(
                "wiki-confidential",
                "Contract renewal details",
                "Private customer terms.",
                ["contract"],
                SensitivityLevel.Confidential),
            Candidate(
                "wiki-blocked",
                "Contract renewal source",
                "Raw vault handoff.",
                ["contract"],
                SensitivityLevel.Public,
                allowsAgentContext: false)
        };

        var response = index.Search(new SafeSearchRequest("contract renewal", ["contract"], 10), candidates);

        var result = Assert.Single(response.Results);
        Assert.Equal("wiki-public", result.Id);
        Assert.DoesNotContain("Private customer terms", result.SafeSummary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Raw vault", result.SafeSummary, StringComparison.OrdinalIgnoreCase);
    }

    private static ContextPackCandidate Candidate(
        string id,
        string title,
        string safeSummary,
        IReadOnlyList<string> coreTags,
        SensitivityLevel sensitivity = SensitivityLevel.Public,
        bool allowsAgentContext = true) =>
        new(id, title, safeSummary, sensitivity, coreTags, allowsAgentContext);
}
