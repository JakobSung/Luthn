using Luthn.Core.Classification;
using Luthn.Core.Common;
using Luthn.Core.Persistence;
using Luthn.Core.Policy;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Luthn.Host.Api;

public static class SensitiveAccessEndpoints
{
    private const int MaxRedactedOutputLength = 1000;
    private const int MaxStoredRedactedSummaryLength = 4000;
    private const int MinExpirySeconds = 60;
    private const int MaxExpirySeconds = 3600;
    private const int DefaultExpirySeconds = 600;
    private static readonly SemaphoreSlim NonRelationalDecisionLock = new(1, 1);

    public static IEndpointRouteBuilder MapSensitiveAccessRequests(this IEndpointRouteBuilder app)
    {
        var requests = app.MapGroup("/api/access-requests");

        requests.MapGet("", ListRequests)
            .RequireServiceScope(ServiceScopes.AccessDecide)
            .WithName("ListSensitiveAccessRequests");

        requests.MapPost("", CreateRequest)
            .RequireServiceScope(ServiceScopes.AccessRequest)
            .WithName("CreateSensitiveAccessRequest");

        requests.MapGet("/{id}", ReadRequest)
            .RequireServiceScope(ServiceScopes.AccessRequest)
            .WithName("ReadSensitiveAccessRequest");

        requests.MapGet("/{id}/result", ReadRequestResult)
            .RequireServiceScope(ServiceScopes.AccessRequest)
            .WithName("ReadSensitiveAccessRequestResult");

        requests.MapPost("/{id}/approve", ApproveRequest)
            .RequireServiceScope(ServiceScopes.AccessDecide)
            .WithName("ApproveSensitiveAccessRequest");

        requests.MapPost("/{id}/deny", DenyRequest)
            .RequireServiceScope(ServiceScopes.AccessDecide)
            .WithName("DenySensitiveAccessRequest");

        return app;
    }

    public static async Task<Results<Ok<SensitiveAccessRequestsResponse>, BadRequest<ProblemDetails>>> ListRequests(
        string? status,
        int? limit,
        LuthnDbContext db,
        CancellationToken cancellationToken)
    {
        var take = Math.Clamp(limit ?? 25, 1, 100);
        await ExpirePendingRequestsAsync(db, requestId: null, cancellationToken);
        var query = db.SensitiveAccessRequests.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!Enum.TryParse<SensitiveAccessRequestStatus>(
                status.Trim(),
                ignoreCase: true,
                out var parsedStatus))
            {
                return TypedResults.BadRequest(new ProblemDetails
                {
                    Title = "Invalid sensitive access request filter.",
                    Detail = "status must be Pending, Approved, Denied, or Expired."
                });
            }

            query = query.Where(record => record.Status == parsedStatus);
        }

        var requests = await query
            .OrderByDescending(record => record.UpdatedAt)
            .ThenByDescending(record => record.CreatedAt)
            .Take(take)
            .Select(record => new SensitiveAccessRequestListItem(
                record,
                db.SensitiveRecordReferences
                    .Where(reference => reference.Id == record.SensitiveRecordReferenceId)
                    .Select(reference => reference.RedactedSummary != "")
                    .SingleOrDefault()))
            .ToArrayAsync(cancellationToken);

        return TypedResults.Ok(new SensitiveAccessRequestsResponse(
            requests.Select(item => ToResponse(item.Request, item.RedactedOutputAvailable)).ToArray()));
    }

    public static async Task<Results<
        Created<SensitiveAccessRequestResponse>,
        BadRequest<ProblemDetails>,
        NotFound>> CreateRequest(
        SensitiveAccessRequestCreateRequest request,
        LuthnDbContext db,
        HttpContext httpContext,
        IOperationalMetrics metrics,
        CancellationToken cancellationToken)
    {
        var validationError = ValidateCreateRequest(request);
        if (validationError is not null)
        {
            return TypedResults.BadRequest(validationError);
        }

        var reference = await db.SensitiveRecordReferences
            .AsNoTracking()
            .Where(record => record.Id == request.SensitiveReferenceId.Trim())
            .Select(record => new { record.Id })
            .SingleOrDefaultAsync(cancellationToken);
        if (reference is null)
        {
            return TypedResults.NotFound();
        }

        var observedAt = DateTimeOffset.UtcNow;
        var sessionId = string.IsNullOrWhiteSpace(request.SessionId)
            ? $"legacy-{Guid.NewGuid():N}"
            : request.SessionId.Trim();
        var expiresInSeconds = request.ExpiresInSeconds == 0
            ? DefaultExpirySeconds
            : request.ExpiresInSeconds;
        var actor = ServiceTokenAuthorization.GetActor(httpContext);
        var accessRequest = new SensitiveAccessRequestRecord
        {
            Id = $"access-{Guid.NewGuid():N}",
            SensitiveRecordReferenceId = reference.Id,
            RequestedBy = actor,
            SessionId = sessionId,
            RequestReason = request.Reason.Trim(),
            Status = SensitiveAccessRequestStatus.Pending,
            CreatedAt = observedAt,
            ExpiresAt = observedAt.AddSeconds(expiresInSeconds),
            UpdatedAt = observedAt
        };

        db.SensitiveAccessRequests.Add(accessRequest);
        db.AuditEvents.Add(new AuditEventRecord
        {
            Id = $"audit-{Guid.NewGuid():N}",
            OccurredAt = observedAt,
            Actor = actor,
            Action = "sensitive_access.requested",
            SubjectId = accessRequest.Id,
            PayloadClass = "metadata-only",
            RedactionState = "sensitive-boundary-only"
        });

        await db.SaveChangesAsync(cancellationToken);
        metrics.RecordSensitiveAccessRequest();

        return TypedResults.Created(
            $"/api/access-requests/{accessRequest.Id}",
            ToResponse(accessRequest, redactedOutputAvailable: false));
    }

    public static async Task<Results<Ok<SensitiveAccessRequestResponse>, NotFound>> ReadRequest(
        string id,
        LuthnDbContext db,
        CancellationToken cancellationToken)
    {
        await ExpirePendingRequestsAsync(db, id, cancellationToken);
        var request = await db.SensitiveAccessRequests
            .SingleOrDefaultAsync(record => record.Id == id, cancellationToken);

        if (request is null)
        {
            return TypedResults.NotFound();
        }

        var redactedOutputAvailable = await HasRedactedOutputAsync(
            request.SensitiveRecordReferenceId,
            db,
            cancellationToken);
        return TypedResults.Ok(ToResponse(request, redactedOutputAvailable));
    }

    public static async Task<Results<Ok<SensitiveAccessResultResponse>, NotFound>> ReadRequestResult(
        string id,
        LuthnDbContext db,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        await ExpirePendingRequestsAsync(db, id, cancellationToken);
        var request = await db.SensitiveAccessRequests
            .SingleOrDefaultAsync(record => record.Id == id, cancellationToken);

        if (request is not null)
        {
            var safeSummary = await db.SensitiveRecordReferences
                .Where(reference => reference.Id == request.SensitiveRecordReferenceId)
                .Select(reference => reference.RedactedSummary)
                .SingleOrDefaultAsync(cancellationToken);
            var response = ToResultResponse(request, safeSummary);
            var observedAt = DateTimeOffset.UtcNow;
            db.AuditEvents.Add(new AuditEventRecord
            {
                Id = $"audit-{Guid.NewGuid():N}",
                OccurredAt = observedAt,
                Actor = ServiceTokenAuthorization.GetActor(httpContext),
                Action = "sensitive_access.result_read",
                SubjectId = request.Id,
                PayloadClass = response.PayloadClass,
                RedactionState = response.RedactionState
            });
            await db.SaveChangesAsync(cancellationToken);

            return TypedResults.Ok(response);
        }

        return TypedResults.NotFound();
    }

    public static Task<Results<
        Ok<SensitiveAccessRequestResponse>,
        BadRequest<ProblemDetails>,
        NotFound,
        ProblemHttpResult>> ApproveRequest(
        string id,
        SensitiveAccessDecisionRequest request,
        IContentClassifier classifier,
        IPolicyEngine policyEngine,
        LuthnDbContext db,
        HttpContext httpContext,
        IOperationalMetrics metrics,
        CancellationToken cancellationToken) =>
        DecideRequest(
            id,
            request,
            classifier,
            policyEngine,
            SensitiveAccessRequestStatus.Approved,
            SensitiveAccessDecisionKind.Approved,
            "sensitive_access.approved",
            "approved-redacted-output-unavailable",
            db,
            httpContext,
            metrics,
            cancellationToken);

    public static Task<Results<
        Ok<SensitiveAccessRequestResponse>,
        BadRequest<ProblemDetails>,
        NotFound,
        ProblemHttpResult>> DenyRequest(
        string id,
        SensitiveAccessDecisionRequest request,
        LuthnDbContext db,
        HttpContext httpContext,
        IOperationalMetrics metrics,
        CancellationToken cancellationToken) =>
        DecideRequest(
            id,
            request,
            null,
            null,
            SensitiveAccessRequestStatus.Denied,
            SensitiveAccessDecisionKind.Denied,
            "sensitive_access.denied",
            "denied-no-output",
            db,
            httpContext,
            metrics,
            cancellationToken);

    private static async Task<Results<
        Ok<SensitiveAccessRequestResponse>,
        BadRequest<ProblemDetails>,
        NotFound,
        ProblemHttpResult>> DecideRequest(
        string id,
        SensitiveAccessDecisionRequest request,
        IContentClassifier? classifier,
        IPolicyEngine? policyEngine,
        SensitiveAccessRequestStatus status,
        SensitiveAccessDecisionKind decisionKind,
        string auditAction,
        string redactionState,
        LuthnDbContext db,
        HttpContext httpContext,
        IOperationalMetrics metrics,
        CancellationToken cancellationToken)
    {
        await ExpirePendingRequestsAsync(db, id, cancellationToken);
        var accessRequest = await db.SensitiveAccessRequests
            .SingleOrDefaultAsync(record => record.Id == id, cancellationToken);
        if (accessRequest is null)
        {
            return TypedResults.NotFound();
        }

        if (accessRequest.Status != SensitiveAccessRequestStatus.Pending)
        {
            return TypedResults.BadRequest(new ProblemDetails
            {
                Title = "Sensitive access request is already decided.",
                Detail = "Only non-expired pending sensitive access requests can be approved or denied."
            });
        }

        var decisionError = ValidateDecisionRequest(request);
        if (decisionError is not null)
        {
            return TypedResults.BadRequest(decisionError);
        }

        var actor = ServiceTokenAuthorization.GetActor(httpContext);
        ValidatedRedactedSummary redactedSummary;
        try
        {
            redactedSummary = await ValidateDecisionRedactedSummaryAsync(
                accessRequest,
                request,
                classifier,
                policyEngine,
                status,
                cancellationToken);
        }
        catch (ClassificationProviderException error)
        {
            return ApiProblems.ClassificationProviderUnavailable(error);
        }
        if (redactedSummary.Error is not null)
        {
            db.AuditEvents.Add(new AuditEventRecord
            {
                Id = $"audit-{Guid.NewGuid():N}",
                OccurredAt = DateTimeOffset.UtcNow,
                Actor = actor,
                Action = "sensitive_access.redacted_summary_rejected",
                SubjectId = accessRequest.Id,
                PayloadClass = "metadata-only",
                RedactionState = "rejected-no-output"
            });
            await db.SaveChangesAsync(cancellationToken);
            return TypedResults.BadRequest(redactedSummary.Error);
        }

        var isRelational = db.Database.IsRelational();
        if (!isRelational)
        {
            await NonRelationalDecisionLock.WaitAsync(cancellationToken);
        }

        var observedAt = DateTimeOffset.UtcNow;
        try
        {
            await using IDbContextTransaction? transaction = isRelational
                ? await db.Database.BeginTransactionAsync(cancellationToken)
                : null;
            var transitioned = false;
            if (isRelational)
            {
                db.Entry(accessRequest).State = EntityState.Detached;
                transitioned = await db.SensitiveAccessRequests
                    .Where(record =>
                        record.Id == id &&
                        record.Status == SensitiveAccessRequestStatus.Pending &&
                        record.ExpiresAt > observedAt)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(record => record.Status, status)
                        .SetProperty(record => record.DecidedBy, actor)
                        .SetProperty(record => record.DecidedAt, observedAt)
                        .SetProperty(record => record.UpdatedAt, observedAt), cancellationToken) == 1;
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
                    accessRequest.UpdatedAt = observedAt;
                }
            }

            if (!transitioned)
            {
                if (transaction is not null)
                {
                    await transaction.RollbackAsync(cancellationToken);
                }
                await ExpirePendingRequestsAsync(db, id, cancellationToken);
                return TypedResults.BadRequest(new ProblemDetails
                {
                    Title = "Sensitive access request is already decided.",
                    Detail = "Only non-expired pending sensitive access requests can be approved or denied."
                });
            }

            db.SensitiveAccessDecisions.Add(new SensitiveAccessDecisionRecord
            {
                Id = $"decision-{Guid.NewGuid():N}",
                SensitiveAccessRequestId = accessRequest.Id,
                Decision = decisionKind,
                DecidedBy = actor,
                DecisionReason = request.Reason?.Trim() ?? "",
                DecidedAt = observedAt,
                PayloadClass = "metadata-only",
                RedactionState = redactionState
            });
            if (redactedSummary.Value is not null)
            {
                var reference = await db.SensitiveRecordReferences
                    .SingleAsync(record => record.Id == accessRequest.SensitiveRecordReferenceId, cancellationToken);
                reference.RedactedSummary = redactedSummary.Value;
            }
            db.AuditEvents.Add(new AuditEventRecord
            {
                Id = $"audit-{Guid.NewGuid():N}",
                OccurredAt = observedAt,
                Actor = actor,
                Action = auditAction,
                SubjectId = accessRequest.Id,
                PayloadClass = "metadata-only",
                RedactionState = redactionState
            });

            await db.SaveChangesAsync(cancellationToken);
            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken);
            }
        }
        finally
        {
            if (!isRelational)
            {
                NonRelationalDecisionLock.Release();
            }
        }
        metrics.RecordSensitiveAccessDecision(decisionKind == SensitiveAccessDecisionKind.Approved ? "approved" : "denied");

        accessRequest.Status = status;
        accessRequest.DecidedBy = actor;
        accessRequest.DecidedAt = observedAt;
        accessRequest.UpdatedAt = observedAt;

        var redactedOutputAvailable = status == SensitiveAccessRequestStatus.Approved &&
            await HasRedactedOutputAsync(
                accessRequest.SensitiveRecordReferenceId,
                db,
                cancellationToken);

        return TypedResults.Ok(ToResponse(accessRequest, redactedOutputAvailable));
    }

    private static ProblemDetails? ValidateCreateRequest(
        SensitiveAccessRequestCreateRequest request)
    {
        var title = "Invalid sensitive access request.";
        var sensitiveReferenceIdError = ApiValidation.ValidateRequiredText(
            request.SensitiveReferenceId,
            "sensitiveReferenceId",
            ApiValidation.PublicRecordIdMaxLength,
            title);
        if (sensitiveReferenceIdError is not null)
        {
            return sensitiveReferenceIdError;
        }

        var reasonError = ApiValidation.ValidateRequiredText(
            request.Reason,
            "reason",
            ApiValidation.ReasonMaxLength,
            title);
        if (reasonError is not null)
        {
            return reasonError;
        }

        if (!string.IsNullOrWhiteSpace(request.SessionId))
        {
            var sessionIdError = ApiValidation.ValidateRequiredText(
                request.SessionId,
                "sessionId",
                ApiValidation.PublicRecordIdMaxLength,
                title);
            if (sessionIdError is not null)
            {
                return sessionIdError;
            }
        }

        if (request.ExpiresInSeconds != 0 &&
            (request.ExpiresInSeconds < MinExpirySeconds || request.ExpiresInSeconds > MaxExpirySeconds))
        {
            return CreateValidationProblem($"expiresInSeconds must be between {MinExpirySeconds} and {MaxExpirySeconds}.");
        }

        return null;
    }

    private static ProblemDetails? ValidateDecisionRequest(
        SensitiveAccessDecisionRequest request)
    {
        if (request.Reason is not null &&
            request.Reason.Trim().Length > ApiValidation.ReasonMaxLength)
        {
            return CreateValidationProblem(
                $"reason must be {ApiValidation.ReasonMaxLength} characters or fewer.");
        }

        return null;
    }

    private static async Task<ValidatedRedactedSummary> ValidateDecisionRedactedSummaryAsync(
        SensitiveAccessRequestRecord accessRequest,
        SensitiveAccessDecisionRequest request,
        IContentClassifier? classifier,
        IPolicyEngine? policyEngine,
        SensitiveAccessRequestStatus status,
        CancellationToken cancellationToken)
    {
        if (status != SensitiveAccessRequestStatus.Approved ||
            string.IsNullOrWhiteSpace(request.RedactedSummary))
        {
            return new(null, null);
        }

        if (classifier is null || policyEngine is null)
        {
            return new(null, CreateValidationProblem("redactedSummary can only be provided for approval decisions."));
        }

        var candidate = request.RedactedSummary.Trim();
        if (candidate.Length > MaxStoredRedactedSummaryLength)
        {
            return new(null, CreateValidationProblem(
                $"redactedSummary must be {MaxStoredRedactedSummaryLength} characters or fewer."));
        }

        var classification = await classifier.ClassifyAsync(
            new PublicRecordId($"{accessRequest.Id}-redacted-summary"),
            candidate,
            "redacted-summary",
            cancellationToken);
        var decision = policyEngine.Decide(classification);
        if (classification.ContainsSensitiveMaterial || !decision.AllowsAgentContext)
        {
            return new(null, CreateValidationProblem("redactedSummary must classify as public agent-safe content."));
        }

        return new(candidate, null);
    }

    private static ProblemDetails CreateValidationProblem(string detail) =>
        ApiValidation.CreateProblem("Invalid sensitive access request.", detail);

    private static SensitiveAccessRequestResponse ToResponse(
        SensitiveAccessRequestRecord request,
        bool redactedOutputAvailable) =>
        new(
            request.Id,
            request.SensitiveRecordReferenceId,
            request.Status.ToString(),
            request.RequestedBy,
            request.SessionId,
            request.CreatedAt,
            request.ExpiresAt,
            request.DecidedBy,
            request.DecidedAt,
            RedactedOutputAvailable: request.Status == SensitiveAccessRequestStatus.Approved &&
                redactedOutputAvailable,
            OutputPolicy: ToOutputPolicy(
                request.Status,
                request.Status == SensitiveAccessRequestStatus.Approved && redactedOutputAvailable));

    private static SensitiveAccessResultResponse ToResultResponse(
        SensitiveAccessRequestRecord request,
        string? safeSummary)
    {
        var redactedOutputAvailable = request.Status == SensitiveAccessRequestStatus.Approved &&
            !string.IsNullOrWhiteSpace(safeSummary);
        var outputPolicy = ToOutputPolicy(request.Status, redactedOutputAvailable);
        var redactedOutput = redactedOutputAvailable
            ? BoundRedactedOutput(safeSummary!)
            : null;
        var payloadClass = redactedOutputAvailable ? "redacted-output" : "metadata-only";
        IReadOnlyList<string> reasons = request.Status switch
        {
            SensitiveAccessRequestStatus.Approved when redactedOutputAvailable =>
                ["Approved limited output is sourced from a public-safe redacted summary."],
            SensitiveAccessRequestStatus.Approved =>
                ["Approval is recorded, but no public-safe redacted summary is available."],
            SensitiveAccessRequestStatus.Denied =>
                ["The sensitive access request was denied; no output is available."],
            SensitiveAccessRequestStatus.Expired =>
                ["The sensitive access request expired before a decision; no output is available."],
            _ =>
                ["The sensitive access request is pending decision; no output is available."]
        };

        return new SensitiveAccessResultResponse(
            request.Id,
            request.SensitiveRecordReferenceId,
            request.Status.ToString(),
            outputPolicy,
            redactedOutputAvailable,
            redactedOutput,
            payloadClass,
            outputPolicy,
            reasons);
    }

    private static string ToOutputPolicy(
        SensitiveAccessRequestStatus status,
        bool redactedOutputAvailable) =>
        status switch
        {
            SensitiveAccessRequestStatus.Approved when redactedOutputAvailable =>
                "approved-redacted-output-available",
            SensitiveAccessRequestStatus.Approved =>
                "approved-redacted-output-unavailable",
            SensitiveAccessRequestStatus.Denied => "denied-no-output",
            SensitiveAccessRequestStatus.Expired => "expired-no-output",
            _ => "pending-approval"
        };

    private static string BoundRedactedOutput(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length <= MaxRedactedOutputLength)
        {
            return trimmed;
        }

        var end = MaxRedactedOutputLength;
        if (end < trimmed.Length &&
            char.IsHighSurrogate(trimmed[end - 1]) &&
            char.IsLowSurrogate(trimmed[end]))
        {
            end--;
        }

        return trimmed[..end];
    }

    private static Task<bool> HasRedactedOutputAsync(
        string sensitiveReferenceId,
        LuthnDbContext db,
        CancellationToken cancellationToken) =>
        db.SensitiveRecordReferences
            .Where(reference => reference.Id == sensitiveReferenceId)
            .Select(reference => reference.RedactedSummary != "")
            .SingleOrDefaultAsync(cancellationToken);

    private static async Task ExpirePendingRequestsAsync(
        LuthnDbContext db,
        string? requestId,
        CancellationToken cancellationToken)
    {
        var observedAt = DateTimeOffset.UtcNow;
        var candidates = await db.SensitiveAccessRequests
            .AsNoTracking()
            .Where(request =>
                request.Status == SensitiveAccessRequestStatus.Pending &&
                request.ExpiresAt <= observedAt &&
                (requestId == null || request.Id == requestId))
            .Select(request => request.Id)
            .ToArrayAsync(cancellationToken);
        if (candidates.Length == 0)
        {
            return;
        }

        await using IDbContextTransaction? transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(cancellationToken)
            : null;

        foreach (var candidateId in candidates)
        {
            bool transitioned;
            if (db.Database.IsRelational())
            {
                transitioned = await db.SensitiveAccessRequests
                    .Where(request =>
                        request.Id == candidateId &&
                        request.Status == SensitiveAccessRequestStatus.Pending &&
                        request.ExpiresAt <= observedAt)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(request => request.Status, SensitiveAccessRequestStatus.Expired)
                        .SetProperty(request => request.UpdatedAt, observedAt), cancellationToken) == 1;
            }
            else
            {
                var request = await db.SensitiveAccessRequests
                    .SingleAsync(record => record.Id == candidateId, cancellationToken);
                transitioned = request.Status == SensitiveAccessRequestStatus.Pending &&
                    request.ExpiresAt <= observedAt;
                if (transitioned)
                {
                    request.Status = SensitiveAccessRequestStatus.Expired;
                    request.UpdatedAt = observedAt;
                }
            }

            if (transitioned)
            {
                db.AuditEvents.Add(new AuditEventRecord
                {
                    Id = $"audit-{Guid.NewGuid():N}",
                    OccurredAt = observedAt,
                    Actor = "local-expiry",
                    Action = "sensitive_access.expired",
                    SubjectId = candidateId,
                    PayloadClass = "metadata-only",
                    RedactionState = "expired-no-output"
                });
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }
    }

    private sealed record SensitiveAccessRequestListItem(
        SensitiveAccessRequestRecord Request,
        bool RedactedOutputAvailable);

    private sealed record SensitiveAccessResultData(
        SensitiveAccessRequestRecord Request,
        string? RedactedOutput);

    private sealed record ValidatedRedactedSummary(
        string? Value,
        ProblemDetails? Error);
}

public sealed record SensitiveAccessRequestCreateRequest
{
    public string SensitiveReferenceId { get; init; } = "";
    public string Reason { get; init; } = "";
    public string SessionId { get; init; } = "";
    public int ExpiresInSeconds { get; init; }
}

public sealed record SensitiveAccessDecisionRequest
{
    public string? Reason { get; init; }
    public string? RedactedSummary { get; init; }
}

public sealed record SensitiveAccessRequestsResponse(
    IReadOnlyList<SensitiveAccessRequestResponse> Requests);

public sealed record SensitiveAccessRequestResponse(
    string Id,
    string SensitiveReferenceId,
    string Status,
    string RequestedBy,
    string SessionId,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    string? DecidedBy,
    DateTimeOffset? DecidedAt,
    bool RedactedOutputAvailable,
    string OutputPolicy);

public sealed record SensitiveAccessResultResponse(
    string Id,
    string SensitiveReferenceId,
    string Status,
    string OutputPolicy,
    bool RedactedOutputAvailable,
    string? RedactedOutput,
    string PayloadClass,
    string RedactionState,
    IReadOnlyList<string> Reasons);
