using Luthn.Core.Classification;
using Luthn.Core.Persistence;
using Luthn.Core.Policy;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Luthn.Host.Api;

public static class SensitiveAccessEndpoints
{
    private const int MinExpirySeconds = 60;
    private const int MaxExpirySeconds = 3600;

    public static IEndpointRouteBuilder MapSensitiveAccessRequests(this IEndpointRouteBuilder app)
    {
        var requests = app.MapGroup("/api/access-requests");

        requests.MapGet("", ListRequestsEndpoint)
            .RequireServiceScope(ServiceScopes.AccessReview)
            .WithName("ListSensitiveAccessRequests");

        requests.MapPost("", CreateRequestEndpoint)
            .RequireServiceScope(ServiceScopes.AccessRequest)
            .WithName("CreateSensitiveAccessRequest");

        requests.MapGet("/{id}", ReadRequestEndpoint)
            .RequireServiceScope(ServiceScopes.AccessRequest)
            .WithName("ReadSensitiveAccessRequest");

        requests.MapGet("/{id}/operator-detail", ReadOperatorDetailEndpoint)
            .RequireServiceScope(ServiceScopes.AccessReview)
            .WithName("ReadSensitiveAccessOperatorDetail");

        requests.MapGet("/{id}/result", ReadRequestResultEndpoint)
            .RequireServiceScope(ServiceScopes.AccessRequest)
            .WithName("ReadSensitiveAccessRequestResult");

        requests.MapPost("/{id}/approve", ApproveRequestEndpoint)
            .RequireServiceScope(ServiceScopes.AccessDecide)
            .WithName("ApproveSensitiveAccessRequest");

        requests.MapPost("/{id}/deny", DenyRequestEndpoint)
            .RequireServiceScope(ServiceScopes.AccessDecide)
            .WithName("DenySensitiveAccessRequest");

        return app;
    }

    private static async Task<Results<Ok<SensitiveAccessRequestsResponse>, BadRequest<ProblemDetails>>> ListRequestsEndpoint(
        string? status,
        int? limit,
        ISensitiveAccessWorkflow workflow,
        HttpContext httpContext,
        CancellationToken cancellationToken) =>
        await ListRequestsCore(status, limit, workflow, httpContext, cancellationToken);

    private static async Task<Results<
        Created<SensitiveAccessRequestResponse>,
        BadRequest<ProblemDetails>,
        NotFound>> CreateRequestEndpoint(
        SensitiveAccessRequestCreateRequest request,
        ISensitiveAccessWorkflow workflow,
        HttpContext httpContext,
        CancellationToken cancellationToken) =>
        await CreateRequestCore(request, workflow, httpContext, cancellationToken);

    private static async Task<Results<Ok<SensitiveAccessRequestResponse>, NotFound>> ReadRequestEndpoint(
        string id,
        ISensitiveAccessWorkflow workflow,
        HttpContext httpContext,
        CancellationToken cancellationToken) =>
        await ReadRequestCore(id, workflow, httpContext, cancellationToken);

    private static async Task<Results<Ok<SensitiveAccessOperatorDetailResponse>, NotFound>> ReadOperatorDetailEndpoint(
        string id,
        ISensitiveAccessWorkflow workflow,
        HttpContext httpContext,
        CancellationToken cancellationToken) =>
        await ReadOperatorDetailCore(id, workflow, httpContext, cancellationToken);

    private static async Task<Results<Ok<SensitiveAccessResultResponse>, NotFound>> ReadRequestResultEndpoint(
        string id,
        ISensitiveAccessWorkflow workflow,
        HttpContext httpContext,
        CancellationToken cancellationToken) =>
        await ReadRequestResultCore(id, workflow, httpContext, cancellationToken);

    private static Task<Results<
        Ok<SensitiveAccessRequestResponse>,
        BadRequest<ProblemDetails>,
        NotFound,
        ProblemHttpResult>> ApproveRequestEndpoint(
        string id,
        SensitiveAccessDecisionRequest request,
        ISensitiveAccessWorkflow workflow,
        HttpContext httpContext,
        CancellationToken cancellationToken) =>
        DecideRequestCore(
            id,
            request,
            SensitiveAccessRequestStatus.Approved,
            workflow,
            httpContext,
            cancellationToken);

    private static Task<Results<
        Ok<SensitiveAccessRequestResponse>,
        BadRequest<ProblemDetails>,
        NotFound,
        ProblemHttpResult>> DenyRequestEndpoint(
        string id,
        SensitiveAccessDecisionRequest request,
        ISensitiveAccessWorkflow workflow,
        HttpContext httpContext,
        CancellationToken cancellationToken) =>
        DecideRequestCore(
            id,
            request,
            SensitiveAccessRequestStatus.Denied,
            workflow,
            httpContext,
            cancellationToken);

    private static async Task<Results<Ok<SensitiveAccessRequestsResponse>, BadRequest<ProblemDetails>>> ListRequestsCore(
        string? status,
        int? limit,
        ISensitiveAccessWorkflow workflow,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var result = await workflow.ListRequestsAsync(
            status,
            Math.Clamp(limit ?? 25, 1, 100),
            ServiceTokenAuthorization.GetPrincipal(httpContext),
            cancellationToken);
        if (result.ValidationError is not null)
        {
            return TypedResults.BadRequest(new ProblemDetails
            {
                Title = "Invalid sensitive access request filter.",
                Detail = result.ValidationError
            });
        }

        return TypedResults.Ok(new SensitiveAccessRequestsResponse(
            result.Requests.Select(SensitiveAccessEndpointMapping.ToResponse).ToArray()));
    }

    private static async Task<Results<
        Created<SensitiveAccessRequestResponse>,
        BadRequest<ProblemDetails>,
        NotFound>> CreateRequestCore(
        SensitiveAccessRequestCreateRequest request,
        ISensitiveAccessWorkflow workflow,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var validationError = ValidateCreateRequest(request);
        if (validationError is not null)
        {
            return TypedResults.BadRequest(validationError);
        }

        var created = await workflow.CreateRequestAsync(
            request,
            ServiceTokenAuthorization.GetPrincipal(httpContext),
            ServiceTokenAuthorization.GetActor(httpContext),
            cancellationToken);
        if (created is null)
        {
            return TypedResults.NotFound();
        }

        return TypedResults.Created(
            $"/api/access-requests/{created.Id}",
            SensitiveAccessEndpointMapping.ToResponse(created));
    }

    private static async Task<Results<Ok<SensitiveAccessRequestResponse>, NotFound>> ReadRequestCore(
        string id,
        ISensitiveAccessWorkflow workflow,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var request = await workflow.ReadRequestAsync(
            id,
            ServiceTokenAuthorization.GetPrincipal(httpContext),
            cancellationToken);
        return request is null
            ? TypedResults.NotFound()
            : TypedResults.Ok(SensitiveAccessEndpointMapping.ToResponse(request));
    }

    private static async Task<Results<Ok<SensitiveAccessOperatorDetailResponse>, NotFound>> ReadOperatorDetailCore(
        string id,
        ISensitiveAccessWorkflow workflow,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        httpContext.Response.Headers.CacheControl = "no-store";
        var detail = await workflow.ReadOperatorDetailAsync(
            id,
            ServiceTokenAuthorization.GetPrincipal(httpContext),
            ServiceTokenAuthorization.GetActor(httpContext),
            cancellationToken);
        return detail is null
            ? TypedResults.NotFound()
            : TypedResults.Ok(SensitiveAccessEndpointMapping.ToResponse(detail));
    }

    private static async Task<Results<Ok<SensitiveAccessResultResponse>, NotFound>> ReadRequestResultCore(
        string id,
        ISensitiveAccessWorkflow workflow,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var result = await workflow.ReadRequestResultAsync(
            id,
            ServiceTokenAuthorization.GetPrincipal(httpContext),
            ServiceTokenAuthorization.GetActor(httpContext),
            cancellationToken);
        return result is null
            ? TypedResults.NotFound()
            : TypedResults.Ok(SensitiveAccessEndpointMapping.ToResponse(result));
    }

    private static async Task<Results<
        Ok<SensitiveAccessRequestResponse>,
        BadRequest<ProblemDetails>,
        NotFound,
        ProblemHttpResult>> DecideRequestCore(
        string id,
        SensitiveAccessDecisionRequest request,
        SensitiveAccessRequestStatus status,
        ISensitiveAccessWorkflow workflow,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        SensitiveAccessDecisionResult result;
        try
        {
            result = await workflow.DecideRequestAsync(
                id,
                request,
                status,
                ServiceTokenAuthorization.GetPrincipal(httpContext),
                ServiceTokenAuthorization.GetActor(httpContext),
                cancellationToken);
        }
        catch (ClassificationProviderException error)
        {
            return ApiProblems.ClassificationProviderUnavailable(error);
        }

        return result.Outcome switch
        {
            SensitiveAccessDecisionOutcome.Succeeded =>
                TypedResults.Ok(SensitiveAccessEndpointMapping.ToResponse(result.Request!)),
            SensitiveAccessDecisionOutcome.NotFound => TypedResults.NotFound(),
            SensitiveAccessDecisionOutcome.AlreadyDecided => TypedResults.BadRequest(new ProblemDetails
            {
                Title = "Sensitive access request is already decided.",
                Detail = "Only non-expired pending sensitive access requests can be approved or denied."
            }),
            _ => TypedResults.BadRequest(CreateValidationProblem(result.ValidationError!))
        };
    }

    public static Task<Results<Ok<SensitiveAccessRequestsResponse>, BadRequest<ProblemDetails>>> ListRequests(
        string? status,
        int? limit,
        LuthnDbContext db,
        HttpContext httpContext,
        CancellationToken cancellationToken) =>
        ListRequestsCore(
            status,
            limit,
            CompatibilityWorkflow(db),
            httpContext,
            cancellationToken);

    public static Task<Results<
        Created<SensitiveAccessRequestResponse>,
        BadRequest<ProblemDetails>,
        NotFound>> CreateRequest(
        SensitiveAccessRequestCreateRequest request,
        LuthnDbContext db,
        HttpContext httpContext,
        IOperationalMetrics metrics,
        CancellationToken cancellationToken) =>
        CreateRequestCore(
            request,
            CompatibilityWorkflow(db, metrics),
            httpContext,
            cancellationToken);

    public static Task<Results<Ok<SensitiveAccessRequestResponse>, NotFound>> ReadRequest(
        string id,
        LuthnDbContext db,
        HttpContext httpContext,
        CancellationToken cancellationToken) =>
        ReadRequestCore(id, CompatibilityWorkflow(db), httpContext, cancellationToken);

    public static Task<Results<Ok<SensitiveAccessOperatorDetailResponse>, NotFound>> ReadOperatorDetail(
        string id,
        LuthnDbContext db,
        HttpContext httpContext,
        CancellationToken cancellationToken) =>
        ReadOperatorDetailCore(id, CompatibilityWorkflow(db), httpContext, cancellationToken);

    public static Task<Results<Ok<SensitiveAccessResultResponse>, NotFound>> ReadRequestResult(
        string id,
        LuthnDbContext db,
        HttpContext httpContext,
        CancellationToken cancellationToken) =>
        ReadRequestResultCore(id, CompatibilityWorkflow(db), httpContext, cancellationToken);

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
        DecideRequestCore(
            id,
            request,
            SensitiveAccessRequestStatus.Approved,
            CompatibilityWorkflow(db, metrics, classifier, policyEngine),
            httpContext,
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
        DecideRequestCore(
            id,
            request,
            SensitiveAccessRequestStatus.Denied,
            CompatibilityWorkflow(db, metrics),
            httpContext,
            cancellationToken);

    private static SensitiveAccessWorkflow CompatibilityWorkflow(
        LuthnDbContext db,
        IOperationalMetrics? metrics = null,
        IContentClassifier? classifier = null,
        IPolicyEngine? policyEngine = null) =>
        new(db, metrics ?? NullOperationalMetrics.Instance, TimeProvider.System, classifier, policyEngine);

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

        if (request.ExpiresInSeconds is int expiresInSeconds &&
            (expiresInSeconds < MinExpirySeconds || expiresInSeconds > MaxExpirySeconds))
        {
            return CreateValidationProblem(
                $"expiresInSeconds must be between {MinExpirySeconds} and {MaxExpirySeconds}.");
        }

        return null;
    }

    private static ProblemDetails CreateValidationProblem(string detail) =>
        ApiValidation.CreateProblem("Invalid sensitive access request.", detail);
}

internal static class SensitiveAccessEndpointMapping
{
    private const int MaxRedactedOutputLength = 1000;

    internal static SensitiveAccessRequestResponse ToResponse(SensitiveAccessRequestState request) =>
        new(
            request.Id,
            request.SensitiveReferenceId,
            request.Status.ToString(),
            request.RequestedBy,
            request.SessionId,
            request.CreatedAt,
            request.ExpiresAt,
            request.DecidedBy,
            request.DecidedAt,
            RedactedOutputAvailable: request.Status == SensitiveAccessRequestStatus.Approved &&
                request.RedactedOutputAvailable,
            OutputPolicy: ToOutputPolicy(
                request.Status,
                request.Status == SensitiveAccessRequestStatus.Approved && request.RedactedOutputAvailable));

    internal static SensitiveAccessOperatorDetailResponse ToResponse(
        SensitiveAccessOperatorDetailState detail) =>
        new(
            detail.Id,
            detail.SensitiveReferenceId,
            detail.Status.ToString(),
            detail.RequestedBy,
            detail.SessionId,
            detail.RequestReason,
            detail.CreatedAt,
            detail.ExpiresAt,
            detail.Decision?.ToString(),
            detail.DecidedBy,
            detail.DecidedAt,
            detail.DecisionReason,
            RedactedOutputAvailable: detail.Status == SensitiveAccessRequestStatus.Approved &&
                detail.RedactedOutputAvailable,
            OutputPolicy: ToOutputPolicy(
                detail.Status,
                detail.Status == SensitiveAccessRequestStatus.Approved && detail.RedactedOutputAvailable),
            Reference: new SensitiveAccessOperatorReferenceResponse(
                detail.Reference.SourceSystem,
                detail.Reference.SourceType,
                detail.Reference.ReferenceLabel,
                detail.Reference.RedactedSummary,
                detail.Reference.ReceivedAt),
            PayloadClass: "operator-sensitive-metadata",
            RedactionState: "local-operator-only");

    internal static SensitiveAccessResultResponse ToResponse(SensitiveAccessResultState result)
    {
        var redactedOutputAvailable = result.RedactedOutputAvailable;
        var outputPolicy = ToOutputPolicy(result.Status, redactedOutputAvailable);
        var redactedOutput = redactedOutputAvailable
            ? BoundRedactedOutput(result.RedactedOutput!)
            : null;
        var payloadClass = redactedOutputAvailable ? "redacted-output" : "metadata-only";
        IReadOnlyList<string> reasons = result.Status switch
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
            result.Id,
            result.SensitiveReferenceId,
            result.Status.ToString(),
            outputPolicy,
            redactedOutputAvailable,
            redactedOutput,
            payloadClass,
            outputPolicy,
            reasons);
    }

    internal static string ToOutputPolicy(
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
        if (char.IsHighSurrogate(trimmed[end - 1]) &&
            char.IsLowSurrogate(trimmed[end]))
        {
            end--;
        }

        return trimmed[..end];
    }
}

public sealed record SensitiveAccessRequestCreateRequest
{
    public string SensitiveReferenceId { get; init; } = "";
    public string Reason { get; init; } = "";
    public string SessionId { get; init; } = "";
    public int? ExpiresInSeconds { get; init; }
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

public sealed record SensitiveAccessOperatorReferenceResponse(
    string SourceSystem,
    string SourceType,
    string ReferenceLabel,
    string RedactedSummary,
    DateTimeOffset ReceivedAt);

public sealed record SensitiveAccessOperatorDetailResponse(
    string Id,
    string SensitiveReferenceId,
    string Status,
    string RequestedBy,
    string SessionId,
    string RequestReason,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    string? Decision,
    string? DecidedBy,
    DateTimeOffset? DecidedAt,
    string? DecisionReason,
    bool RedactedOutputAvailable,
    string OutputPolicy,
    SensitiveAccessOperatorReferenceResponse Reference,
    string PayloadClass,
    string RedactionState);

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
