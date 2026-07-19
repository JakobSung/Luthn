using Luthn.Core.Classification;
using Luthn.Core.Context;
using Luthn.Core.Persistence;
using Luthn.Core.Search;
using Microsoft.AspNetCore.Http;
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
            new OperationalMetrics(),
            TimeProvider.System,
            new DefaultHttpContext(),
            CancellationToken.None);

        var ok = Assert.IsType<Ok<SafeSearchResponse>>(result.Result);
        var response = ok.Value!;

        Assert.Equal(
            ["wiki-title-match", "wiki-summary-match"],
            response.Results.Select(item => item.Id).ToArray());
        Assert.DoesNotContain(response.Results, item => item.Id == "wiki-confidential");
        Assert.True(SearchTelemetry.IsValidRetrievalId(response.RetrievalId));
    }

    [Fact]
    public async Task AgentSearchRecordsDeterministicLatencyAndZeroResults()
    {
        await using var db = CreateDbContext();
        var timeProvider = new ManualTimeProvider();
        var metrics = new OperationalMetrics();
        var selector = new AdvancingCandidateSelector(timeProvider, TimeSpan.FromMilliseconds(25));

        var result = await ClassificationEndpoints.SearchAgentContext(
            new SafeSearchRequest("missing", ["missing"], 10),
            new DeterministicRetrievalBackend(new SafeSearchIndex(timeProvider)),
            selector,
            metrics,
            timeProvider,
            new DefaultHttpContext(),
            CancellationToken.None);

        var response = Assert.IsType<Ok<SafeSearchResponse>>(result.Result).Value!;
        Assert.Empty(response.Results);
        Assert.True(SearchTelemetry.IsValidRetrievalId(response.RetrievalId));
        var request = Assert.Single(metrics.Snapshot().SearchRequests);
        Assert.Equal(("agent_search", "zero_result", "not_applicable", 1L, 25L, 25L, 0L, 1L),
            (request.Surface, request.Outcome, request.CacheStatus, request.Count,
                request.TotalDurationMilliseconds, request.MaxDurationMilliseconds,
                request.TotalResults, request.ZeroResultCount));
        Assert.Equal(
            new SearchDurationBucketSnapshot[]
            {
                new(10, 0),
                new(50, 1),
                new(100, 1),
                new(500, 1),
                new(1_000, 1),
                new(5_000, 1),
                new(60_000, 1)
            },
            request.DurationBuckets.ToArray());
    }

    [Fact]
    public async Task AgentSearchRemainsSuccessfulWhenTelemetryRecordingFails()
    {
        var result = await ClassificationEndpoints.SearchAgentContext(
            new SafeSearchRequest("missing", ["missing"], 10),
            new DeterministicRetrievalBackend(new SafeSearchIndex()),
            new AdvancingCandidateSelector(new ManualTimeProvider(), TimeSpan.Zero),
            new ThrowingOperationalMetrics(),
            TimeProvider.System,
            new DefaultHttpContext(),
            CancellationToken.None);

        var response = Assert.IsType<Ok<SafeSearchResponse>>(result.Result).Value!;
        Assert.Empty(response.Results);
        Assert.True(SearchTelemetry.IsValidRetrievalId(response.RetrievalId));
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
            new OperationalMetrics(),
            TimeProvider.System,
            new DefaultHttpContext(),
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

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => 1_000;
        public override long GetTimestamp() => Interlocked.Read(ref _timestamp);
        public void Advance(TimeSpan duration) =>
            Interlocked.Add(ref _timestamp, (long)duration.TotalMilliseconds);
    }

    private sealed class AdvancingCandidateSelector(
        ManualTimeProvider timeProvider,
        TimeSpan duration) : IRetrievalCandidateSelector
    {
        public Task<IReadOnlyList<ContextPackCandidate>> SelectAgentContextAsync(
            SafeSearchRequest request,
            string ownerUserId,
            CancellationToken cancellationToken)
        {
            timeProvider.Advance(duration);
            return Task.FromResult<IReadOnlyList<ContextPackCandidate>>([]);
        }

        public Task<IReadOnlyList<ContextPackCandidate>> SelectSharedMemoryAsync(
            SafeSearchRequest request,
            string ownerUserId,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ContextPackCandidate>>([]);
    }

    private sealed class ThrowingOperationalMetrics : IOperationalMetrics
    {
        public void RecordClassificationProviderRequest(string provider, string outcome, TimeSpan duration) { }
        public void RecordSensitiveAccessRequest() { }
        public void RecordSensitiveAccessDecision(string outcome) { }
        public void RecordSafeSearchCandidates(string source, int count) { }
        public void RecordSearchRequest(string surface, string outcome, string cacheStatus, TimeSpan duration, int resultCount) =>
            throw new InvalidOperationException("simulated telemetry failure");
        public void RecordSearchFeedback(string judgment) { }
        public OperationalMetricsSnapshot Snapshot() => OperationalMetricsSnapshot.Empty;
    }
}
