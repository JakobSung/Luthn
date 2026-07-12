using Luthn.Core.Persistence;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

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

    public static async Task<Ok<AuditEventsResponse>> ReadAuditEvents(
        string? subjectId,
        int? limit,
        LuthnDbContext db,
        CancellationToken cancellationToken)
    {
        var take = Math.Clamp(limit ?? 50, 1, 100);
        var query = db.AuditEvents.AsNoTracking();
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
                record.Actor,
                record.Action,
                record.SubjectId,
                record.PayloadVersion,
                record.PayloadClass,
                record.RedactionState))
            .ToArrayAsync(cancellationToken);

        return TypedResults.Ok(new AuditEventsResponse(events));
    }
}

public sealed record AuditEventsResponse(IReadOnlyList<AuditEventMetadata> Events);

public sealed record AuditEventMetadata(
    string Id,
    DateTimeOffset OccurredAt,
    string Actor,
    string Action,
    string SubjectId,
    int PayloadVersion,
    string PayloadClass,
    string RedactionState);
