using Luthn.Core.Classification;
using Luthn.Core.Common;
using Luthn.Core.Memory;
using Luthn.Core.Persistence;
using Luthn.Core.Policy;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Luthn.Host.Api.Tests;

public sealed class ProtectedInformationAccessResolutionTests
{
    [Fact]
    public async Task SharedMemoryWriteWithProtectedProjectionCreatesReviewableRequest()
    {
        await using var db = TestData.CreateDbContext();
        var result = await MemoryEndpoints.CreateMemoryItem(
            new CreateMemoryItemRequest
            {
                Title = "홍길동 견적 메모",
                SafeSummary = "홍길동 사원의 주소는 person@example.com이고 신규 견적을 발행했다.",
                CoreTags = ["sales", "quote"],
                Visibility = MemoryVisibility.SharedAcrossAgents
            },
            new AgentSafeMemoryProjectionSelector(
                new LocalContextualContentClassifier(),
                new DeterministicSensitiveDataDetector(),
                new PolicyEngine()),
            TestSensitiveMemoryProtection.Create(),
            db,
            new DefaultHttpContext(),
            CancellationToken.None);
        var created = Assert.IsType<Created<MemoryItemResponse>>(result.Result).Value!;
        Assert.True(created.AllowsAgentContext);
        Assert.Equal(SensitivityLevel.Public, created.Sensitivity);
        var memoryItemId = created.Id;
        var principal = new LuthnRequestPrincipal(
            LuthnIdentityOptions.DefaultSingleOwnerUserId,
            WorkspaceIds.Default,
            LuthnActorKind.Agent,
            "agent",
            IsOperator: false);
        var workflow = TestData.CreateWorkflow(db, new ManualTimeProvider(DateTimeOffset.UtcNow));

        var resolution = await ResolveAsync(workflow, memoryItemId, principal);

        Assert.Equal(ProtectedInformationAccessStatuses.Requested, resolution.Status);
        Assert.NotNull(resolution.RequestId);
        var reference = await db.SensitiveRecordReferences.SingleAsync();
        Assert.Equal(memoryItemId, reference.MemoryItemId);
        Assert.Equal(
            reference.Id,
            (await db.SensitiveAccessRequests.SingleAsync()).SensitiveRecordReferenceId);
    }

    [Fact]
    public async Task SafeMemoryCorrelationCreatesAndReusesReviewableRequest()
    {
        await using var db = TestData.CreateDbContext();
        AddProtectedMemory(db, "memory-a", TestData.Principal);
        await db.SaveChangesAsync();
        var workflow = TestData.CreateWorkflow(db, new ManualTimeProvider(TestData.ObservedAt));

        var first = await workflow.ResolveProtectedInformationAccessAsync(
            new ProtectedInformationAccessRequest
            {
                MemoryItemId = "memory-a",
                Reason = "Please confirm the amount discussed earlier."
            },
            TestData.Principal,
            "agent",
            CancellationToken.None);
        var second = await workflow.ResolveProtectedInformationAccessAsync(
            new ProtectedInformationAccessRequest { MemoryItemId = "memory-a" },
            TestData.Principal,
            "agent",
            CancellationToken.None);

        Assert.Equal(ProtectedInformationAccessStatuses.Requested, first.Status);
        Assert.Equal(first.RequestId, second.RequestId);
        Assert.Equal(ProtectedInformationAccessMessages.Requested, first.Message);
        Assert.DoesNotContain("SensitiveRecordReference", first.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("memoryItemId", first.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("request_protected_information_access", first.Message, StringComparison.Ordinal);
        var stored = await db.SensitiveAccessRequests.SingleAsync();
        Assert.Equal("Please confirm the amount discussed earlier.", stored.RequestReason);

        var operatorList = await workflow.ListRequestsAsync(
            "Pending",
            25,
            TestData.OperatorPrincipal,
            CancellationToken.None);
        Assert.Equal(first.RequestId, Assert.Single(operatorList.Requests).Id);
    }

    [Fact]
    public async Task CorrelationIsFailClosedAcrossOwnerWorkspaceAndAmbiguousMatches()
    {
        await using var db = TestData.CreateDbContext();
        AddProtectedMemory(db, "memory-a", TestData.Principal, "reference-a");
        await db.SaveChangesAsync();
        var workflow = TestData.CreateWorkflow(db, new ManualTimeProvider(TestData.ObservedAt));

        var otherOwner = await ResolveAsync(
            workflow,
            "memory-a",
            TestData.Principal with { UserId = "owner-b" });
        var otherWorkspace = await ResolveAsync(
            workflow,
            "memory-a",
            TestData.Principal with { WorkspaceId = "workspace-b" });

        db.SensitiveRecordReferences.Add(new SensitiveRecordReferenceRecord
        {
            Id = "reference-b",
            SourceEventId = "source-reference-b",
            MemoryItemId = "memory-a",
            SourceSystem = "local",
            SourceType = "turn-summary",
            ReceivedAt = TestData.ObservedAt,
            ContainsSensitiveMaterial = true,
            ReferenceLabel = "Protected information",
            RedactedSummary = "Safe summary.",
            WorkspaceId = TestData.Principal.WorkspaceId,
            OwnerUserId = TestData.Principal.UserId
        });
        await db.SaveChangesAsync();
        var ambiguous = await ResolveAsync(workflow, "memory-a", TestData.Principal);

        Assert.Equal(ProtectedInformationAccessStatuses.NotFound, otherOwner.Status);
        Assert.Equal(ProtectedInformationAccessStatuses.NotFound, otherWorkspace.Status);
        Assert.Equal(ProtectedInformationAccessStatuses.NotFound, ambiguous.Status);
        Assert.All(new[] { otherOwner, otherWorkspace, ambiguous }, result =>
        {
            Assert.Null(result.RequestId);
            Assert.Equal(ProtectedInformationAccessMessages.NotFound, result.Message);
        });
        Assert.Empty(await db.SensitiveAccessRequests.ToArrayAsync());
    }

    [Fact]
    public async Task ExpiredOrUnalignedProtectedPayloadReturnsHumanMessageWithoutRequest()
    {
        await using var db = TestData.CreateDbContext();
        AddProtectedMemory(
            db,
            "memory-expired",
            TestData.Principal,
            expiresAt: TestData.ObservedAt);
        AddProtectedMemory(
            db,
            "memory-unaligned",
            TestData.Principal,
            expiresAt: TestData.ObservedAt.AddMinutes(10),
            payloadExpiresAt: TestData.ObservedAt.AddMinutes(9));
        await db.SaveChangesAsync();
        var workflow = TestData.CreateWorkflow(db, new ManualTimeProvider(TestData.ObservedAt));

        var expired = await ResolveAsync(workflow, "memory-expired", TestData.Principal);
        var unaligned = await ResolveAsync(workflow, "memory-unaligned", TestData.Principal);

        Assert.Equal(ProtectedInformationAccessStatuses.Expired, expired.Status);
        Assert.Equal(ProtectedInformationAccessStatuses.Expired, unaligned.Status);
        Assert.Equal(ProtectedInformationAccessMessages.Expired, expired.Message);
        Assert.Null(expired.RequestId);
        Assert.Empty(await db.SensitiveAccessRequests.ToArrayAsync());
    }

    private static Task<ProtectedInformationAccessResolution> ResolveAsync(
        SensitiveAccessWorkflow workflow,
        string memoryItemId,
        LuthnRequestPrincipal principal) =>
        workflow.ResolveProtectedInformationAccessAsync(
            new ProtectedInformationAccessRequest { MemoryItemId = memoryItemId },
            principal,
            "agent",
            CancellationToken.None);

    private static void AddProtectedMemory(
        LuthnDbContext db,
        string memoryItemId,
        LuthnRequestPrincipal principal,
        string? referenceId = null,
        DateTimeOffset? expiresAt = null,
        DateTimeOffset? payloadExpiresAt = null)
    {
        db.SharedMemoryItems.Add(new SharedMemoryItemRecord
        {
            Id = memoryItemId,
            Title = "Safe topic",
            SafeSummary = "A protected detail exists for this topic.",
            Sensitivity = SensitivityLevel.Public,
            Visibility = MemoryVisibility.SharedAcrossAgents,
            RetentionKind = expiresAt is null ? MemoryRetentionKind.Durable : MemoryRetentionKind.Ephemeral,
            ExpiresAt = expiresAt,
            AllowsAgentContext = true,
            CreatedAt = TestData.ObservedAt,
            UpdatedAt = TestData.ObservedAt,
            CreatedBy = "agent",
            WorkspaceId = principal.WorkspaceId,
            OwnerUserId = principal.UserId
        });
        db.SensitiveMemoryPayloads.Add(new SensitiveMemoryPayloadRecord
        {
            MemoryItemId = memoryItemId,
            ProtectionScheme = "test-protected",
            ProtectedPayload = "opaque-test-payload",
            ExpiresAt = payloadExpiresAt ?? expiresAt,
            CreatedAt = TestData.ObservedAt,
            UpdatedAt = TestData.ObservedAt
        });
        db.SensitiveRecordReferences.Add(new SensitiveRecordReferenceRecord
        {
            Id = referenceId ?? $"reference-{memoryItemId}",
            SourceEventId = $"source-{memoryItemId}",
            MemoryItemId = memoryItemId,
            SourceSystem = "local",
            SourceType = "turn-summary",
            ReceivedAt = TestData.ObservedAt,
            ExpiresAt = expiresAt,
            ContainsSensitiveMaterial = true,
            ReferenceLabel = "Protected information",
            RedactedSummary = "A protected detail exists for this topic.",
            WorkspaceId = principal.WorkspaceId,
            OwnerUserId = principal.UserId
        });
    }
}
