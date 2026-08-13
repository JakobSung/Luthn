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
              "usedReads": 0,
              "maxReads": 1
            }
            """);

        Assert.Null(legacy.StatusCode);
        Assert.Null(legacy.UsedReads);
        Assert.Equal("grant-active", current!.StatusCode);
        Assert.Equal(1, current.RemainingReads);
        Assert.Equal(0, current.UsedReads);
        Assert.Equal(1, current.MaxReads);
        Assert.DoesNotContain("usedReads", JsonSerializer.Serialize(legacy), StringComparison.Ordinal);
        Assert.Contains("\"usedReads\":0", JsonSerializer.Serialize(current), StringComparison.Ordinal);
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
              "usedReads": 1,
              "maxReads": 1
            }
            """);

        Assert.Equal("grant-consumed", result!.StatusCode);
        Assert.Equal(0, result.RemainingReads);
        Assert.Equal(1, result.UsedReads);
        Assert.Null(result.RedactedOutput);
        Assert.Equal("metadata-only", result.PayloadClass);
        Assert.Contains("\"usedReads\":1", JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }

    [Fact]
    public void TombstoneContractStructurallyOmitsContentBearingProperties()
    {
        var tombstone = new SensitiveAccessTombstoneDto(
            "access-expired",
            "Expired",
            "expired-no-output");

        var properties = typeof(SensitiveAccessTombstoneDto)
            .GetProperties()
            .Select(property => property.Name)
            .ToArray();
        var json = JsonSerializer.Serialize(tombstone);

        Assert.Equal(["Id", "Status", "OutputPolicy"], properties);
        Assert.DoesNotContain("reference", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("reason", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("decision", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("summary", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("payload", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cipher", json, StringComparison.OrdinalIgnoreCase);
    }
}
