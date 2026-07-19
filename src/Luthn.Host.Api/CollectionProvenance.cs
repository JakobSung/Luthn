using Luthn.Core.Persistence;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Luthn.Host.Api;

public sealed record CollectionProvenanceClaims
{
    public string? UserId { get; init; }
    public string? AgentId { get; init; }
    public string? ApplicationId { get; init; }
    public string? PluginId { get; init; }
    public string? ConnectorId { get; init; }
    public string? ConnectorVersion { get; init; }
    public DateTimeOffset? CollectedAt { get; init; }
}

public static class CollectionProvenance
{
    public const int CurrentContractVersion = 1;
    public const string ServiceTokenActorTrust = "service-token";
    public const string LocalRuntimeActorTrust = "local-runtime";
    public const string CallerClaimsTrust = "caller-supplied";
    public const string NoClaimsTrust = "no-claims";
    public const string LegacyUnknownTrust = "legacy-unknown";
    public static readonly TimeSpan MaximumClockSkew = TimeSpan.FromMinutes(5);

    public static ProblemDetails? TryCreate(
        string? sourceEventId,
        string? memoryItemId,
        CollectionProvenanceClaims? claims,
        string authenticatedActor,
        bool isServiceTokenAuthenticated,
        DateTimeOffset receivedAt,
        out CollectionProvenanceRecord record,
        string? fallbackAgentId = null,
        string? fallbackApplicationId = null)
    {
        record = null!;
        var normalized = new CollectionProvenanceClaims
        {
            UserId = NormalizeIdentifier(claims?.UserId),
            AgentId = NormalizeIdentifier(claims?.AgentId ?? fallbackAgentId),
            ApplicationId = NormalizeIdentifier(claims?.ApplicationId ?? fallbackApplicationId),
            PluginId = NormalizeIdentifier(claims?.PluginId),
            ConnectorId = NormalizeIdentifier(claims?.ConnectorId),
            ConnectorVersion = NormalizeVersion(claims?.ConnectorVersion),
            CollectedAt = claims?.CollectedAt
        };

        foreach (var (name, original, value, maximum) in new[]
        {
            ("userId", claims?.UserId, normalized.UserId, 128),
            ("agentId", claims?.AgentId, normalized.AgentId, 128),
            ("applicationId", claims?.ApplicationId, normalized.ApplicationId, 128),
            ("pluginId", claims?.PluginId, normalized.PluginId, 128),
            ("connectorId", claims?.ConnectorId, normalized.ConnectorId, 128),
            ("connectorVersion", claims?.ConnectorVersion, normalized.ConnectorVersion, 64)
        })
        {
            if (!string.IsNullOrWhiteSpace(original) && value is null)
            {
                return ApiValidation.CreateProblem(
                    "Invalid collection provenance.",
                    $"provenance.{name} must be a bounded identifier of {maximum} characters or fewer.");
            }
        }

        if (normalized.CollectedAt is { } collectedAt &&
            collectedAt > receivedAt.Add(MaximumClockSkew))
        {
            return ApiValidation.CreateProblem(
                "Invalid collection provenance.",
                "provenance.collectedAt cannot be more than five minutes in the future.");
        }

        var hasClaims = normalized.UserId is not null ||
            normalized.AgentId is not null ||
            normalized.ApplicationId is not null ||
            normalized.PluginId is not null ||
            normalized.ConnectorId is not null ||
            normalized.ConnectorVersion is not null ||
            normalized.CollectedAt is not null;
        record = new CollectionProvenanceRecord
        {
            Id = $"provenance-{Guid.NewGuid():N}",
            ContractVersion = CurrentContractVersion,
            SourceEventId = sourceEventId,
            MemoryItemId = memoryItemId,
            AuthenticatedActor = authenticatedActor,
            ActorTrust = isServiceTokenAuthenticated ? ServiceTokenActorTrust : LocalRuntimeActorTrust,
            ClaimsTrust = hasClaims ? CallerClaimsTrust : NoClaimsTrust,
            ClaimedUserId = normalized.UserId,
            AgentId = normalized.AgentId,
            ApplicationId = normalized.ApplicationId,
            PluginId = normalized.PluginId,
            ConnectorId = normalized.ConnectorId,
            ConnectorVersion = normalized.ConnectorVersion,
            CollectedAt = normalized.CollectedAt,
            ReceivedAt = receivedAt
        };
        return null;
    }

    private static string? NormalizeIdentifier(string? value) =>
        Normalize(value, 128, allowPlus: false)?.ToLowerInvariant();

    private static string? NormalizeVersion(string? value) => Normalize(value, 64, allowPlus: true);

    private static string? Normalize(string? value, int maximumLength, bool allowPlus)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim();
        if (normalized.Length > maximumLength ||
            !IsAsciiLetterOrDigit(normalized[0]) ||
            normalized.Any(character =>
                !IsAsciiLetterOrDigit(character) &&
                character is not '.' and not '_' and not ':' and not '@' and not '-' &&
                (!allowPlus || character != '+')))
        {
            return null;
        }

        return normalized;
    }

    private static bool IsAsciiLetterOrDigit(char value) =>
        value is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9';
}

public static class CollectionProvenanceEndpoints
{
    public static IEndpointRouteBuilder MapCollectionProvenance(this IEndpointRouteBuilder app)
    {
        var provenance = app.MapGroup("/api/provenance")
            .RequireServiceScope(ServiceScopes.AuditRead);

        provenance.MapGet("/source-events/{sourceEventId}", ReadBySourceEvent)
            .WithName("ReadSourceEventProvenance");
        provenance.MapGet("/memory-items/{memoryItemId}", ReadByMemoryItem)
            .WithName("ReadMemoryItemProvenance");
        return app;
    }

    private static Task<Results<Ok<CollectionProvenanceResponse>, NotFound>> ReadBySourceEvent(
        string sourceEventId,
        LuthnDbContext db,
        CancellationToken cancellationToken) =>
        ReadAsync(db.CollectionProvenance.Where(record => record.SourceEventId == sourceEventId), cancellationToken);

    private static Task<Results<Ok<CollectionProvenanceResponse>, NotFound>> ReadByMemoryItem(
        string memoryItemId,
        LuthnDbContext db,
        CancellationToken cancellationToken) =>
        ReadAsync(db.CollectionProvenance.Where(record => record.MemoryItemId == memoryItemId), cancellationToken);

    private static async Task<Results<Ok<CollectionProvenanceResponse>, NotFound>> ReadAsync(
        IQueryable<CollectionProvenanceRecord> query,
        CancellationToken cancellationToken)
    {
        var record = await query.AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        return record is null
            ? TypedResults.NotFound()
            : TypedResults.Ok(ToResponse(record));
    }

    private static CollectionProvenanceResponse ToResponse(CollectionProvenanceRecord record) => new(
        record.Id,
        record.ContractVersion,
        record.SourceEventId,
        record.MemoryItemId,
        record.AuthenticatedActor,
        record.ActorTrust,
        record.ClaimsTrust,
        record.ClaimedUserId,
        record.AgentId,
        record.ApplicationId,
        record.PluginId,
        record.ConnectorId,
        record.ConnectorVersion,
        record.CollectedAt,
        record.ReceivedAt);
}

public sealed record CollectionProvenanceResponse(
    string Id,
    int ContractVersion,
    string? SourceEventId,
    string? MemoryItemId,
    string AuthenticatedActor,
    string ActorTrust,
    string ClaimsTrust,
    string? ClaimedUserId,
    string? AgentId,
    string? ApplicationId,
    string? PluginId,
    string? ConnectorId,
    string? ConnectorVersion,
    DateTimeOffset? CollectedAt,
    DateTimeOffset ReceivedAt);
