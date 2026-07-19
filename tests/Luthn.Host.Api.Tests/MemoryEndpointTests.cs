using Luthn.Core.Classification;
using Luthn.Core.Common;
using Luthn.Core.Memory;
using Luthn.Core.Persistence;
using Luthn.Core.Policy;
using Luthn.Core.Search;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Luthn.Host.Api.Tests;

public sealed class MemoryEndpointTests
{
    [Fact]
    public async Task MemoryWriteReadAndQueryUseSafeAgentVisibleProjection()
    {
        await using var db = CreateDbContext();
        var httpContext = new DefaultHttpContext();
        var request = new CreateMemoryItemRequest
        {
            Title = "Release guide memory",
            SafeSummary = "Public-safe deployment memory.",
            CoreTags = [" guide ", "Guide", "release"],
            Visibility = MemoryVisibility.SharedAcrossAgents,
            ProjectKey = " LUTHN ",
            TaskKey = " RELEASE ",
            TopicTags = [" Delivery ", "delivery"]
        };

        var createResult = await MemoryEndpoints.CreateMemoryItem(
            request,
            new MockContentClassifier(),
            new PolicyEngine(),
            TestSensitiveMemoryProtection.Create(),
            db,
            httpContext,
            CancellationToken.None);
        var created = Assert.IsType<Created<MemoryItemResponse>>(createResult.Result);
        var id = created.Value!.Id;

        Assert.True(created.Value.AllowsAgentContext);
        Assert.Equal(["guide", "release"], created.Value.CoreTags);
        Assert.Equal("luthn", created.Value.ProjectKey);
        Assert.Equal("release", created.Value.TaskKey);
        Assert.Equal(["delivery"], created.Value.TopicTags);
        Assert.DoesNotContain("raw", created.Value.SafeSummary, StringComparison.OrdinalIgnoreCase);

        var readResult = await MemoryEndpoints.ReadMemoryItem(id, db, CancellationToken.None);
        var ok = Assert.IsType<Ok<MemoryItemResponse>>(readResult.Result);

        Assert.Equal(id, ok.Value!.Id);
        Assert.Equal("Public-safe deployment memory.", ok.Value.SafeSummary);

        var metrics = new OperationalMetrics();
        var queryResult = await MemoryEndpoints.QueryMemoryItems(
            new MemoryQueryRequest("deployment", ["release"], 10, "LUTHN", "RELEASE", ["Delivery"]),
            new DeterministicRetrievalBackend(new SafeSearchIndex()),
            new DbBackedRetrievalCandidateSelector(db, TimeProvider.System),
            db,
            metrics,
            TimeProvider.System,
            CancellationToken.None);
        var queryOk = Assert.IsType<Ok<MemoryQueryResponse>>(queryResult.Result);
        var item = Assert.Single(queryOk.Value!.Items);

        Assert.Equal(id, item.Id);
        Assert.Equal("luthn", queryOk.Value.ProjectKey);
        Assert.Equal("release", queryOk.Value.TaskKey);
        Assert.Equal(["delivery"], queryOk.Value.TopicTags);
        Assert.Equal("luthn", item.ProjectKey);
        Assert.Equal("release", item.TaskKey);
        Assert.Equal(["delivery"], item.TopicTags);
        Assert.Equal("memory_query", Assert.Single(metrics.Snapshot().SearchRequests).Surface);
        Assert.True(SearchTelemetry.IsValidRetrievalId(queryOk.Value.RetrievalId));
        var audit = Assert.Single(await db.AuditEvents
            .Where(record => record.Action == "memory.item.classified")
            .ToArrayAsync());
        Assert.Equal(id, audit.SubjectId);
        Assert.Equal("metadata-only", audit.PayloadClass);
        Assert.Equal("safe-projection-only", audit.RedactionState);
    }

    [Fact]
    public async Task MemoryWriteRejectsRestrictedSharedMemory()
    {
        await using var db = CreateDbContext();
        var request = new CreateMemoryItemRequest
        {
            Title = "Restricted note",
            SafeSummary = "Redacted restricted note.",
            Sensitivity = Luthn.Core.Classification.SensitivityLevel.Restricted,
            CoreTags = ["restricted"],
            Visibility = MemoryVisibility.SharedAcrossAgents
        };
        var result = await MemoryEndpoints.CreateMemoryItem(
            request,
            new MockContentClassifier(),
            new PolicyEngine(),
            TestSensitiveMemoryProtection.Create(),
            db,
            new DefaultHttpContext(),
            CancellationToken.None);

        Assert.IsType<BadRequest<Microsoft.AspNetCore.Mvc.ProblemDetails>>(result.Result);
        Assert.Empty(await db.SharedMemoryItems.ToArrayAsync());
    }

    [Fact]
    public async Task MemoryWriteDowngradesClassifierSensitiveSummaryToPrivateBoundary()
    {
        await using var db = CreateDbContext();
        var request = new CreateMemoryItemRequest
        {
            Title = "Sensitive memory",
            SafeSummary = "Credential rotation detail was requested but should stay private.",
            CoreTags = ["security"],
            Visibility = MemoryVisibility.SharedAcrossAgents
        };
        var protector = TestSensitiveMemoryProtection.Create();

        var result = await MemoryEndpoints.CreateMemoryItem(
            request,
            new MockContentClassifier(),
            new PolicyEngine(),
            protector,
            db,
            new DefaultHttpContext(),
            CancellationToken.None);

        var created = Assert.IsType<Created<MemoryItemResponse>>(result.Result);
        Assert.False(created.Value!.AllowsAgentContext);
        Assert.Equal(MemoryVisibility.PrivateToOwner, created.Value.Visibility);
        Assert.Equal(SensitiveMemoryPersistence.ProtectedTitle, created.Value.Title);
        Assert.Equal(SensitiveMemoryPersistence.ProtectedSummary, created.Value.SafeSummary);
        var stored = await db.SharedMemoryItems.AsNoTracking().SingleAsync();
        var encrypted = await db.SensitiveMemoryPayloads.AsNoTracking().SingleAsync();
        Assert.True(SensitiveMemoryPersistence.IsInertProjection(stored));
        Assert.DoesNotContain("Credential", encrypted.ProtectedPayload, StringComparison.OrdinalIgnoreCase);
        var plaintext = protector.Unprotect(stored.Id, encrypted.ProtectedPayload);
        Assert.Equal(request.Title, plaintext.Title);
        Assert.Equal(request.SafeSummary, plaintext.SafeSummary);
        var audit = await db.AuditEvents.SingleAsync(record => record.Action == "memory.item.classified");
        Assert.Equal("metadata-only", audit.PayloadClass);
        Assert.Equal("encrypted-payload-only", audit.RedactionState);

        var read = await MemoryEndpoints.ReadMemoryItem(created.Value.Id, db, CancellationToken.None);
        var query = await MemoryEndpoints.QueryMemoryItems(
            new MemoryQueryRequest("credential", ["security"], 10),
            new DeterministicRetrievalBackend(new SafeSearchIndex()),
            new DbBackedRetrievalCandidateSelector(db, TimeProvider.System),
            db,
            new OperationalMetrics(),
            TimeProvider.System,
            CancellationToken.None);

        Assert.IsType<NotFound>(read.Result);
        var queryOk = Assert.IsType<Ok<MemoryQueryResponse>>(query.Result);
        Assert.Empty(queryOk.Value!.Items);
    }

    [Fact]
    public async Task KoreanSensitiveTitleAloneKeepsSharedMemoryPrivate()
    {
        await using var db = CreateDbContext();
        var request = new CreateMemoryItemRequest
        {
            Title = "고객 비밀번호 교체 메모",
            SafeSummary = "공개 가능한 운영 요약입니다.",
            CoreTags = ["security"],
            Visibility = MemoryVisibility.SharedAcrossAgents
        };

        var result = await MemoryEndpoints.CreateMemoryItem(
            request,
            new MockContentClassifier(),
            new PolicyEngine(),
            TestSensitiveMemoryProtection.Create(),
            db,
            new DefaultHttpContext(),
            CancellationToken.None);

        var created = Assert.IsType<Created<MemoryItemResponse>>(result.Result);
        Assert.Equal(SensitivityLevel.Restricted, created.Value!.Sensitivity);
        Assert.Equal(MemoryVisibility.PrivateToOwner, created.Value.Visibility);
        Assert.False(created.Value.AllowsAgentContext);
        Assert.IsType<NotFound>(
            (await MemoryEndpoints.ReadMemoryItem(created.Value.Id, db, CancellationToken.None)).Result);
    }

    [Fact]
    public async Task KoreanSensitiveTagAloneKeepsSharedMemoryPrivate()
    {
        await using var db = CreateDbContext();
        var request = new CreateMemoryItemRequest
        {
            Title = "공개 가능한 운영 메모",
            SafeSummary = "공개 가능한 운영 요약입니다.",
            CoreTags = ["release", "주민등록번호"],
            Visibility = MemoryVisibility.PublicSafe
        };

        var result = await MemoryEndpoints.CreateMemoryItem(
            request,
            new MockContentClassifier(),
            new PolicyEngine(),
            TestSensitiveMemoryProtection.Create(),
            db,
            new DefaultHttpContext(),
            CancellationToken.None);

        var created = Assert.IsType<Created<MemoryItemResponse>>(result.Result);
        Assert.Equal(SensitivityLevel.Confidential, created.Value!.Sensitivity);
        Assert.Equal(MemoryVisibility.PrivateToOwner, created.Value.Visibility);
        Assert.False(created.Value.AllowsAgentContext);
    }

    [Fact]
    public async Task MemoryWriteMapsClassificationProviderFailureToProblem()
    {
        await using var db = CreateDbContext();
        var request = new CreateMemoryItemRequest
        {
            Title = "Release memory",
            SafeSummary = "Public-safe release summary.",
            CoreTags = ["release"],
            Visibility = MemoryVisibility.PublicSafe
        };

        var result = await MemoryEndpoints.CreateMemoryItem(
            request,
            new UnavailableContentClassifier(),
            new PolicyEngine(),
            TestSensitiveMemoryProtection.Create(),
            db,
            new DefaultHttpContext(),
            CancellationToken.None);

        var problem = Assert.IsType<ProblemHttpResult>(result.Result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, problem.StatusCode);
        Assert.Empty(await db.SharedMemoryItems.ToArrayAsync());
        Assert.Empty(await db.AuditEvents.ToArrayAsync());
    }

    [Fact]
    public async Task MemoryWriteRejectsDurableMemoryWithExpiration()
    {
        await using var db = CreateDbContext();
        var request = new CreateMemoryItemRequest
        {
            Title = "Durable memory",
            SafeSummary = "Public-safe durable summary.",
            CoreTags = ["runbook"],
            Visibility = MemoryVisibility.PublicSafe,
            RetentionKind = MemoryRetentionKind.Durable,
            ExpiresAt = DateTimeOffset.Parse("2026-01-01T00:00:00Z")
        };

        var result = await MemoryEndpoints.CreateMemoryItem(
            request,
            new MockContentClassifier(),
            new PolicyEngine(),
            TestSensitiveMemoryProtection.Create(),
            db,
            new DefaultHttpContext(),
            CancellationToken.None);

        Assert.IsType<BadRequest<Microsoft.AspNetCore.Mvc.ProblemDetails>>(result.Result);
        Assert.Empty(await db.SharedMemoryItems.ToArrayAsync());
    }

    [Fact]
    public async Task MemoryWriteRejectsOversizedSourceSessionId()
    {
        await using var db = CreateDbContext();
        var request = new CreateMemoryItemRequest
        {
            Title = "Release memory",
            SafeSummary = "Public-safe release summary.",
            CoreTags = ["runbook"],
            Visibility = MemoryVisibility.PublicSafe,
            SourceSessionId = new string('s', 129)
        };

        var result = await MemoryEndpoints.CreateMemoryItem(
            request,
            new MockContentClassifier(),
            new PolicyEngine(),
            TestSensitiveMemoryProtection.Create(),
            db,
            new DefaultHttpContext(),
            CancellationToken.None);

        var badRequest = Assert.IsType<BadRequest<Microsoft.AspNetCore.Mvc.ProblemDetails>>(result.Result);
        Assert.Equal("sourceSessionId must be 128 characters or fewer.", badRequest.Value!.Detail);
        Assert.Empty(await db.SharedMemoryItems.ToArrayAsync());
    }

    [Fact]
    public async Task MemoryReadDoesNotExposePrivateOrExpiredSafeSummaries()
    {
        await using var db = CreateDbContext();
        var privateId = await CreateMemoryAsync(db, new CreateMemoryItemRequest
        {
            Title = "Private memory",
            SafeSummary = "Private safe summary.",
            Sensitivity = Luthn.Core.Classification.SensitivityLevel.Internal,
            CoreTags = ["private"],
            Visibility = MemoryVisibility.PrivateToOwner
        });
        var expiredId = await CreateMemoryAsync(db, new CreateMemoryItemRequest
        {
            Title = "Expired public memory",
            SafeSummary = "Expired public safe summary.",
            CoreTags = ["release"],
            Visibility = MemoryVisibility.PublicSafe,
            RetentionKind = MemoryRetentionKind.Ephemeral,
            ExpiresAt = DateTimeOffset.Parse("2026-01-01T00:00:00Z")
        });

        var privateRead = await MemoryEndpoints.ReadMemoryItem(privateId, db, CancellationToken.None);
        var expiredRead = await MemoryEndpoints.ReadMemoryItem(expiredId, db, CancellationToken.None);
        var query = await MemoryEndpoints.QueryMemoryItems(
            new MemoryQueryRequest(CoreTags: ["release", "private"], MaxItems: 10),
            new DeterministicRetrievalBackend(new SafeSearchIndex()),
            new DbBackedRetrievalCandidateSelector(db, TimeProvider.System),
            db,
            new OperationalMetrics(),
            TimeProvider.System,
            CancellationToken.None);

        Assert.IsType<NotFound>(privateRead.Result);
        Assert.IsType<NotFound>(expiredRead.Result);
        var queryOk = Assert.IsType<Ok<MemoryQueryResponse>>(query.Result);
        Assert.Empty(queryOk.Value!.Items);
    }

    [Fact]
    public async Task MemoryWriteRejectsOversizedCoreTags()
    {
        await using var db = CreateDbContext();
        var request = new CreateMemoryItemRequest
        {
            Title = "Release memory",
            SafeSummary = "Public-safe release summary.",
            CoreTags = [new string('t', 65)],
            Visibility = MemoryVisibility.PublicSafe
        };

        var result = await MemoryEndpoints.CreateMemoryItem(
            request,
            new MockContentClassifier(),
            new PolicyEngine(),
            TestSensitiveMemoryProtection.Create(),
            db,
            new DefaultHttpContext(),
            CancellationToken.None);

        var badRequest = Assert.IsType<BadRequest<Microsoft.AspNetCore.Mvc.ProblemDetails>>(result.Result);
        Assert.Equal("coreTags entries must be 64 characters or fewer.", badRequest.Value!.Detail);
        Assert.Empty(await db.SharedMemoryItems.ToArrayAsync());
    }

    [Fact]
    public async Task MemoryQueryRejectsOversizedQueryBeforeSearch()
    {
        await using var db = CreateDbContext();

        var result = await MemoryEndpoints.QueryMemoryItems(
            new MemoryQueryRequest(new string('q', 501), ["runbook"], 10),
            new DeterministicRetrievalBackend(new SafeSearchIndex()),
            new DbBackedRetrievalCandidateSelector(db, TimeProvider.System),
            db,
            new OperationalMetrics(),
            TimeProvider.System,
            CancellationToken.None);

        var badRequest = Assert.IsType<BadRequest<Microsoft.AspNetCore.Mvc.ProblemDetails>>(result.Result);
        Assert.Equal("query must be 500 characters or fewer.", badRequest.Value!.Detail);
    }

    [Fact]
    public async Task MemoryQueryFindsOlderMatchingItemWhenCorpusExceedsCandidateCap()
    {
        await using var db = CreateDbContext();
        var now = DateTimeOffset.UtcNow;
        db.SharedMemoryItems.Add(new SharedMemoryItemRecord
        {
            Id = "memory-old-match",
            Title = "Needle recovery memory",
            SafeSummary = "Public-safe recovery memory.",
            Sensitivity = Luthn.Core.Classification.SensitivityLevel.Public,
            CoreTags = ["needle"],
            Visibility = MemoryVisibility.PublicSafe,
            RetentionKind = MemoryRetentionKind.Durable,
            AllowsAgentContext = true,
            CreatedAt = now.AddDays(-2),
            CreatedBy = "test"
        });
        db.SharedMemoryItems.AddRange(Enumerable.Range(0, 1001).Select(index => new SharedMemoryItemRecord
        {
            Id = $"memory-newer-unmatched-{index}",
            Title = $"General release memory {index}",
            SafeSummary = "Public-safe unmatched memory.",
            Sensitivity = Luthn.Core.Classification.SensitivityLevel.Public,
            CoreTags = ["release"],
            Visibility = MemoryVisibility.PublicSafe,
            RetentionKind = MemoryRetentionKind.Durable,
            AllowsAgentContext = true,
            CreatedAt = now.AddMinutes(index),
            CreatedBy = "test"
        }));
        await db.SaveChangesAsync();

        var result = await MemoryEndpoints.QueryMemoryItems(
            new MemoryQueryRequest("needle", ["needle"], 10),
            new DeterministicRetrievalBackend(new SafeSearchIndex()),
            new DbBackedRetrievalCandidateSelector(db, TimeProvider.System),
            db,
            new OperationalMetrics(),
            TimeProvider.System,
            CancellationToken.None);

        var ok = Assert.IsType<Ok<MemoryQueryResponse>>(result.Result);
        var item = Assert.Single(ok.Value!.Items);
        Assert.Equal("memory-old-match", item.Id);
    }

    private static async Task<string> CreateMemoryAsync(
        LuthnDbContext db,
        CreateMemoryItemRequest request)
    {
        var result = await MemoryEndpoints.CreateMemoryItem(
            request,
            new MockContentClassifier(),
            new PolicyEngine(),
            TestSensitiveMemoryProtection.Create(),
            db,
            new DefaultHttpContext(),
            CancellationToken.None);
        var created = Assert.IsType<Created<MemoryItemResponse>>(result.Result);

        return created.Value!.Id;
    }

    private static LuthnDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<LuthnDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        return new LuthnDbContext(options);
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
