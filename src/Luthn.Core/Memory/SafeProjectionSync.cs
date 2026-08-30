using Luthn.Core.Classification;

namespace Luthn.Core.Memory;

public enum ExternalPublicationState
{
    LocalOnly,
    ApprovedForExternal,
    Revoked
}

public enum SafeProjectionSyncOperation
{
    Upsert,
    Revoke
}

public enum SafeProjectionSyncTransportState
{
    Disabled,
    NotConnected,
    Ready
}

public static class SafeProjectionSyncContractVersions
{
    public const int Current = 2;
}

public sealed record SafeProjectionSyncEnvelope(
    int ContractVersion,
    string WorkspaceId,
    string OriginInstanceId,
    string LocalRecordId,
    long Revision,
    SafeProjectionSyncOperation Operation,
    string? Title,
    string? SafeSummary,
    IReadOnlyList<string> CoreTags,
    string ProjectionKind,
    string PayloadClass,
    string RedactionState,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset DecidedAt,
    DateTimeOffset? ExpiresAt);

public sealed record SafeProjectionSyncTransportResult(
    bool Accepted,
    string? Checkpoint = null,
    string? ErrorCode = null,
    bool Retryable = false);

public interface ISafeProjectionSyncTransport
{
    string Name { get; }

    SafeProjectionSyncTransportState State { get; }

    Task<SafeProjectionSyncTransportResult> SendAsync(
        SafeProjectionSyncEnvelope envelope,
        CancellationToken cancellationToken);
}

public sealed class DisabledSafeProjectionSyncTransport : ISafeProjectionSyncTransport
{
    public string Name => "disabled";

    public SafeProjectionSyncTransportState State => SafeProjectionSyncTransportState.Disabled;

    public Task<SafeProjectionSyncTransportResult> SendAsync(
        SafeProjectionSyncEnvelope envelope,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        return Task.FromResult(new SafeProjectionSyncTransportResult(
            Accepted: false,
            ErrorCode: "transport.disabled"));
    }
}

public enum HubRelayTransportState
{
    Disabled,
    Disconnected,
    Stale,
    Revoked,
    Ready
}

public sealed record HubRelayTransportResult(
    bool Accepted,
    string? Checkpoint = null,
    string? ErrorCode = null);

public interface IHubOutboundRelayTransport
{
    string Name { get; }
    HubRelayTransportState State { get; }
    Task<HubRelayTransportResult> SendSafeProjectionAsync(
        SafeProjectionSyncEnvelope envelope,
        CancellationToken cancellationToken);
}

public sealed class DisabledHubOutboundRelayTransport : IHubOutboundRelayTransport
{
    public string Name => "disabled";
    public HubRelayTransportState State => HubRelayTransportState.Disabled;

    public Task<HubRelayTransportResult> SendSafeProjectionAsync(
        SafeProjectionSyncEnvelope envelope,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        return Task.FromResult(new HubRelayTransportResult(false, ErrorCode: "relay.disabled"));
    }
}

public sealed class HubRelaySafeProjectionSyncTransport(IHubOutboundRelayTransport relay)
    : ISafeProjectionSyncTransport
{
    public string Name => $"hub-relay:{BoundToken(relay.Name, 96, "unknown")}";

    public SafeProjectionSyncTransportState State => relay.State switch
    {
        HubRelayTransportState.Disabled => SafeProjectionSyncTransportState.Disabled,
        HubRelayTransportState.Ready => SafeProjectionSyncTransportState.Ready,
        _ => SafeProjectionSyncTransportState.NotConnected
    };

    public async Task<SafeProjectionSyncTransportResult> SendAsync(
        SafeProjectionSyncEnvelope envelope,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (relay.State != HubRelayTransportState.Ready)
        {
            return new SafeProjectionSyncTransportResult(
                false,
                ErrorCode: relay.State switch
                {
                    HubRelayTransportState.Disabled => "relay.disabled",
                    HubRelayTransportState.Stale => "relay.stale",
                    HubRelayTransportState.Revoked => "relay.revoked",
                    _ => "relay.disconnected"
                });
        }
        if (!IsSafeEnvelope(envelope))
        {
            return new SafeProjectionSyncTransportResult(false, ErrorCode: "relay.invalid_envelope");
        }

        var result = await relay.SendSafeProjectionAsync(envelope, cancellationToken);
        return new SafeProjectionSyncTransportResult(
            result.Accepted,
            BoundToken(result.Checkpoint, 512, fallback: null),
            BoundErrorCode(result.ErrorCode));
    }

    private static bool IsSafeEnvelope(SafeProjectionSyncEnvelope envelope)
    {
        if (envelope.ContractVersion != SafeProjectionSyncContractVersions.Current ||
            !string.Equals(envelope.PayloadClass, ExternalMemoryAdapterCatalog.MetadataOnlyPayload, StringComparison.Ordinal) ||
            !string.Equals(envelope.RedactionState, ExternalMemoryAdapterCatalog.SafeProjectionOnly, StringComparison.Ordinal) ||
            !string.Equals(envelope.ProjectionKind, ExternalMemoryAdapterCatalog.SharedMemoryProjection, StringComparison.Ordinal) ||
            envelope.Title is not null ||
            envelope.CoreTags.Count != 0)
        {
            return false;
        }

        return envelope.Operation switch
        {
            SafeProjectionSyncOperation.Revoke => envelope.SafeSummary is null && envelope.ExpiresAt is null,
            SafeProjectionSyncOperation.Upsert =>
                !string.IsNullOrWhiteSpace(envelope.SafeSummary) &&
                envelope.SafeSummary.Length <= 4000,
            _ => false
        };
    }

    private static string? BoundErrorCode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        var normalized = value.Trim().ToLowerInvariant();
        return normalized.Length <= 64 && normalized.All(character =>
            character is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or '-')
            ? normalized
            : "relay.failure";
    }

    private static string? BoundToken(string? value, int maxLength, string? fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }
        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }
}

public static class SafeProjectionSyncPolicy
{
    public static bool AllowsPublication(
        ExternalPublicationState publicationState,
        SensitivityLevel sensitivity,
        MemoryVisibility visibility,
        DateTimeOffset? expiresAt,
        DateTimeOffset now) =>
        publicationState == ExternalPublicationState.ApprovedForExternal &&
        ExternalMemoryProjectionPolicy.AllowsExternalMemoryExport(
            sensitivity,
            visibility,
            expiresAt,
            now);

    public static SafeProjectionSyncEnvelope CreateUpsert(
        string workspaceId,
        string originInstanceId,
        string localRecordId,
        long revision,
        string safeSummary,
        ExternalPublicationState publicationState,
        SensitivityLevel sensitivity,
        MemoryVisibility visibility,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt,
        DateTimeOffset decidedAt,
        DateTimeOffset? expiresAt)
    {
        if (!AllowsPublication(publicationState, sensitivity, visibility, expiresAt, updatedAt))
        {
            throw new ArgumentException(
                "External publication requires explicit approval and a public, agent-visible, non-expired safe projection.",
                nameof(publicationState));
        }

        ValidateRevision(revision);
        ValidateTimeline(createdAt, updatedAt, decidedAt, expiresAt);
        return new SafeProjectionSyncEnvelope(
            SafeProjectionSyncContractVersions.Current,
            RequiredExtensionWorkspaceId(workspaceId),
            RequiredOpaqueOriginInstanceId(originInstanceId),
            RequiredOpaqueRecordId(localRecordId),
            revision,
            SafeProjectionSyncOperation.Upsert,
            Title: null,
            RequiredText(safeSummary, nameof(safeSummary)),
            CoreTags: [],
            ExternalMemoryAdapterCatalog.SharedMemoryProjection,
            ExternalMemoryAdapterCatalog.MetadataOnlyPayload,
            ExternalMemoryAdapterCatalog.SafeProjectionOnly,
            createdAt,
            updatedAt,
            decidedAt,
            expiresAt);
    }

    public static SafeProjectionSyncEnvelope CreateRevoke(
        string workspaceId,
        string originInstanceId,
        string localRecordId,
        long revision,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt,
        DateTimeOffset decidedAt)
    {
        ValidateRevision(revision);
        ValidateTimeline(createdAt, updatedAt, decidedAt, expiresAt: null);
        return new SafeProjectionSyncEnvelope(
            SafeProjectionSyncContractVersions.Current,
            RequiredExtensionWorkspaceId(workspaceId),
            RequiredOpaqueOriginInstanceId(originInstanceId),
            RequiredOpaqueRecordId(localRecordId),
            revision,
            SafeProjectionSyncOperation.Revoke,
            Title: null,
            SafeSummary: null,
            CoreTags: [],
            ExternalMemoryAdapterCatalog.SharedMemoryProjection,
            ExternalMemoryAdapterCatalog.MetadataOnlyPayload,
            ExternalMemoryAdapterCatalog.SafeProjectionOnly,
            createdAt,
            updatedAt,
            decidedAt,
            ExpiresAt: null);
    }

    public static string CreateIdempotencyKey(SafeProjectionSyncEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        return $"{envelope.WorkspaceId}:{envelope.OriginInstanceId}:{envelope.LocalRecordId}:{envelope.Revision}:{envelope.Operation}";
    }

    private static void ValidateRevision(long revision)
    {
        if (revision < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(revision), "Sync revision must be positive.");
        }
    }

    /// <summary>
    /// Validates the generic same-network extension routing identities without
    /// accepting a local filesystem path or arbitrary free-form text.
    /// Workspace identifiers retain the public local-installation grammar;
    /// origin and record identifiers are opaque tokens.
    /// </summary>
    public static bool HasValidExtensionIdentity(
        string? workspaceId,
        string? originInstanceId,
        string? localRecordId) =>
        IsExtensionWorkspaceId(workspaceId) &&
        IsOpaqueToken(originInstanceId, 128, rejectLeadingDot: false) &&
        IsOpaqueToken(localRecordId, 128, rejectLeadingDot: true);

    public static bool HasValidTimeline(
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt,
        DateTimeOffset decidedAt,
        DateTimeOffset? expiresAt) =>
        createdAt != default &&
        updatedAt >= createdAt &&
        decidedAt >= updatedAt &&
        (expiresAt is null || expiresAt > decidedAt);

    private static void ValidateTimeline(
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt,
        DateTimeOffset decidedAt,
        DateTimeOffset? expiresAt)
    {
        if (!HasValidTimeline(createdAt, updatedAt, decidedAt, expiresAt))
        {
            throw new ArgumentException("Safe projection timestamps are invalid or out of order.");
        }
    }

    private static string RequiredText(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Safe projection text is required.", parameterName);
        }

        return value.Trim();
    }

    private static string RequiredToken(string value, string parameterName)
    {
        var token = RequiredText(value, parameterName);
        if (token.Any(char.IsWhiteSpace))
        {
            throw new ArgumentException("Safe projection identity cannot contain whitespace.", parameterName);
        }

        return token;
    }

    private static string RequiredExtensionWorkspaceId(string value)
    {
        var workspaceId = RequiredToken(value, nameof(value));
        if (!IsExtensionWorkspaceId(workspaceId))
        {
            throw new ArgumentException(
                "Safe projection workspace identifiers must use the local extension routing grammar.",
                nameof(value));
        }

        return workspaceId;
    }

    private static string RequiredOpaqueOriginInstanceId(string value)
    {
        var originInstanceId = RequiredToken(value, nameof(value));
        if (!IsOpaqueToken(originInstanceId, 128, rejectLeadingDot: false))
        {
            throw new ArgumentException(
                "Safe projection origin identifiers must be opaque tokens.",
                nameof(value));
        }

        return originInstanceId;
    }

    private static string RequiredOpaqueRecordId(string value)
    {
        var recordId = RequiredToken(value, nameof(value));
        if (!IsOpaqueToken(recordId, 128, rejectLeadingDot: true))
        {
            throw new ArgumentException(
                "Safe projection record identifiers must be opaque, path-free tokens.",
                nameof(value));
        }

        return recordId;
    }

    private static bool IsExtensionWorkspaceId(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 160 &&
        char.IsAsciiLetterOrDigit(value[0]) &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or ':' or '@');

    private static bool IsOpaqueToken(string? value, int maxLength, bool rejectLeadingDot) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maxLength &&
        (!rejectLeadingDot || value[0] != '.') &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');
}
