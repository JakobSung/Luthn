using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Security.Cryptography;
using System.Text;
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

internal interface ISensitiveAccessSystemWorkflow
{
    Task<SensitiveAccessExpiryMaterializationResult> MaterializeExpiriesAsync(
        DateTimeOffset observedAt,
        int batchSize,
        CancellationToken cancellationToken);
}

internal sealed record SensitiveAccessExpiryMaterializationResult(
    int RequestsExpired,
    int GrantsExpired)
{
    public int MaterializedCount => RequestsExpired + GrantsExpired;
}

internal sealed class SensitiveAccessWorkflow(
    LuthnDbContext db,
    IOperationalMetrics metrics,
    TimeProvider timeProvider,
    IContentClassifier? classifier = null,
    IPolicyEngine? policyEngine = null) : ISensitiveAccessWorkflow, ISensitiveAccessSystemWorkflow
{
    private const int MaxStoredRedactedSummaryLength = 4000;
    internal const int MinimumExpiryMaterializationBatchSize = 1;
    internal const int MaximumExpiryMaterializationBatchSize = 1000;
    internal const int DefaultExpiryMaterializationBatchSize = 100;
    private static readonly TimeSpan ReadPermitLifetime = TimeSpan.FromSeconds(5);
    private static readonly SemaphoreSlim PolicyRevisionLock = new(1, 1);
    private static readonly SemaphoreSlim RequestResolutionLock = new(1, 1);
    private static readonly SemaphoreSlim NonRelationalDecisionLock = new(1, 1);
    private static readonly SemaphoreSlim NonRelationalGrantReadLock = new(1, 1);
    private static readonly SemaphoreSlim ExpiryMaterializationLock = new(1, 1);
    private static readonly SemaphoreSlim LifecycleAuditLock = new(1, 1);
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
            metrics.RecordSensitiveAccessLifecycle("policy_updated");
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

        var observedAt = timeProvider.GetUtcNow();
        var requests = await query
            .OrderByDescending(record => record.UpdatedAt)
            .ThenByDescending(record => record.CreatedAt)
            .Take(take)
            .Select(ToResolutionCandidateExpression())
            .ToArrayAsync(cancellationToken);

        return new SensitiveAccessListResult(
            requests.Select(request => ToState(request, observedAt)).ToArray(),
            null);
    }

    public async Task<SensitiveAccessRequestState?> CreateRequestAsync(
        SensitiveAccessRequestCreateRequest request,
        LuthnRequestPrincipal principal,
        string actor,
        CancellationToken cancellationToken)
    {
        var sensitiveReferenceId = request.SensitiveReferenceId.Trim();
        var observedAt = timeProvider.GetUtcNow();
        var reference = await db.SensitiveRecordReferences
            .AsNoTracking()
            .Where(record => record.Id == sensitiveReferenceId &&
                record.WorkspaceId == principal.WorkspaceId &&
                record.OwnerUserId == principal.UserId &&
                (record.ExpiresAt == null || record.ExpiresAt > observedAt) &&
                (record.MemoryItemId == null ||
                    (record.MemoryItem != null &&
                        record.MemoryItem.WorkspaceId == principal.WorkspaceId &&
                        record.MemoryItem.OwnerUserId == principal.UserId &&
                        record.MemoryItem.ExpiresAt == record.ExpiresAt &&
                        db.SensitiveMemoryPayloads.Any(payload =>
                            payload.MemoryItemId == record.MemoryItemId &&
                            payload.ExpiresAt == record.ExpiresAt))))
            .Select(record => new SensitiveAccessReferenceIdentity(
                record.Id,
                record.WorkspaceId,
                record.OwnerUserId,
                record.ExpiresAt))
            .SingleOrDefaultAsync(cancellationToken);
        if (reference is null)
        {
            return null;
        }

        var sessionId = string.IsNullOrWhiteSpace(request.SessionId)
            ? $"legacy-{Guid.NewGuid():N}"
            : request.SessionId.Trim();
        await RequestResolutionLock.WaitAsync(cancellationToken);
        try
        {
            await ExpirePendingRequestsAsync(requestId: null, principal, cancellationToken);
            observedAt = timeProvider.GetUtcNow();
            if (reference.ExpiresAt <= observedAt)
            {
                return null;
            }
            var existing = await ResolveForCreateAsync(
                reference,
                sessionId,
                observedAt,
                cancellationToken);
            if (existing is not null)
            {
                db.AuditEvents.Add(AuditEventFactory.ForWorkspace(
                    principal,
                    actor,
                    "sensitive_access.request_reused",
                    existing.Id,
                    "metadata-only",
                    "request-reused-no-output",
                    observedAt,
                    subjectType: "sensitive_access_request",
                    outcome: "reused"));
                await db.SaveChangesAsync(cancellationToken);
                metrics.RecordSensitiveAccessLifecycle("request_reused");
                return ToState(existing, observedAt);
            }

            var policy = await GetOrCreateActivePolicyAsync(
                reference.WorkspaceId,
                "workflow-default",
                cancellationToken);
            var accessRequest = new SensitiveAccessRequestRecord
            {
                Id = CreateStableRequestId(reference, sessionId),
                SensitiveRecordReferenceId = reference.Id,
                RequestedBy = actor,
                SessionId = sessionId,
                RequestReason = request.Reason.Trim(),
                Status = SensitiveAccessRequestStatus.Pending,
                CreatedAt = observedAt,
                ExpiresAt = MinExpiry(
                    observedAt.AddSeconds(policy.RequestTimeoutSeconds),
                    reference.ExpiresAt),
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

            return ToState(
                accessRequest,
                redactedOutputAvailable: false,
                SensitiveAccessStatusCodes.RequestCreated);
        }
        finally
        {
            RequestResolutionLock.Release();
        }
    }

    public async Task<SensitiveAccessRequestState?> ReadRequestAsync(
        string id,
        LuthnRequestPrincipal principal,
        CancellationToken cancellationToken)
    {
        await ExpirePendingRequestsAsync(id, principal, cancellationToken);
        var request = await ReadResolutionCandidateAsync(id, principal, cancellationToken);
        return request is null ? null : ToState(request, timeProvider.GetUtcNow());
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
                record.UpdatedAt,
                record.DecidedBy,
                record.DecidedAt,
                record.WorkspaceId,
                record.OwnerUserId,
                record.RedactedSummary != "",
                record.SensitiveRecordReference == null
                    ? null
                    : record.SensitiveRecordReference.ExpiresAt,
                record.Grant == null ? null : record.Grant.StartsAt,
                record.Grant == null ? null : record.Grant.ExpiresAt,
                record.Grant == null ? null : record.Grant.MaximumSuccessfulReads,
                record.Grant == null ? null : record.Grant.SuccessfulReadCount))
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

        var lifecycle = new SensitiveAccessResolutionCandidate(
            request.Id,
            request.SensitiveReferenceId,
            request.Status,
            request.RequestedBy,
            request.SessionId,
            request.CreatedAt,
            request.ExpiresAt,
            request.UpdatedAt,
            request.DecidedBy,
            request.DecidedAt,
            request.RedactedOutputAvailable,
            request.ReferenceExpiresAt,
            request.GrantStartsAt,
            request.GrantExpiresAt,
            request.MaximumSuccessfulReads,
            request.SuccessfulReadCount);
        var statusCode = ResolveStatusCode(lifecycle, timeProvider.GetUtcNow());
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
            reference)
        {
            StatusCode = statusCode,
            RequestExpiresAt = request.ExpiresAt,
            GrantExpiresAt = request.GrantExpiresAt,
            RemainingReads = RemainingReads(lifecycle),
            MaxReads = request.MaximumSuccessfulReads
        };
    }

    public async Task<SensitiveAccessResultState?> ReadRequestResultAsync(
        string id,
        LuthnRequestPrincipal principal,
        string actor,
        CancellationToken cancellationToken)
    {
        await ExpirePendingRequestsAsync(id, principal, cancellationToken);
        var request = await ReadResolutionCandidateAsync(id, principal, cancellationToken);
        if (request is null)
        {
            return null;
        }

        var statusCode = ResolveStatusCode(request, timeProvider.GetUtcNow());
        string? redactedOutput = null;
        if (statusCode == SensitiveAccessStatusCodes.GrantActive && request.RedactedOutputAvailable)
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

        var current = statusCode == SensitiveAccessStatusCodes.GrantActive
            ? await ReadResolutionCandidateAsync(id, principal, cancellationToken) ?? request
            : request;
        var result = new SensitiveAccessResultState(
            request.Id,
            request.SensitiveReferenceId,
            request.Status,
            redactedOutput)
        {
            StatusCode = redactedOutput is null
                ? ResolveStatusCode(current, timeProvider.GetUtcNow())
                : SensitiveAccessStatusCodes.ResultReturned,
            RequestExpiresAt = current.RequestExpiresAt,
            GrantExpiresAt = current.GrantExpiresAt,
            RemainingReads = RemainingReads(current),
            MaxReads = current.MaximumSuccessfulReads
        };
        db.AuditEvents.Add(AuditEventFactory.ForWorkspace(
            principal,
            actor,
            "sensitive_access.result_read",
            request.Id,
            result.RedactedOutputAvailable ? "redacted-output" : "metadata-only",
            SensitiveAccessEndpointMapping.ToOutputPolicy(request.Status, result.RedactedOutputAvailable),
            timeProvider.GetUtcNow(),
            subjectType: "sensitive_access_request",
            outcome: result.RedactedOutputAvailable ? "returned" : "unavailable"));
        await db.SaveChangesAsync(cancellationToken);
        metrics.RecordSensitiveAccessLifecycle(
            result.RedactedOutputAvailable ? "result_returned" : "result_unavailable");

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

        var referenceLifetime = await ReadReferenceLifetimeAsync(accessRequest, cancellationToken);
        if (!IsActive(referenceLifetime, timeProvider.GetUtcNow()))
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
        referenceLifetime = await ReadReferenceLifetimeAsync(accessRequest, cancellationToken);
        if (!IsActive(referenceLifetime, observedAt))
        {
            return SensitiveAccessDecisionResult.AlreadyDecided();
        }
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
                ExpiresAt = MinExpiry(
                    observedAt.AddSeconds(approvalPolicy.GrantDurationSeconds),
                    referenceLifetime!.ExpiresAt),
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
        var grantAuditRecord = grantRecord is null
            ? null
            : AuditEventFactory.ForWorkspace(
                principal,
                actor,
                "sensitive_access.grant_created",
                accessRequest.Id,
                "metadata-only",
                "bounded-grant-created",
                observedAt,
                subjectType: "sensitive_access_grant",
                outcome: "created",
                id: LifecycleAuditId(
                    "sensitive_access.grant_created",
                    accessRequest.WorkspaceId,
                    accessRequest.Id));
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
                            if (grantAuditRecord is not null)
                            {
                                db.Entry(grantAuditRecord).State = EntityState.Detached;
                            }
                            if (grantRecord is not null)
                            {
                                db.Entry(grantRecord).State = EntityState.Detached;
                            }
                        }
                        transitioned = await db.SensitiveAccessRequests
                            .Where(record =>
                                record.Id == id &&
                                record.Status == SensitiveAccessRequestStatus.Pending &&
                                record.ExpiresAt > observedAt &&
                                db.SensitiveRecordReferences.Any(reference =>
                                    reference.Id == record.SensitiveRecordReferenceId &&
                                    reference.WorkspaceId == record.WorkspaceId &&
                                    reference.OwnerUserId == record.OwnerUserId &&
                                    (reference.ExpiresAt == null || reference.ExpiresAt > observedAt)))
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
                                db.AuditEvents.Add(grantAuditRecord!);
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
                    accessRequest.ExpiresAt > observedAt &&
                    IsActive(
                        await ReadReferenceLifetimeAsync(accessRequest, cancellationToken),
                        observedAt);
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
                        db.AuditEvents.Add(grantAuditRecord!);
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
        if (grantRecord is not null)
        {
            metrics.RecordSensitiveAccessLifecycle("grant_created");
        }

        accessRequest.Status = status;
        accessRequest.DecidedBy = actor;
        accessRequest.DecidedAt = observedAt;
        accessRequest.RedactedSummary = redactedSummary.Value ?? "";
        accessRequest.UpdatedAt = observedAt;

        return SensitiveAccessDecisionResult.Succeeded(ToState(
            accessRequest,
            status == SensitiveAccessRequestStatus.Approved &&
                !string.IsNullOrWhiteSpace(accessRequest.RedactedSummary),
            status == SensitiveAccessRequestStatus.Approved
                ? SensitiveAccessStatusCodes.GrantActive
                : SensitiveAccessStatusCodes.RequestDenied,
            grantRecord));
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
                grant.SensitiveAccessRequest.RedactedSummary != "" &&
                grant.SensitiveAccessRequest.SensitiveRecordReference != null &&
                (grant.SensitiveAccessRequest.SensitiveRecordReference.ExpiresAt == null ||
                    grant.SensitiveAccessRequest.SensitiveRecordReference.ExpiresAt > observedAt))
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

        var observedAt = timeProvider.GetUtcNow();
        var redactedOutput = await db.SensitiveAccessRequests
            .AsNoTracking()
            .Where(record => record.Id == requestId &&
                record.WorkspaceId == principal.WorkspaceId &&
                record.OwnerUserId == principal.UserId &&
                record.Status == SensitiveAccessRequestStatus.Approved &&
                record.RedactedSummary != "" &&
                record.SensitiveRecordReference != null &&
                (record.SensitiveRecordReference.ExpiresAt == null ||
                    record.SensitiveRecordReference.ExpiresAt > observedAt))
            .Select(record => record.RedactedSummary)
            .SingleOrDefaultAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(redactedOutput))
        {
            return null;
        }

        var reservation = await ReserveGrantReadAsync(requestId, principal, cancellationToken);
        if (!reservation.Succeeded)
        {
            return null;
        }

        if (reservation.Consumed)
        {
            await AuditLifecycleOnceAsync(
                requestId,
                principal,
                actor: "luthn-sensitive-access-workflow",
                action: "sensitive_access.grant_consumed",
                subjectType: "sensitive_access_grant",
                outcome: "consumed",
                redactionState: "bounded-grant-consumed",
                metricEvent: "grant_consumed",
                cancellationToken);
        }

        return redactedOutput;
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

    private async Task<SensitiveAccessGrantReadReservation> ReserveGrantReadAsync(
        string requestId,
        LuthnRequestPrincipal principal,
        CancellationToken cancellationToken)
    {
        var observedAt = timeProvider.GetUtcNow();
        if (db.Database.IsRelational())
        {
            var reserved = await db.SensitiveAccessGrants
                .Where(grant =>
                    grant.SensitiveAccessRequestId == requestId &&
                    grant.WorkspaceId == principal.WorkspaceId &&
                    grant.OwnerUserId == principal.UserId &&
                    grant.StartsAt <= observedAt &&
                    grant.ExpiresAt > observedAt &&
                    grant.SuccessfulReadCount < grant.MaximumSuccessfulReads &&
                    grant.SensitiveAccessRequest != null &&
                    grant.SensitiveAccessRequest.SensitiveRecordReference != null &&
                    (grant.SensitiveAccessRequest.SensitiveRecordReference.ExpiresAt == null ||
                        grant.SensitiveAccessRequest.SensitiveRecordReference.ExpiresAt > observedAt))
                .ExecuteUpdateAsync(setters => setters.SetProperty(
                    grant => grant.SuccessfulReadCount,
                    grant => grant.SuccessfulReadCount + 1), cancellationToken) == 1;
            if (!reserved)
            {
                return SensitiveAccessGrantReadReservation.Rejected;
            }

            var consumed = await db.SensitiveAccessGrants
                .AsNoTracking()
                .AnyAsync(grant =>
                    grant.SensitiveAccessRequestId == requestId &&
                    grant.WorkspaceId == principal.WorkspaceId &&
                    grant.OwnerUserId == principal.UserId &&
                    grant.SuccessfulReadCount >= grant.MaximumSuccessfulReads,
                    cancellationToken);
            return new SensitiveAccessGrantReadReservation(true, consumed);
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
                return SensitiveAccessGrantReadReservation.Rejected;
            }

            await db.Entry(grant).ReloadAsync(cancellationToken);
            var accessRequest = await db.SensitiveAccessRequests
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    request => request.Id == requestId &&
                        request.WorkspaceId == principal.WorkspaceId &&
                        request.OwnerUserId == principal.UserId,
                    cancellationToken);
            if (grant.StartsAt > observedAt ||
                grant.ExpiresAt <= observedAt ||
                grant.SuccessfulReadCount >= grant.MaximumSuccessfulReads ||
                accessRequest is null ||
                !IsActive(
                    await ReadReferenceLifetimeAsync(accessRequest, cancellationToken),
                    observedAt))
            {
                return SensitiveAccessGrantReadReservation.Rejected;
            }

            grant.SuccessfulReadCount++;
            await db.SaveChangesAsync(cancellationToken);
            return new SensitiveAccessGrantReadReservation(
                true,
                grant.SuccessfulReadCount >= grant.MaximumSuccessfulReads);
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
        metrics.RecordSensitiveAccessLifecycle("bypass_rejected");
    }

    private async Task AuditLifecycleOnceAsync(
        string subjectId,
        LuthnRequestPrincipal principal,
        string actor,
        string action,
        string subjectType,
        string outcome,
        string redactionState,
        string metricEvent,
        CancellationToken cancellationToken)
    {
        await LifecycleAuditLock.WaitAsync(cancellationToken);
        try
        {
            var auditId = LifecycleAuditId(action, principal.WorkspaceId, subjectId);
            if (await db.AuditEvents.AsNoTracking().AnyAsync(
                audit => audit.Id == auditId,
                cancellationToken))
            {
                return;
            }

            db.AuditEvents.Add(AuditEventFactory.ForWorkspace(
                principal,
                actor,
                action,
                subjectId,
                "metadata-only",
                redactionState,
                timeProvider.GetUtcNow(),
                subjectType,
                outcome,
                id: auditId));
            await db.SaveChangesAsync(cancellationToken);
            metrics.RecordSensitiveAccessLifecycle(metricEvent);
        }
        finally
        {
            LifecycleAuditLock.Release();
        }
    }

    private static string LifecycleAuditId(string action, string workspaceId, string subjectId)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes($"{action}\n{workspaceId}\n{subjectId}"));
        return $"audit-sensitive-{Convert.ToHexString(digest).ToLowerInvariant()}";
    }

    private static string CreateStableRequestId(
        SensitiveAccessReferenceIdentity reference,
        string sessionId)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{reference.WorkspaceId}\n{reference.OwnerUserId}\n{reference.Id}\n{sessionId}"));
        return $"access-{Convert.ToHexString(digest).ToLowerInvariant()[..32]}";
    }

    private static DateTimeOffset MinExpiry(
        DateTimeOffset policyExpiry,
        DateTimeOffset? referenceExpiry) =>
        referenceExpiry is DateTimeOffset expiresAt && expiresAt < policyExpiry
            ? expiresAt
            : policyExpiry;

    private Task<SensitiveAccessReferenceLifetime?> ReadReferenceLifetimeAsync(
        SensitiveAccessRequestRecord request,
        CancellationToken cancellationToken) =>
        db.SensitiveRecordReferences
            .AsNoTracking()
            .Where(reference =>
                reference.Id == request.SensitiveRecordReferenceId &&
                reference.WorkspaceId == request.WorkspaceId &&
                reference.OwnerUserId == request.OwnerUserId)
            .Select(reference => new SensitiveAccessReferenceLifetime(reference.ExpiresAt))
            .SingleOrDefaultAsync(cancellationToken);

    private static bool IsActive(
        SensitiveAccessReferenceLifetime? reference,
        DateTimeOffset observedAt) =>
        reference is not null &&
        (reference.ExpiresAt is null || reference.ExpiresAt > observedAt);

    private static string BoundAuditSubjectId(string requestId) =>
        !string.IsNullOrWhiteSpace(requestId) &&
        requestId.Length <= ApiValidation.PublicRecordIdMaxLength &&
        !requestId.Any(char.IsControl)
            ? requestId
            : "invalid-sensitive-access-request";

    private async Task<ValidatedRedactedSummary> ValidateDecisionRedactedSummaryAsync(
        SensitiveAccessRequestRecord accessRequest,
        SensitiveAccessDecisionRequest request,
        SensitiveAccessRequestStatus status,
        CancellationToken cancellationToken)
    {
        if (status != SensitiveAccessRequestStatus.Approved)
        {
            return new ValidatedRedactedSummary(null, null);
        }

        var candidate = request.RedactedSummary?.Trim();
        if (string.IsNullOrWhiteSpace(candidate))
        {
            candidate = await db.SensitiveRecordReferences
                .AsNoTracking()
                .Where(reference =>
                    reference.Id == accessRequest.SensitiveRecordReferenceId &&
                    reference.WorkspaceId == accessRequest.WorkspaceId &&
                    reference.OwnerUserId == accessRequest.OwnerUserId &&
                    reference.MemoryItemId != null)
                .Select(reference => reference.RedactedSummary)
                .SingleOrDefaultAsync(cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(candidate))
        {
            return new ValidatedRedactedSummary(null, null);
        }

        if (classifier is null || policyEngine is null)
        {
            return new ValidatedRedactedSummary(
                null,
                "redactedSummary can only be provided for approval decisions.");
        }

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

    public async Task<SensitiveAccessExpiryMaterializationResult> MaterializeExpiriesAsync(
        DateTimeOffset observedAt,
        int batchSize,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, MinimumExpiryMaterializationBatchSize);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(batchSize, MaximumExpiryMaterializationBatchSize);

        await ExpiryMaterializationLock.WaitAsync(cancellationToken);
        try
        {
            var requestsExpired = await MaterializeRequestExpiriesAsync(
                observedAt,
                batchSize,
                workspaceId: null,
                ownerUserId: null,
                requestId: null,
                cancellationToken);
            var grantsExpired = requestsExpired < batchSize
                ? await MaterializeGrantExpiriesAsync(
                    observedAt,
                    batchSize - requestsExpired,
                    cancellationToken)
                : 0;
            return new SensitiveAccessExpiryMaterializationResult(requestsExpired, grantsExpired);
        }
        finally
        {
            ExpiryMaterializationLock.Release();
        }
    }

    private async Task ExpirePendingRequestsAsync(
        string? requestId,
        LuthnRequestPrincipal principal,
        CancellationToken cancellationToken)
    {
        await ExpiryMaterializationLock.WaitAsync(cancellationToken);
        try
        {
            await MaterializeRequestExpiriesAsync(
                timeProvider.GetUtcNow(),
                requestId is null ? DefaultExpiryMaterializationBatchSize : 1,
                principal.WorkspaceId,
                principal.IsOperator ? null : principal.UserId,
                requestId,
                cancellationToken);
        }
        finally
        {
            ExpiryMaterializationLock.Release();
        }
    }

    private async Task<int> MaterializeRequestExpiriesAsync(
        DateTimeOffset observedAt,
        int batchSize,
        string? workspaceId,
        string? ownerUserId,
        string? requestId,
        CancellationToken cancellationToken)
    {
        var candidates = await db.SensitiveAccessRequests
            .AsNoTracking()
            .Where(request =>
                request.Status == SensitiveAccessRequestStatus.Pending &&
                request.ExpiresAt <= observedAt &&
                (workspaceId == null || request.WorkspaceId == workspaceId) &&
                (ownerUserId == null || request.OwnerUserId == ownerUserId) &&
                (requestId == null || request.Id == requestId))
            .OrderBy(request => request.ExpiresAt)
            .ThenBy(request => request.Id)
            .Take(batchSize)
            .Select(request => new SensitiveAccessExpiryCandidate(
                request.Id,
                request.WorkspaceId,
                request.OwnerUserId))
            .ToArrayAsync(cancellationToken);
        if (candidates.Length == 0)
        {
            return 0;
        }

        var transitionedCandidates = new List<SensitiveAccessExpiryCandidate>(candidates.Length);
        if (db.Database.IsRelational())
        {
            var auditRecords = candidates.ToDictionary(
                candidate => candidate.Id,
                candidate => CreateRequestExpiredAudit(candidate, observedAt),
                StringComparer.Ordinal);
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

                    transitionedCandidates.Clear();
                    foreach (var candidate in candidates)
                    {
                        var transitioned = await db.SensitiveAccessRequests
                            .Where(request =>
                                request.Id == candidate.Id &&
                                request.Status == SensitiveAccessRequestStatus.Pending &&
                                request.ExpiresAt <= observedAt)
                            .ExecuteUpdateAsync(setters => setters
                                .SetProperty(request => request.Status, SensitiveAccessRequestStatus.Expired)
                                .SetProperty(request => request.UpdatedAt, observedAt), operationCancellationToken) == 1;
                        if (transitioned)
                        {
                            transitionedCandidates.Add(candidate);
                            var auditRecord = auditRecords[candidate.Id];
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
                    var auditIds = transitionedCandidates
                        .Select(candidate => auditRecords[candidate.Id].Id)
                        .ToArray();
                    return auditIds.Length == 0 || await db.AuditEvents
                        .AsNoTracking()
                        .CountAsync(record => auditIds.Contains(record.Id), operationCancellationToken) == auditIds.Length;
                },
                cancellationToken);
            db.ChangeTracker.AcceptAllChanges();
        }
        else
        {
            foreach (var candidate in candidates)
            {
                var request = await db.SensitiveAccessRequests
                    .SingleAsync(record => record.Id == candidate.Id, cancellationToken);
                if (request.Status != SensitiveAccessRequestStatus.Pending || request.ExpiresAt > observedAt)
                {
                    continue;
                }

                request.Status = SensitiveAccessRequestStatus.Expired;
                request.UpdatedAt = observedAt;
                transitionedCandidates.Add(candidate);
                db.AuditEvents.Add(CreateRequestExpiredAudit(candidate, observedAt));
            }

            await db.SaveChangesAsync(cancellationToken);
        }

        RecordSensitiveAccessLifecycle("request_expired", transitionedCandidates.Count);
        return transitionedCandidates.Count;
    }

    private async Task<int> MaterializeGrantExpiriesAsync(
        DateTimeOffset observedAt,
        int batchSize,
        CancellationToken cancellationToken)
    {
        if (batchSize == 0)
        {
            return 0;
        }

        const string action = "sensitive_access.grant_expired";
        var candidates = await db.SensitiveAccessGrants
            .AsNoTracking()
            .Where(grant =>
                grant.ExpiresAt <= observedAt &&
                !db.AuditEvents.Any(audit =>
                    audit.WorkspaceId == grant.WorkspaceId &&
                    audit.Action == action &&
                    audit.SubjectType == "sensitive_access_grant" &&
                    audit.SubjectId == grant.SensitiveAccessRequestId))
            .OrderBy(grant => grant.ExpiresAt)
            .ThenBy(grant => grant.SensitiveAccessRequestId)
            .Take(batchSize)
            .Select(grant => new SensitiveAccessExpiryCandidate(
                grant.SensitiveAccessRequestId,
                grant.WorkspaceId,
                grant.OwnerUserId))
            .ToArrayAsync(cancellationToken);
        if (candidates.Length == 0)
        {
            return 0;
        }

        var materializedCount = 0;
        foreach (var candidate in candidates)
        {
            var auditId = LifecycleAuditId(action, candidate.WorkspaceId, candidate.Id);
            if (await db.AuditEvents.AsNoTracking().AnyAsync(
                audit => audit.Id == auditId,
                cancellationToken))
            {
                continue;
            }

            var auditRecord = AuditEventFactory.ForWorkspace(
                candidate.WorkspaceId,
                actorUserId: null,
                actorKind: "system",
                actor: "luthn-sensitive-access-expiry",
                action,
                subjectId: candidate.Id,
                payloadClass: "metadata-only",
                redactionState: "bounded-grant-expired",
                occurredAt: observedAt,
                subjectType: "sensitive_access_grant",
                outcome: "expired",
                id: auditId);
            db.AuditEvents.Add(auditRecord);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                materializedCount++;
            }
            catch (DbUpdateException)
            {
                db.Entry(auditRecord).State = EntityState.Detached;
                if (!await db.AuditEvents.AsNoTracking().AnyAsync(
                    audit => audit.Id == auditId,
                    cancellationToken))
                {
                    throw;
                }
            }
        }

        RecordSensitiveAccessLifecycle("grant_expired", materializedCount);
        return materializedCount;
    }

    private static AuditEventRecord CreateRequestExpiredAudit(
        SensitiveAccessExpiryCandidate candidate,
        DateTimeOffset observedAt) =>
        AuditEventFactory.ForWorkspace(
            candidate.WorkspaceId,
            actorUserId: null,
            actorKind: "system",
            actor: "luthn-sensitive-access-expiry",
            action: "sensitive_access.expired",
            subjectId: candidate.Id,
            payloadClass: "metadata-only",
            redactionState: "expired-no-output",
            occurredAt: observedAt,
            subjectType: "sensitive_access_request",
            outcome: "expired",
            id: LifecycleAuditId("sensitive_access.expired", candidate.WorkspaceId, candidate.Id));

    private void RecordSensitiveAccessLifecycle(string eventName, int count)
    {
        for (var index = 0; index < count; index++)
        {
            metrics.RecordSensitiveAccessLifecycle(eventName);
        }
    }

    private async Task<SensitiveAccessResolutionCandidate?> ResolveForCreateAsync(
        SensitiveAccessReferenceIdentity reference,
        string sessionId,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken)
    {
        var requests = await db.SensitiveAccessRequests
            .AsNoTracking()
            .Where(record =>
                record.SensitiveRecordReferenceId == reference.Id &&
                record.WorkspaceId == reference.WorkspaceId &&
                record.OwnerUserId == reference.OwnerUserId)
            .OrderByDescending(record => record.UpdatedAt)
            .ThenByDescending(record => record.CreatedAt)
            .ThenByDescending(record => record.Id)
            .Select(ToResolutionCandidateExpression())
            .ToArrayAsync(cancellationToken);

        var activeGrant = requests.FirstOrDefault(candidate =>
            ResolveStatusCode(candidate, observedAt) == SensitiveAccessStatusCodes.GrantActive);
        if (activeGrant is not null)
        {
            return activeGrant;
        }

        var pending = requests.FirstOrDefault(candidate =>
            ResolveStatusCode(candidate, observedAt) == SensitiveAccessStatusCodes.RequestPending);
        if (pending is not null)
        {
            return pending;
        }

        return requests.FirstOrDefault(candidate =>
            string.Equals(candidate.SessionId, sessionId, StringComparison.Ordinal));
    }

    private Task<SensitiveAccessResolutionCandidate?> ReadResolutionCandidateAsync(
        string id,
        LuthnRequestPrincipal principal,
        CancellationToken cancellationToken) =>
        db.SensitiveAccessRequests
            .AsNoTracking()
            .Where(record =>
                record.Id == id &&
                record.WorkspaceId == principal.WorkspaceId &&
                record.OwnerUserId == principal.UserId)
            .Select(ToResolutionCandidateExpression())
            .SingleOrDefaultAsync(cancellationToken);

    private static Expression<Func<SensitiveAccessRequestRecord, SensitiveAccessResolutionCandidate>>
        ToResolutionCandidateExpression() =>
        record => new SensitiveAccessResolutionCandidate(
            record.Id,
            record.SensitiveRecordReferenceId,
            record.Status,
            record.RequestedBy,
            record.SessionId,
            record.CreatedAt,
            record.ExpiresAt,
            record.UpdatedAt,
            record.DecidedBy,
            record.DecidedAt,
            record.RedactedSummary != "",
            record.SensitiveRecordReference == null
                ? null
                : record.SensitiveRecordReference.ExpiresAt,
            record.Grant == null ? null : record.Grant.StartsAt,
            record.Grant == null ? null : record.Grant.ExpiresAt,
            record.Grant == null ? null : record.Grant.MaximumSuccessfulReads,
            record.Grant == null ? null : record.Grant.SuccessfulReadCount);

    private static string ResolveStatusCode(
        SensitiveAccessResolutionCandidate request,
        DateTimeOffset observedAt)
    {
        if (request.ReferenceExpiresAt is DateTimeOffset referenceExpiresAt &&
            referenceExpiresAt <= observedAt)
        {
            return request.Status == SensitiveAccessRequestStatus.Pending
                ? SensitiveAccessStatusCodes.RequestExpired
                : SensitiveAccessStatusCodes.GrantExpired;
        }

        if (request.Status == SensitiveAccessRequestStatus.Pending)
        {
            return request.RequestExpiresAt > observedAt
                ? SensitiveAccessStatusCodes.RequestPending
                : SensitiveAccessStatusCodes.RequestExpired;
        }

        if (request.Status == SensitiveAccessRequestStatus.Denied)
        {
            return SensitiveAccessStatusCodes.RequestDenied;
        }

        if (request.Status == SensitiveAccessRequestStatus.Expired)
        {
            return SensitiveAccessStatusCodes.RequestExpired;
        }

        if (request.MaximumSuccessfulReads is int maximumSuccessfulReads &&
            request.SuccessfulReadCount is int successfulReadCount &&
            successfulReadCount >= maximumSuccessfulReads)
        {
            return SensitiveAccessStatusCodes.GrantConsumed;
        }

        if (request.GrantStartsAt is DateTimeOffset startsAt &&
            request.GrantExpiresAt is DateTimeOffset expiresAt &&
            request.MaximumSuccessfulReads is int maxReads &&
            request.SuccessfulReadCount is int readCount &&
            startsAt <= observedAt &&
            expiresAt > observedAt &&
            readCount < maxReads)
        {
            return SensitiveAccessStatusCodes.GrantActive;
        }

        return SensitiveAccessStatusCodes.GrantExpired;
    }

    private static int? RemainingReads(SensitiveAccessResolutionCandidate request) =>
        request.MaximumSuccessfulReads is int maximumSuccessfulReads &&
        request.SuccessfulReadCount is int successfulReadCount
            ? Math.Max(0, maximumSuccessfulReads - successfulReadCount)
            : null;

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
        bool redactedOutputAvailable,
        string statusCode,
        SensitiveAccessGrantRecord? grant = null) =>
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
            redactedOutputAvailable)
        {
            StatusCode = statusCode,
            RequestExpiresAt = request.ExpiresAt,
            GrantExpiresAt = grant?.ExpiresAt,
            RemainingReads = grant is null
                ? null
                : Math.Max(0, grant.MaximumSuccessfulReads - grant.SuccessfulReadCount),
            MaxReads = grant?.MaximumSuccessfulReads
        };

    private static SensitiveAccessRequestState ToState(
        SensitiveAccessResolutionCandidate request,
        DateTimeOffset observedAt)
    {
        var statusCode = ResolveStatusCode(request, observedAt);
        return new SensitiveAccessRequestState(
            request.Id,
            request.SensitiveReferenceId,
            request.Status,
            request.RequestedBy,
            request.SessionId,
            request.CreatedAt,
            request.RequestExpiresAt,
            request.DecidedBy,
            request.DecidedAt,
            statusCode == SensitiveAccessStatusCodes.GrantActive && request.RedactedOutputAvailable)
        {
            StatusCode = statusCode,
            RequestExpiresAt = request.RequestExpiresAt,
            GrantExpiresAt = request.GrantExpiresAt,
            RemainingReads = RemainingReads(request),
            MaxReads = request.MaximumSuccessfulReads
        };
    }

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
        string OwnerUserId,
        DateTimeOffset? ExpiresAt);

    private sealed record SensitiveAccessReferenceLifetime(DateTimeOffset? ExpiresAt);

    private sealed record SensitiveAccessOperatorRequestState(
        string Id,
        string SensitiveReferenceId,
        SensitiveAccessRequestStatus Status,
        string RequestedBy,
        string SessionId,
        string RequestReason,
        DateTimeOffset CreatedAt,
        DateTimeOffset ExpiresAt,
        DateTimeOffset UpdatedAt,
        string? DecidedBy,
        DateTimeOffset? DecidedAt,
        string WorkspaceId,
        string OwnerUserId,
        bool RedactedOutputAvailable,
        DateTimeOffset? ReferenceExpiresAt,
        DateTimeOffset? GrantStartsAt,
        DateTimeOffset? GrantExpiresAt,
        int? MaximumSuccessfulReads,
        int? SuccessfulReadCount);

    private sealed record SensitiveAccessResolutionCandidate(
        string Id,
        string SensitiveReferenceId,
        SensitiveAccessRequestStatus Status,
        string RequestedBy,
        string SessionId,
        DateTimeOffset CreatedAt,
        DateTimeOffset RequestExpiresAt,
        DateTimeOffset UpdatedAt,
        string? DecidedBy,
        DateTimeOffset? DecidedAt,
        bool RedactedOutputAvailable,
        DateTimeOffset? ReferenceExpiresAt,
        DateTimeOffset? GrantStartsAt,
        DateTimeOffset? GrantExpiresAt,
        int? MaximumSuccessfulReads,
        int? SuccessfulReadCount);

    private sealed record SensitiveAccessPermitScope(
        string RequestId,
        string WorkspaceId,
        string OwnerUserId);

    private sealed record SensitiveAccessExpiryCandidate(
        string Id,
        string WorkspaceId,
        string OwnerUserId);

    private sealed record SensitiveAccessGrantReadReservation(bool Succeeded, bool Consumed)
    {
        public static readonly SensitiveAccessGrantReadReservation Rejected = new(false, false);
    }

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
    bool RedactedOutputAvailable)
{
    public string? StatusCode { get; init; }
    public DateTimeOffset? RequestExpiresAt { get; init; }
    public DateTimeOffset? GrantExpiresAt { get; init; }
    public int? RemainingReads { get; init; }
    public int? MaxReads { get; init; }
}

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
    SensitiveAccessOperatorReferenceState Reference)
{
    public string? StatusCode { get; init; }
    public DateTimeOffset? RequestExpiresAt { get; init; }
    public DateTimeOffset? GrantExpiresAt { get; init; }
    public int? RemainingReads { get; init; }
    public int? MaxReads { get; init; }
}

internal sealed record SensitiveAccessResultState(
    string Id,
    string SensitiveReferenceId,
    SensitiveAccessRequestStatus Status,
    string? RedactedOutput)
{
    public string? StatusCode { get; init; }
    public DateTimeOffset? RequestExpiresAt { get; init; }
    public DateTimeOffset? GrantExpiresAt { get; init; }
    public int? RemainingReads { get; init; }
    public int? MaxReads { get; init; }

    public bool RedactedOutputAvailable =>
        Status == SensitiveAccessRequestStatus.Approved &&
        !string.IsNullOrWhiteSpace(RedactedOutput);
}

internal static class SensitiveAccessStatusCodes
{
    internal const string RequestCreated = "request-created";
    internal const string RequestPending = "request-pending";
    internal const string RequestDenied = "request-denied";
    internal const string RequestExpired = "request-expired";
    internal const string GrantActive = "grant-active";
    internal const string GrantExpired = "grant-expired";
    internal const string GrantConsumed = "grant-consumed";
    internal const string ResultReturned = "result-returned";
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
