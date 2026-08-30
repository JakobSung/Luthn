using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Luthn.Core.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Luthn.Host.Api;

public static class AuditEndpoints
{
    private const int AuditFilterMaxLength = 128;
    private const int AuditCursorMaxLength = 2048;
    private const int AuditExportMaxItems = 1000;
    private const string AuditCursorProtectionPurpose = "Luthn.Audit.Cursor.v1";
    public static IEndpointRouteBuilder MapAuditEvents(this IEndpointRouteBuilder app)
    {
        var audit = app.MapGroup("/api/audit-events");

        audit.MapGet("", ReadAuditEvents)
            .RequireServiceScope(ServiceScopes.AuditRead)
            .WithName("ReadAuditEvents");
        audit.MapGet("/export", ExportAuditEvents)
            .RequireServiceScope(ServiceScopes.AuditRead)
            .WithName("ExportAuditEvents");

        return app;
    }

    public static async Task<Results<Ok<AuditEventsResponse>, ProblemHttpResult>> ReadAuditEvents(
        string? subjectId,
        int? limit,
        LuthnDbContext db,
        HttpContext httpContext,
        IOptions<LuthnIdentityOptions> identityOptions,
        IOptions<AuditRetentionOptions> retentionOptions,
        IDataProtectionProvider dataProtectionProvider,
        CancellationToken cancellationToken,
        string? scope = null,
        string? action = null,
        string? actionPrefix = null,
        string? outcome = null,
        string? subjectType = null,
        string? actorKind = null,
        string? correlationId = null,
        string? category = null,
        string? cursor = null,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null)
    {
        var prepared = PrepareQuery(
            db,
            httpContext,
            identityOptions.Value,
            new AuditFilterArguments(
                subjectId,
                scope,
                action,
                actionPrefix,
                outcome,
                subjectType,
                actorKind,
                correlationId,
                category,
                from,
                to,
                cursor),
            dataProtectionProvider);
        if (prepared.Error is not null)
        {
            return prepared.Error;
        }

        var take = Math.Clamp(limit ?? 50, 1, 100);
        var records = await prepared.Query!
            .OrderByDescending(record => record.OccurredAt)
            .ThenBy(record => record.Id)
            .Take(take + 1)
            .ToArrayAsync(cancellationToken);
        var hasMore = records.Length > take;
        var pageRecords = records.Take(take).ToArray();
        var events = pageRecords
            .Select(record => ToMetadata(record, retentionOptions.Value))
            .ToArray();
        var nextCursor = hasMore
            ? EncodeCursor(pageRecords[^1], prepared.FilterHash!, dataProtectionProvider)
            : null;

        return TypedResults.Ok(new AuditEventsResponse(events, nextCursor));
    }

    public static async Task<Results<FileContentHttpResult, ProblemHttpResult>> ExportAuditEvents(
        string? subjectId,
        int? limit,
        LuthnDbContext db,
        HttpContext httpContext,
        IOptions<LuthnIdentityOptions> identityOptions,
        IOptions<AuditRetentionOptions> retentionOptions,
        IDataProtectionProvider dataProtectionProvider,
        TimeProvider timeProvider,
        CancellationToken cancellationToken,
        string? scope = null,
        string? action = null,
        string? actionPrefix = null,
        string? outcome = null,
        string? subjectType = null,
        string? actorKind = null,
        string? correlationId = null,
        string? category = null,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null)
    {
        var prepared = PrepareQuery(
            db,
            httpContext,
            identityOptions.Value,
            new AuditFilterArguments(
                subjectId,
                scope,
                action,
                actionPrefix,
                outcome,
                subjectType,
                actorKind,
                correlationId,
                category,
                from,
                to,
                Cursor: null),
            dataProtectionProvider);
        if (prepared.Error is not null)
        {
            return prepared.Error;
        }

        var take = Math.Clamp(limit ?? AuditExportMaxItems, 1, AuditExportMaxItems);
        var records = await prepared.Query!
            .OrderByDescending(record => record.OccurredAt)
            .ThenBy(record => record.Id)
            .Take(take)
            .ToArrayAsync(cancellationToken);
        var exportedAt = timeProvider.GetUtcNow();
        var export = new AuditExportDocument(
            exportedAt,
            "metadata-only-no-protected-content",
            records.Select(record => ToExportMetadata(record, retentionOptions.Value)).ToArray());
        var bytes = JsonSerializer.SerializeToUtf8Bytes(export, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        return TypedResults.File(
            bytes,
            contentType: "application/json",
            fileDownloadName: $"luthn-audit-metadata-{exportedAt:yyyyMMddHHmmss}.json");
    }

    private static AuditQueryPreparation PrepareQuery(
        LuthnDbContext db,
        HttpContext httpContext,
        LuthnIdentityOptions identityOptions,
        AuditFilterArguments filters,
        IDataProtectionProvider dataProtectionProvider)
    {
        var principal = ServiceTokenAuthorization.GetPrincipal(httpContext);
        if (identityOptions.Mode == LuthnIdentityMode.MultiUser && !principal.IsOperator)
        {
            return AuditQueryPreparation.Fail(TypedResults.Problem(
                title: "Operator role required.",
                detail: "Multi-user audit-event listing is restricted to explicitly configured operators.",
                statusCode: StatusCodes.Status403Forbidden));
        }

        var scopeKind = ParseScope(filters.Scope);
        if (scopeKind is null)
        {
            return AuditQueryPreparation.Fail(TypedResults.Problem(
                title: "Invalid audit-event scope.",
                detail: "scope must be workspace or installation.",
                statusCode: StatusCodes.Status400BadRequest));
        }

        if (scopeKind == AuditEventScopeKind.Installation && !principal.IsOperator)
        {
            return AuditQueryPreparation.Fail(TypedResults.Problem(
                title: "Operator role required.",
                detail: "Installation audit events are restricted to explicitly configured operators.",
                statusCode: StatusCodes.Status403Forbidden));
        }

        var filterError = ValidateOptionalFilter(filters.SubjectId, "subjectId") ??
            ValidateOptionalFilter(filters.Action, "action") ??
            ValidateOptionalFilter(filters.Outcome, "outcome", 32) ??
            ValidateOptionalFilter(filters.SubjectType, "subjectType", 64) ??
            ValidateOptionalFilter(filters.ActorKind, "actorKind", 32) ??
            ValidateOptionalFilter(filters.CorrelationId, "correlationId") ??
            ValidateOptionalFilter(filters.Category, "category", 32) ??
            ValidateOptionalFilter(filters.Cursor, "cursor", AuditCursorMaxLength);
        if (filterError is not null)
        {
            return AuditQueryPreparation.Fail(BadRequest(filterError));
        }

        var normalizedActionPrefix = Normalize(filters.ActionPrefix);
        if (normalizedActionPrefix is not null && !AuditActionFamilies.AllowedPrefixes.Contains(normalizedActionPrefix))
        {
            return AuditQueryPreparation.Fail(BadRequest(
                $"actionPrefix must be one of: {string.Join(", ", AuditActionFamilies.AllowedPrefixes.Order())}"));
        }

        if (!AuditEventCategories.TryNormalize(filters.Category, out var normalizedCategory))
        {
            return AuditQueryPreparation.Fail(BadRequest(
                $"category must be one of: {string.Join(", ", AuditEventCategories.All)}"));
        }

        if ((filters.From.HasValue && filters.From.Value.Offset != TimeSpan.Zero) ||
            (filters.To.HasValue && filters.To.Value.Offset != TimeSpan.Zero))
        {
            return AuditQueryPreparation.Fail(BadRequest("from and to must use UTC (Z or +00:00)."));
        }

        if (filters.From > filters.To)
        {
            return AuditQueryPreparation.Fail(BadRequest("from must be earlier than or equal to to."));
        }

        var normalizedSubjectId = Normalize(filters.SubjectId);
        var normalizedAction = Normalize(filters.Action);
        var normalizedOutcome = Normalize(filters.Outcome);
        var normalizedSubjectType = Normalize(filters.SubjectType);
        var normalizedActorKind = Normalize(filters.ActorKind);
        var normalizedCorrelationId = Normalize(filters.CorrelationId);
        var normalizedFilters = filters with
        {
            SubjectId = normalizedSubjectId,
            Scope = scopeKind.Value.ToString(),
            Action = normalizedAction,
            ActionPrefix = normalizedActionPrefix,
            Outcome = normalizedOutcome,
            SubjectType = normalizedSubjectType,
            ActorKind = normalizedActorKind,
            CorrelationId = normalizedCorrelationId,
            Category = normalizedCategory,
            Cursor = null
        };
        var authorizationScope = scopeKind == AuditEventScopeKind.Workspace
            ? $"workspace:{principal.WorkspaceId}"
            : "installation";
        var filterHash = ComputeFilterHash(normalizedFilters, authorizationScope);
        var cursorError = DecodeCursor(
            filters.Cursor,
            filterHash,
            dataProtectionProvider,
            out var cursor);
        if (cursorError is not null)
        {
            return AuditQueryPreparation.Fail(BadRequest(cursorError));
        }

        IQueryable<AuditEventRecord> query = db.AuditEvents.AsNoTracking();
        query = scopeKind == AuditEventScopeKind.Installation
            ? query.Where(record => record.ScopeKind == AuditEventScopeKind.Installation)
            : query.Where(record =>
                record.ScopeKind == AuditEventScopeKind.Workspace &&
                record.WorkspaceId == principal.WorkspaceId);
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
        if (normalizedCategory is not null)
        {
            query = AuditEventCategories.Apply(query, normalizedCategory);
        }
        if (filters.From.HasValue)
        {
            query = query.Where(record => record.OccurredAt >= filters.From.Value);
        }
        if (filters.To.HasValue)
        {
            query = query.Where(record => record.OccurredAt <= filters.To.Value);
        }
        if (cursor is not null)
        {
            query = query.Where(record =>
                record.OccurredAt < cursor.OccurredAt ||
                (record.OccurredAt == cursor.OccurredAt && record.Id.CompareTo(cursor.Id) > 0));
        }

        return new AuditQueryPreparation(query, filterHash, null);
    }

    private static AuditEventMetadata ToMetadata(
        AuditEventRecord record,
        AuditRetentionOptions retentionOptions)
    {
        var category = AuditEventCategories.FromAction(record.Action);
        var retentionDays = retentionOptions.DaysFor(category);
        return new AuditEventMetadata(
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
            record.RedactionState,
            category,
            $"{category.ToLowerInvariant()}-{retentionDays}d",
            record.OccurredAt.AddDays(retentionDays));
    }

    private static AuditExportEventMetadata ToExportMetadata(
        AuditEventRecord record,
        AuditRetentionOptions retentionOptions)
    {
        var metadata = ToMetadata(record, retentionOptions);
        return new AuditExportEventMetadata(
            metadata.Id,
            metadata.OccurredAt,
            metadata.ScopeKind,
            metadata.Actor,
            metadata.ActorKind,
            metadata.Action,
            metadata.SubjectId,
            metadata.SubjectType,
            metadata.Outcome,
            metadata.CorrelationId,
            metadata.PayloadVersion,
            metadata.PayloadClass,
            metadata.RedactionState,
            metadata.Category,
            metadata.RetentionClass,
            metadata.RetainedUntil);
    }

    private static string EncodeCursor(
        AuditEventRecord record,
        string filterHash,
        IDataProtectionProvider dataProtectionProvider)
    {
        var payload = JsonSerializer.Serialize(
            new AuditCursorPayload(record.OccurredAt, record.Id, filterHash));
        return dataProtectionProvider
            .CreateProtector(AuditCursorProtectionPurpose)
            .Protect(payload);
    }

    private static string? DecodeCursor(
        string? value,
        string expectedFilterHash,
        IDataProtectionProvider dataProtectionProvider,
        out AuditCursorPayload? cursor)
    {
        cursor = null;
        var normalized = Normalize(value);
        if (normalized is null)
        {
            return null;
        }

        try
        {
            cursor = JsonSerializer.Deserialize<AuditCursorPayload>(
                dataProtectionProvider
                    .CreateProtector(AuditCursorProtectionPurpose)
                    .Unprotect(normalized));
        }
        catch (Exception error) when (error is CryptographicException or JsonException)
        {
            return "cursor is invalid.";
        }

        if (cursor is null ||
            cursor.OccurredAt.Offset != TimeSpan.Zero ||
            string.IsNullOrWhiteSpace(cursor.Id) ||
            cursor.Id.Length > AuditFilterMaxLength ||
            cursor.Id.Any(char.IsControl) ||
            !string.Equals(cursor.FilterHash, expectedFilterHash, StringComparison.Ordinal))
        {
            cursor = null;
            return "cursor does not match the current audit filters.";
        }

        return null;
    }

    private static string ComputeFilterHash(
        AuditFilterArguments filters,
        string authorizationScope)
    {
        var canonical = string.Join('\n',
            authorizationScope,
            filters.Scope,
            filters.Action,
            filters.ActionPrefix,
            filters.Outcome,
            filters.SubjectId,
            filters.SubjectType,
            filters.ActorKind,
            filters.CorrelationId,
            filters.Category,
            filters.From?.ToString("O"),
            filters.To?.ToString("O"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
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
        if (value?.Any(char.IsControl) == true)
        {
            return $"{fieldName} cannot contain control characters.";
        }

        var normalized = Normalize(value);
        if (normalized is null)
        {
            return null;
        }

        return normalized.Length > maxLength
            ? $"{fieldName} must be {maxLength} characters or fewer."
            : null;
    }

    private static ProblemHttpResult BadRequest(string detail) =>
        TypedResults.Problem(
            title: "Invalid audit-event filter.",
            detail: detail,
            statusCode: StatusCodes.Status400BadRequest);

    private sealed record AuditFilterArguments(
        string? SubjectId,
        string? Scope,
        string? Action,
        string? ActionPrefix,
        string? Outcome,
        string? SubjectType,
        string? ActorKind,
        string? CorrelationId,
        string? Category,
        DateTimeOffset? From,
        DateTimeOffset? To,
        string? Cursor);

    private sealed record AuditCursorPayload(
        DateTimeOffset OccurredAt,
        string Id,
        string FilterHash);

    private sealed record AuditQueryPreparation(
        IQueryable<AuditEventRecord>? Query,
        string? FilterHash,
        ProblemHttpResult? Error)
    {
        public static AuditQueryPreparation Fail(ProblemHttpResult error) =>
            new(null, null, error);
    }
}

public sealed record AuditEventsResponse(
    IReadOnlyList<AuditEventMetadata> Events,
    string? NextCursor = null);

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
    string RedactionState,
    string Category,
    string RetentionClass,
    DateTimeOffset RetainedUntil);

public sealed record AuditExportDocument(
    DateTimeOffset ExportedAt,
    string ExportBoundary,
    IReadOnlyList<AuditExportEventMetadata> Events);

public sealed record AuditExportEventMetadata(
    string Id,
    DateTimeOffset OccurredAt,
    AuditEventScopeKind ScopeKind,
    string Actor,
    string ActorKind,
    string Action,
    string SubjectId,
    string SubjectType,
    string Outcome,
    string? CorrelationId,
    int PayloadVersion,
    string PayloadClass,
    string RedactionState,
    string Category,
    string RetentionClass,
    DateTimeOffset RetainedUntil);
