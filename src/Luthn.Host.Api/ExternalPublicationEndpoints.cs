using Luthn.Core.Memory;
using Luthn.Core.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Luthn.Host.Api;

public static class ExternalPublicationEndpoints
{
    public static IEndpointRouteBuilder MapExternalPublication(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/external-publication");

        group.MapGet("/status", ReadStatus)
            .RequireServiceScope(ServiceScopes.ExternalPublicationRead)
            .WithName("ReadExternalPublicationStatus");

        group.MapGet("/memory-items/{id}", ReadMemoryStatus)
            .RequireServiceScope(ServiceScopes.ExternalPublicationRead)
            .WithName("ReadMemoryExternalPublicationStatus");

        group.MapPost("/memory-items/{id}/approve", Approve)
            .RequireServiceScope(ServiceScopes.ExternalPublicationWrite)
            .WithName("ApproveMemoryForExternalPublication");

        group.MapPost("/memory-items/{id}/revoke", Revoke)
            .RequireServiceScope(ServiceScopes.ExternalPublicationWrite)
            .WithName("RevokeMemoryExternalPublication");

        return app;
    }

    private static async Task<IResult> ReadStatus(
        LuthnDbContext db,
        ISafeProjectionSyncTransport transport,
        CancellationToken cancellationToken)
    {
        var counts = await db.SafeProjectionSyncOutbox
            .AsNoTracking()
            .GroupBy(record => record.State)
            .Select(group => new { State = group.Key, Count = group.Count() })
            .ToArrayAsync(cancellationToken);
        var countByState = counts.ToDictionary(item => item.State, item => item.Count);
        var pending = Count(SafeProjectionSyncOutboxState.Pending) +
            Count(SafeProjectionSyncOutboxState.Processing);
        var failed = Count(SafeProjectionSyncOutboxState.Failed);
        var acknowledged = Count(SafeProjectionSyncOutboxState.Acknowledged);

        return TypedResults.Ok(new SafeProjectionSyncStatusResponse(
            transport.State.ToString(),
            ResolveOutboxState(pending, failed, acknowledged),
            pending,
            failed,
            acknowledged));

        int Count(SafeProjectionSyncOutboxState state) =>
            countByState.GetValueOrDefault(state);
    }

    private static string ResolveOutboxState(int pending, int failed, int acknowledged)
    {
        if (failed > 0)
        {
            return "Failed";
        }

        if (pending > 0)
        {
            return "Pending";
        }

        return acknowledged > 0 ? "Synced" : "Idle";
    }

    private static async Task<IResult> ReadMemoryStatus(
        string id,
        SafeProjectionPublicationService service,
        CancellationToken cancellationToken)
    {
        var idError = ValidateId(id);
        if (idError is not null)
        {
            return TypedResults.BadRequest(idError);
        }

        var result = await service.GetAsync(id.Trim(), cancellationToken);
        return result is null
            ? TypedResults.NotFound()
            : TypedResults.Ok(ToResponse(result));
    }

    private static Task<IResult> Approve(
        string id,
        SafeProjectionPublicationService service,
        HttpContext httpContext,
        TimeProvider timeProvider,
        CancellationToken cancellationToken) =>
        ChangePublicationState(
            id,
            service,
            httpContext,
            timeProvider,
            approve: true,
            cancellationToken);

    private static Task<IResult> Revoke(
        string id,
        SafeProjectionPublicationService service,
        HttpContext httpContext,
        TimeProvider timeProvider,
        CancellationToken cancellationToken) =>
        ChangePublicationState(
            id,
            service,
            httpContext,
            timeProvider,
            approve: false,
            cancellationToken);

    private static async Task<IResult> ChangePublicationState(
        string id,
        SafeProjectionPublicationService service,
        HttpContext httpContext,
        TimeProvider timeProvider,
        bool approve,
        CancellationToken cancellationToken)
    {
        var idError = ValidateId(id);
        if (idError is not null)
        {
            return TypedResults.BadRequest(idError);
        }

        try
        {
            var result = approve
                ? await service.ApproveAsync(
                    id.Trim(),
                    ServiceTokenAuthorization.GetActor(httpContext),
                    timeProvider.GetUtcNow(),
                    cancellationToken)
                : await service.RevokeAsync(
                    id.Trim(),
                    ServiceTokenAuthorization.GetActor(httpContext),
                    timeProvider.GetUtcNow(),
                    cancellationToken);
            return TypedResults.Ok(ToResponse(result));
        }
        catch (KeyNotFoundException)
        {
            return TypedResults.NotFound();
        }
        catch (SafeProjectionPublicationException error)
        {
            return TypedResults.Problem(
                title: "External publication state conflict.",
                detail: error.Message,
                statusCode: StatusCodes.Status409Conflict);
        }
    }

    private static ProblemDetails? ValidateId(string id) =>
        ApiValidation.ValidatePublicRecordId(
            id,
            "id",
            "Invalid memory item identifier.",
            out _);

    private static ExternalPublicationStatusResponse ToResponse(ExternalPublicationResult result) =>
        new(
            result.MemoryItemId,
            result.PublicationState.ToString(),
            result.Revision,
            result.UpdatedAt,
            result.DecidedAt,
            result.SyncState?.ToString() ?? "NotQueued");
}

public sealed record ExternalPublicationStatusResponse(
    string MemoryItemId,
    string PublicationState,
    long Revision,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? DecidedAt,
    string SyncState);

public sealed record SafeProjectionSyncStatusResponse(
    string ConnectionState,
    string OutboxState,
    int PendingCount,
    int FailedCount,
    int AcknowledgedCount);
