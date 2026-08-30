using Luthn.Core.Classification;
using Luthn.Core.Common;
using Luthn.Core.Memory;
using Luthn.Core.Persistence;
using Luthn.Core.Policy;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Luthn.Host.Api.Tests;

public sealed class ProtectedInformationAccessResolutionTests
{
    [Fact]
    public async Task FailedProtectedReadAuditDoesNotConsumeGrantRead()
    {
        var interceptor = new FailProtectedReadAuditInterceptor();
        var options = new DbContextOptionsBuilder<LuthnDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .AddInterceptors(interceptor)
            .Options;
        await using var db = new LuthnDbContext(options);
        var protector = TestSensitiveMemoryProtection.Create();
        AddDecryptableProtectedMemory(
            db,
            protector,
            "memory-atomic-read",
            "원문 메모",
            "승인된 원문입니다.");
        await db.SaveChangesAsync();
        var workflow = CreateProtectedWorkflow(db, protector);
        var resolution = await ResolveAsync(workflow, "memory-atomic-read", TestData.Principal);
        var approved = await workflow.DecideRequestAsync(
            resolution.RequestId!,
            new SensitiveAccessDecisionRequest(),
            SensitiveAccessRequestStatus.Approved,
            TestData.OperatorPrincipal,
            "operator",
            CancellationToken.None);
        Assert.Equal(SensitiveAccessDecisionOutcome.Succeeded, approved.Outcome);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            workflow.ReadProtectedInformationResultAsync(
                resolution.AccessHandle!,
                TestData.Principal,
                "agent",
                CancellationToken.None));

        db.ChangeTracker.Clear();
        interceptor.Disable();
        var retry = await workflow.ReadProtectedInformationResultAsync(
            resolution.AccessHandle!,
            TestData.Principal,
            "agent",
            CancellationToken.None);

        Assert.True(retry.ContentAvailable);
        Assert.Equal(1, (await db.SensitiveAccessGrants.SingleAsync()).SuccessfulReadCount);
        Assert.Single(await db.AuditEvents.Where(record =>
            record.Action == "sensitive_access.protected_result_read").ToArrayAsync());
    }

    [Fact]
    public async Task ApprovedProtectedMemoryReturnsExactOriginalOnlyToBoundRequester()
    {
        await using var db = TestData.CreateDbContext();
        var protector = TestSensitiveMemoryProtection.Create();
        AddDecryptableProtectedMemory(
            db,
            protector,
            "memory-quote",
            "퍼시스 견적",
            "퍼시스 가구회사에 견적 10억을 제시했어.");
        await db.SaveChangesAsync();
        var workflow = CreateProtectedWorkflow(db, protector);

        var resolution = await ResolveAsync(workflow, "memory-quote", TestData.Principal);
        Assert.Equal(ProtectedInformationAccessStatuses.Requested, resolution.Status);
        Assert.NotNull(resolution.RequestId);
        Assert.Matches("^[0-9a-f]{64}$", resolution.AccessHandle!);

        var decision = await workflow.DecideRequestAsync(
            resolution.RequestId!,
            new SensitiveAccessDecisionRequest
            {
                Reason = "견적 금액 공개 승인",
                GrantDurationSeconds = 3600,
                MaximumSuccessfulReads = 2
            },
            SensitiveAccessRequestStatus.Approved,
            TestData.OperatorPrincipal,
            "operator",
            CancellationToken.None);
        Assert.Equal(SensitiveAccessDecisionOutcome.Succeeded, decision.Outcome);

        var otherActor = await workflow.ReadProtectedInformationResultAsync(
            resolution.AccessHandle!,
            TestData.Principal with { ActorId = "another-agent" },
            "another-agent",
            CancellationToken.None);
        Assert.Equal(SensitiveAccessStatusCodes.ProtectedResultNotFound, otherActor.Status);
        Assert.False(otherActor.ContentAvailable);

        var first = await workflow.ReadProtectedInformationResultAsync(
            resolution.AccessHandle!,
            TestData.Principal,
            "agent",
            CancellationToken.None);
        var second = await workflow.ReadProtectedInformationResultAsync(
            resolution.AccessHandle!,
            TestData.Principal,
            "agent",
            CancellationToken.None);
        var exhausted = await workflow.ReadProtectedInformationResultAsync(
            resolution.AccessHandle!,
            TestData.Principal,
            "agent",
            CancellationToken.None);

        Assert.Equal(SensitiveAccessStatusCodes.ProtectedResultReturned, first.Status);
        Assert.True(first.ContentAvailable);
        Assert.Equal("퍼시스 견적", first.Title);
        Assert.Equal("퍼시스 가구회사에 견적 10억을 제시했어.", first.Content);
        Assert.Equal(1, first.RemainingReads);
        Assert.Equal(SensitiveAccessStatusCodes.ProtectedResultReturned, second.Status);
        Assert.Equal(0, second.RemainingReads);
        Assert.Equal(SensitiveAccessStatusCodes.GrantConsumed, exhausted.Status);
        Assert.False(exhausted.ContentAvailable);
        Assert.Null(exhausted.Content);

        var grant = await db.SensitiveAccessGrants.SingleAsync();
        Assert.Equal(TestData.ObservedAt.AddMinutes(60), grant.ExpiresAt);
        Assert.Equal(2, grant.MaximumSuccessfulReads);
        Assert.Equal(2, grant.SuccessfulReadCount);

        var operatorDetail = await workflow.ReadOperatorDetailAsync(
            resolution.RequestId!,
            TestData.OperatorPrincipal,
            "operator",
            CancellationToken.None);
        Assert.NotNull(operatorDetail);
        Assert.Equal(SensitiveAccessMode.ProtectedMemory, operatorDetail.AccessMode);
        Assert.DoesNotContain("10억", operatorDetail.Reference.RedactedSummary, StringComparison.Ordinal);
        Assert.DoesNotContain(resolution.AccessHandle!, operatorDetail.Reference.RedactedSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProtectedApprovalDefaultsToSixtyMinutesAndOneReadAndRejectsMoreThanThree()
    {
        await using var db = TestData.CreateDbContext();
        var protector = TestSensitiveMemoryProtection.Create();
        AddDecryptableProtectedMemory(
            db,
            protector,
            "memory-default-policy",
            "견적",
            "승인된 견적 금액은 10억이다.");
        await db.SaveChangesAsync();
        var workflow = CreateProtectedWorkflow(db, protector);
        var resolution = await ResolveAsync(workflow, "memory-default-policy", TestData.Principal);

        var invalid = await workflow.DecideRequestAsync(
            resolution.RequestId!,
            new SensitiveAccessDecisionRequest { MaximumSuccessfulReads = 4 },
            SensitiveAccessRequestStatus.Approved,
            TestData.OperatorPrincipal,
            "operator",
            CancellationToken.None);
        Assert.Equal(SensitiveAccessDecisionOutcome.Invalid, invalid.Outcome);

        var approved = await workflow.DecideRequestAsync(
            resolution.RequestId!,
            new SensitiveAccessDecisionRequest(),
            SensitiveAccessRequestStatus.Approved,
            TestData.OperatorPrincipal,
            "operator",
            CancellationToken.None);
        Assert.Equal(SensitiveAccessDecisionOutcome.Succeeded, approved.Outcome);

        var grant = await db.SensitiveAccessGrants.SingleAsync();
        Assert.Equal(ProtectedAccessPolicyLimits.DefaultGrantDurationSeconds, grant.GrantDurationSeconds);
        Assert.Equal(TestData.ObservedAt.AddMinutes(60), grant.ExpiresAt);
        Assert.Equal(1, grant.MaximumSuccessfulReads);

        var first = await workflow.ReadProtectedInformationResultAsync(
            resolution.AccessHandle!,
            TestData.Principal,
            "agent",
            CancellationToken.None);
        var second = await workflow.ReadProtectedInformationResultAsync(
            resolution.AccessHandle!,
            TestData.Principal,
            "agent",
            CancellationToken.None);
        Assert.True(first.ContentAvailable);
        Assert.Equal(SensitiveAccessStatusCodes.GrantConsumed, second.Status);
    }

    [Fact]
    public async Task CredentialMaterialIsNeverReleasedAndDoesNotConsumeRead()
    {
        await using var db = TestData.CreateDbContext();
        var protector = TestSensitiveMemoryProtection.Create();
        AddDecryptableProtectedMemory(
            db,
            protector,
            "memory-credential",
            "배포 설정",
            "api key = sk-test-1234567890abcdef");
        await db.SaveChangesAsync();
        var workflow = CreateProtectedWorkflow(db, protector);
        var resolution = await ResolveAsync(workflow, "memory-credential", TestData.Principal);
        var approved = await workflow.DecideRequestAsync(
            resolution.RequestId!,
            new SensitiveAccessDecisionRequest(),
            SensitiveAccessRequestStatus.Approved,
            TestData.OperatorPrincipal,
            "operator",
            CancellationToken.None);
        Assert.Equal(SensitiveAccessDecisionOutcome.Succeeded, approved.Outcome);

        var result = await workflow.ReadProtectedInformationResultAsync(
            resolution.AccessHandle!,
            TestData.Principal,
            "agent",
            CancellationToken.None);

        Assert.Equal(SensitiveAccessStatusCodes.CredentialBlocked, result.Status);
        Assert.False(result.ContentAvailable);
        Assert.Null(result.Title);
        Assert.Null(result.Content);
        Assert.Equal(0, (await db.SensitiveAccessGrants.SingleAsync()).SuccessfulReadCount);
    }

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
    public async Task SafeMemoryCorrelationCreatesFreshRequesterBoundRequestEachTime()
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
        Assert.NotEqual(first.RequestId, second.RequestId);
        Assert.NotNull(first.AccessHandle);
        Assert.NotNull(second.AccessHandle);
        Assert.NotEqual(first.AccessHandle, second.AccessHandle);
        Assert.Equal(ProtectedInformationAccessMessages.Requested, first.Message);
        Assert.DoesNotContain("SensitiveRecordReference", first.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("memoryItemId", first.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("request_protected_information_access", first.Message, StringComparison.Ordinal);
        var stored = await db.SensitiveAccessRequests
            .OrderBy(record => record.CreatedAt)
            .ThenBy(record => record.Id)
            .ToArrayAsync();
        Assert.Equal(2, stored.Length);
        Assert.All(stored, record => Assert.Equal(SensitiveAccessMode.ProtectedMemory, record.AccessMode));
        Assert.Contains(stored, record =>
            record.RequestReason == "Please confirm the amount discussed earlier.");
        Assert.All(stored, record =>
        {
            Assert.StartsWith("sha256:", record.AccessHandleDigest, StringComparison.Ordinal);
            Assert.StartsWith("sha256:", record.RequesterBindingDigest, StringComparison.Ordinal);
        });

        var operatorList = await workflow.ListRequestsAsync(
            "Pending",
            25,
            TestData.OperatorPrincipal,
            CancellationToken.None);
        Assert.Equal(2, operatorList.Requests.Count);
        Assert.Contains(operatorList.Requests, request => request.Id == first.RequestId);
        Assert.Contains(operatorList.Requests, request => request.Id == second.RequestId);
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

    private static SensitiveAccessWorkflow CreateProtectedWorkflow(
        LuthnDbContext db,
        ISensitiveMemoryPayloadProtector protector) =>
        new(
            db,
            NullOperationalMetrics.Instance,
            new ManualTimeProvider(TestData.ObservedAt),
            payloadProtector: protector,
            sensitiveDataDetector: new DeterministicSensitiveDataDetector());

    private static void AddDecryptableProtectedMemory(
        LuthnDbContext db,
        ISensitiveMemoryPayloadProtector protector,
        string memoryItemId,
        string originalTitle,
        string originalSummary)
    {
        db.SharedMemoryItems.Add(new SharedMemoryItemRecord
        {
            Id = memoryItemId,
            Title = "보호된 상세 정보",
            SafeSummary = "승인 후 확인할 수 있는 보호 정보가 있다.",
            CoreTags = ["protected"],
            Sensitivity = SensitivityLevel.Public,
            Visibility = MemoryVisibility.SharedAcrossAgents,
            RetentionKind = MemoryRetentionKind.Durable,
            AllowsAgentContext = true,
            CreatedAt = TestData.ObservedAt,
            UpdatedAt = TestData.ObservedAt,
            CreatedBy = "agent",
            WorkspaceId = TestData.Principal.WorkspaceId,
            OwnerUserId = TestData.Principal.UserId
        });
        var original = new SensitiveMemoryPayload(
            SensitiveMemoryPayload.CurrentContractVersion,
            originalTitle,
            originalSummary,
            ["protected"],
            null,
            null,
            [],
            null);
        db.SensitiveMemoryPayloads.Add(new SensitiveMemoryPayloadRecord
        {
            MemoryItemId = memoryItemId,
            ContractVersion = original.ContractVersion,
            ProtectionScheme = protector.ProtectionScheme,
            ProtectedPayload = protector.Protect(memoryItemId, original),
            CreatedAt = TestData.ObservedAt,
            UpdatedAt = TestData.ObservedAt
        });
        db.SensitiveRecordReferences.Add(new SensitiveRecordReferenceRecord
        {
            Id = $"reference-{memoryItemId}",
            SourceEventId = $"source-{memoryItemId}",
            MemoryItemId = memoryItemId,
            SourceSystem = "local",
            SourceType = "turn-summary",
            ReceivedAt = TestData.ObservedAt,
            ContainsSensitiveMaterial = true,
            ReferenceLabel = "Protected information",
            RedactedSummary = "승인 후 확인할 수 있는 보호 정보가 있다.",
            WorkspaceId = TestData.Principal.WorkspaceId,
            OwnerUserId = TestData.Principal.UserId
        });
    }

    private sealed class FailProtectedReadAuditInterceptor : SaveChangesInterceptor
    {
        private bool _enabled = true;

        internal void Disable() => _enabled = false;

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (_enabled && eventData.Context is LuthnDbContext db &&
                db.AuditEvents.Local.Any(record =>
                    record.Action == "sensitive_access.protected_result_read"))
            {
                throw new InvalidOperationException("simulated protected read audit failure");
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }
}
