using System.Collections.Concurrent;
using Luthn.Core.Classification;
using Luthn.Core.Common;
using Luthn.Core.Persistence;
using Luthn.Core.Policy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Luthn.Host.Api;

internal interface ISensitiveAccessWorkflow
{
    Task<SensitiveAccessPolicyState> GetPolicyAsync(
        LuthnRequestPrincipal principal,
        CancellationToken cancellationToken);

    Task<SensitiveAccessPolicyRevisionResult> CreatePolicyRevisionAsync(
        SensitiveAccessPolicyUpdate update,
        LuthnRequestPrincipal principal,
        string actor,
        CancellationToken cancellationToken);

    Task<SensitiveAccessListResult> ListRequestsAsync(
        string? status,
        int take,
        LuthnRequestPrincipal principal,
        CancellationToken cancellationToken);

    Task<SensitiveAccessRequestState?> CreateRequestAsync(
        SensitiveAccessRequestCreateRequest request,
        LuthnRequestPrincipal principal,
        string actor,
        CancellationToken cancellationToken);

    Task<SensitiveAccessRequestState?> ReadRequestAsync(
        string id,
        LuthnRequestPrincipal principal,
        CancellationToken cancellationToken);

    Task<SensitiveAccessOperatorDetailState?> ReadOperatorDetailAsync(
        string id,
        LuthnRequestPrincipal principal,
        string actor,
        CancellationToken cancellationToken);

    Task<SensitiveAccessResultState?> ReadRequestResultAsync(
        string id,
        LuthnRequestPrincipal principal,
        string actor,
        CancellationToken cancellationToken);

    Task<SensitiveAccessDecisionResult> DecideRequestAsync(
        string id,
        SensitiveAccessDecisionRequest request,
        SensitiveAccessRequestStatus status,
        LuthnRequestPrincipal principal,
        string actor,
        CancellationToken cancellationToken);
}

internal sealed class SensitiveAccessWorkflow(
    LuthnDbContext db,
    IOperationalMetrics metrics,
    TimeProvider timeProvider,
    IContentClassifier? classifier = null,
    IPolicyEngine? policyEngine = null) : ISensitiveAccessWorkflow
{
    private const int MaxStoredRedactedSummaryLength = 4000;
    private static readonly TimeSpan ReadPermitLifetime = TimeSpan.FromSeconds(5);
    private static readonly SemaphoreSlim PolicyRevisionLock = new(1, 1);
    private static readonly SemaphoreSlim NonRelationalDecisionLock = new(1, 1);
    private static readonly SemaphoreSlim NonRelationalGrantReadLock = new(1, 1);
    private readonly ConcurrentDictionary<string, SensitiveAccessReadPermitState> _readPermits =
        new(StringComparer.Ordinal);

    public async Task<SensitiveAccessPolicyState> GetPolicyAsync(
        LuthnRequestPrincipal principal,
        CancellationToken cancellationToken) =>
        ToState(await GetOrCreateActivePolicyAsync(
            principal.WorkspaceId,
            "workflow-default",
            cancellationToken));

    public async Task<SensitiveAccessPolicyRevisionResult> CreatePolicyRevisionAsync(
        SensitiveAccessPolicyUpdate update,
        LuthnRequestPrincipal principal,
        string actor,
        CancellationToken cancellationToken)
    {
        var validationError = ValidatePolicyUpdate(update);
        if (validationError is not null)
        {
            return SensitiveAccessPolicyRevisionResult.Invalid(validationError);
        }

        await PolicyRevisionLock.WaitAsync(cancellationToken);
        try
        {
            var latestRevision = await db.SensitiveAccessPolicyRevisions
                .Where(record => record.WorkspaceId == principal.WorkspaceId)
                .Select(record => (int?)record.Revision)
                .MaxAsync(cancellationToken) ?? 0;
            var policy = new SensitiveAccessPolicyRevisionRecord
            {
                WorkspaceId = principal.WorkspaceId,
                Revision = latestRevision + 1,
                RequestTimeoutSeconds = update.RequestTimeoutSeconds,
                GrantDurationSeconds = update.GrantDurationSeconds,
                MaximumSuccessfulReads = update.MaximumSuccessfulReads,
                CreatedAt = timeProvider.GetUtcNow(),
                CreatedBy = actor
            };
            db.SensitiveAccessPolicyRevisions.Add(policy);
            db.AuditEvents.Add(AuditEventFactory.ForWorkspace(
                principal,
                actor,
                "sensitive_access.policy_updated",
                $"policy-revision-{policy.Revision}",
                "metadata-only",
                "policy-metadata-only",
                policy.CreatedAt,
                subjectType: "sensitive_access_policy",
                outcome: "updated"));
            await db.SaveChangesAsync(cancellationToken);
            return SensitiveAccessPolicyRevisionResult.Succeeded(ToState(policy));
        }
        finally
        {
            PolicyRevisionLock.Release();
        }
    }

    public async Task<SensitiveAccessListResult> ListRequestsAsync(
        string? status,
        int take,
        LuthnRequestPrincipal principal,
        CancellationToken cancellationToken)
    {
        await ExpirePendingRequestsAsync(requestId: null, principal, cancellationToken);
        var query = db.SensitiveAccessRequests
            .AsNoTracking()
            .Where(record =>
                record.WorkspaceId == principal.WorkspaceId &&
                (principal.IsOperator || record.OwnerUserId == principal.UserId));
        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!Enum.TryParse<SensitiveAccessRequestStatus>(
                status.Trim(),
                ignoreCase: true,
                out var parsedStatus))
            {
                return new SensitiveAccessListResult(
                    [],
                    "status must be Pending, Approved, Denied, or Expired.");
            }

            query = query.Where(record => record.Status == parsedStatus);
        }

        var requests = await query
            .OrderByDescending(record => record.UpdatedAt)
            .ThenByDescending(record => record.CreatedAt)
            .Take(take)
            .Select(record => new SensitiveAccessRequestState(
                record.Id,
                record.SensitiveRecordReferenceId,
                record.Status,
                record.RequestedBy,
                record.SessionId,
                record.CreatedAt,
                record.ExpiresAt,
                record.DecidedBy,
                record.DecidedAt,
                record.RedactedSummary != ""))
            .ToArrayAsync(cancellationToken);

        return new SensitiveAccessListResult(requests, null);
    }

    public async Task<SensitiveAccessRequestState?> CreateRequestAsync(
        SensitiveAccessRequestCreateRequest request,
        LuthnRequestPrincipal principal,
        string actor,
        CancellationToken cancellationToken)
    {
        var sensitiveReferenceId = request.SensitiveReferenceId.Trim();
        var reference = await db.SensitiveRecordReferences
            .AsNoTracking()
            .Where(record => record.Id == sensitiveReferenceId &&
                record.WorkspaceId == principal.WorkspaceId &&
                record.OwnerUserId == principal.UserId)
            .Select(record => new SensitiveAccessReferenceIdentity(
                record.Id,
                record.WorkspaceId,
                record.OwnerUserId))
            .SingleOrDefaultAsync(cancellationToken);
        if (reference is null)
        {
            return null;
        }

        var observedAt = timeProvider.GetUtcNow();
        var policy = await GetOrCreateActivePolicyAsync(
            reference.WorkspaceId,
            "workflow-default",
            cancellationToken);
        var sessionId = string.IsNullOrWhiteSpace(request.SessionId)
            ? $"legacy-{Guid.NewGuid():N}"
            : request.SessionId.Trim();
        var accessRequest = new SensitiveAccessRequestRecord
        {
            Id = $"access-{Guid.NewGuid():N}",
            SensitiveRecordReferenceId = reference.Id,
            RequestedBy = actor,
            SessionId = sessionId,
            RequestReason = request.Reason.Trim(),
            Status = SensitiveAccessRequestStatus.Pending,
            CreatedAt = observedAt,
            ExpiresAt = observedAt.AddSeconds(policy.RequestTimeoutSeconds),
            UpdatedAt = observedAt,
            WorkspaceId = reference.WorkspaceId,
            OwnerUserId = reference.OwnerUserId,
            PolicyRevision = policy.Revision,
            RequestTimeoutSeconds = policy.RequestTimeoutSeconds
        };

        db.SensitiveAccessRequests.Add(accessRequest);
        db.AuditEvents.Add(AuditEventFactory.ForWorkspace(
            principal,
            actor,
            "sensitive_access.requested",
            accessRequest.Id,
            "metadata-only",
            "sensitive-boundary-only",
            observedAt,
            subjectType: "sensitive_access_request",
            outcome: "requested"));

        await db.SaveChangesAsync(cancellationToken);
        metrics.RecordSensitiveAccessRequest();

        return ToState(accessRequest, redactedOutputAvailable: false);
    }

    public async Task<SensitiveAccessRequestState?> ReadRequestAsync(
        string id,
        LuthnRequestPrincipal principal,
        CancellationToken cancellationToken)
    {
        await ExpirePendingRequestsAsync(id, principal, cancellationToken);
        return await db.SensitiveAccessRequests
            .AsNoTracking()
            .Where(record => record.Id == id &&
                record.WorkspaceId == principal.WorkspaceId &&
                record.OwnerUserId == principal.UserId)
            .Select(record => new SensitiveAccessRequestState(
                record.Id,
                record.SensitiveRecordReferenceId,
                record.Status,
                record.RequestedBy,
                record.SessionId,
                record.CreatedAt,
                record.ExpiresAt,
                record.DecidedBy,
                record.DecidedAt,
                record.RedactedSummary != ""))
            .SingleOrDefaultAsync(cancellationToken);
    }

    public async Task<SensitiveAccessOperatorDetailState?> ReadOperatorDetailAsync(
        string id,
        LuthnRequestPrincipal principal,
        string actor,
        CancellationToken cancellationToken)
    {
        await ExpirePendingRequestsAsync(id, principal, cancellationToken);
        var request = await db.SensitiveAccessRequests
            .AsNoTracking()
            .Where(record => record.Id == id &&
                record.WorkspaceId == principal.WorkspaceId &&
                (principal.IsOperator || record.OwnerUserId == principal.UserId))
            .Select(record => new SensitiveAccessOperatorRequestState(
                record.Id,
                record.SensitiveRecordReferenceId,
                record.Status,
                record.RequestedBy,
                record.SessionId,
                record.RequestReason,
                record.CreatedAt,
                record.ExpiresAt,
                record.DecidedBy,
                record.DecidedAt,
                record.WorkspaceId,
                record.OwnerUserId,
                record.RedactedSummary != ""))
            .SingleOrDefaultAsync(cancellationToken);
        if (request is null)
        {
            return null;
        }

        var reference = await db.SensitiveRecordReferences
            .AsNoTracking()
            .Where(record => record.Id == request.SensitiveReferenceId &&
                record.WorkspaceId == request.WorkspaceId &&
                record.OwnerUserId == request.OwnerUserId)
            .Select(record => new SensitiveAccessOperatorReferenceState(
                record.SourceSystem,
                record.SourceType,
                record.ReferenceLabel,
                record.RedactedSummary,
                record.ReceivedAt))
            .SingleOrDefaultAsync(cancellationToken);
        if (reference is null)
        {
            return null;
        }

        var decision = await db.SensitiveAccessDecisions
            .AsNoTracking()
            .Where(record => record.SensitiveAccessRequestId == request.Id)
            .OrderByDescending(record => record.DecidedAt)
            .ThenByDescending(record => record.Id)
            .Select(record => new SensitiveAccessOperatorDecisionState(
                record.Decision,
                record.DecidedBy,
                record.DecidedAt,
                record.DecisionReason))
            .FirstOrDefaultAsync(cancellationToken);

        db.AuditEvents.Add(AuditEventFactory.ForWorkspace(
            principal,
            actor,
            "sensitive_access.operator_detail_read",
            request.Id,
            "metadata-only",
            "operator-detail-read-no-content",
            timeProvider.GetUtcNow(),
            subjectType: "sensitive_access_request",
            outcome: "read"));
        await db.SaveChangesAsync(cancellationToken);

        return new SensitiveAccessOperatorDetailState(
            request.Id,
            request.SensitiveReferenceId,
            request.Status,
            request.RequestedBy,
            request.SessionId,
            request.RequestReason,
            request.CreatedAt,
            request.ExpiresAt,
            decision?.Decision,
            decision?.DecidedBy ?? request.DecidedBy,
            decision?.DecidedAt ?? request.DecidedAt,
            decision?.DecisionReason,
            request.Status == SensitiveAccessRequestStatus.Approved && request.RedactedOutputAvailable,
            reference);
    }

    public async Task<SensitiveAccessResultState?> ReadRequestResultAsync(
        string id,
        LuthnRequestPrincipal principal,
        string actor,
        CancellationToken cancellationToken)
    {
        await ExpirePendingRequestsAsync(id, principal, cancellationToken);
        var request = await ReadResultMetadataAsync(id, principal, cancellationToken);
        if (request is null)
        {
            return null;
        }

        string? redactedOutput = null;
        if (request.Status == SensitiveAccessRequestStatus.Approved && request.RedactedOutputAvailable)
        {
            var permit = await IssueReadPermitAsync(id, principal, cancellationToken);
            if (permit is not null)
            {
                redactedOutput = await ReadApprovedResultAsync(
                    id,
                    principal,
                    actor,
                    permit,
                    cancellationToken);
            }
        }

        var result = new SensitiveAccessResultState(
            request.Id,
            request.SensitiveReferenceId,
            request.Status,
            redactedOutput);
        db.AuditEvents.Add(AuditEventFactory.ForWorkspace(
            principal,
            actor,
            "sensitive_access.result_read",
            request.Id,
            result.RedactedOutputAvailable ? "redacted-output" : "metadata-only",
            SensitiveAccessEndpointMapping.ToOutputPolicy(request.Status, result.RedactedOutputAvailable),
            timeProvider.GetUtcNow(),
            subjectType: "sensitive_access_request",
            outcome: "read"));
        await db.SaveChangesAsync(cancellationToken);

        return result;
    }

    public async Task<SensitiveAccessDecisionResult> DecideRequestAsync(
        string id,
        SensitiveAccessDecisionRequest request,
        SensitiveAccessRequestStatus status,
        LuthnRequestPrincipal principal,
        string actor,
        CancellationToken cancellationToken)
    {
        await ExpirePendingRequestsAsync(id, principal, cancellationToken);
        var accessRequest = await db.SensitiveAccessRequests
            .SingleOrDefaultAsync(
                record => record.Id == id &&
                    record.WorkspaceId == principal.WorkspaceId &&
                    (principal.IsOperator || record.OwnerUserId == principal.UserId),
                cancellationToken);
        if (accessRequest is null)
        {
            return SensitiveAccessDecisionResult.NotFound();
        }

        if (accessRequest.Status != SensitiveAccessRequestStatus.Pending)
        {
            return SensitiveAccessDecisionResult.AlreadyDecided();
        }

        if (request.Reason is not null &&
            request.Reason.Trim().Length > ApiValidation.ReasonMaxLength)
        {
            return SensitiveAccessDecisionResult.Invalid(
                $"reason must be {ApiValidation.ReasonMaxLength} characters or fewer.");
        }

        var redactedSummary = await ValidateDecisionRedactedSummaryAsync(
            accessRequest,
            request,
            status,
            cancellationToken);
        if (redactedSummary.ErrorDetail is not null)
        {
            db.AuditEvents.Add(AuditEventFactory.ForWorkspace(
                principal,
                actor,
                "sensitive_access.redacted_summary_rejected",
                accessRequest.Id,
                "metadata-only",
                "rejected-no-output",
                timeProvider.GetUtcNow(),
                subjectType: "sensitive_access_request",
                outcome: "rejected"));
            await db.SaveChangesAsync(cancellationToken);
            return SensitiveAccessDecisionResult.Invalid(redactedSummary.ErrorDetail);
        }

        var decisionKind = status == SensitiveAccessRequestStatus.Approved
            ? SensitiveAccessDecisionKind.Approved
            : SensitiveAccessDecisionKind.Denied;
        var auditAction = status == SensitiveAccessRequestStatus.Approved
            ? "sensitive_access.approved"
            : "sensitive_access.denied";
        var redactionState = status == SensitiveAccessRequestStatus.Approved
            ? "approved-redacted-output-unavailable"
            : "denied-no-output";
        var approvalPolicy = status == SensitiveAccessRequestStatus.Approved
            ? await GetOrCreateActivePolicyAsync(
                accessRequest.WorkspaceId,
                "workflow-default",
                cancellationToken)
            : null;
        var isRelational = db.Database.IsRelational();
        if (!isRelational)
        {
            await NonRelationalDecisionLock.WaitAsync(cancellationToken);
        }

        var observedAt = timeProvider.GetUtcNow();
        var decisionRecord = new SensitiveAccessDecisionRecord
        {
            Id = $"decision-{Guid.NewGuid():N}",
            SensitiveAccessRequestId = accessRequest.Id,
            Decision = decisionKind,
            DecidedBy = actor,
            DecisionReason = request.Reason?.Trim() ?? "",
            DecidedAt = observedAt,
            PayloadClass = "metadata-only",
            RedactionState = redactionState
        };
        var grantRecord = approvalPolicy is null
            ? null
            : new SensitiveAccessGrantRecord
            {
                SensitiveAccessRequestId = accessRequest.Id,
                WorkspaceId = accessRequest.WorkspaceId,
                OwnerUserId = accessRequest.OwnerUserId,
                PolicyRevision = approvalPolicy.Revision,
                GrantDurationSeconds = approvalPolicy.GrantDurationSeconds,
                StartsAt = observedAt,
                ExpiresAt = observedAt.AddSeconds(approvalPolicy.GrantDurationSeconds),
                MaximumSuccessfulReads = approvalPolicy.MaximumSuccessfulReads,
                SuccessfulReadCount = 0
            };
        var auditRecord = AuditEventFactory.ForWorkspace(
            principal,
            actor,
            auditAction,
            accessRequest.Id,
            "metadata-only",
            redactionState,
            observedAt,
            subjectType: "sensitive_access_request",
            outcome: status.ToString().ToLowerInvariant());
        var transitioned = false;
        try
        {
            if (isRelational)
            {
                db.Entry(accessRequest).State = EntityState.Detached;
                var strategy = db.Database.CreateExecutionStrategy();
                await strategy.ExecuteInTransactionAsync(
                    async operationCancellationToken =>
                    {
                        if (db.Entry(decisionRecord).State == EntityState.Added)
                        {
                            db.Entry(decisionRecord).State = EntityState.Detached;
                            db.Entry(auditRecord).State = EntityState.Detached;
                            if (grantRecord is not null)
                            {
                                db.Entry(grantRecord).State = EntityState.Detached;
                            }
                        }
                        transitioned = await db.SensitiveAccessRequests
                            .Where(record =>
                                record.Id == id &&
                                record.Status == SensitiveAccessRequestStatus.Pending &&
                                record.ExpiresAt > observedAt)
                            .ExecuteUpdateAsync(setters => setters
                                .SetProperty(record => record.Status, status)
                                .SetProperty(record => record.DecidedBy, actor)
                                .SetProperty(record => record.DecidedAt, observedAt)
                                .SetProperty(record => record.RedactedSummary, redactedSummary.Value ?? "")
                                .SetProperty(record => record.UpdatedAt, observedAt), operationCancellationToken) == 1;
                        if (!transitioned)
                        {
                            transitioned = await db.SensitiveAccessDecisions
                                .AsNoTracking()
                                .AnyAsync(record => record.Id == decisionRecord.Id, operationCancellationToken);
                            return;
                        }

                        if (db.Entry(decisionRecord).State == EntityState.Detached)
                        {
                            db.SensitiveAccessDecisions.Add(decisionRecord);
                            db.AuditEvents.Add(auditRecord);
                            if (grantRecord is not null)
                            {
                                db.SensitiveAccessGrants.Add(grantRecord);
                            }
                        }
                        await db.SaveChangesAsync(acceptAllChangesOnSuccess: false, operationCancellationToken);
                    },
                    operationCancellationToken => db.SensitiveAccessDecisions
                        .AsNoTracking()
                        .AnyAsync(record => record.Id == decisionRecord.Id, operationCancellationToken),
                    cancellationToken);
                db.ChangeTracker.AcceptAllChanges();
            }
            else
            {
                await db.Entry(accessRequest).ReloadAsync(cancellationToken);
                transitioned = accessRequest.Status == SensitiveAccessRequestStatus.Pending &&
                    accessRequest.ExpiresAt > observedAt;
                if (transitioned)
                {
                    accessRequest.Status = status;
                    accessRequest.DecidedBy = actor;
                    accessRequest.DecidedAt = observedAt;
                    accessRequest.RedactedSummary = redactedSummary.Value ?? "";
                    accessRequest.UpdatedAt = observedAt;
                    db.SensitiveAccessDecisions.Add(decisionRecord);
                    db.AuditEvents.Add(auditRecord);
                    if (grantRecord is not null)
                    {
                        db.SensitiveAccessGrants.Add(grantRecord);
                    }
                    await db.SaveChangesAsync(cancellationToken);
                }
            }

            if (!transitioned)
            {
                await ExpirePendingRequestsAsync(id, principal, cancellationToken);
                return SensitiveAccessDecisionResult.AlreadyDecided();
            }
        }
        finally
        {
            if (!isRelational)
            {
                NonRelationalDecisionLock.Release();
            }
        }

        metrics.RecordSensitiveAccessDecision(
            decisionKind == SensitiveAccessDecisionKind.Approved ? "approved" : "denied");

        accessRequest.Status = status;
        accessRequest.DecidedBy = actor;
        accessRequest.DecidedAt = observedAt;
        accessRequest.RedactedSummary = redactedSummary.Value ?? "";
        accessRequest.UpdatedAt = observedAt;

        return SensitiveAccessDecisionResult.Succeeded(ToState(
            accessRequest,
            status == SensitiveAccessRequestStatus.Approved &&
                !string.IsNullOrWhiteSpace(accessRequest.RedactedSummary)));
    }

    internal async Task<SensitiveAccessReadPermit?> IssueReadPermitAsync(
        string requestId,
        LuthnRequestPrincipal principal,
        CancellationToken cancellationToken)
    {
        var observedAt = timeProvider.GetUtcNow();
        var scope = await db.SensitiveAccessGrants
            .AsNoTracking()
            .Where(grant =>
                grant.SensitiveAccessRequestId == requestId &&
                grant.WorkspaceId == principal.WorkspaceId &&
                grant.OwnerUserId == principal.UserId &&
                grant.StartsAt <= observedAt &&
                grant.ExpiresAt > observedAt &&
                grant.SuccessfulReadCount < grant.MaximumSuccessfulReads &&
                grant.SensitiveAccessRequest != null &&
                grant.SensitiveAccessRequest.Status == SensitiveAccessRequestStatus.Approved &&
                grant.SensitiveAccessRequest.RedactedSummary != "")
            .Select(grant => new SensitiveAccessPermitScope(
                grant.SensitiveAccessRequestId,
                grant.WorkspaceId,
                grant.OwnerUserId))
            .SingleOrDefaultAsync(cancellationToken);
        if (scope is null)
        {
            return null;
        }

        var permit = new SensitiveAccessReadPermit(
            Guid.NewGuid().ToString("N"),
            scope.RequestId,
            scope.WorkspaceId,
            scope.OwnerUserId,
            timeProvider.GetUtcNow().Add(ReadPermitLifetime));
        if (!_readPermits.TryAdd(permit.Id, new SensitiveAccessReadPermitState(permit)))
        {
            return null;
        }
        return permit;
    }

    internal async Task<string?> ReadApprovedResultAsync(
        string requestId,
        LuthnRequestPrincipal principal,
        string actor,
        SensitiveAccessReadPermit? permit,
        CancellationToken cancellationToken)
    {
        var rejection = ValidateAndConsumeReadPermit(requestId, principal, permit);
        if (rejection is not null)
        {
            await AuditReadBypassAsync(requestId, principal, actor, rejection, cancellationToken);
            return null;
        }

        var redactedOutput = await db.SensitiveAccessRequests
            .AsNoTracking()
            .Where(record => record.Id == requestId &&
                record.WorkspaceId == principal.WorkspaceId &&
                record.OwnerUserId == principal.UserId &&
                record.Status == SensitiveAccessRequestStatus.Approved &&
                record.RedactedSummary != "")
            .Select(record => record.RedactedSummary)
            .SingleOrDefaultAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(redactedOutput))
        {
            return null;
        }

        return await ReserveGrantReadAsync(requestId, principal, cancellationToken)
            ? redactedOutput
            : null;
    }

    private string? ValidateAndConsumeReadPermit(
        string requestId,
        LuthnRequestPrincipal principal,
        SensitiveAccessReadPermit? permit)
    {
        if (permit is null)
        {
            return "missing-permit";
        }

        if (!_readPermits.TryGetValue(permit.Id, out var state) ||
            !ReferenceEquals(state.Permit, permit))
        {
            return "invalid-permit";
        }

        if (Interlocked.Exchange(ref state.Consumed, 1) != 0)
        {
            return "reused-permit";
        }
        if (permit.ExpiresAt <= timeProvider.GetUtcNow())
        {
            return "expired-permit";
        }

        if (!string.Equals(permit.RequestId, requestId, StringComparison.Ordinal) ||
            !string.Equals(permit.WorkspaceId, principal.WorkspaceId, StringComparison.Ordinal) ||
            !string.Equals(permit.OwnerUserId, principal.UserId, StringComparison.Ordinal))
        {
            return "scope-mismatch";
        }

        return null;
    }

    private async Task<bool> ReserveGrantReadAsync(
        string requestId,
        LuthnRequestPrincipal principal,
        CancellationToken cancellationToken)
    {
        var observedAt = timeProvider.GetUtcNow();
        if (db.Database.IsRelational())
        {
            return await db.SensitiveAccessGrants
                .Where(grant =>
                    grant.SensitiveAccessRequestId == requestId &&
                    grant.WorkspaceId == principal.WorkspaceId &&
                    grant.OwnerUserId == principal.UserId &&
                    grant.StartsAt <= observedAt &&
                    grant.ExpiresAt > observedAt &&
                    grant.SuccessfulReadCount < grant.MaximumSuccessfulReads)
                .ExecuteUpdateAsync(setters => setters.SetProperty(
                    grant => grant.SuccessfulReadCount,
                    grant => grant.SuccessfulReadCount + 1), cancellationToken) == 1;
        }

        await NonRelationalGrantReadLock.WaitAsync(cancellationToken);
        try
        {
            var grant = await db.SensitiveAccessGrants
                .SingleOrDefaultAsync(candidate =>
                    candidate.SensitiveAccessRequestId == requestId &&
                    candidate.WorkspaceId == principal.WorkspaceId &&
                    candidate.OwnerUserId == principal.UserId,
                    cancellationToken);
            if (grant is null)
            {
                return false;
            }

            await db.Entry(grant).ReloadAsync(cancellationToken);
            if (grant.StartsAt > observedAt ||
                grant.ExpiresAt <= observedAt ||
                grant.SuccessfulReadCount >= grant.MaximumSuccessfulReads)
            {
                return false;
            }

            grant.SuccessfulReadCount++;
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        finally
        {
            NonRelationalGrantReadLock.Release();
        }
    }

    private async Task AuditReadBypassAsync(
        string requestId,
        LuthnRequestPrincipal principal,
        string actor,
        string reason,
        CancellationToken cancellationToken)
    {
        db.AuditEvents.Add(AuditEventFactory.ForWorkspace(
            principal,
            actor,
            "sensitive_access.read_bypass_rejected",
            BoundAuditSubjectId(requestId),
            "metadata-only",
            $"{reason}-no-output",
            timeProvider.GetUtcNow(),
            subjectType: "sensitive_access_request",
            outcome: "rejected"));
        await db.SaveChangesAsync(cancellationToken);
    }

    private static string BoundAuditSubjectId(string requestId) =>
        !string.IsNullOrWhiteSpace(requestId) &&
        requestId.Length <= ApiValidation.PublicRecordIdMaxLength &&
        !requestId.Any(char.IsControl)
            ? requestId
            : "invalid-sensitive-access-request";

    private Task<SensitiveAccessResultMetadata?> ReadResultMetadataAsync(
        string id,
        LuthnRequestPrincipal principal,
        CancellationToken cancellationToken) =>
        db.SensitiveAccessRequests
            .AsNoTracking()
            .Where(record => record.Id == id &&
                record.WorkspaceId == principal.WorkspaceId &&
                record.OwnerUserId == principal.UserId)
            .Select(record => new SensitiveAccessResultMetadata(
                record.Id,
                record.SensitiveRecordReferenceId,
                record.Status,
                record.RedactedSummary != ""))
            .SingleOrDefaultAsync(cancellationToken);

    private async Task<ValidatedRedactedSummary> ValidateDecisionRedactedSummaryAsync(
        SensitiveAccessRequestRecord accessRequest,
        SensitiveAccessDecisionRequest request,
        SensitiveAccessRequestStatus status,
        CancellationToken cancellationToken)
    {
        if (status != SensitiveAccessRequestStatus.Approved ||
            string.IsNullOrWhiteSpace(request.RedactedSummary))
        {
            return new ValidatedRedactedSummary(null, null);
        }

        if (classifier is null || policyEngine is null)
        {
            return new ValidatedRedactedSummary(
                null,
                "redactedSummary can only be provided for approval decisions.");
        }

        var candidate = request.RedactedSummary.Trim();
        if (candidate.Length > MaxStoredRedactedSummaryLength)
        {
            return new ValidatedRedactedSummary(
                null,
                $"redactedSummary must be {MaxStoredRedactedSummaryLength} characters or fewer.");
        }

        var classification = ClassificationResultNormalizer.Normalize(await classifier.ClassifyAsync(
            new PublicRecordId($"{accessRequest.Id}-redacted-summary"),
            candidate,
            "redacted-summary",
            cancellationToken));
        var decision = policyEngine.Decide(classification);
        if (classification.ContainsSensitiveMaterial || !decision.AllowsAgentContext)
        {
            return new ValidatedRedactedSummary(
                null,
                "redactedSummary must classify as public agent-safe content.");
        }

        return new ValidatedRedactedSummary(candidate, null);
    }

    private async Task ExpirePendingRequestsAsync(
        string? requestId,
        LuthnRequestPrincipal principal,
        CancellationToken cancellationToken)
    {
        var observedAt = timeProvider.GetUtcNow();
        var candidates = await db.SensitiveAccessRequests
            .AsNoTracking()
            .Where(request =>
                request.Status == SensitiveAccessRequestStatus.Pending &&
                request.ExpiresAt <= observedAt &&
                request.WorkspaceId == principal.WorkspaceId &&
                (principal.IsOperator || request.OwnerUserId == principal.UserId) &&
                (requestId == null || request.Id == requestId))
            .Select(request => request.Id)
            .ToArrayAsync(cancellationToken);
        if (candidates.Length == 0)
        {
            return;
        }

        if (db.Database.IsRelational())
        {
            var auditRecords = candidates.ToDictionary(
                candidateId => candidateId,
                candidateId => AuditEventFactory.ForWorkspace(
                    principal.WorkspaceId,
                    actorUserId: null,
                    actorKind: "system",
                    actor: "local-expiry",
                    action: "sensitive_access.expired",
                    subjectId: candidateId,
                    payloadClass: "metadata-only",
                    redactionState: "expired-no-output",
                    occurredAt: observedAt,
                    subjectType: "sensitive_access_request",
                    outcome: "expired"));
            var transitionedCandidateIds = new HashSet<string>(StringComparer.Ordinal);
            var strategy = db.Database.CreateExecutionStrategy();
            await strategy.ExecuteInTransactionAsync(
                async operationCancellationToken =>
                {
                    foreach (var auditRecord in auditRecords.Values)
                    {
                        if (db.Entry(auditRecord).State == EntityState.Added)
                        {
                            db.Entry(auditRecord).State = EntityState.Detached;
                        }
                    }
                    transitionedCandidateIds.Clear();
                    foreach (var candidateId in candidates)
                    {
                        var transitioned = await db.SensitiveAccessRequests
                            .Where(request =>
                                request.Id == candidateId &&
                                request.Status == SensitiveAccessRequestStatus.Pending &&
                                request.ExpiresAt <= observedAt)
                            .ExecuteUpdateAsync(setters => setters
                                .SetProperty(request => request.Status, SensitiveAccessRequestStatus.Expired)
                                .SetProperty(request => request.UpdatedAt, observedAt), operationCancellationToken) == 1;
                        if (transitioned)
                        {
                            transitionedCandidateIds.Add(candidateId);
                            var auditRecord = auditRecords[candidateId];
                            if (db.Entry(auditRecord).State == EntityState.Detached)
                            {
                                db.AuditEvents.Add(auditRecord);
                            }
                        }
                    }

                    await db.SaveChangesAsync(acceptAllChangesOnSuccess: false, operationCancellationToken);
                },
                async operationCancellationToken =>
                {
                    if (transitionedCandidateIds.Count == 0)
                    {
                        return true;
                    }

                    var auditIds = transitionedCandidateIds
                        .Select(candidateId => auditRecords[candidateId].Id)
                        .ToArray();
                    return await db.AuditEvents
                        .AsNoTracking()
                        .CountAsync(record => auditIds.Contains(record.Id), operationCancellationToken) == auditIds.Length;
                },
                cancellationToken);
            db.ChangeTracker.AcceptAllChanges();
            return;
        }

        foreach (var candidateId in candidates)
        {
            var request = await db.SensitiveAccessRequests
                .SingleAsync(record => record.Id == candidateId, cancellationToken);
            var transitioned = request.Status == SensitiveAccessRequestStatus.Pending &&
                request.ExpiresAt <= observedAt;
            if (!transitioned)
            {
                continue;
            }

            request.Status = SensitiveAccessRequestStatus.Expired;
            request.UpdatedAt = observedAt;
            db.AuditEvents.Add(AuditEventFactory.ForWorkspace(
                principal.WorkspaceId,
                actorUserId: null,
                actorKind: "system",
                actor: "local-expiry",
                action: "sensitive_access.expired",
                subjectId: candidateId,
                payloadClass: "metadata-only",
                redactionState: "expired-no-output",
                occurredAt: observedAt,
                subjectType: "sensitive_access_request",
                outcome: "expired"));
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<SensitiveAccessPolicyRevisionRecord> GetOrCreateActivePolicyAsync(
        string workspaceId,
        string createdBy,
        CancellationToken cancellationToken)
    {
        await PolicyRevisionLock.WaitAsync(cancellationToken);
        try
        {
            var policy = await db.SensitiveAccessPolicyRevisions
                .Where(record =>
                    record.WorkspaceId == workspaceId &&
                    record.RequestTimeoutSeconds >= SensitiveAccessPolicyLimits.MinimumDurationSeconds &&
                    record.RequestTimeoutSeconds <= SensitiveAccessPolicyLimits.MaximumDurationSeconds &&
                    record.GrantDurationSeconds >= SensitiveAccessPolicyLimits.MinimumDurationSeconds &&
                    record.GrantDurationSeconds <= SensitiveAccessPolicyLimits.MaximumDurationSeconds &&
                    record.MaximumSuccessfulReads >= SensitiveAccessPolicyLimits.MinimumSuccessfulReads &&
                    record.MaximumSuccessfulReads <= SensitiveAccessPolicyLimits.MaximumSuccessfulReads)
                .OrderByDescending(record => record.Revision)
                .FirstOrDefaultAsync(cancellationToken);
            if (policy is not null)
            {
                return policy;
            }

            var nextRevision = (await db.SensitiveAccessPolicyRevisions
                .Where(record => record.WorkspaceId == workspaceId)
                .Select(record => (int?)record.Revision)
                .MaxAsync(cancellationToken) ?? 0) + 1;
            policy = new SensitiveAccessPolicyRevisionRecord
            {
                WorkspaceId = workspaceId,
                Revision = nextRevision,
                RequestTimeoutSeconds = SensitiveAccessPolicyLimits.DefaultRequestTimeoutSeconds,
                GrantDurationSeconds = SensitiveAccessPolicyLimits.DefaultGrantDurationSeconds,
                MaximumSuccessfulReads = SensitiveAccessPolicyLimits.DefaultMaximumSuccessfulReads,
                CreatedAt = timeProvider.GetUtcNow(),
                CreatedBy = createdBy
            };
            db.SensitiveAccessPolicyRevisions.Add(policy);
            await db.SaveChangesAsync(cancellationToken);
            return policy;
        }
        finally
        {
            PolicyRevisionLock.Release();
        }
    }

    private static string? ValidatePolicyUpdate(SensitiveAccessPolicyUpdate update)
    {
        if (!SensitiveAccessPolicyLimits.IsValidDuration(update.RequestTimeoutSeconds))
        {
            return "requestTimeoutSeconds must be between 60 and 3600.";
        }

        if (!SensitiveAccessPolicyLimits.IsValidDuration(update.GrantDurationSeconds))
        {
            return "grantDurationSeconds must be between 60 and 3600.";
        }

        if (!SensitiveAccessPolicyLimits.IsValidMaximumSuccessfulReads(update.MaximumSuccessfulReads))
        {
            return "maximumSuccessfulReads must be between 1 and 10.";
        }

        return null;
    }

    private static SensitiveAccessRequestState ToState(
        SensitiveAccessRequestRecord request,
        bool redactedOutputAvailable) =>
        new(
            request.Id,
            request.SensitiveRecordReferenceId,
            request.Status,
            request.RequestedBy,
            request.SessionId,
            request.CreatedAt,
            request.ExpiresAt,
            request.DecidedBy,
            request.DecidedAt,
            redactedOutputAvailable);

    private static SensitiveAccessPolicyState ToState(SensitiveAccessPolicyRevisionRecord policy) =>
        new(
            policy.WorkspaceId,
            policy.Revision,
            policy.RequestTimeoutSeconds,
            policy.GrantDurationSeconds,
            policy.MaximumSuccessfulReads,
            policy.CreatedAt);

    private sealed record SensitiveAccessReferenceIdentity(
        string Id,
        string WorkspaceId,
        string OwnerUserId);

    private sealed record SensitiveAccessOperatorRequestState(
        string Id,
        string SensitiveReferenceId,
        SensitiveAccessRequestStatus Status,
        string RequestedBy,
        string SessionId,
        string RequestReason,
        DateTimeOffset CreatedAt,
        DateTimeOffset ExpiresAt,
        string? DecidedBy,
        DateTimeOffset? DecidedAt,
        string WorkspaceId,
        string OwnerUserId,
        bool RedactedOutputAvailable);

    private sealed record SensitiveAccessResultMetadata(
        string Id,
        string SensitiveReferenceId,
        SensitiveAccessRequestStatus Status,
        bool RedactedOutputAvailable);

    private sealed record SensitiveAccessPermitScope(
        string RequestId,
        string WorkspaceId,
        string OwnerUserId);

    private sealed record ValidatedRedactedSummary(
        string? Value,
        string? ErrorDetail);

    private sealed class SensitiveAccessReadPermitState(SensitiveAccessReadPermit permit)
    {
        public SensitiveAccessReadPermit Permit { get; } = permit;
        public int Consumed;
    }
}

internal sealed class SensitiveAccessReadPermit
{
    internal SensitiveAccessReadPermit(
        string id,
        string requestId,
        string workspaceId,
        string ownerUserId,
        DateTimeOffset expiresAt)
    {
        Id = id;
        RequestId = requestId;
        WorkspaceId = workspaceId;
        OwnerUserId = ownerUserId;
        ExpiresAt = expiresAt;
    }

    internal string Id { get; }
    internal string RequestId { get; }
    internal string WorkspaceId { get; }
    internal string OwnerUserId { get; }
    internal DateTimeOffset ExpiresAt { get; }
}

internal sealed record SensitiveAccessListResult(
    IReadOnlyList<SensitiveAccessRequestState> Requests,
    string? ValidationError);

internal sealed record SensitiveAccessPolicyUpdate(
    int RequestTimeoutSeconds,
    int GrantDurationSeconds,
    int MaximumSuccessfulReads);

internal sealed record SensitiveAccessPolicyState(
    string WorkspaceId,
    int Revision,
    int RequestTimeoutSeconds,
    int GrantDurationSeconds,
    int MaximumSuccessfulReads,
    DateTimeOffset CreatedAt);

internal sealed record SensitiveAccessPolicyRevisionResult(
    SensitiveAccessPolicyState? Policy,
    string? ValidationError)
{
    public static SensitiveAccessPolicyRevisionResult Succeeded(SensitiveAccessPolicyState policy) =>
        new(policy, null);

    public static SensitiveAccessPolicyRevisionResult Invalid(string validationError) =>
        new(null, validationError);
}

internal sealed record SensitiveAccessRequestState(
    string Id,
    string SensitiveReferenceId,
    SensitiveAccessRequestStatus Status,
    string RequestedBy,
    string SessionId,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    string? DecidedBy,
    DateTimeOffset? DecidedAt,
    bool RedactedOutputAvailable);

internal sealed record SensitiveAccessOperatorDecisionState(
    SensitiveAccessDecisionKind Decision,
    string DecidedBy,
    DateTimeOffset DecidedAt,
    string DecisionReason);

internal sealed record SensitiveAccessOperatorReferenceState(
    string SourceSystem,
    string SourceType,
    string ReferenceLabel,
    string RedactedSummary,
    DateTimeOffset ReceivedAt);

internal sealed record SensitiveAccessOperatorDetailState(
    string Id,
    string SensitiveReferenceId,
    SensitiveAccessRequestStatus Status,
    string RequestedBy,
    string SessionId,
    string RequestReason,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    SensitiveAccessDecisionKind? Decision,
    string? DecidedBy,
    DateTimeOffset? DecidedAt,
    string? DecisionReason,
    bool RedactedOutputAvailable,
    SensitiveAccessOperatorReferenceState Reference);

internal sealed record SensitiveAccessResultState(
    string Id,
    string SensitiveReferenceId,
    SensitiveAccessRequestStatus Status,
    string? RedactedOutput)
{
    public bool RedactedOutputAvailable =>
        Status == SensitiveAccessRequestStatus.Approved &&
        !string.IsNullOrWhiteSpace(RedactedOutput);
}

internal enum SensitiveAccessDecisionOutcome
{
    Succeeded,
    NotFound,
    AlreadyDecided,
    Invalid
}

internal sealed record SensitiveAccessDecisionResult(
    SensitiveAccessDecisionOutcome Outcome,
    SensitiveAccessRequestState? Request,
    string? ValidationError)
{
    public static SensitiveAccessDecisionResult Succeeded(SensitiveAccessRequestState request) =>
        new(SensitiveAccessDecisionOutcome.Succeeded, request, null);

    public static SensitiveAccessDecisionResult NotFound() =>
        new(SensitiveAccessDecisionOutcome.NotFound, null, null);

    public static SensitiveAccessDecisionResult AlreadyDecided() =>
        new(SensitiveAccessDecisionOutcome.AlreadyDecided, null, null);

    public static SensitiveAccessDecisionResult Invalid(string detail) =>
        new(SensitiveAccessDecisionOutcome.Invalid, null, detail);
}
