using Luthn.Core.Common;

namespace Luthn.Core.Ingestion;

public sealed class PluginIngestionEnvelope
{
    public PluginIngestionEnvelope(
        PublicRecordId id,
        IngestionSourceIdentity sourceIdentity,
        IngestionConsent consent,
        string contentDigest,
        IngestionPayloadClass payloadClass,
        IngestionRetryState retry,
        DateTimeOffset receivedAt,
        IReadOnlyList<string>? coreTags,
        string? payloadMediaType,
        long? payloadSizeBytes)
        : this(
            id,
            sourceIdentity,
            consent,
            contentDigest,
            payloadClass,
            retry,
            receivedAt,
            coreTags,
            payloadMediaType,
            payloadSizeBytes,
            ordering: null,
            deadLetter: null)
    {
    }

    public PluginIngestionEnvelope(
        PublicRecordId id,
        IngestionSourceIdentity sourceIdentity,
        IngestionConsent consent,
        string contentDigest,
        IngestionPayloadClass payloadClass,
        IngestionRetryState retry,
        DateTimeOffset receivedAt,
        IReadOnlyList<string>? coreTags = null,
        string? payloadMediaType = null,
        long? payloadSizeBytes = null,
        IngestionOrderingState? ordering = null,
        IngestionDeadLetterState? deadLetter = null)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(sourceIdentity);
        ArgumentNullException.ThrowIfNull(consent);
        ArgumentNullException.ThrowIfNull(retry);

        if (payloadSizeBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(payloadSizeBytes), "Payload size cannot be negative.");
        }

        Id = id;
        SourceIdentity = sourceIdentity;
        Consent = consent;
        ContentDigest = RequiredSha256Digest(contentDigest, nameof(contentDigest));
        PayloadClass = payloadClass;
        Retry = retry;
        ReceivedAt = receivedAt;
        CoreTags = NormalizeTags(coreTags ?? []);
        PayloadMediaType = string.IsNullOrWhiteSpace(payloadMediaType) ? null : payloadMediaType.Trim();
        PayloadSizeBytes = payloadSizeBytes;
        Ordering = ordering;
        DeadLetter = ValidateDeadLetter(deadLetter, retry);
    }

    public PublicRecordId Id { get; }

    public IngestionSourceIdentity SourceIdentity { get; }

    public IngestionConsent Consent { get; }

    public string ContentDigest { get; }

    public IngestionPayloadClass PayloadClass { get; }

    public IngestionRetryState Retry { get; }

    public DateTimeOffset ReceivedAt { get; }

    public IReadOnlyList<string> CoreTags { get; }

    public string? PayloadMediaType { get; }

    public long? PayloadSizeBytes { get; }

    public IngestionOrderingState? Ordering { get; }

    public IngestionDeadLetterState? DeadLetter { get; }

    public bool IsDeadLettered => DeadLetter is not null;

    private static string RequiredSha256Digest(string value, string parameterName)
    {
        const string Prefix = "sha256:";

        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Ingestion content digest is required.", parameterName);
        }

        var trimmed = value.Trim();
        if (!trimmed.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Ingestion content digest must use the sha256: prefix.", parameterName);
        }

        var digest = trimmed[Prefix.Length..];
        if (!IsSha256HexDigest(digest))
        {
            throw new ArgumentException("Ingestion content digest must be a sha256: digest with 64 hexadecimal characters.", parameterName);
        }

        return trimmed.ToLowerInvariant();
    }

    private static bool IsSha256HexDigest(string digest) =>
        digest.Length == 64 && digest.All(IsHexDigit);

    private static bool IsHexDigit(char value) =>
        value is >= '0' and <= '9'
            or >= 'a' and <= 'f'
            or >= 'A' and <= 'F';

    private static IReadOnlyList<string> NormalizeTags(IEnumerable<string> tags) =>
        tags
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Select(tag => tag.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static IngestionDeadLetterState? ValidateDeadLetter(
        IngestionDeadLetterState? deadLetter,
        IngestionRetryState retry)
    {
        if (deadLetter is null)
        {
            return null;
        }

        if (deadLetter.Reason == IngestionDeadLetterReason.RetryExhausted && retry.CanRetry)
        {
            throw new ArgumentException("Retry-exhausted dead-letter state requires exhausted retry attempts.", nameof(deadLetter));
        }

        return deadLetter;
    }
}
