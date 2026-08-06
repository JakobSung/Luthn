using System.Text.Json;
using System.Text.Json.Nodes;
using Luthn.Sdk.Access;
using Luthn.Sdk.Audit;
using Luthn.Sdk.Sync;

namespace Luthn.Sdk.Tests;

public sealed class CloudContractTests
{
    [Fact]
    public void CloudNeutralContractsSerializeOnlyBoundedFields()
    {
        var now = DateTimeOffset.Parse("2026-08-06T00:00:00Z");
        var challenge = new InstallationEnrollmentChallengeDto(
            "enrollment-1",
            new Uri("https://cloud.example/activate"),
            "ABCD-EFGH",
            now.AddMinutes(10),
            5);
        var status = new InstallationEnrollmentStatusDto(
            "enrollment-1",
            InstallationEnrollmentState.Approved,
            now.AddMinutes(10),
            "installation-1",
            null);
        var capabilities = new InstallationCapabilitySetDto(
            CloudSyncContractVersions.V2,
            ["safe-projection.v2", "checkpoint.v1"],
            now);
        var receipt = new SafeProjectionSyncReceiptDto(
            "operation-1",
            "memory-1",
            2,
            "acknowledged",
            false,
            now,
            null);
        var checkpoint = new SafeProjectionSyncCheckpointDto(
            "checkpoint-1",
            "operation-1",
            now);

        var json = JsonSerializer.Serialize(new { challenge, status, capabilities, receipt, checkpoint });

        Assert.Contains("\"enrollmentId\"", json, StringComparison.Ordinal);
        Assert.Contains("\"supportedCapabilities\"", json, StringComparison.Ordinal);
        Assert.Contains("\"operationId\"", json, StringComparison.Ordinal);
        Assert.Contains("\"checkpoint\"", json, StringComparison.Ordinal);
        AssertForbiddenTokensAbsent(json);
    }

    internal static SafeProjectionSyncBatchDto CreateBatch()
    {
        var now = DateTimeOffset.Parse("2026-08-06T00:00:00Z");
        return new SafeProjectionSyncBatchDto(
            CloudSyncContractVersions.V2,
            "batch-1",
            ["safe-projection.v2"],
            [
                new SafeProjectionSyncEnvelopeV2Dto(
                    "memory-1",
                    2,
                    "Upsert",
                    "Release decision",
                    "Use the approved release process.",
                    ["release"],
                    "shared-memory-safe-projection",
                    "metadata-only",
                    "safe-projection-only",
                    now,
                    now,
                    now,
                    now.AddYears(1))
            ],
            now);
    }

    internal static void AssertForbiddenTokensAbsent(string json)
    {
        string[] forbidden =
        [
            "vault", "encryptedPayload", "credential", "secret", "prompt", "transcript",
            "workingDirectory", "localPath", "organizationId", "workspaceId"
        ];

        foreach (var token in forbidden)
        {
            Assert.DoesNotContain(token, json, StringComparison.OrdinalIgnoreCase);
        }
    }
}

public sealed class SafeProjectionAuthorityTests
{
    [Fact]
    public void VersionTwoProjectionSeparatesAuthenticatedAuthorityFromPayload()
    {
        var authority = new AuthenticatedInstallationAuthorityDto(
            "installation-1",
            "authenticated-installation",
            DateTimeOffset.Parse("2026-08-06T00:00:00Z"));
        var batch = CloudContractTests.CreateBatch();

        var authorityJson = JsonSerializer.Serialize(authority);
        var batchJson = JsonSerializer.Serialize(batch);

        Assert.Contains("\"installationId\"", authorityJson, StringComparison.Ordinal);
        Assert.DoesNotContain("organizationId", authorityJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("workspaceId", authorityJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("installationId", batchJson, StringComparison.OrdinalIgnoreCase);
        CloudContractTests.AssertForbiddenTokensAbsent(batchJson);
    }
}

public sealed class ForbiddenFieldContractTests
{
    public static TheoryData<string> ForbiddenRootFields => new()
    {
        "raw", "rawSource", "vaultContent", "encryptedPayload", "credential", "secret",
        "prompt", "transcript", "workingDirectory", "localPath", "organizationId", "workspaceId",
        "content"
    };

    [Theory]
    [MemberData(nameof(ForbiddenRootFields))]
    public void VersionTwoBatchRejectsForbiddenAndUnknownContentFields(string fieldName)
    {
        var node = JsonNode.Parse(JsonSerializer.Serialize(CloudContractTests.CreateBatch()))!.AsObject();
        node[fieldName] = "must-not-be-accepted";

        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<SafeProjectionSyncBatchDto>(node.ToJsonString()));
    }

    [Theory]
    [MemberData(nameof(ForbiddenRootFields))]
    public void VersionTwoEnvelopeRejectsForbiddenAndUnknownContentFields(string fieldName)
    {
        var node = JsonNode.Parse(JsonSerializer.Serialize(CloudContractTests.CreateBatch()))!.AsObject();
        node["items"]![0]![fieldName] = "must-not-be-accepted";

        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<SafeProjectionSyncBatchDto>(node.ToJsonString()));
    }

    [Fact]
    public void ReceiptErrorAndAuditMetadataRemainContentFree()
    {
        var now = DateTimeOffset.Parse("2026-08-06T00:00:00Z");
        var error = new BoundedErrorDto("sync.retry_later", true, 30, "correlation-1");
        var receipt = new SafeProjectionSyncReceiptDto(
            "operation-1", "memory-1", 2, "failed", true, now, error);
        var audit = new AuditEventMetadataDto(
            "audit-1", now, AuditEventCategory.Publication, "Workspace", "installation",
            "installation", "publication.failed", "memory-1", "safe_projection", "failed",
            "correlation-1", 1, "metadata-only", "safe-projection-only");

        var json = JsonSerializer.Serialize(new { receipt, error, audit });

        CloudContractTests.AssertForbiddenTokensAbsent(json);
        Assert.DoesNotContain("detail", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("message", json, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class SerializationCompatibilityTests
{
    [Fact]
    public void VersionOneProjectionAndAgentSensitiveAccessContractsStayUnchanged()
    {
        var now = DateTimeOffset.Parse("2026-08-06T00:00:00Z");
        var legacy = new SafeProjectionSyncEnvelopeDto(
            1,
            "default",
            "instance-1",
            "memory-1",
            1,
            "Upsert",
            "Release decision",
            "Use the approved release process.",
            ["release"],
            "shared-memory-safe-projection",
            "metadata-only",
            "safe-projection-only",
            now,
            now,
            now,
            null);
        var access = new SensitiveAccessRequestDto(
            "access-1",
            "sensitive-ref-1",
            "Pending",
            "agent",
            now,
            null,
            null,
            false,
            "pending-approval");

        var legacyJson = JsonSerializer.Serialize(legacy);
        var accessJson = JsonSerializer.Serialize(access);

        Assert.Contains("\"workspaceId\":\"default\"", legacyJson, StringComparison.Ordinal);
        Assert.Contains("\"originInstanceId\":\"instance-1\"", legacyJson, StringComparison.Ordinal);
        Assert.DoesNotContain("capabilities", legacyJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("requestReason", accessJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("decisionReason", accessJson, StringComparison.OrdinalIgnoreCase);
    }
}
