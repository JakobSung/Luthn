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

    [Fact]
    public void SearchScopesProjectsAndBoostsTaskTopicAndRecentMatches()
    {
        var now = DateTimeOffset.Parse("2026-07-19T12:00:00Z");
        var index = new SafeSearchIndex(new FixedTimeProvider(now));
        var candidates = new[]
        {
            Candidate("other", "Recall note", "Relevant recall note.", ["recall"], projectKey: "other"),
            Candidate("global", "Recall note", "Relevant recall note.", ["recall"], timestamp: now.AddHours(-2)),
            Candidate("stale", "Recall note", "Relevant recall note.", ["recall"], projectKey: "luthn", timestamp: now.AddDays(-90)),
            Candidate(
                "matched",
                "Recall note",
                "Relevant recall note.",
                ["recall"],
                projectKey: "luthn",
                taskKey: "ranking",
                topicTags: ["quality"],
                timestamp: now.AddHours(-1))
        };

        var response = index.Search(
            new SafeSearchRequest("recall", ["recall"], 10, "LUTHN", "RANKING", ["Quality"]),
            candidates);

        Assert.Equal(["matched", "stale", "global"], response.Results.Select(result => result.Id));
        Assert.DoesNotContain(response.Results, result => result.Id == "other");
        Assert.Equal("luthn", response.ProjectKey);
        Assert.Equal("ranking", response.TaskKey);
        Assert.Equal(["quality"], response.TopicTags);
        Assert.Equal(now.AddHours(-1), response.Results[0].ProjectionTimestamp);
    }

    [Fact]
    public void RecencyTreatsFutureAndStaleTimestampsConservatively()
    {
        var now = DateTimeOffset.Parse("2026-07-19T12:00:00Z");
        var index = new SafeSearchIndex(new FixedTimeProvider(now));
        var candidates = new[]
        {
            Candidate("future", "Recall", "Recall.", ["recall"], timestamp: now.AddMinutes(1)),
            Candidate("stale", "Recall", "Recall.", ["recall"], timestamp: now.AddDays(-31)),
            Candidate("recent", "Recall", "Recall.", ["recall"], timestamp: now.AddHours(-1))
        };

        var response = index.Search(new SafeSearchRequest("recall", ["recall"], 10), candidates);

        Assert.Equal("recent", response.Results[0].Id);
        Assert.Equal(
            response.Results.Single(result => result.Id == "future").Score,
            response.Results.Single(result => result.Id == "stale").Score);
    }

    [Fact]
    public void RecallMetadataNormalizesAndRejectsPathsOrUnboundedValues()
    {
        Assert.Equal("luthn:search", RecallMetadata.NormalizeKey(" LUTHN:Search "));
        Assert.Equal(["quality", "검색"], RecallMetadata.NormalizeTopicTags([" Quality ", "quality", "검색"]));
        Assert.Throws<ArgumentException>(() => RecallMetadata.NormalizeKey("/Users/example/project"));
        Assert.Throws<ArgumentException>(() => RecallMetadata.NormalizeTopicTags(["raw/path"]));
        Assert.Throws<ArgumentException>(() => RecallMetadata.NormalizeTopicTags(
            Enumerable.Range(0, RecallMetadata.MaximumTopicTags + 1).Select(index => $"tag-{index}")));
    }

    private static ContextPackCandidate Candidate(
        string id,
        string title,
        string safeSummary,
        IReadOnlyList<string> coreTags,
        SensitivityLevel sensitivity = SensitivityLevel.Public,
        bool allowsAgentContext = true,
        string? projectKey = null,
        string? taskKey = null,
        IReadOnlyList<string>? topicTags = null,
        DateTimeOffset timestamp = default) =>
        new ContextPackCandidate(id, title, safeSummary, sensitivity, coreTags, allowsAgentContext)
        {
            ProjectKey = projectKey,
            TaskKey = taskKey,
            TopicTags = topicTags ?? [],
            ProjectionTimestamp = timestamp
        };

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
