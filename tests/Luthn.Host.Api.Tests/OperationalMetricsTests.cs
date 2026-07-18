using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

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
    }

    private static string Digest(string value) => "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
