using Luthn.Core.Common;
using Luthn.Core.Ingestion;

namespace Luthn.Core.Tests;

public sealed class PluginIngestionContractTests
{
    [Theory]
    [InlineData(IngestionSourceKind.Email)]
    [InlineData(IngestionSourceKind.Messenger)]
    [InlineData(IngestionSourceKind.Document)]
    [InlineData(IngestionSourceKind.LocalFile)]
    [InlineData(IngestionSourceKind.AgentChat)]
    public void SourceIdentitySupportsPlannedPluginSourceKinds(IngestionSourceKind kind)
    {
        var identity = new IngestionSourceIdentity(
            "plugin-mail",
            "gmail",
            kind,
            "external-1",
            "Inbox item");

        Assert.Equal(kind, identity.SourceKind);
        Assert.Equal("plugin-mail", identity.PluginId);
    }

    [Fact]
    public void SourceIdentityRejectsAmbiguousTokens()
    {
        Assert.Throws<ArgumentException>(() =>
            new IngestionSourceIdentity(
                "plugin mail",
                "gmail",
                IngestionSourceKind.Email,
                "external-1"));
    }

    [Fact]
    public void EnvelopeRequiresSha256DigestAndCarriesMetadataOnly()
    {
        Assert.Throws<ArgumentException>(() =>
            new PluginIngestionEnvelope(
                new PublicRecordId("ingestion-1"),
                CreateIdentity(),
                CreateConsent(),
                "raw customer message",
                IngestionPayloadClass.RawSource,
                new IngestionRetryState(0, 3),
                DateTimeOffset.Parse("2026-01-01T00:00:00Z")));
    }

    [Fact]
    public void EnvelopeNormalizesDigestAndCoreTags()
    {
        var envelope = new PluginIngestionEnvelope(
            new PublicRecordId("ingestion-1"),
            CreateIdentity(),
            CreateConsent(),
            "SHA256:ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789",
            IngestionPayloadClass.MetadataOnly,
            new IngestionRetryState(1, 3, DateTimeOffset.Parse("2026-01-01T01:00:00Z"), "timeout"),
            DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
            [" Ops ", "ops", "", "Email"],
            "message/rfc822",
            1024);

        Assert.Equal("sha256:abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789", envelope.ContentDigest);
        Assert.Equal(["Ops", "Email"], envelope.CoreTags);
        Assert.True(envelope.Retry.CanRetry);
        Assert.Equal(IngestionPayloadClass.MetadataOnly, envelope.PayloadClass);
        Assert.Equal("message/rfc822", envelope.PayloadMediaType);
    }

    [Fact]
    public void RetryStateWaitsForNextAttemptAndStopsAtMaxAttempts()
    {
        var retry = new IngestionRetryState(
            1,
            3,
            DateTimeOffset.Parse("2026-01-01T01:00:00Z"),
            "timeout");

        Assert.False(retry.IsReady(DateTimeOffset.Parse("2026-01-01T00:59:59Z")));
        Assert.True(retry.IsReady(DateTimeOffset.Parse("2026-01-01T01:00:00Z")));

        var secondFailure = retry.RecordFailure(
            "transient-timeout",
            DateTimeOffset.Parse("2026-01-01T02:00:00Z"));
        var exhausted = secondFailure.RecordFailure("transient-timeout");

        Assert.Equal(3, exhausted.AttemptCount);
        Assert.True(exhausted.IsExhausted);
        Assert.False(exhausted.IsReady(DateTimeOffset.Parse("2026-01-01T03:00:00Z")));
        Assert.Null(exhausted.NextAttemptAt);
        Assert.Throws<InvalidOperationException>(() => exhausted.RecordFailure("transient-timeout"));
    }

    [Fact]
    public void OrderingStateDefinesPartitionAndMonotonicSequence()
    {
        var previous = new IngestionOrderingState(
            "plugin-mail:gmail:thread-1",
            41,
            DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var next = new IngestionOrderingState(
            "plugin-mail:gmail:thread-1",
            42,
            DateTimeOffset.Parse("2026-01-01T00:00:01Z"));
        var otherPartition = new IngestionOrderingState(
            "plugin-mail:gmail:thread-2",
            43,
            DateTimeOffset.Parse("2026-01-01T00:00:02Z"));

        Assert.True(next.IsAfter(previous));
        Assert.False(otherPartition.IsAfter(previous));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new IngestionOrderingState(
                "plugin-mail:gmail:thread-1",
                -1,
                DateTimeOffset.Parse("2026-01-01T00:00:00Z")));
    }

    [Fact]
    public void EnvelopeCarriesOrderingAndDeadLetterMetadataOnly()
    {
        var retry = new IngestionRetryState(3, 3, lastErrorClass: "transient-timeout");
        var deadLetter = new IngestionDeadLetterState(
            IngestionDeadLetterReason.RetryExhausted,
            DateTimeOffset.Parse("2026-01-01T03:00:00Z"),
            "transient-timeout",
            "retry-exhausted");

        var envelope = new PluginIngestionEnvelope(
            new PublicRecordId("ingestion-dead-letter-1"),
            CreateIdentity(),
            CreateConsent(),
            "sha256:abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789",
            IngestionPayloadClass.MetadataOnly,
            retry,
            DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
            ordering: new IngestionOrderingState(
                "plugin-mail:gmail:message-1",
                7,
                DateTimeOffset.Parse("2026-01-01T00:00:00Z")),
            deadLetter: deadLetter);

        Assert.True(envelope.IsDeadLettered);
        Assert.Equal("plugin-mail:gmail:message-1", envelope.Ordering?.PartitionKey);
        Assert.Equal(7, envelope.Ordering?.SequenceNumber);
        Assert.Equal(IngestionDeadLetterReason.RetryExhausted, envelope.DeadLetter?.Reason);
        Assert.Equal("retry-exhausted", envelope.DeadLetter?.DiagnosticCode);
    }

    [Fact]
    public void RetryExhaustedDeadLetterRequiresExhaustedRetryState()
    {
        Assert.Throws<ArgumentException>(() =>
            new PluginIngestionEnvelope(
                new PublicRecordId("ingestion-active-retry"),
                CreateIdentity(),
                CreateConsent(),
                "sha256:abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789",
                IngestionPayloadClass.MetadataOnly,
                new IngestionRetryState(1, 3),
                DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
                deadLetter: new IngestionDeadLetterState(
                    IngestionDeadLetterReason.RetryExhausted,
                    DateTimeOffset.Parse("2026-01-01T03:00:00Z"),
                    "transient-timeout")));
    }

    [Theory]
    [InlineData("sha256:abc123")]
    [InlineData("sha256:ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF012345678Z")]
    public void EnvelopeRejectsMalformedSha256Digest(string digest)
    {
        Assert.Throws<ArgumentException>(() =>
            new PluginIngestionEnvelope(
                new PublicRecordId("ingestion-1"),
                CreateIdentity(),
                CreateConsent(),
                digest,
                IngestionPayloadClass.MetadataOnly,
                new IngestionRetryState(0, 3),
                DateTimeOffset.Parse("2026-01-01T00:00:00Z")));
    }

    [Fact]
    public void RetryStateRejectsImpossibleAttempts()
    {
        Assert.Throws<ArgumentException>(() => new IngestionRetryState(4, 3));
    }

    private static IngestionSourceIdentity CreateIdentity() =>
        new(
            "plugin-mail",
            "gmail",
            IngestionSourceKind.Email,
            "message-1",
            "Support thread");

    private static IngestionConsent CreateConsent() =>
        new(
            IngestionConsentKind.UserGranted,
            "user-1",
            DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
}
