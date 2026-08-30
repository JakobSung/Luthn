using System.Text.Json;
using Luthn.Core.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Luthn.Host.Api.Tests;

public sealed class SensitiveAccessWorkflowBypassTests
{
    private const string RequestId = "access-approved-result";
    private const string SafeSummary = "Public-safe approved summary.";
    private static readonly DateTimeOffset InitialTime =
        new(2026, 8, 11, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task MissingPermitFailsClosedAndWritesBoundedMetadataOnlyAudit()
    {
        await using var harness = CreateHarness();

        var output = await harness.Workflow.ReadApprovedResultAsync(
            RequestId,
            harness.Principal,
            "requester",
            permit: null,
            CancellationToken.None);

        Assert.Null(output);
        await AssertRejectedWithoutRequestMutationAsync(harness, "missing-permit-no-output");
    }

    [Fact]
    public async Task InvalidPermitFailsClosedAndWritesBoundedMetadataOnlyAudit()
    {
        await using var harness = CreateHarness();
        var forged = new SensitiveAccessReadPermit(
            "forged",
            RequestId,
            harness.Principal.WorkspaceId,
            harness.Principal.UserId,
            InitialTime.AddMinutes(1));

        var output = await harness.Workflow.ReadApprovedResultAsync(
            RequestId,
            harness.Principal,
            "requester",
            forged,
            CancellationToken.None);

        Assert.Null(output);
        await AssertRejectedWithoutRequestMutationAsync(harness, "invalid-permit-no-output");
    }

    [Fact]
    public async Task ReusedPermitFailsClosedAfterOneSuccessfulRead()
    {
        await using var harness = CreateHarness();
        var permit = await harness.Workflow.IssueReadPermitAsync(
            RequestId,
            harness.Principal,
            CancellationToken.None);
        Assert.NotNull(permit);

        var first = await harness.Workflow.ReadApprovedResultAsync(
            RequestId,
            harness.Principal,
            "requester",
            permit,
            CancellationToken.None);
        var reused = await harness.Workflow.ReadApprovedResultAsync(
            RequestId,
            harness.Principal,
            "requester",
            permit,
            CancellationToken.None);

        Assert.Equal(SafeSummary, first);
        Assert.Null(reused);
        await AssertRejectedWithoutRequestMutationAsync(harness, "reused-permit-no-output");
    }

    [Fact]
    public async Task ExpiredPermitFailsClosedAndWritesBoundedMetadataOnlyAudit()
    {
        await using var harness = CreateHarness();
        var permit = await harness.Workflow.IssueReadPermitAsync(
            RequestId,
            harness.Principal,
            CancellationToken.None);
        Assert.NotNull(permit);
        harness.Time.Advance(TimeSpan.FromSeconds(6));

        var output = await harness.Workflow.ReadApprovedResultAsync(
            RequestId,
            harness.Principal,
            "requester",
            permit,
            CancellationToken.None);

        Assert.Null(output);
        await AssertRejectedWithoutRequestMutationAsync(harness, "expired-permit-no-output");
    }

    [Fact]
    public async Task ScopeMismatchedPermitFailsClosedAndWritesBoundedMetadataOnlyAudit()
    {
        await using var harness = CreateHarness();
        var permit = await harness.Workflow.IssueReadPermitAsync(
            RequestId,
            harness.Principal,
            CancellationToken.None);
        Assert.NotNull(permit);
        var otherOwner = harness.Principal with { UserId = "other-owner", ActorId = "other-agent" };

        var output = await harness.Workflow.ReadApprovedResultAsync(
            RequestId,
            otherOwner,
            "other-agent",
            permit,
            CancellationToken.None);

        Assert.Null(output);
        await AssertRejectedWithoutRequestMutationAsync(
            harness,
            "scope-mismatch-no-output",
            expectedActorUserId: otherOwner.UserId);
    }

    private static WorkflowHarness CreateHarness()
    {
        var options = new DbContextOptionsBuilder<LuthnDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        var db = new LuthnDbContext(options);
        db.Database.EnsureCreated();
        db.SensitiveAccessPolicyRevisions.Add(new SensitiveAccessPolicyRevisionRecord
        {
            WorkspaceId = "workspace-a",
            Revision = 1,
            RequestTimeoutSeconds = SensitiveAccessPolicyLimits.DefaultRequestTimeoutSeconds,
            GrantDurationSeconds = SensitiveAccessPolicyLimits.DefaultGrantDurationSeconds,
            MaximumSuccessfulReads = SensitiveAccessPolicyLimits.DefaultMaximumSuccessfulReads,
            CreatedAt = InitialTime.AddMinutes(-3),
            CreatedBy = "test"
        });
        db.SensitiveRecordReferences.Add(new SensitiveRecordReferenceRecord
        {
            Id = "sensitive-ref-approved-result",
            SourceEventId = "source-approved-result",
            SourceSystem = "local",
            SourceType = "turn-summary",
            ReceivedAt = InitialTime.AddMinutes(-3),
            ContainsSensitiveMaterial = true,
            ReferenceLabel = "sensitive-turn-summary:source-approved-result",
            WorkspaceId = "workspace-a",
            OwnerUserId = "owner-a"
        });
        db.SensitiveAccessRequests.Add(new SensitiveAccessRequestRecord
        {
            Id = RequestId,
            SensitiveRecordReferenceId = "sensitive-ref-approved-result",
            RequestedBy = "requester",
            SessionId = "session-approved-result",
            RequestReason = "Need the approved public-safe summary.",
            RedactedSummary = SafeSummary,
            Status = SensitiveAccessRequestStatus.Approved,
            CreatedAt = InitialTime.AddMinutes(-2),
            ExpiresAt = InitialTime.AddMinutes(8),
            UpdatedAt = InitialTime.AddMinutes(-1),
            DecidedBy = "operator",
            DecidedAt = InitialTime.AddMinutes(-1),
            WorkspaceId = "workspace-a",
            OwnerUserId = "owner-a"
        });
        db.SensitiveAccessGrants.Add(new SensitiveAccessGrantRecord
        {
            SensitiveAccessRequestId = RequestId,
            WorkspaceId = "workspace-a",
            OwnerUserId = "owner-a",
            PolicyRevision = 1,
            GrantDurationSeconds = SensitiveAccessPolicyLimits.DefaultGrantDurationSeconds,
            StartsAt = InitialTime.AddMinutes(-1),
            ExpiresAt = InitialTime.AddMinutes(9),
            MaximumSuccessfulReads = SensitiveAccessPolicyLimits.DefaultMaximumSuccessfulReads,
            SuccessfulReadCount = 0
        });
        db.SaveChanges();

        var time = new ManualTimeProvider(InitialTime);
        var workflow = new SensitiveAccessWorkflow(
            db,
            NullOperationalMetrics.Instance,
            time);
        var principal = new LuthnRequestPrincipal(
            "owner-a",
            "workspace-a",
            LuthnActorKind.Agent,
            "requester",
            IsOperator: false);
        return new WorkflowHarness(db, workflow, time, principal);
    }

    private static async Task AssertRejectedWithoutRequestMutationAsync(
        WorkflowHarness harness,
        string expectedReason,
        string? expectedActorUserId = null)
    {
        var request = await harness.Db.SensitiveAccessRequests
            .AsNoTracking()
            .SingleAsync(record => record.Id == RequestId);
        var audit = await harness.Db.AuditEvents
            .AsNoTracking()
            .SingleAsync(record => record.Action == "sensitive_access.read_bypass_rejected");

        Assert.Equal(SensitiveAccessRequestStatus.Approved, request.Status);
        Assert.Equal(SafeSummary, request.RedactedSummary);
        Assert.Equal(InitialTime.AddMinutes(-1), request.UpdatedAt);
        Assert.Equal("operator", request.DecidedBy);
        Assert.Equal(InitialTime.AddMinutes(-1), request.DecidedAt);
        Assert.Empty(harness.Db.SensitiveAccessDecisions);

        Assert.Equal(RequestId, audit.SubjectId);
        Assert.Equal("sensitive_access_request", audit.SubjectType);
        Assert.Equal("metadata-only", audit.PayloadClass);
        Assert.Equal(expectedReason, audit.RedactionState);
        Assert.Equal("rejected", audit.Outcome);
        Assert.Equal(expectedActorUserId ?? harness.Principal.UserId, audit.ActorUserId);
        Assert.InRange(audit.SubjectId.Length, 1, ApiValidation.PublicRecordIdMaxLength);
        Assert.InRange(audit.RedactionState.Length, 1, 64);

        var auditJson = JsonSerializer.Serialize(audit);
        Assert.DoesNotContain(SafeSummary, auditJson, StringComparison.Ordinal);
        Assert.DoesNotContain("Need the approved public-safe summary.", auditJson, StringComparison.Ordinal);
    }

    private sealed record WorkflowHarness(
        LuthnDbContext Db,
        SensitiveAccessWorkflow Workflow,
        ManualTimeProvider Time,
        LuthnRequestPrincipal Principal) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    private sealed class ManualTimeProvider(DateTimeOffset current) : TimeProvider
    {
        private DateTimeOffset _current = current;

        public override DateTimeOffset GetUtcNow() => _current;

        public void Advance(TimeSpan amount) => _current = _current.Add(amount);
    }
}
