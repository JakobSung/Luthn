using Luthn.Core.Persistence;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Luthn.Host.Api;

public static class AuditEndpoints
{
    private const int AuditFilterMaxLength = 128;
    private static readonly HashSet<string> AllowedActionPrefixes = new(StringComparer.Ordinal)
    {
        "classification.provider.",
        "memory.",
        "operator.classification_provider.",
        "processing.",
        "retrieval.",
        "sensitive_access.",
        "source.intake.",
        "transport.",
        "turn_summary."
    };

    public static IEndpointRouteBuilder MapAuditEvents(this IEndpointRouteBuilder app)
    {
        var audit = app.MapGroup("/api/audit-events");

        audit.MapGet("", ReadAuditEvents)
            .RequireServiceScope(ServiceScopes.AuditRead)
            .WithName("ReadAuditEvents");

        return app;
    }

    public static async Task<Results<Ok<AuditEventsResponse>, ProblemHttpResult>> ReadAuditEvents(
        string? subjectId,
        int? limit,
        LuthnDbContext db,
        HttpContext httpContext,
        IOptions<LuthnIdentityOptions> identityOptions,
        CancellationToken cancellationToken,
        string? scope = null,
        string? action = null,
        string? actionPrefix = null,
        string? outcome = null,
        string? subjectType = null,
        string? actorKind = null,
        string? correlationId = null,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null)
    {
        var principal = ServiceTokenAuthorization.GetPrincipal(httpContext);
        if (identityOptions.Value.Mode == LuthnIdentityMode.MultiUser &&
            !principal.IsOperator)
        {
            return TypedResults.Problem(
                title: "Operator role required.",
                detail: "Multi-user audit-event listing is restricted to explicitly configured operators.",
                statusCode: StatusCodes.Status403Forbidden);
        }

        var scopeKind = ParseScope(scope);
        if (scopeKind is null)
        {
            return TypedResults.Problem(
                title: "Invalid audit-event scope.",
                detail: "scope must be workspace or installation.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (scopeKind == AuditEventScopeKind.Installation && !principal.IsOperator)
        {
            return TypedResults.Problem(
                title: "Operator role required.",
                detail: "Installation audit events are restricted to explicitly configured operators.",
                statusCode: StatusCodes.Status403Forbidden);
        }

        var filterError = ValidateOptionalFilter(subjectId, "subjectId") ??
            ValidateOptionalFilter(action, "action") ??
            ValidateOptionalFilter(outcome, "outcome", 32) ??
            ValidateOptionalFilter(subjectType, "subjectType", 64) ??
            ValidateOptionalFilter(actorKind, "actorKind", 32) ??
            ValidateOptionalFilter(correlationId, "correlationId");
        if (filterError is not null)
        {
            return BadRequest(filterError);
        }

        var normalizedActionPrefix = Normalize(actionPrefix);
        if (normalizedActionPrefix is not null && !AllowedActionPrefixes.Contains(normalizedActionPrefix))
        {
            return BadRequest($"actionPrefix must be one of: {string.Join(", ", AllowedActionPrefixes.Order())}");
        }

        if ((from.HasValue && from.Value.Offset != TimeSpan.Zero) ||
            (to.HasValue && to.Value.Offset != TimeSpan.Zero))
        {
            return BadRequest("from and to must use UTC (Z or +00:00).");
        }

        if (from > to)
        {
            return BadRequest("from must be earlier than or equal to to.");
        }

        var take = Math.Clamp(limit ?? 50, 1, 100);
        var query = db.AuditEvents.AsNoTracking();
        query = scopeKind == AuditEventScopeKind.Installation
            ? query.Where(record => record.ScopeKind == AuditEventScopeKind.Installation)
            : query.Where(record =>
                record.ScopeKind == AuditEventScopeKind.Workspace &&
                record.WorkspaceId == principal.WorkspaceId);
        var normalizedSubjectId = Normalize(subjectId);
        var normalizedAction = Normalize(action);
        var normalizedOutcome = Normalize(outcome);
        var normalizedSubjectType = Normalize(subjectType);
        var normalizedActorKind = Normalize(actorKind);
        var normalizedCorrelationId = Normalize(correlationId);
        if (normalizedSubjectId is not null)
        {
            query = query.Where(record => record.SubjectId == normalizedSubjectId);
        }
        if (normalizedAction is not null)
        {
            query = query.Where(record => record.Action == normalizedAction);
        }
        if (normalizedActionPrefix is not null)
        {
            query = query.Where(record => record.Action.StartsWith(normalizedActionPrefix));
        }
        if (normalizedOutcome is not null)
        {
            query = query.Where(record => record.Outcome == normalizedOutcome);
        }
        if (normalizedSubjectType is not null)
        {
            query = query.Where(record => record.SubjectType == normalizedSubjectType);
        }
        if (normalizedActorKind is not null)
        {
            query = query.Where(record => record.ActorKind == normalizedActorKind);
        }
        if (normalizedCorrelationId is not null)
        {
            query = query.Where(record => record.CorrelationId == normalizedCorrelationId);
        }
        if (from.HasValue)
        {
            query = query.Where(record => record.OccurredAt >= from.Value);
        }
        if (to.HasValue)
        {
            query = query.Where(record => record.OccurredAt <= to.Value);
        }

        var events = await query
            .OrderByDescending(record => record.OccurredAt)
            .ThenBy(record => record.Id)
            .Take(take)
            .Select(record => new AuditEventMetadata(
                record.Id,
                record.OccurredAt,
                record.ScopeKind,
                record.WorkspaceId,
                record.Actor,
                record.ActorUserId,
                record.ActorKind,
                record.Action,
                record.SubjectId,
                record.SubjectType,
                record.Outcome,
                record.CorrelationId,
                record.PayloadVersion,
                record.PayloadClass,
                record.RedactionState))
            .ToArrayAsync(cancellationToken);

        return TypedResults.Ok(new AuditEventsResponse(events));
    }

    private static AuditEventScopeKind? ParseScope(string? value) =>
        string.IsNullOrWhiteSpace(value) ||
        string.Equals(value.Trim(), "workspace", StringComparison.OrdinalIgnoreCase)
            ? AuditEventScopeKind.Workspace
            : string.Equals(value.Trim(), "installation", StringComparison.OrdinalIgnoreCase)
                ? AuditEventScopeKind.Installation
                : null;

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? ValidateOptionalFilter(
        string? value,
        string fieldName,
        int maxLength = AuditFilterMaxLength)
    {
        var normalized = Normalize(value);
        if (normalized is null)
        {
            return null;
        }

        if (normalized.Length > maxLength)
        {
            return $"{fieldName} must be {maxLength} characters or fewer.";
        }

        return normalized.Any(char.IsControl)
            ? $"{fieldName} cannot contain control characters."
            : null;
    }

    private static ProblemHttpResult BadRequest(string detail) =>
        TypedResults.Problem(
            title: "Invalid audit-event filter.",
            detail: detail,
            statusCode: StatusCodes.Status400BadRequest);
}

public sealed record AuditEventsResponse(IReadOnlyList<AuditEventMetadata> Events);

public sealed record AuditEventMetadata(
    string Id,
    DateTimeOffset OccurredAt,
    AuditEventScopeKind ScopeKind,
    string WorkspaceId,
    string Actor,
    string? ActorUserId,
    string ActorKind,
    string Action,
    string SubjectId,
    string SubjectType,
    string Outcome,
    string? CorrelationId,
    int PayloadVersion,
    string PayloadClass,
    string RedactionState);
