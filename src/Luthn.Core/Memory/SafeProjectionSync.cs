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
    public const int Current = 1;
}

public sealed record SafeProjectionSyncEnvelope(
    int ContractVersion,
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
    string? ErrorCode = null);

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
        return new SafeProjectionSyncEnvelope(
            SafeProjectionSyncContractVersions.Current,
            RequiredToken(originInstanceId, nameof(originInstanceId)),
            RequiredToken(localRecordId, nameof(localRecordId)),
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
        string originInstanceId,
        string localRecordId,
        long revision,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt,
        DateTimeOffset decidedAt)
    {
        ValidateRevision(revision);
        return new SafeProjectionSyncEnvelope(
            SafeProjectionSyncContractVersions.Current,
            RequiredToken(originInstanceId, nameof(originInstanceId)),
            RequiredToken(localRecordId, nameof(localRecordId)),
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
        return $"{envelope.OriginInstanceId}:{envelope.LocalRecordId}:{envelope.Revision}:{envelope.Operation}";
    }

    private static void ValidateRevision(long revision)
    {
        if (revision < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(revision), "Sync revision must be positive.");
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
}
