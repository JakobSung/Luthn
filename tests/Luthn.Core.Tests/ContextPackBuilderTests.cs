using Luthn.Core.Classification;
using Luthn.Core.Context;

namespace Luthn.Core.Tests;

public sealed class ContextPackBuilderTests
{
    [Fact]
    public void BuilderFiltersSensitiveRecordsAndCoreTags()
    {
        var builder = new ContextPackBuilder();
        var records = new[]
        {
            new ContextPackCandidate(
                "wiki-public",
                "Public runbook",
                "Public-safe release steps.",
                SensitivityLevel.Public,
                ["release", "runbook"],
                AllowsAgentContext: true),
            new ContextPackCandidate(
                "wiki-sensitive",
                "Contract note",
                "Redacted sensitive placeholder.",
                SensitivityLevel.Confidential,
                ["contract"],
                AllowsAgentContext: true),
            new ContextPackCandidate(
                "wiki-agent-blocked",
                "Blocked note",
                "Agent-blocked placeholder.",
                SensitivityLevel.Public,
                ["runbook"],
                AllowsAgentContext: false)
        };

        var pack = builder.Build(new ContextPackRequest(["runbook"], 10), records);

        var item = Assert.Single(pack.Items);
        Assert.Equal("wiki-public", item.Id);
        Assert.Equal(["runbook"], pack.CoreTags);
        Assert.Contains("runbook", item.CoreTags);
        Assert.DoesNotContain(pack.Items, candidate => candidate.Id == "wiki-sensitive");
        Assert.DoesNotContain(pack.Items, candidate => candidate.Id == "wiki-agent-blocked");
    }

    [Fact]
    public void BuilderUsesQueryAndCoreTagsForRankedContextPacks()
    {
        var builder = new ContextPackBuilder();
        var records = new[]
        {
            new ContextPackCandidate(
                "wiki-summary-match",
                "Operations guide",
                "Billing outage recovery steps.",
                SensitivityLevel.Public,
                ["runbook"],
                AllowsAgentContext: true),
            new ContextPackCandidate(
                "wiki-title-match",
                "Billing outage runbook",
                "Queue restart steps.",
                SensitivityLevel.Public,
                ["runbook"],
                AllowsAgentContext: true),
            new ContextPackCandidate(
                "wiki-wrong-tag",
                "Billing outage escalation",
                "Public-safe summary.",
                SensitivityLevel.Public,
                ["support"],
                AllowsAgentContext: true),
            new ContextPackCandidate(
                "wiki-unmatched",
                "Release checklist",
                "Deployment smoke tests.",
                SensitivityLevel.Public,
                ["runbook"],
                AllowsAgentContext: true)
        };

        var pack = builder.Build(new ContextPackRequest(["runbook"], 10, "billing outage"), records);

        Assert.Equal(["wiki-title-match", "wiki-summary-match"], pack.Items.Select(item => item.Id).ToArray());
    }

    [Fact]
    public void BuilderCarriesNormalizedRecallMetadataAndProjectionTimestamp()
    {
        var timestamp = DateTimeOffset.Parse("2026-07-19T12:00:00Z");
        var candidate = new ContextPackCandidate(
            "memory-1",
            "Recall plan",
            "Public-safe recall plan.",
            SensitivityLevel.Public,
            ["recall"],
            AllowsAgentContext: true)
        {
            ProjectKey = "luthn",
            TaskKey = "ranking",
            TopicTags = ["quality"],
            ProjectionTimestamp = timestamp
        };

        var pack = new ContextPackBuilder().Build(
            new ContextPackRequest(["recall"], 3, "recall", "LUTHN", "RANKING", ["Quality"]),
            [candidate]);

        var item = Assert.Single(pack.Items);
        Assert.Equal("luthn", pack.ProjectKey);
        Assert.Equal("ranking", pack.TaskKey);
        Assert.Equal(["quality"], pack.TopicTags);
        Assert.Equal(candidate.ProjectKey, item.ProjectKey);
        Assert.Equal(candidate.TaskKey, item.TaskKey);
        Assert.Equal(candidate.TopicTags, item.TopicTags);
        Assert.Equal(timestamp, item.ProjectionTimestamp);
    }
}
