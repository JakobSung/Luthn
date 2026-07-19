using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Diagnostics.Metrics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Luthn.Host.Api.Tests;

public sealed class OperationalMetricsTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string MetricsBearer = "metrics-read-local";
    private readonly WebApplicationFactory<Program> _factory;

    public OperationalMetricsTests(WebApplicationFactory<Program> factory) => _factory = factory;

    [Fact]
    public void SnapshotUsesBoundedLabelsAndMetadataOnlyAggregates()
    {
        var metrics = new OperationalMetrics();
        metrics.RecordClassificationProviderRequest("untrusted-provider-name", "untrusted-outcome", TimeSpan.FromMilliseconds(1.2));
        metrics.RecordSensitiveAccessRequest();
        metrics.RecordSensitiveAccessDecision("approved");
        metrics.RecordSafeSearchCandidates("shared_memory_items", 7);
        metrics.RecordSearchRequest("mcp_context_pack", "zero_result", "hit", TimeSpan.FromMilliseconds(12.2), 0);
        metrics.RecordSearchRequest("mcp_context_pack", "timeout", "miss", TimeSpan.FromMilliseconds(20), 0);
        metrics.RecordSearchRequest("untrusted-surface", "untrusted-outcome", "untrusted-cache", TimeSpan.FromMinutes(2), 999);
        metrics.RecordSearchFeedback("helpful");

        var snapshot = metrics.Snapshot();

        Assert.Equal("metadata-only", snapshot.PayloadClass);
        Assert.Equal("no-external-publication", snapshot.ExportBoundary.Publication);
        var provider = Assert.Single(snapshot.ClassificationProvider);
        Assert.Equal("other", provider.Provider);
        Assert.Equal("other", provider.Outcome);
        Assert.Equal(1, provider.Count);
        Assert.Equal(2, provider.TotalDurationMilliseconds);
        Assert.Equal(1, snapshot.SensitiveAccess.Requests);
        Assert.Equal(new OutcomeCountSnapshot("approved", 1), Assert.Single(snapshot.SensitiveAccess.Decisions));
        Assert.Equal(new SearchMetricSnapshot("shared_memory_items", 1, 7, 7), Assert.Single(snapshot.SafeSearch));
        var zeroResult = snapshot.SearchRequests.Single(metric => metric.Outcome == "zero_result");
        Assert.Equal(("mcp_context_pack", "hit", 1L, 13L, 13L, 0L, 1L),
            (zeroResult.Surface, zeroResult.CacheStatus, zeroResult.Count,
                zeroResult.TotalDurationMilliseconds, zeroResult.MaxDurationMilliseconds,
                zeroResult.TotalResults, zeroResult.ZeroResultCount));
        Assert.Equal(Buckets(0, 1, 1, 1, 1, 1, 1).ToArray(), zeroResult.DurationBuckets.ToArray());

        var timeout = snapshot.SearchRequests.Single(metric => metric.Outcome == "timeout");
        Assert.Equal(("mcp_context_pack", "miss", 1L, 20L, 20L, 0L, 0L),
            (timeout.Surface, timeout.CacheStatus, timeout.Count,
                timeout.TotalDurationMilliseconds, timeout.MaxDurationMilliseconds,
                timeout.TotalResults, timeout.ZeroResultCount));
        Assert.Equal(Buckets(0, 1, 1, 1, 1, 1, 1).ToArray(), timeout.DurationBuckets.ToArray());

        var bounded = snapshot.SearchRequests.Single(metric => metric.Surface == "other");
        Assert.Equal(("other", "other", 1L, 60_000L, 60_000L, 50L, 0L),
            (bounded.Outcome, bounded.CacheStatus, bounded.Count,
                bounded.TotalDurationMilliseconds, bounded.MaxDurationMilliseconds,
                bounded.TotalResults, bounded.ZeroResultCount));
        Assert.Equal(Buckets(0, 0, 0, 0, 0, 0, 1).ToArray(), bounded.DurationBuckets.ToArray());
        Assert.Equal(new SearchFeedbackMetricSnapshot("helpful", 1), Assert.Single(snapshot.SearchFeedback));
    }

    [Fact]
    public void SearchDurationBucketsUseStableCumulativeBoundaries()
    {
        var metrics = new OperationalMetrics();
        metrics.RecordSearchRequest("agent_search", "succeeded", "not_applicable", TimeSpan.FromMilliseconds(10), 1);
        metrics.RecordSearchRequest("agent_search", "succeeded", "not_applicable", TimeSpan.FromMilliseconds(11), 1);
        metrics.RecordSearchRequest("agent_search", "succeeded", "not_applicable", TimeSpan.FromMilliseconds(50), 1);
        metrics.RecordSearchRequest("agent_search", "succeeded", "not_applicable", TimeSpan.FromMilliseconds(60_001), 1);

        var request = Assert.Single(metrics.Snapshot().SearchRequests);

        Assert.Equal(Buckets(1, 3, 3, 3, 3, 3, 4).ToArray(), request.DurationBuckets.ToArray());
        Assert.Equal(60_071, request.TotalDurationMilliseconds);
        Assert.Equal(60_000, request.MaxDurationMilliseconds);
    }

    [Fact]
    public void SearchMetersExposeOnlyBoundedLowCardinalityLabels()
    {
        var measurements = new List<(string Name, IReadOnlyDictionary<string, object?> Tags)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, current) =>
        {
            if (instrument.Meter.Name == "Luthn.Host.Api" && instrument.Name.StartsWith("luthn.search.", StringComparison.Ordinal))
            {
                current.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, _, tags, _) =>
            measurements.Add((instrument.Name, tags.ToArray().ToDictionary(tag => tag.Key, tag => tag.Value))));
        listener.Start();

        var metrics = new OperationalMetrics();
        metrics.RecordSearchRequest("unsafe-project-name", "unsafe-query-outcome", "unsafe-cache-key", TimeSpan.FromMilliseconds(4), 1);
        metrics.RecordSearchFeedback("unsafe-free-form-comment");

        Assert.Contains(measurements, item => item.Name == "luthn.search.requests");
        Assert.Contains(measurements, item => item.Name == "luthn.search.duration");
        Assert.Contains(measurements, item => item.Name == "luthn.search.results");
        Assert.Contains(measurements, item => item.Name == "luthn.search.feedback");
        Assert.All(measurements, item =>
        {
            Assert.DoesNotContain(item.Tags.Keys, key => key.Contains("query", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(item.Tags.Keys, key => key.Contains("project", StringComparison.OrdinalIgnoreCase));
            Assert.All(item.Tags.Values, value => Assert.Equal("other", value));
        });
    }

    [Fact]
    public async Task SearchObservationAndFeedbackRequireWriteScopeAndRejectUnsafePayloads()
    {
        const string bearer = "metrics-write-local";
        using var factory = _factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Luthn:TestingDatabaseName", Guid.NewGuid().ToString("N"));
            builder.UseSetting("Luthn:Auth:RequireServiceToken", "true");
            builder.UseSetting("Luthn:Auth:Tokens:0:Name", "metrics-writer");
            builder.UseSetting("Luthn:Auth:Tokens:0:Sha256Digest", Digest(bearer));
            builder.UseSetting("Luthn:Auth:Tokens:0:Scopes:0", "metrics.write");
        });
        using var anonymous = factory.CreateClient();
        using var unauthorized = await anonymous.PostAsJsonAsync(
            "/api/agent/search-telemetry/observations",
            new { surface = "mcp_context_pack", outcome = "succeeded", cacheStatus = "hit", durationMilliseconds = 3, resultCount = 1 });
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        using var accepted = await client.PostAsJsonAsync(
            "/api/agent/search-telemetry/observations",
            new { surface = "mcp_context_pack", outcome = "zero_result", cacheStatus = "miss", durationMilliseconds = 7, resultCount = 0 });
        using var invalid = await client.PostAsJsonAsync(
            "/api/agent/search-telemetry/observations",
            new { surface = "query-text", outcome = "succeeded", cacheStatus = "hit", durationMilliseconds = 7, resultCount = 1 });
        using var extraField = await client.PostAsJsonAsync(
            "/api/agent/search-telemetry/observations",
            new { surface = "mcp_context_pack", outcome = "succeeded", cacheStatus = "hit", durationMilliseconds = 7, resultCount = 1, query = "secret query" });
        var retrievalId = SearchTelemetry.CreateRetrievalId();
        using var feedback = await client.PostAsJsonAsync(
            "/api/agent/search-telemetry/feedback",
            new { retrievalId, judgment = "helpful" });
        using var invalidFeedback = await client.PostAsJsonAsync(
            "/api/agent/search-telemetry/feedback",
            new { retrievalId = "/private/path", judgment = "helpful" });

        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, extraField.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, feedback.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, invalidFeedback.StatusCode);
        var metrics = factory.Services.GetRequiredService<IOperationalMetrics>().Snapshot();
        Assert.Equal("zero_result", Assert.Single(metrics.SearchRequests).Outcome);
        Assert.Equal("helpful", Assert.Single(metrics.SearchFeedback).Judgment);
    }

    [Fact]
    public async Task MetricsEndpointsRequireScopeAndExportDeterministicEmptySnapshot()
    {
        using var factory = _factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Luthn:TestingDatabaseName", Guid.NewGuid().ToString("N"));
            builder.UseSetting("Luthn:Auth:RequireServiceToken", "true");
            builder.UseSetting("Luthn:Auth:Tokens:0:Name", "metrics-reader");
            builder.UseSetting("Luthn:Auth:Tokens:0:Sha256Digest", Digest(MetricsBearer));
            builder.UseSetting("Luthn:Auth:Tokens:0:Scopes:0", "metrics.read");
        });
        using var anonymous = factory.CreateClient();
        using var forbidden = await anonymous.GetAsync("/api/operator/metrics");
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", MetricsBearer);
        using var snapshotResponse = await client.GetAsync("/api/operator/metrics");
        using var exportResponse = await client.GetAsync("/api/operator/metrics/export");
        var snapshot = await snapshotResponse.Content.ReadAsStringAsync();
        var export = await exportResponse.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, forbidden.StatusCode);
        Assert.Equal(HttpStatusCode.OK, snapshotResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, exportResponse.StatusCode);
        Assert.Equal("application/json", exportResponse.Content.Headers.ContentType?.MediaType);
        Assert.Equal("attachment", exportResponse.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Contains("luthn-operational-metrics.json", exportResponse.Content.Headers.ContentDisposition?.FileNameStar?.ToString());
        using var body = JsonDocument.Parse(snapshot);
        Assert.Equal("metadata-only", body.RootElement.GetProperty("payloadClass").GetString());
        Assert.Equal("no-external-publication", body.RootElement.GetProperty("exportBoundary").GetProperty("publication").GetString());
        Assert.Equal("[]", body.RootElement.GetProperty("classificationProvider").GetRawText());
        Assert.Equal(snapshot, export);
        Assert.DoesNotContain("token", snapshot, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sourceId", snapshot, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("query", snapshot, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("projectKey", snapshot, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cacheKey", snapshot, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("retrievalId", snapshot, StringComparison.OrdinalIgnoreCase);
    }

    private static string Digest(string value) => "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static IReadOnlyList<SearchDurationBucketSnapshot> Buckets(params long[] counts)
    {
        long[] upperBounds = [10, 50, 100, 500, 1_000, 5_000, 60_000];
        return upperBounds
            .Zip(counts, static (upperBound, count) => new SearchDurationBucketSnapshot(upperBound, count))
            .ToArray();
    }
}
