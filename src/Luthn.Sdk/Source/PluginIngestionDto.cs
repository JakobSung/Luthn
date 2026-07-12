using System.Text.Json.Serialization;

namespace Luthn.Sdk.Source;

public sealed record PluginIngestionEnvelopeDto(
    [property: JsonPropertyName("sourceIdentity")] IngestionSourceIdentityDto SourceIdentity,
    [property: JsonPropertyName("consent")] IngestionConsentDto Consent,
    [property: JsonPropertyName("contentDigest")] string ContentDigest,
    [property: JsonPropertyName("payloadClass")] string PayloadClass,
    [property: JsonPropertyName("retry")] IngestionRetryStateDto Retry,
    [property: JsonPropertyName("receivedAt")] DateTimeOffset ReceivedAt,
    [property: JsonPropertyName("coreTags")] IReadOnlyList<string> CoreTags,
    [property: JsonPropertyName("payloadMediaType")] string? PayloadMediaType = null,
    [property: JsonPropertyName("payloadSizeBytes")] long? PayloadSizeBytes = null,
    [property: JsonPropertyName("ordering")] IngestionOrderingStateDto? Ordering = null,
    [property: JsonPropertyName("deadLetter")] IngestionDeadLetterStateDto? DeadLetter = null)
{
    public PluginIngestionEnvelopeDto(
        IngestionSourceIdentityDto sourceIdentity,
        IngestionConsentDto consent,
        string contentDigest,
        string payloadClass,
        IngestionRetryStateDto retry,
        DateTimeOffset receivedAt,
        IReadOnlyList<string> coreTags,
        string? payloadMediaType,
        long? payloadSizeBytes)
        : this(
            sourceIdentity,
            consent,
            contentDigest,
            payloadClass,
            retry,
            receivedAt,
            coreTags,
            payloadMediaType,
            payloadSizeBytes,
            Ordering: null,
            DeadLetter: null)
    {
    }

    public void Deconstruct(
        out IngestionSourceIdentityDto sourceIdentity,
        out IngestionConsentDto consent,
        out string contentDigest,
        out string payloadClass,
        out IngestionRetryStateDto retry,
        out DateTimeOffset receivedAt,
        out IReadOnlyList<string> coreTags,
        out string? payloadMediaType,
        out long? payloadSizeBytes)
    {
        sourceIdentity = SourceIdentity;
        consent = Consent;
        contentDigest = ContentDigest;
        payloadClass = PayloadClass;
        retry = Retry;
        receivedAt = ReceivedAt;
        coreTags = CoreTags;
        payloadMediaType = PayloadMediaType;
        payloadSizeBytes = PayloadSizeBytes;
    }
}

public sealed record IngestionSourceIdentityDto(
    [property: JsonPropertyName("pluginId")] string PluginId,
    [property: JsonPropertyName("sourceSystem")] string SourceSystem,
    [property: JsonPropertyName("sourceKind")] string SourceKind,
    [property: JsonPropertyName("externalSourceId")] string ExternalSourceId,
    [property: JsonPropertyName("displayName")] string? DisplayName = null);

public sealed record IngestionConsentDto(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("grantedBy")] string GrantedBy,
    [property: JsonPropertyName("grantedAt")] DateTimeOffset GrantedAt);

public sealed record IngestionRetryStateDto(
    [property: JsonPropertyName("attemptCount")] int AttemptCount,
    [property: JsonPropertyName("maxAttempts")] int MaxAttempts,
    [property: JsonPropertyName("nextAttemptAt")] DateTimeOffset? NextAttemptAt = null,
    [property: JsonPropertyName("lastErrorClass")] string? LastErrorClass = null);

public sealed record IngestionOrderingStateDto(
    [property: JsonPropertyName("partitionKey")] string PartitionKey,
    [property: JsonPropertyName("sequenceNumber")] long SequenceNumber,
    [property: JsonPropertyName("enqueuedAt")] DateTimeOffset EnqueuedAt,
    [property: JsonPropertyName("requiresOrderedProcessing")] bool RequiresOrderedProcessing = true);

public sealed record IngestionDeadLetterStateDto(
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("deadLetteredAt")] DateTimeOffset DeadLetteredAt,
    [property: JsonPropertyName("errorClass")] string ErrorClass,
    [property: JsonPropertyName("diagnosticCode")] string? DiagnosticCode = null);
