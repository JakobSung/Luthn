using System.Text.Json;
using Luthn.Sdk.Access;

namespace Luthn.Sdk.Tests;

public sealed class SensitiveAccessLifecycleContractTests
{
    [Fact]
    public void RequestLifecycleMetadataDeserializesAdditivelyWithoutChangingLegacyConstructor()
    {
        var legacy = new SensitiveAccessRequestDto(
            "access-1",
            "reference-1",
            "Approved",
            "agent",
            DateTimeOffset.Parse("2026-08-11T00:00:00Z"),
            "operator",
            DateTimeOffset.Parse("2026-08-11T00:01:00Z"),
            true,
            "approved-redacted-output-available");
        var current = JsonSerializer.Deserialize<SensitiveAccessRequestDto>("""
            {
              "id": "access-1",
              "sensitiveReferenceId": "reference-1",
              "status": "Approved",
              "requestedBy": "agent",
              "createdAt": "2026-08-11T00:00:00Z",
              "decidedBy": "operator",
              "decidedAt": "2026-08-11T00:01:00Z",
              "redactedOutputAvailable": true,
              "outputPolicy": "approved-redacted-output-available",
              "statusCode": "grant-active",
              "requestExpiresAt": "2026-08-11T00:10:00Z",
              "grantExpiresAt": "2026-08-11T00:11:00Z",
              "remainingReads": 1,
              "maxReads": 1
            }
            """);

        Assert.Null(legacy.StatusCode);
        Assert.Equal("grant-active", current!.StatusCode);
        Assert.Equal(1, current.RemainingReads);
        Assert.Equal(1, current.MaxReads);
    }

    [Fact]
    public void ResultLifecycleMetadataDeserializesWithoutSensitiveContentExpansion()
    {
        var result = JsonSerializer.Deserialize<SensitiveAccessResultDto>("""
            {
              "id": "access-1",
              "sensitiveReferenceId": "reference-1",
              "status": "Approved",
              "outputPolicy": "approved-redacted-output-unavailable",
              "redactedOutputAvailable": false,
              "redactedOutput": null,
              "payloadClass": "metadata-only",
              "redactionState": "approved-redacted-output-unavailable",
              "reasons": ["No output."],
              "statusCode": "grant-consumed",
              "remainingReads": 0,
              "maxReads": 1
            }
            """);

        Assert.Equal("grant-consumed", result!.StatusCode);
        Assert.Equal(0, result.RemainingReads);
        Assert.Null(result.RedactedOutput);
        Assert.Equal("metadata-only", result.PayloadClass);
    }
}
