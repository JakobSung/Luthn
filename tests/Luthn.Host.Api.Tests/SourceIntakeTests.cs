using Luthn.Core.Classification;
using Luthn.Core.Common;
using Luthn.Core.Persistence;
using Luthn.Core.Policy;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Luthn.Host.Api.Tests;

public sealed class SourceIntakeTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public SourceIntakeTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task PublicSafeSourceCreatesPublicPersistenceRecords()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        const string content = "Published onboarding checklist for local contributors.";

        using var response = await client.PostAsJsonAsync("/api/sources", new
        {
            sourceSystem = "local",
            sourceType = "note",
            content,
            title = "Contributor onboarding",
            safeSummary = "Public onboarding checklist for local contributors.",
            coreTags = new[] { "onboarding", "public" },
            projectKey = " LUTHN ",
            taskKey = " ONBOARDING ",
            topicTags = new[] { " Docs ", "docs" }
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        var sourceId = body.RootElement.GetProperty("sourceId").GetString();
        var sourceEventId = body.RootElement.GetProperty("sourceEventId").GetString();
        var wikiProposalId = body.RootElement.GetProperty("wikiProposalId").GetString();
        Assert.Equal(sourceEventId, sourceId);
        Assert.False(string.IsNullOrWhiteSpace(sourceEventId));
        Assert.False(string.IsNullOrWhiteSpace(wikiProposalId));
        Assert.Equal("Public", body.RootElement.GetProperty("classification").GetProperty("sensitivity").GetString());
        Assert.True(body.RootElement.GetProperty("storageDecision").GetProperty("allowsAgentContext").GetBoolean());
        Assert.Equal("luthn", body.RootElement.GetProperty("projectKey").GetString());
        Assert.Equal("onboarding", body.RootElement.GetProperty("taskKey").GetString());
        Assert.Equal(["docs"], body.RootElement.GetProperty("topicTags").EnumerateArray().Select(item => item.GetString()));

        using var scope = CreateScope(factory);
        var db = GetDb(scope);
        var source = await db.SourceEvents.SingleAsync();
        Assert.Equal(sourceEventId, source.Id);
        Assert.Equal("local", source.SourceSystem);
        Assert.Equal("note", source.SourceType);
        Assert.False(source.ContainsSensitiveMaterial);
        Assert.StartsWith("sha256:", source.ContentDigest, StringComparison.Ordinal);
        Assert.DoesNotContain(content, source.ContentDigest, StringComparison.Ordinal);

        var classification = await db.ClassificationResults.SingleAsync();
        Assert.Equal(sourceEventId, classification.SourceEventId);
        Assert.Equal(SensitivityLevel.Public, classification.Sensitivity);
        Assert.Equal(StorageDecisionKind.WikiCandidate, classification.StorageDecision);
        Assert.False(classification.ContainsSensitiveMaterial);

        var proposal = await db.WikiProposals.SingleAsync();
        Assert.Equal(wikiProposalId, proposal.Id);
        Assert.Equal(sourceEventId, proposal.SourceEventId);
        Assert.Equal("Contributor onboarding", proposal.Title);
        Assert.Equal("Public onboarding checklist for local contributors.", proposal.SafeSummary);
        Assert.Equal(["onboarding", "public"], proposal.CoreTags);
        Assert.Equal("luthn", proposal.ProjectKey);
        Assert.Equal("onboarding", proposal.TaskKey);
        Assert.Equal(["docs"], proposal.TopicTags);
        Assert.True(proposal.AllowsAgentContext);
        Assert.Equal(SensitivityLevel.Public, proposal.Sensitivity);

        var providerAudit = await db.AuditEvents.SingleAsync(record => record.Action == "classification.provider.invoked");
        Assert.Equal(sourceEventId, providerAudit.SubjectId);
        Assert.Equal("local-classification-input", providerAudit.PayloadClass);
        Assert.Equal("local-only", providerAudit.RedactionState);

        var audit = await db.AuditEvents.SingleAsync(record => record.Action == "source.intake.classified");
        Assert.Equal(sourceEventId, audit.SubjectId);
        Assert.Equal("metadata-only", audit.PayloadClass);
        Assert.Equal("safe-projection-only", audit.RedactionState);
    }

    [Fact]
    public async Task SourceIntakeRejectsRawPathRecallMetadataBeforePersistence()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/api/sources", new
        {
            sourceSystem = "local",
            sourceType = "note",
            content = "Published onboarding checklist.",
            title = "Contributor onboarding",
            safeSummary = "Public onboarding checklist.",
            coreTags = new[] { "onboarding" },
            projectKey = "/Users/example/private-project"
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var scope = CreateScope(factory);
        Assert.Empty(await GetDb(scope).SourceEvents.ToArrayAsync());
    }

    [Fact]
    public async Task SensitiveSourceCreatesSensitiveReferenceWithoutAgentVisibleWikiProposal()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        const string content = "Customer contract includes payment terms and a private key.";

        using var response = await client.PostAsJsonAsync("/api/sources", new
        {
            sourceSystem = "local",
            sourceType = "note",
            content,
            title = "Customer contract",
            safeSummary = "Public-safe release steps.",
            coreTags = new[] { "contract" }
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        var sourceId = body.RootElement.GetProperty("sourceId").GetString();
        var sourceEventId = body.RootElement.GetProperty("sourceEventId").GetString();
        var sensitiveReferenceId = body.RootElement.GetProperty("sensitiveReferenceId").GetString();
        Assert.Equal(sourceEventId, sourceId);
        Assert.False(string.IsNullOrWhiteSpace(sourceEventId));
        Assert.False(string.IsNullOrWhiteSpace(sensitiveReferenceId));
        Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("wikiProposalId").ValueKind);
        Assert.Equal("Restricted", body.RootElement.GetProperty("classification").GetProperty("sensitivity").GetString());
        Assert.False(body.RootElement.GetProperty("storageDecision").GetProperty("allowsAgentContext").GetBoolean());

        using var scope = CreateScope(factory);
        var db = GetDb(scope);
        var source = await db.SourceEvents.SingleAsync();
        Assert.Equal(sourceEventId, source.Id);
        Assert.True(source.ContainsSensitiveMaterial);
        Assert.StartsWith("sha256:", source.ContentDigest, StringComparison.Ordinal);
        Assert.DoesNotContain(content, source.ContentDigest, StringComparison.Ordinal);

        var classification = await db.ClassificationResults.SingleAsync();
        Assert.Equal(sourceEventId, classification.SourceEventId);
        Assert.Equal(SensitivityLevel.Restricted, classification.Sensitivity);
        Assert.Equal(StorageDecisionKind.SensitiveDbOnly, classification.StorageDecision);
        Assert.True(classification.ContainsSensitiveMaterial);
        Assert.Contains("private key", classification.Categories);

        var reference = await db.SensitiveRecordReferences.SingleAsync();
        Assert.Equal(sensitiveReferenceId, reference.Id);
        Assert.Equal(sourceEventId, reference.SourceEventId);
        Assert.Equal("local", reference.SourceSystem);
        Assert.Equal("note", reference.SourceType);
        Assert.True(reference.ContainsSensitiveMaterial);
        Assert.Equal($"sensitive-record:{sourceEventId}", reference.ReferenceLabel);
        Assert.Equal("", reference.RedactedSummary);

        Assert.Empty(await db.WikiProposals.ToArrayAsync());
        Assert.Equal(2, await db.AuditEvents.CountAsync());
    }

    [Fact]
    public async Task DetectorOnlySignalCreatesSensitiveReferenceWithoutPersistingMatchedValue()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        const string matchedValue = "010-1234-5678";
        var content = $"담당 연락처는 {matchedValue}입니다.";

        using var response = await client.PostAsJsonAsync("/api/sources", new
        {
            sourceSystem = "local",
            sourceType = "note",
            content,
            title = "담당자 연락",
            safeSummary = "연락 절차",
            coreTags = new[] { "contact" }
        });
        var responseJson = await response.Content.ReadAsStringAsync();
        using var body = JsonDocument.Parse(responseJson);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(
            "Confidential",
            body.RootElement.GetProperty("classification").GetProperty("sensitivity").GetString());
        Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("wikiProposalId").ValueKind);
        Assert.NotEqual(JsonValueKind.Null, body.RootElement.GetProperty("sensitiveReferenceId").ValueKind);
        Assert.DoesNotContain(matchedValue, responseJson, StringComparison.Ordinal);

        using var scope = CreateScope(factory);
        var db = GetDb(scope);
        var source = await db.SourceEvents.SingleAsync();
        var classification = await db.ClassificationResults.SingleAsync();
        var reference = await db.SensitiveRecordReferences.SingleAsync();
        var audits = await db.AuditEvents.ToArrayAsync();

        Assert.DoesNotContain(matchedValue, source.ContentDigest, StringComparison.Ordinal);
        Assert.Contains("personal identifier", classification.Categories);
        Assert.DoesNotContain(classification.Categories, category => category.Contains(matchedValue, StringComparison.Ordinal));
        Assert.DoesNotContain(matchedValue, reference.ReferenceLabel, StringComparison.Ordinal);
        Assert.DoesNotContain(matchedValue, reference.RedactedSummary, StringComparison.Ordinal);
        Assert.All(audits, audit =>
        {
            Assert.DoesNotContain(matchedValue, audit.SubjectId, StringComparison.Ordinal);
            Assert.DoesNotContain(matchedValue, audit.PayloadClass, StringComparison.Ordinal);
            Assert.DoesNotContain(matchedValue, audit.RedactionState, StringComparison.Ordinal);
        });
        Assert.Empty(await db.WikiProposals.ToArrayAsync());
    }

    [Fact]
    public async Task SensitiveSourceDoesNotTrustUnsafeCallerSummaryAsRedactedOutput()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/api/sources", new
        {
            sourceSystem = "local",
            sourceType = "note",
            content = "Customer contract includes payment terms and a private key.",
            title = "Customer contract",
            safeSummary = "Private customer raw vault terms.",
            coreTags = new[] { "contract" }
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        using var scope = CreateScope(factory);
        var db = GetDb(scope);
        var reference = await db.SensitiveRecordReferences.SingleAsync();
        Assert.True(reference.ContainsSensitiveMaterial);
        Assert.Equal("", reference.RedactedSummary);

        Assert.Empty(await db.WikiProposals.ToArrayAsync());
        Assert.Equal(2, await db.AuditEvents.CountAsync());
    }

    [Fact]
    public async Task SensitiveSourceIsAbsentFromAgentContextAndWikiProposalEndpoints()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var intakeResponse = await client.PostAsJsonAsync("/api/sources", new
        {
            sourceSystem = "local",
            sourceType = "note",
            content = "Customer contract includes payment terms.",
            title = "Customer contract",
            safeSummary = "Redacted customer contract placeholder.",
            coreTags = new[] { "contract" }
        });
        Assert.Equal(HttpStatusCode.Created, intakeResponse.StatusCode);
        using var intakeBody = await JsonDocument.ParseAsync(await intakeResponse.Content.ReadAsStreamAsync());
        var sourceEventId = intakeBody.RootElement.GetProperty("sourceEventId").GetString();
        var sensitiveReferenceId = intakeBody.RootElement.GetProperty("sensitiveReferenceId").GetString();

        using var contextResponse = await client.PostAsJsonAsync("/api/agent/context-packs", new
        {
            coreTags = new[] { "contract" },
            maxItems = 10
        });
        using var contextBody = await JsonDocument.ParseAsync(await contextResponse.Content.ReadAsStreamAsync());

        Assert.Equal(HttpStatusCode.OK, contextResponse.StatusCode);
        Assert.Empty(contextBody.RootElement.GetProperty("items").EnumerateArray());

        using var sourceWikiResponse = await client.GetAsync($"/api/wiki/proposals/{sourceEventId}");
        using var referenceWikiResponse = await client.GetAsync($"/api/wiki/proposals/{sensitiveReferenceId}");

        Assert.Equal(HttpStatusCode.NotFound, sourceWikiResponse.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, referenceWikiResponse.StatusCode);
    }

    [Fact]
    public async Task RawContentIsNotPersistedInCurrentPublicPersistenceRecords()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        const string rawContent = "Customer contract raw phrase never persisted.";

        using var response = await client.PostAsJsonAsync("/api/sources", new
        {
            sourceSystem = "local",
            sourceType = "note",
            content = rawContent,
            title = "Customer contract",
            safeSummary = "Redacted customer contract placeholder.",
            coreTags = new[] { "contract" }
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        using var scope = CreateScope(factory);
        var db = GetDb(scope);
        var publicRecords = new List<object>
        {
            await db.SourceEvents.SingleAsync(),
            await db.ClassificationResults.SingleAsync(),
            await db.SensitiveRecordReferences.SingleAsync()
        };
        publicRecords.AddRange(await db.AuditEvents.ToArrayAsync());

        foreach (var record in publicRecords)
        {
            var stringValues = record.GetType()
                .GetProperties()
                .Where(property => property.PropertyType == typeof(string))
                .Select(property => (string?)property.GetValue(record))
                .Where(value => value is not null);

            Assert.DoesNotContain(
                stringValues,
                value => value!.Contains(rawContent, StringComparison.Ordinal));
        }

        Assert.Empty(await db.WikiProposals.ToArrayAsync());
    }

    [Fact]
    public async Task OversizedPublicProjectionFieldsReturnBadRequestBeforePersistence()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/api/sources", new
        {
            sourceSystem = "local",
            sourceType = "note",
            content = "Published onboarding checklist for local contributors.",
            title = new string('a', 201),
            safeSummary = "Public onboarding checklist for local contributors.",
            coreTags = new[] { "onboarding" }
        });
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("title must be 200 characters or fewer.", body.RootElement.GetProperty("detail").GetString());

        using var scope = CreateScope(factory);
        var db = GetDb(scope);
        Assert.Empty(await db.AuditEvents.ToArrayAsync());
        Assert.Empty(await db.SourceEvents.ToArrayAsync());
    }

    [Fact]
    public async Task OversizedContentReturnsBadRequestBeforeClassifierAndPersistence()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/api/sources", new
        {
            sourceSystem = "local",
            sourceType = "note",
            content = new string('c', 20_001),
            title = "Contributor onboarding",
            safeSummary = "Public onboarding checklist for local contributors.",
            coreTags = new[] { "onboarding" }
        });
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("content must be 20000 characters or fewer.", body.RootElement.GetProperty("detail").GetString());

        using var scope = CreateScope(factory);
        var db = GetDb(scope);
        Assert.Empty(await db.AuditEvents.ToArrayAsync());
        Assert.Empty(await db.SourceEvents.ToArrayAsync());
    }

    [Fact]
    public async Task OversizedRawContentReturnsBadRequestBeforeClassifierAndPersistence()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/api/sources", new
        {
            sourceSystem = "local",
            sourceType = "note",
            content = new string('c', 20_000) + " ",
            title = "Contributor onboarding",
            safeSummary = "Public onboarding checklist for local contributors.",
            coreTags = new[] { "onboarding" }
        });
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("content must be 20000 characters or fewer.", body.RootElement.GetProperty("detail").GetString());

        using var scope = CreateScope(factory);
        var db = GetDb(scope);
        Assert.Empty(await db.AuditEvents.ToArrayAsync());
        Assert.Empty(await db.SourceEvents.ToArrayAsync());
    }

    [Fact]
    public async Task MaxSizedUnicodeContentIsAccepted()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/api/sources", new
        {
            sourceSystem = "local",
            sourceType = "note",
            content = new string('한', 20_000),
            title = "Contributor onboarding",
            safeSummary = "Public onboarding checklist for local contributors.",
            coreTags = new[] { "onboarding" }
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        using var scope = CreateScope(factory);
        var db = GetDb(scope);
        Assert.Single(await db.SourceEvents.ToArrayAsync());
    }

    [Fact]
    public async Task KoreanSensitiveTitleAloneBlocksPublicProjection()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/api/sources", new
        {
            sourceSystem = "local",
            sourceType = "note",
            content = "공개 가능한 배포 안내입니다.",
            title = "운영 개인 키 교체",
            safeSummary = "공개 가능한 배포 요약입니다.",
            coreTags = new[] { "release" }
        });
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("Restricted", body.RootElement.GetProperty("classification").GetProperty("sensitivity").GetString());
        Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("wikiProposalId").ValueKind);
        Assert.False(body.RootElement.GetProperty("storageDecision").GetProperty("allowsAgentContext").GetBoolean());

        using var scope = CreateScope(factory);
        var db = GetDb(scope);
        Assert.Empty(await db.WikiProposals.ToArrayAsync());
        Assert.Single(await db.SensitiveRecordReferences.ToArrayAsync());
    }

    [Fact]
    public async Task KoreanSensitiveTagAloneBlocksPublicProjection()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/api/sources", new
        {
            sourceSystem = "local",
            sourceType = "note",
            content = "공개 가능한 배포 안내입니다.",
            title = "배포 안내",
            safeSummary = "공개 가능한 배포 요약입니다.",
            coreTags = new[] { "release", "주민등록번호" }
        });
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("Confidential", body.RootElement.GetProperty("classification").GetProperty("sensitivity").GetString());
        Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("wikiProposalId").ValueKind);
        Assert.False(body.RootElement.GetProperty("storageDecision").GetProperty("allowsAgentContext").GetBoolean());
    }

    [Fact]
    public async Task KoreanSensitiveSafeSummaryAloneBlocksPublicProjection()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/api/sources", new
        {
            sourceSystem = "local",
            sourceType = "note",
            content = "공개 가능한 배포 안내입니다.",
            title = "배포 안내",
            safeSummary = "고객 계약서의 결제 조건입니다.",
            coreTags = new[] { "release" }
        });
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("Confidential", body.RootElement.GetProperty("classification").GetProperty("sensitivity").GetString());
        Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("wikiProposalId").ValueKind);
        Assert.False(body.RootElement.GetProperty("storageDecision").GetProperty("allowsAgentContext").GetBoolean());
    }

    [Fact]
    public async Task OversizedCoreTagsReturnBadRequestBeforePersistence()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/api/sources", new
        {
            sourceSystem = "local",
            sourceType = "note",
            content = "Published onboarding checklist for local contributors.",
            title = "Contributor onboarding",
            safeSummary = "Public onboarding checklist for local contributors.",
            coreTags = Enumerable.Range(0, 33).Select(index => $"tag-{index}").ToArray()
        });
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("coreTags must include 32 tags or fewer.", body.RootElement.GetProperty("detail").GetString());

        using var scope = CreateScope(factory);
        var db = GetDb(scope);
        Assert.Empty(await db.AuditEvents.ToArrayAsync());
        Assert.Empty(await db.SourceEvents.ToArrayAsync());
    }

    [Fact]
    public async Task ProviderAuditIsPersistedWhenClassificationProviderFails()
    {
        var options = new DbContextOptionsBuilder<LuthnDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        await using var db = new LuthnDbContext(options);
        var request = new SourceIntakeRequest
        {
            SourceSystem = "local",
            SourceType = "note",
            Content = "Customer contract summary.",
            Title = "Customer contract",
            SafeSummary = "Redacted customer contract placeholder.",
            CoreTags = ["contract"]
        };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => SourceIntakeEndpoints.IntakeSource(
            request,
            new FailingContentClassifier(),
            new PolicyEngine(),
            db,
            new DefaultHttpContext(),
            CancellationToken.None));
        Assert.Equal("Simulated provider failure.", error.Message);

        var audit = await db.AuditEvents.SingleAsync(record => record.Action == "classification.provider.invoked");
        Assert.StartsWith("source-", audit.SubjectId, StringComparison.Ordinal);
        Assert.Equal("external-classification-input", audit.PayloadClass);
        Assert.Equal("external-provider-opt-in", audit.RedactionState);
        Assert.Empty(await db.SourceEvents.ToArrayAsync());
        Assert.Empty(await db.ClassificationResults.ToArrayAsync());
    }

    [Fact]
    public async Task ProviderUnavailableReturnsProblemWithoutPersistingSourceRecords()
    {
        var options = new DbContextOptionsBuilder<LuthnDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        await using var db = new LuthnDbContext(options);
        var request = new SourceIntakeRequest
        {
            SourceSystem = "local",
            SourceType = "note",
            Content = "Customer contract summary.",
            Title = "Customer contract",
            SafeSummary = "Redacted customer contract placeholder.",
            CoreTags = ["contract"]
        };

        var result = await SourceIntakeEndpoints.IntakeSource(
            request,
            new UnavailableContentClassifier(),
            new PolicyEngine(),
            db,
            new DefaultHttpContext(),
            CancellationToken.None);

        var problem = Assert.IsType<ProblemHttpResult>(result.Result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, problem.StatusCode);
        Assert.Single(await db.AuditEvents.ToArrayAsync());
        Assert.Empty(await db.SourceEvents.ToArrayAsync());
        Assert.Empty(await db.ClassificationResults.ToArrayAsync());
    }

    private WebApplicationFactory<Program> CreateFactory()
    {
        return _factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Luthn:TestingDatabaseName", Guid.NewGuid().ToString("N"));
        });
    }

    private static IServiceScope CreateScope(WebApplicationFactory<Program> factory) =>
        factory.Services.CreateScope();

    private static LuthnDbContext GetDb(IServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<LuthnDbContext>();

    private sealed class FailingContentClassifier : IContentClassifier
    {
        public ClassificationProviderBoundary Boundary { get; } =
            new("external-http", "external-classification-input", "external-provider-opt-in");

        public ValueTask<ClassificationResult> ClassifyAsync(
            PublicRecordId sourceId,
            string content,
            string? sourceType,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Simulated provider failure.");
    }

    private sealed class UnavailableContentClassifier : IContentClassifier
    {
        public ClassificationProviderBoundary Boundary { get; } =
            new("external-http", "external-classification-input", "external-provider-opt-in");

        public ValueTask<ClassificationResult> ClassifyAsync(
            PublicRecordId sourceId,
            string content,
            string? sourceType,
            CancellationToken cancellationToken = default) =>
            throw new ClassificationProviderException("Classification provider request failed.");
    }
}
