using Luthn.Core.Classification;
using Luthn.Core.Memory;
using Luthn.Core.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Luthn.Host.Api.Tests;

public sealed class SensitiveAccessTombstoneCleanupTests
{
    private static readonly DateTimeOffset Now =
        DateTimeOffset.Parse("2026-08-13T00:00:00Z");
    private const string WorkspaceId = "default";
    private const string OwnerUserId = "local-owner";
    private const string SourceEventId = "source-sensitive-turn";
    private const string MemoryItemId = "memory-sensitive-turn";
    private const string ReferenceId = "sensitive-turn-reference";
    private const string RequestId = "access-sensitive-turn";

    [Fact]
    public async Task CleanupAtomicallyRemovesExpiredSensitiveGraphAndPreservesContentFreeEvidence()
    {
        var options = CreateOptions();
        await using var db = new LuthnDbContext(options);
        AddExpiredSensitiveGraph(db);
        await db.SaveChangesAsync();
        var workflow = new SensitiveAccessWorkflow(
            db,
            NullOperationalMetrics.Instance,
            new ManualTimeProvider(Now.AddMinutes(-2)));
        var permit = await workflow.IssueReadPermitAsync(
            RequestId,
            Principal(),
            CancellationToken.None);

        var first = await new AutomaticTurnRetentionCleanupProcessor(db)
            .ProcessBatchAsync(Now, 10);
        var retry = await new AutomaticTurnRetentionCleanupProcessor(db)
            .ProcessBatchAsync(Now, 10);

        Assert.Equal(1, first.DeletedCount);
        Assert.Equal(0, retry.DeletedCount);
        Assert.Empty(await db.SensitiveMemoryPayloads.ToArrayAsync());
        Assert.Empty(await db.SensitiveRecordReferences.ToArrayAsync());
        Assert.Empty(await db.SensitiveAccessGrants.ToArrayAsync());
        Assert.Empty(await db.SensitiveAccessDecisions.ToArrayAsync());
        Assert.Empty(await db.SensitiveAccessRequests.ToArrayAsync());
        Assert.Empty(await db.CollectionProvenance.ToArrayAsync());
        Assert.Empty(await db.ClassificationResults.ToArrayAsync());
        Assert.Empty(await db.SharedMemoryItems.ToArrayAsync());
        Assert.Empty(await db.SourceEvents.ToArrayAsync());
        Assert.Single(await db.SensitiveAccessTombstones.ToArrayAsync());
        Assert.Equal(1, await db.AuditEvents.CountAsync(audit =>
            audit.Action == "sensitive_access.content_pruned" &&
            audit.SubjectId == RequestId &&
            audit.PayloadClass == "metadata-only" &&
            audit.RedactionState == "expired-no-output"));
        Assert.Equal(1, await db.AuditEvents.CountAsync(audit =>
            audit.Action == "turn_summary.retention.pruned" &&
            audit.SubjectId == SourceEventId));
        Assert.True(await db.AuditEvents.AnyAsync(audit => audit.Id == "audit-before-cleanup"));
        Assert.Equal(1, await db.SensitiveAccessPolicyRevisions.CountAsync());

        var staleOutput = await workflow.ReadApprovedResultAsync(
            RequestId,
            Principal(),
            "agent",
            permit,
            CancellationToken.None);
        Assert.Null(staleOutput);

        var tombstone = await workflow.ReadTombstoneAsync(
            RequestId,
            Principal(),
            CancellationToken.None);
        Assert.NotNull(tombstone);
        Assert.Equal(["Id"], typeof(SensitiveAccessTombstoneState)
            .GetProperties()
            .Select(property => property.Name)
            .ToArray());
    }

    [Fact]
    public async Task FailedSaveCommitsNoPartialStateAndRetryCompletes()
    {
        var interceptor = new FailNextSaveChangesInterceptor();
        var options = CreateOptions(interceptor);
        await using var db = new LuthnDbContext(options);
        AddExpiredSensitiveGraph(db);
        await db.SaveChangesAsync();
        interceptor.Arm();
        var processor = new AutomaticTurnRetentionCleanupProcessor(db);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            processor.ProcessBatchAsync(Now, 10));

        Assert.Equal(1, await db.SensitiveMemoryPayloads.CountAsync());
        Assert.Equal(1, await db.SensitiveRecordReferences.CountAsync());
        Assert.Equal(1, await db.SensitiveAccessRequests.CountAsync());
        Assert.Equal(1, await db.SensitiveAccessGrants.CountAsync());
        Assert.Equal(1, await db.SensitiveAccessDecisions.CountAsync());
        Assert.Equal(1, await db.SharedMemoryItems.CountAsync());
        Assert.Equal(1, await db.CollectionProvenance.CountAsync());
        Assert.Equal(1, await db.ClassificationResults.CountAsync());
        Assert.Equal(1, await db.SourceEvents.CountAsync());
        Assert.Empty(await db.SensitiveAccessTombstones.ToArrayAsync());
        Assert.DoesNotContain(await db.AuditEvents.ToArrayAsync(), audit =>
            audit.Action is "sensitive_access.content_pruned" or "turn_summary.retention.pruned");

        var retry = await processor.ProcessBatchAsync(Now, 10);
        Assert.Equal(1, retry.DeletedCount);
        Assert.Single(await db.SensitiveAccessTombstones.ToArrayAsync());
    }

    [Fact]
    public async Task ConcurrentApprovalCleanupAndRetryEndInOneTombstoneWithoutOutput()
    {
        var options = CreateOptions();
        await using (var seed = new LuthnDbContext(options))
        {
            AddExpiredSensitiveGraph(seed, pending: true);
            await seed.SaveChangesAsync();
        }

        await using var cleanupDb = new LuthnDbContext(options);
        await using var decisionDb = new LuthnDbContext(options);
        var cleanup = new AutomaticTurnRetentionCleanupProcessor(cleanupDb)
            .ProcessBatchAsync(Now, 10);
        var decision = new SensitiveAccessWorkflow(
            decisionDb,
            NullOperationalMetrics.Instance,
            new ManualTimeProvider(Now.AddMinutes(-2)))
            .DecideRequestAsync(
                RequestId,
                new SensitiveAccessDecisionRequest { Reason = "operator text removed by cleanup" },
                SensitiveAccessRequestStatus.Approved,
                OperatorPrincipal(),
                "operator",
                CancellationToken.None);

        await Task.WhenAll(cleanup, decision);

        var decisionResult = await decision;
        Assert.True(decisionResult.Outcome is
            SensitiveAccessDecisionOutcome.Succeeded or
            SensitiveAccessDecisionOutcome.AlreadyDecided);
        await using var verify = new LuthnDbContext(options);
        Assert.Single(await verify.SensitiveAccessTombstones.ToArrayAsync());
        Assert.Empty(await verify.SensitiveAccessRequests.ToArrayAsync());
        Assert.Empty(await verify.SensitiveAccessDecisions.ToArrayAsync());
        Assert.Empty(await verify.SensitiveAccessGrants.ToArrayAsync());
        Assert.Equal(1, await verify.AuditEvents.CountAsync(audit =>
            audit.Action == "sensitive_access.content_pruned"));
        Assert.Equal(0, (await new AutomaticTurnRetentionCleanupProcessor(verify)
            .ProcessBatchAsync(Now, 10)).DeletedCount);
    }

    [Fact]
    public async Task AgentAndOperatorReadsReturnOnlyExpiredNoOutputTombstone()
    {
        var options = CreateOptions();
        await using var db = new LuthnDbContext(options);
        AddExpiredSensitiveGraph(db);
        await db.SaveChangesAsync();
        await new AutomaticTurnRetentionCleanupProcessor(db).ProcessBatchAsync(Now, 10);
        var context = new DefaultHttpContext();

        var status = await SensitiveAccessEndpoints.ReadRequest(
            RequestId, db, context, CancellationToken.None);
        var detail = await SensitiveAccessEndpoints.ReadOperatorDetail(
            RequestId, db, context, CancellationToken.None);
        var result = await SensitiveAccessEndpoints.ReadRequestResult(
            RequestId, db, context, CancellationToken.None);
        var list = await SensitiveAccessEndpoints.ListRequests(
            "Expired", 25, db, context, CancellationToken.None);

        AssertContentFree(Assert.IsType<Ok<SensitiveAccessTombstoneResponse>>(status.Result).Value!);
        AssertContentFree(Assert.IsType<Ok<SensitiveAccessTombstoneResponse>>(detail.Result).Value!);
        AssertContentFree(Assert.IsType<Ok<SensitiveAccessTombstoneResponse>>(result.Result).Value!);
        var listResponse = Assert.IsType<Ok<SensitiveAccessRequestsResponse>>(list.Result).Value!;
        Assert.Empty(listResponse.Requests);
        AssertContentFree(Assert.Single(listResponse.Tombstones));
        Assert.Equal(1, await db.AuditEvents.CountAsync(audit =>
            audit.Action == "sensitive_access.operator_detail_read" &&
            audit.SubjectId == RequestId));
        Assert.Equal(1, await db.AuditEvents.CountAsync(audit =>
            audit.Action == "sensitive_access.result_read" &&
            audit.SubjectId == RequestId));
        var workflow = new SensitiveAccessWorkflow(
            db,
            NullOperationalMetrics.Instance,
            new ManualTimeProvider(Now));
        Assert.Null(await workflow.ReadTombstoneAsync(
            RequestId,
            Principal() with { UserId = "other-owner" },
            CancellationToken.None));
        Assert.Null(await workflow.ReadTombstoneAsync(
            RequestId,
            OperatorPrincipal() with { WorkspaceId = "other-workspace" },
            CancellationToken.None));
    }

    private static void AssertContentFree(SensitiveAccessTombstoneResponse tombstone)
    {
        Assert.Equal("Expired", tombstone.Status);
        Assert.Equal("expired-no-output", tombstone.OutputPolicy);
        Assert.Equal(
            ["Id", "Status", "OutputPolicy"],
            typeof(SensitiveAccessTombstoneResponse)
                .GetProperties()
                .Select(property => property.Name)
                .ToArray());
    }

    private static DbContextOptions<LuthnDbContext> CreateOptions(
        SaveChangesInterceptor? interceptor = null)
    {
        var builder = new DbContextOptionsBuilder<LuthnDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"));
        if (interceptor is not null)
        {
            builder.AddInterceptors(interceptor);
        }
        return builder.Options;
    }

    private static LuthnRequestPrincipal Principal() =>
        new(OwnerUserId, WorkspaceId, LuthnActorKind.Agent, "agent", IsOperator: false);

    private static LuthnRequestPrincipal OperatorPrincipal() =>
        new("operator", WorkspaceId, LuthnActorKind.User, "operator", IsOperator: true);

    internal static void AddExpiredSensitiveGraph(LuthnDbContext db, bool pending = false)
    {
        var expiresAt = Now.AddMinutes(-1);
        db.SourceEvents.Add(new SourceEventRecord
        {
            Id = SourceEventId,
            SourceSystem = "codex",
            SourceType = "turn-summary",
            ReceivedAt = Now.AddMinutes(-10),
            ContentDigest = "sha256:test",
            ContainsSensitiveMaterial = true,
            WorkspaceId = WorkspaceId,
            OwnerUserId = OwnerUserId
        });
        db.ClassificationResults.Add(new ClassificationResultRecord
        {
            Id = "classification-sensitive-turn",
            SourceEventId = SourceEventId,
            Sensitivity = SensitivityLevel.Restricted,
            Confidence = 1,
            ContainsSensitiveMaterial = true,
            StorageDecision = StorageDecisionKind.SensitiveDbOnly
        });
        db.SharedMemoryItems.Add(new SharedMemoryItemRecord
        {
            Id = MemoryItemId,
            Title = "Sensitive turn",
            SafeSummary = "[protected]",
            Sensitivity = SensitivityLevel.Restricted,
            CoreTags = ["sensitive"],
            Visibility = MemoryVisibility.PrivateToOwner,
            RetentionKind = MemoryRetentionKind.Ephemeral,
            ExpiresAt = expiresAt,
            AllowsAgentContext = false,
            CreatedAt = Now.AddMinutes(-10),
            UpdatedAt = Now.AddMinutes(-10),
            CreatedBy = "agent",
            WorkspaceId = WorkspaceId,
            OwnerUserId = OwnerUserId
        });
        db.SensitiveMemoryPayloads.Add(new SensitiveMemoryPayloadRecord
        {
            MemoryItemId = MemoryItemId,
            ProtectionScheme = "test",
            ProtectedPayload = "ciphertext-secret",
            ExpiresAt = expiresAt,
            CreatedAt = Now.AddMinutes(-10),
            UpdatedAt = Now.AddMinutes(-10)
        });
        db.CollectionProvenance.Add(new CollectionProvenanceRecord
        {
            Id = "provenance-sensitive-turn",
            SourceEventId = SourceEventId,
            MemoryItemId = MemoryItemId,
            AuthenticatedActor = "agent",
            ActorTrust = CollectionProvenance.ServiceTokenActorTrust,
            ClaimsTrust = CollectionProvenance.NoClaimsTrust,
            WorkspaceId = WorkspaceId,
            AuthenticatedUserId = OwnerUserId,
            ReceivedAt = Now.AddMinutes(-10)
        });
        db.SensitiveRecordReferences.Add(new SensitiveRecordReferenceRecord
        {
            Id = ReferenceId,
            SourceEventId = SourceEventId,
            MemoryItemId = MemoryItemId,
            SourceSystem = "codex",
            SourceType = "turn-summary",
            ReceivedAt = Now.AddMinutes(-10),
            ExpiresAt = expiresAt,
            ContainsSensitiveMaterial = true,
            ReferenceLabel = "sensitive-turn-summary:test",
            RedactedSummary = "Public-safe summary removed by cleanup.",
            WorkspaceId = WorkspaceId,
            OwnerUserId = OwnerUserId
        });
        db.SensitiveAccessPolicyRevisions.Add(new SensitiveAccessPolicyRevisionRecord
        {
            WorkspaceId = WorkspaceId,
            Revision = 1,
            RequestTimeoutSeconds = 600,
            GrantDurationSeconds = 600,
            MaximumSuccessfulReads = 1,
            CreatedAt = Now.AddMinutes(-10),
            CreatedBy = "operator"
        });
        db.SensitiveAccessRequests.Add(new SensitiveAccessRequestRecord
        {
            Id = RequestId,
            SensitiveRecordReferenceId = ReferenceId,
            RequestedBy = "agent",
            SessionId = "session-secret",
            RequestReason = "request reason removed by cleanup",
            RedactedSummary = pending ? "" : "Public-safe approved output removed by cleanup.",
            Status = pending ? SensitiveAccessRequestStatus.Pending : SensitiveAccessRequestStatus.Approved,
            CreatedAt = Now.AddMinutes(-5),
            ExpiresAt = expiresAt,
            UpdatedAt = Now.AddMinutes(-2),
            DecidedBy = pending ? null : "operator",
            DecidedAt = pending ? null : Now.AddMinutes(-2),
            WorkspaceId = WorkspaceId,
            OwnerUserId = OwnerUserId,
            PolicyRevision = 1,
            RequestTimeoutSeconds = 600
        });
        if (!pending)
        {
            db.SensitiveAccessDecisions.Add(new SensitiveAccessDecisionRecord
            {
                Id = "decision-sensitive-turn",
                SensitiveAccessRequestId = RequestId,
                Decision = SensitiveAccessDecisionKind.Approved,
                DecidedBy = "operator",
                DecisionReason = "decision text removed by cleanup",
                DecidedAt = Now.AddMinutes(-2),
                PayloadClass = "metadata-only",
                RedactionState = "approved-redacted-output-available"
            });
            db.SensitiveAccessGrants.Add(new SensitiveAccessGrantRecord
            {
                SensitiveAccessRequestId = RequestId,
                WorkspaceId = WorkspaceId,
                OwnerUserId = OwnerUserId,
                PolicyRevision = 1,
                GrantDurationSeconds = 600,
                StartsAt = Now.AddMinutes(-2),
                ExpiresAt = expiresAt,
                MaximumSuccessfulReads = 1
            });
        }
        db.AuditEvents.Add(new AuditEventRecord
        {
            Id = "audit-before-cleanup",
            OccurredAt = Now.AddMinutes(-2),
            Actor = "operator",
            Action = "sensitive_access.approved",
            SubjectId = RequestId,
            PayloadClass = "metadata-only",
            RedactionState = "approved-redacted-output-available"
        });
    }

    private sealed class FailNextSaveChangesInterceptor : SaveChangesInterceptor
    {
        private bool _armed;

        internal void Arm() => _armed = true;

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (_armed)
            {
                _armed = false;
                throw new InvalidOperationException("simulated atomic cleanup failure");
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }
}
