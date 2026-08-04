using Luthn.Core.Persistence;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Luthn.Host.Api;

public static class AuditEndpoints
{
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
        string? scope = null)
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

        var take = Math.Clamp(limit ?? 50, 1, 100);
        var query = db.AuditEvents.AsNoTracking();
        query = scopeKind == AuditEventScopeKind.Installation
            ? query.Where(record => record.ScopeKind == AuditEventScopeKind.Installation)
            : query.Where(record =>
                record.ScopeKind == AuditEventScopeKind.Workspace &&
                record.WorkspaceId == principal.WorkspaceId);
        if (!string.IsNullOrWhiteSpace(subjectId))
        {
            query = query.Where(record => record.SubjectId == subjectId.Trim());
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
