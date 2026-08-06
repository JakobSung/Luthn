using System.Data;
using System.Security.Cryptography;
using System.Text;
using Luthn.Core.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Luthn.Host.Api;

public sealed class HubIngressOptions
{
    public bool Enabled { get; init; }
    public int MaxCapsuleBytes { get; init; } = 16 * 1024;
    public int OrganizationPendingLimit { get; init; } = 5_000;
    public int WorkspacePendingLimit { get; init; } = 1_000;
    public int MemberPendingLimit { get; init; } = 500;
    public int AgentPendingLimit { get; init; } = 250;
    public int OrganizationPerMinuteLimit { get; init; } = 6_000;
    public int WorkspacePerMinuteLimit { get; init; } = 1_200;
    public int MemberPerMinuteLimit { get; init; } = 600;
    public int AgentPerMinuteLimit { get; init; } = 300;
    public int RetryAfterSeconds { get; init; } = 5;

    public bool IsValid =>
        MaxCapsuleBytes is >= 256 and <= 256 * 1024 &&
        OrganizationPendingLimit is >= 2 and <= 100_000 &&
        WorkspacePendingLimit >= 1 && WorkspacePendingLimit <= OrganizationPendingLimit &&
        MemberPendingLimit >= 1 && MemberPendingLimit <= WorkspacePendingLimit &&
        AgentPendingLimit >= 1 && AgentPendingLimit <= MemberPendingLimit &&
        OrganizationPerMinuteLimit is >= 2 and <= 1_000_000 &&
        WorkspacePerMinuteLimit >= 1 && WorkspacePerMinuteLimit <= OrganizationPerMinuteLimit &&
        MemberPerMinuteLimit >= 1 && MemberPerMinuteLimit <= WorkspacePerMinuteLimit &&
        AgentPerMinuteLimit >= 1 && AgentPerMinuteLimit <= MemberPerMinuteLimit &&
        RetryAfterSeconds is >= 1 and <= 300;
}

public interface IHubIngressCapsuleProtector
{
    string ProtectionScheme { get; }
    string Protect(string queueItemId, string capsule);
    string Unprotect(string queueItemId, string protectedCapsule);
}

public sealed class DataProtectionHubIngressCapsuleProtector(
    IDataProtectionProvider dataProtectionProvider) : IHubIngressCapsuleProtector
{
    private const string RootPurpose = "Luthn.Hub.Ingress.Capsule.v1";
    private readonly IDataProtector _rootProtector = dataProtectionProvider.CreateProtector(RootPurpose);

    public string ProtectionScheme => "aspnet-data-protection:v1";

    public string Protect(string queueItemId, string capsule) =>
        ForItem(queueItemId).Protect(capsule);

    public string Unprotect(string queueItemId, string protectedCapsule) =>
        ForItem(queueItemId).Unprotect(protectedCapsule);

    private IDataProtector ForItem(string queueItemId) =>
        _rootProtector.CreateProtector("queue-item", queueItemId);
}

public sealed record HubIngressRequest(
    string? IdempotencyKey,
    string? ContentDigest,
    string? Capsule);

public sealed record HubIngressReceipt(
    string ReceiptId,
    string State,
    bool Duplicate,
    DateTimeOffset AcceptedAt,
    string PayloadClass = "metadata-only");

public enum HubIngressAdmissionKind
{
    Accepted,
    Duplicate,
    Conflict,
    Backpressured
}

public sealed record HubIngressAdmission(
    HubIngressAdmissionKind Kind,
    HubIngressReceipt? Receipt = null,
    string? ErrorCode = null,
    int? RetryAfterSeconds = null);

public sealed class HubIngressQueueService(
    LuthnDbContext db,
    IHubIngressCapsuleProtector protector,
    IOptions<HubIngressOptions> options,
    TimeProvider timeProvider)
{
    private readonly HubIngressOptions _options = options.Value;

    public async Task<HubIngressAdmission> EnqueueAsync(
        HubIngressRequest request,
        LuthnRequestPrincipal principal,
        string actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(principal);
        if (!principal.HasTrustedHubBinding)
        {
            throw new InvalidOperationException("A trusted Hub identity binding is required.");
        }

        var idempotencyKey = request.IdempotencyKey!.Trim().ToLowerInvariant();
        var contentDigest = NormalizeDigest(request.ContentDigest!);
        var existing = await FindExistingAsync(principal, idempotencyKey, cancellationToken);
        if (existing is not null)
        {
            return string.Equals(existing.ContentDigest, contentDigest, StringComparison.Ordinal)
                ? new HubIngressAdmission(HubIngressAdmissionKind.Duplicate, ToReceipt(existing, duplicate: true))
                : new HubIngressAdmission(HubIngressAdmissionKind.Conflict, ErrorCode: "hub.ingress.idempotency_conflict");
        }

        var now = timeProvider.GetUtcNow();
        var backpressureCode = await ResolveBackpressureAsync(principal, now, cancellationToken);
        if (backpressureCode is not null)
        {
            return new HubIngressAdmission(
                HubIngressAdmissionKind.Backpressured,
                ErrorCode: backpressureCode,
                RetryAfterSeconds: _options.RetryAfterSeconds);
        }

        var id = $"hub-ingress-{Guid.NewGuid():N}";
        var record = new HubIngressQueueRecord
        {
            Id = id,
            ReceiptId = $"hub-receipt-{Guid.NewGuid():N}",
            OrganizationId = principal.HubOrganizationId!,
            WorkspaceId = principal.WorkspaceId,
            MemberUserId = principal.UserId,
            AgentConnectionId = principal.HubAgentConnectionId!,
            AgentId = principal.HubAgentId!,
            SessionId = principal.HubSessionId!,
            TurnId = CreateTurnId(principal, idempotencyKey),
            IdempotencyKey = idempotencyKey,
            ContentDigest = contentDigest,
            CapsuleSizeBytes = Encoding.UTF8.GetByteCount(request.Capsule!),
            ProtectionScheme = protector.ProtectionScheme,
            ProtectedCapsule = protector.Protect(id, request.Capsule!),
            State = HubIngressQueueState.Pending,
            AcceptedAt = now
        };

        var strategy = db.Database.CreateExecutionStrategy();
        try
        {
            return await strategy.ExecuteAsync(async () =>
            {
                await using var transaction = db.Database.IsRelational()
                    ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
                    : null;
                db.HubIngressQueue.Add(record);
                db.AuditEvents.Add(AuditEventFactory.ForWorkspace(
                    principal,
                    actor,
                    "hub.ingress.accepted",
                    record.Id,
                    "metadata-only",
                    "protected-capsule-only",
                    now,
                    subjectType: "hub_ingress_item",
                    outcome: "accepted",
                    correlationId: record.ReceiptId));
                await db.SaveChangesAsync(cancellationToken);
                if (transaction is not null)
                {
                    await transaction.CommitAsync(cancellationToken);
                }
                return new HubIngressAdmission(
                    HubIngressAdmissionKind.Accepted,
                    ToReceipt(record, duplicate: false));
            });
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            existing = await FindExistingAsync(principal, idempotencyKey, cancellationToken);
            if (existing is null)
            {
                throw;
            }
            return string.Equals(existing.ContentDigest, contentDigest, StringComparison.Ordinal)
                ? new HubIngressAdmission(HubIngressAdmissionKind.Duplicate, ToReceipt(existing, duplicate: true))
                : new HubIngressAdmission(HubIngressAdmissionKind.Conflict, ErrorCode: "hub.ingress.idempotency_conflict");
        }
    }

    private async Task<HubIngressQueueRecord?> FindExistingAsync(
        LuthnRequestPrincipal principal,
        string idempotencyKey,
        CancellationToken cancellationToken) =>
        await db.HubIngressQueue.AsNoTracking().SingleOrDefaultAsync(record =>
            record.WorkspaceId == principal.WorkspaceId &&
            record.AgentConnectionId == principal.HubAgentConnectionId &&
            record.IdempotencyKey == idempotencyKey,
            cancellationToken);

    private async Task<string?> ResolveBackpressureAsync(
        LuthnRequestPrincipal principal,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var active = db.HubIngressQueue.AsNoTracking().Where(record =>
            record.State == HubIngressQueueState.Pending ||
            record.State == HubIngressQueueState.Processing ||
            record.State == HubIngressQueueState.Failed);
        if (await active.CountAsync(record => record.OrganizationId == principal.HubOrganizationId, cancellationToken) >=
            _options.OrganizationPendingLimit)
        {
            return "hub.ingress.organization_capacity";
        }
        if (await active.CountAsync(record => record.WorkspaceId == principal.WorkspaceId, cancellationToken) >=
            _options.WorkspacePendingLimit)
        {
            return "hub.ingress.workspace_capacity";
        }
        if (await active.CountAsync(record =>
                record.WorkspaceId == principal.WorkspaceId && record.MemberUserId == principal.UserId,
                cancellationToken) >= _options.MemberPendingLimit)
        {
            return "hub.ingress.member_capacity";
        }
        if (await active.CountAsync(record =>
                record.WorkspaceId == principal.WorkspaceId && record.AgentId == principal.HubAgentId,
                cancellationToken) >= _options.AgentPendingLimit)
        {
            return "hub.ingress.agent_capacity";
        }

        var since = now.AddMinutes(-1);
        var recent = db.HubIngressQueue.AsNoTracking().Where(record => record.AcceptedAt >= since);
        if (await recent.CountAsync(record => record.OrganizationId == principal.HubOrganizationId, cancellationToken) >=
            _options.OrganizationPerMinuteLimit)
        {
            return "hub.ingress.organization_rate";
        }
        if (await recent.CountAsync(record => record.WorkspaceId == principal.WorkspaceId, cancellationToken) >=
            _options.WorkspacePerMinuteLimit)
        {
            return "hub.ingress.workspace_rate";
        }
        if (await recent.CountAsync(record =>
                record.WorkspaceId == principal.WorkspaceId && record.MemberUserId == principal.UserId,
                cancellationToken) >= _options.MemberPerMinuteLimit)
        {
            return "hub.ingress.member_rate";
        }
        return await recent.CountAsync(record =>
                record.WorkspaceId == principal.WorkspaceId && record.AgentId == principal.HubAgentId,
                cancellationToken) >= _options.AgentPerMinuteLimit
            ? "hub.ingress.agent_rate"
            : null;
    }

    private static HubIngressReceipt ToReceipt(HubIngressQueueRecord record, bool duplicate) =>
        new(record.ReceiptId, record.State.ToString(), duplicate, record.AcceptedAt);

    private static string CreateTurnId(LuthnRequestPrincipal principal, string idempotencyKey)
    {
        var value = string.Join('\n', principal.WorkspaceId, principal.UserId,
            principal.HubAgentConnectionId, principal.HubSessionId, idempotencyKey);
        return $"turn-{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant()[..32]}";
    }

    public static string NormalizeDigest(string value) =>
        value.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
            ? $"sha256:{value["sha256:".Length..].Trim().ToLowerInvariant()}"
            : $"sha256:{value.Trim().ToLowerInvariant()}";
}

public static class HubIngressEndpoints
{
    public static IEndpointRouteBuilder MapHubIngress(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/hub/ingress/capsules", Enqueue)
            .RequireServiceScope(ServiceScopes.HubIngressWrite)
            .WithName("EnqueueHubIngressCapsule");
        return app;
    }

    public static async Task<IResult> Enqueue(
        HubIngressRequest request,
        HubIngressQueueService queue,
        IOptions<HubIngressOptions> options,
        IOptions<LuthnIdentityOptions> identity,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        if (!options.Value.Enabled || identity.Value.Mode != LuthnIdentityMode.MultiUser)
        {
            return Error(StatusCodes.Status404NotFound, "hub.ingress.disabled", "Hub ingress is disabled.");
        }

        var principal = ServiceTokenAuthorization.GetPrincipal(httpContext);
        if (!ServiceTokenAuthorization.IsServiceTokenAuthenticated(httpContext) || !principal.HasTrustedHubBinding)
        {
            return Error(
                StatusCodes.Status503ServiceUnavailable,
                "hub.ingress.identity_binding_required",
                "Hub ingress requires a complete server-configured identity binding.");
        }

        var validationError = Validate(request, options.Value);
        if (validationError is not null)
        {
            return validationError;
        }

        var admission = await queue.EnqueueAsync(
            request,
            principal,
            ServiceTokenAuthorization.GetActor(httpContext),
            cancellationToken);
        if (admission.Kind == HubIngressAdmissionKind.Backpressured)
        {
            httpContext.Response.Headers.RetryAfter = admission.RetryAfterSeconds!.Value.ToString();
            return Error(
                StatusCodes.Status429TooManyRequests,
                admission.ErrorCode!,
                "Hub ingress is temporarily saturated.",
                admission.RetryAfterSeconds);
        }
        if (admission.Kind == HubIngressAdmissionKind.Conflict)
        {
            return Error(
                StatusCodes.Status409Conflict,
                admission.ErrorCode!,
                "The idempotency key was already used with a different digest.");
        }
        return admission.Kind == HubIngressAdmissionKind.Duplicate
            ? TypedResults.Ok(admission.Receipt)
            : TypedResults.Accepted<HubIngressReceipt>((string?)null, admission.Receipt!);
    }

    private static IResult? Validate(HubIngressRequest request, HubIngressOptions options)
    {
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey) ||
            request.IdempotencyKey.Trim().Length > 128 ||
            !IsBoundedKey(request.IdempotencyKey.Trim()))
        {
            return Error(StatusCodes.Status400BadRequest, "hub.ingress.invalid_idempotency_key", "idempotencyKey is invalid.");
        }
        if (string.IsNullOrWhiteSpace(request.Capsule))
        {
            return Error(StatusCodes.Status400BadRequest, "hub.ingress.invalid_capsule", "capsule is required.");
        }
        var capsuleBytes = Encoding.UTF8.GetByteCount(request.Capsule);
        if (capsuleBytes > options.MaxCapsuleBytes)
        {
            return Error(StatusCodes.Status413PayloadTooLarge, "hub.ingress.capsule_too_large", "capsule exceeds the configured limit.");
        }
        var digest = string.IsNullOrWhiteSpace(request.ContentDigest)
            ? ""
            : HubIngressQueueService.NormalizeDigest(request.ContentDigest);
        if (digest.Length != 71 || digest[7..].Any(character =>
                character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            return Error(StatusCodes.Status400BadRequest, "hub.ingress.invalid_digest", "contentDigest must be a SHA-256 digest.");
        }
        var actual = $"sha256:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request.Capsule))).ToLowerInvariant()}";
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(digest),
            Encoding.ASCII.GetBytes(actual))
            ? null
            : Error(StatusCodes.Status400BadRequest, "hub.ingress.digest_mismatch", "contentDigest does not match capsule.");
    }

    private static bool IsBoundedKey(string value) =>
        value.Length > 0 && value.All(character =>
            character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '.' or '_' or ':' or '-');

    private static IResult Error(
        int statusCode,
        string code,
        string detail,
        int? retryAfterSeconds = null)
    {
        Dictionary<string, object?> extensions = new(StringComparer.Ordinal)
        {
            ["code"] = code
        };
        if (retryAfterSeconds is not null)
        {
            extensions["retryAfterSeconds"] = retryAfterSeconds.Value;
        }
        return TypedResults.Problem(
            title: "Hub ingress request rejected.",
            detail: detail,
            statusCode: statusCode,
            extensions: extensions);
    }
}
