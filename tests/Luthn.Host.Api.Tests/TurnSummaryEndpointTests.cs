using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Luthn.Core.Classification;
using Luthn.Core.Common;
using Luthn.Core.Memory;
using Luthn.Core.Persistence;
using Luthn.Core.Policy;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Luthn.Host.Api.Tests;

public sealed class TurnSummaryEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public TurnSummaryEndpointTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task SafeTurnSummaryCreatesAgentVisibleSharedMemory()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/api/agent/turn-summaries", new
        {
            sessionId = "session-safe-1",
            turnId = "turn-1",
            sourceAgent = "codex",
            summary = "Published release note for external contributors.",
            coreTags = new[] { "release", "codex" },
            title = "Codex release note",
            idempotencyKey = "summary-safe-1",
            projectKey = " LUTHN ",
            taskKey = " RELEASE ",
            topicTags = new[] { " Delivery ", "delivery" },
            provenance = new
            {
                userId = "Owner.One",
                agentId = "Codex",
                applicationId = "Codex.Desktop",
                pluginId = "Luthn.Hook",
                connectorId = "Luthn.Codex.Connector",
                connectorVersion = "2"
            }
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.False(body.RootElement.GetProperty("duplicate").GetBoolean());
        Assert.True(body.RootElement.GetProperty("allowsAgentContext").GetBoolean());
        Assert.Equal("Public", body.RootElement.GetProperty("classification").GetProperty("sensitivity").GetString());

        using var contextResponse = await client.PostAsJsonAsync("/api/agent/context-packs", new
        {
            query = "release note",
            coreTags = new[] { "release" },
            maxItems = 10,
            projectKey = "luthn",
            taskKey = "release",
            topicTags = new[] { "delivery" }
        });
        using var contextBody = await JsonDocument.ParseAsync(await contextResponse.Content.ReadAsStreamAsync());

        Assert.Equal(HttpStatusCode.OK, contextResponse.StatusCode);
        var item = Assert.Single(contextBody.RootElement.GetProperty("items").EnumerateArray());
        Assert.StartsWith("memory-turn-summary-", item.GetProperty("id").GetString(), StringComparison.Ordinal);
        Assert.Equal("Codex release note", item.GetProperty("title").GetString());
        Assert.Equal("luthn", item.GetProperty("projectKey").GetString());
        Assert.Equal("release", item.GetProperty("taskKey").GetString());
        Assert.Equal(["delivery"], item.GetProperty("topicTags").EnumerateArray().Select(tag => tag.GetString()));
        Assert.NotEqual(default, item.GetProperty("projectionTimestamp").GetDateTimeOffset());
        Assert.True(SearchTelemetry.IsValidRetrievalId(contextBody.RootElement.GetProperty("retrievalId").GetString()));

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LuthnDbContext>();
        var source = await db.SourceEvents.SingleAsync();
        var classification = await db.ClassificationResults.SingleAsync();
        var memory = await db.SharedMemoryItems.SingleAsync();
        var provenance = await db.CollectionProvenance.SingleAsync();

        Assert.Equal("turn-summary", source.SourceType);
        Assert.False(source.ContainsSensitiveMaterial);
        Assert.Equal(StorageDecisionKind.WikiCandidate, classification.StorageDecision);
        Assert.Equal(SensitivityLevel.Public, memory.Sensitivity);
        Assert.Equal(MemoryVisibility.SharedAcrossAgents, memory.Visibility);
        Assert.Equal(MemoryRetentionKind.Ephemeral, memory.RetentionKind);
        Assert.Equal(source.ReceivedAt.AddDays(30), memory.ExpiresAt);
        Assert.True(memory.AllowsAgentContext);
        Assert.Equal("session-safe-1", memory.SourceSessionId);
        Assert.Equal("luthn", memory.ProjectKey);
        Assert.Equal("release", memory.TaskKey);
        Assert.Equal(["delivery"], memory.TopicTags);
        Assert.Equal(source.Id, provenance.SourceEventId);
        Assert.Equal(memory.Id, provenance.MemoryItemId);
        Assert.Equal("local-owner", provenance.AuthenticatedUserId);
        Assert.Equal("owner.one", provenance.ClaimedUserId);
        Assert.Equal("codex", provenance.AgentId);
        Assert.Equal("codex.desktop", provenance.ApplicationId);
        Assert.Equal("luthn.hook", provenance.PluginId);
        Assert.Equal("luthn.codex.connector", provenance.ConnectorId);
        Assert.Equal("2", provenance.ConnectorVersion);
        var searchMetrics = factory.Services.GetRequiredService<IOperationalMetrics>().Snapshot().SearchRequests;
        Assert.Equal("context_pack", Assert.Single(searchMetrics).Surface);
    }

    [Fact]
    public async Task RawProjectPathAndFreeFormSourceMetadataAreRejected()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/api/agent/turn-summaries", new
        {
            sessionId = "session-private-metadata",
            sourceAgent = "codex",
            summary = "Public-safe summary.",
            coreTags = new[] { "privacy" },
            projectPath = "/private/workspace/Luthn",
            sourceMetadata = new Dictionary<string, string> { ["transcript_path"] = "/private/transcript.jsonl" }
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LuthnDbContext>();
        Assert.Empty(await db.SourceEvents.AsNoTracking().ToArrayAsync());
        Assert.Empty(await db.SharedMemoryItems.AsNoTracking().ToArrayAsync());
        Assert.Empty(await db.CollectionProvenance.AsNoTracking().ToArrayAsync());
    }

    [Fact]
    public async Task SensitiveTurnSummaryStaysBehindMemoryBoundary()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/api/agent/turn-summaries", new
        {
            sessionId = "session-sensitive-1",
            turnId = "turn-1",
            sourceAgent = "codex",
            summary = "Customer credential and private key rotation detail.",
            coreTags = new[] { "security" },
            idempotencyKey = "summary-sensitive-1"
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.False(body.RootElement.GetProperty("allowsAgentContext").GetBoolean());
        Assert.Equal("Restricted", body.RootElement.GetProperty("classification").GetProperty("sensitivity").GetString());

        using var contextResponse = await client.PostAsJsonAsync("/api/agent/context-packs", new
        {
            query = "credential",
            coreTags = new[] { "security" },
            maxItems = 10
        });
        using var contextBody = await JsonDocument.ParseAsync(await contextResponse.Content.ReadAsStreamAsync());

        Assert.Equal(HttpStatusCode.OK, contextResponse.StatusCode);
        Assert.Empty(contextBody.RootElement.GetProperty("items").EnumerateArray());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LuthnDbContext>();
        var memory = await db.SharedMemoryItems.SingleAsync();
        var encrypted = await db.SensitiveMemoryPayloads.SingleAsync();
        Assert.Equal(SensitivityLevel.Restricted, memory.Sensitivity);
        Assert.Equal(MemoryVisibility.PrivateToOwner, memory.Visibility);
        Assert.False(memory.AllowsAgentContext);
        Assert.True(SensitiveMemoryPersistence.IsInertProjection(memory));
        Assert.DoesNotContain("credential", encrypted.ProtectedPayload, StringComparison.OrdinalIgnoreCase);
        var protector = factory.Services.GetRequiredService<ISensitiveMemoryPayloadProtector>();
        var plaintext = protector.Unprotect(memory.Id, encrypted.ProtectedPayload);
        Assert.Equal("Customer credential and private key rotation detail.", plaintext.SafeSummary);
        var audit = await db.AuditEvents.SingleAsync(record => record.Action == "turn_summary.intake.classified");
        Assert.Equal("metadata-only", audit.PayloadClass);
        Assert.Equal("encrypted-payload-only", audit.RedactionState);
    }

    [Fact]
    public async Task SensitiveTurnSummaryTagAloneStaysBehindMemoryBoundary()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/api/agent/turn-summaries", new
        {
            sessionId = "session-sensitive-tag",
            turnId = "turn-1",
            sourceAgent = "codex",
            summary = "공개 가능한 배포 결과입니다.",
            coreTags = new[] { "release", "api 키" },
            idempotencyKey = "summary-sensitive-tag"
        });
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("Restricted", body.RootElement.GetProperty("classification").GetProperty("sensitivity").GetString());
        Assert.False(body.RootElement.GetProperty("allowsAgentContext").GetBoolean());
    }

    [Fact]
    public async Task TurnSummaryClassificationInputIncludesSummaryOnlyOnce()
    {
        var options = new DbContextOptionsBuilder<LuthnDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        await using var db = new LuthnDbContext(options);
        var classifier = new CapturingPublicClassifier();
        const string summary = "Unique provider payload for a release note.";

        await TurnSummaryEndpoints.IntakeTurnSummary(
            new TurnSummaryIntakeRequest
            {
                SessionId = "session-single-summary",
                TurnId = "turn-1",
                SourceAgent = "codex",
                Summary = summary,
                Title = "Release note",
                CoreTags = ["release"],
                IdempotencyKey = "summary-single-payload"
            },
            classifier,
            new PolicyEngine(),
            TestSensitiveMemoryProtection.Create(),
            Options.Create(new LuthnMemoryOptions()),
            db,
            new DefaultHttpContext(),
            CancellationToken.None);

        Assert.Equal(1, CountOccurrences(classifier.Content, summary));
        Assert.DoesNotContain($"content:\n{summary}", classifier.Content, StringComparison.Ordinal);
        Assert.Contains($"safeSummary:\n{summary}", classifier.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TurnSummaryIntakeIsIdempotentByKey()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var request = new
        {
            sessionId = "session-duplicate-1",
            turnId = "turn-1",
            sourceAgent = "codex",
            summary = "Published setup note for contributors.",
            coreTags = new[] { "setup" },
            idempotencyKey = "summary-duplicate-1"
        };

        using var first = await client.PostAsJsonAsync("/api/agent/turn-summaries", request);
        using var second = await client.PostAsJsonAsync("/api/agent/turn-summaries", request);
        using var firstBody = await JsonDocument.ParseAsync(await first.Content.ReadAsStreamAsync());
        using var secondBody = await JsonDocument.ParseAsync(await second.Content.ReadAsStreamAsync());

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.False(firstBody.RootElement.GetProperty("duplicate").GetBoolean());
        Assert.True(secondBody.RootElement.GetProperty("duplicate").GetBoolean());
        Assert.Equal(
            firstBody.RootElement.GetProperty("summaryId").GetString(),
            secondBody.RootElement.GetProperty("summaryId").GetString());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LuthnDbContext>();
        Assert.Equal(1, await db.SourceEvents.CountAsync());
        Assert.Equal(1, await db.ClassificationResults.CountAsync());
        Assert.Equal(1, await db.SharedMemoryItems.CountAsync());
    }

    [Fact]
    public async Task ExpiredAutomaticTurnSummaryIsExcludedFromRecall()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var intake = await client.PostAsJsonAsync("/api/agent/turn-summaries", new
        {
            sessionId = "session-expiring-summary",
            turnId = "turn-1",
            sourceAgent = "codex",
            summary = "Bounded automatic retention verification note.",
            coreTags = new[] { "retention" },
            idempotencyKey = "summary-expiring-1"
        });
        Assert.Equal(HttpStatusCode.Created, intake.StatusCode);

        using var beforeExpiry = await client.PostAsJsonAsync("/api/agent/context-packs", new
        {
            query = "bounded automatic retention",
            coreTags = new[] { "retention" },
            maxItems = 10
        });
        using var beforeBody = await JsonDocument.ParseAsync(await beforeExpiry.Content.ReadAsStreamAsync());
        Assert.Single(beforeBody.RootElement.GetProperty("items").EnumerateArray());

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LuthnDbContext>();
            var memory = await db.SharedMemoryItems.SingleAsync();
            memory.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1);
            await db.SaveChangesAsync();
        }

        using var afterExpiry = await client.PostAsJsonAsync("/api/agent/context-packs", new
        {
            query = "bounded automatic retention",
            coreTags = new[] { "retention" },
            maxItems = 10
        });
        using var afterBody = await JsonDocument.ParseAsync(await afterExpiry.Content.ReadAsStreamAsync());
        Assert.Empty(afterBody.RootElement.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task ConfiguredAutomaticTurnRetentionControlsExpiration()
    {
        using var factory = CreateFactory(retentionDays: 45);
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/api/agent/turn-summaries", new
        {
            sessionId = "session-custom-retention",
            turnId = "turn-1",
            sourceAgent = "codex",
            summary = "Custom automatic retention verification note.",
            coreTags = new[] { "retention" },
            idempotencyKey = "summary-custom-retention"
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LuthnDbContext>();
        var source = await db.SourceEvents.SingleAsync();
        var memory = await db.SharedMemoryItems.SingleAsync();
        Assert.Equal(MemoryRetentionKind.Ephemeral, memory.RetentionKind);
        Assert.Equal(source.ReceivedAt.AddDays(45), memory.ExpiresAt);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(366)]
    public void InvalidAutomaticTurnRetentionFailsStartup(int retentionDays)
    {
        using var factory = CreateFactory(retentionDays);

        var error = Assert.Throws<OptionsValidationException>(() => factory.CreateClient());

        Assert.Contains(
            LuthnMemoryOptions.AutomaticTurnRetentionValidationMessage,
            error.Message,
            StringComparison.Ordinal);
    }

    private WebApplicationFactory<Program> CreateFactory(int? retentionDays = null)
    {
        return _factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Luthn:TestingDatabaseName", Guid.NewGuid().ToString("N"));
            if (retentionDays is not null)
            {
                builder.UseSetting(
                    "Luthn:Memory:AutomaticTurnRetentionDays",
                    retentionDays.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
        });
    }

    private static int CountOccurrences(string content, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = content.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    private sealed class CapturingPublicClassifier : IContentClassifier
    {
        public ClassificationProviderBoundary Boundary { get; } =
            new("test", "classification-input", "local-only");

        public string Content { get; private set; } = "";

        public ValueTask<ClassificationResult> ClassifyAsync(
            PublicRecordId sourceId,
            string content,
            string? sourceType,
            CancellationToken cancellationToken = default)
        {
            Content = content;
            return ValueTask.FromResult(new ClassificationResult(
                sourceId,
                SensitivityLevel.Public,
                0.9,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                ContainsSensitiveMaterial: false));
        }
    }
}
