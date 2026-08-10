using System.Text.Json;
using System.Text.Json.Nodes;
using Luthn.Sdk.Console;
using Luthn.Sdk.Sync;

namespace Luthn.Sdk.Tests;

public sealed class ConsoleSessionContractTests
{
    [Fact]
    public void SessionContractContainsOnlyBoundedServerDerivedState()
    {
        var response = new ConsoleSessionDto(
            ConsoleAccessMode.LocalAuto,
            ConsoleSessionState.Active,
            DateTimeOffset.Parse("2026-08-10T12:00:00Z"),
            DateTimeOffset.Parse("2026-08-10T10:15:00Z"),
            [ConsoleCapability.AccessReview, ConsoleCapability.AuditRead],
            "continue",
            true);

        var json = JsonSerializer.Serialize(response);

        Assert.Contains("\"mode\":\"LocalAuto\"", json, StringComparison.Ordinal);
        Assert.Contains("\"state\":\"Active\"", json, StringComparison.Ordinal);
        Assert.Contains("\"serverDerived\":true", json, StringComparison.Ordinal);
        AssertForbiddenFieldsAbsent(json);
    }

    [Theory]
    [InlineData("credential")]
    [InlineData("serviceToken")]
    [InlineData("decisionToken")]
    [InlineData("tenantId")]
    [InlineData("organizationId")]
    [InlineData("workspaceId")]
    [InlineData("prompt")]
    [InlineData("transcript")]
    [InlineData("raw")]
    [InlineData("localPath")]
    public void SessionContractRejectsUnknownSensitiveFields(string fieldName)
    {
        var node = JsonNode.Parse("""
            {
              "mode": "CloudLoginRequired",
              "state": "LoginRequired",
              "expiresAt": null,
              "idleExpiresAt": null,
              "capabilities": [],
              "nextAction": "cloud-login",
              "serverDerived": true
            }
            """)!.AsObject();
        node[fieldName] = "must-not-be-accepted";

        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<ConsoleSessionDto>(node.ToJsonString()));
    }

    private static void AssertForbiddenFieldsAbsent(string json)
    {
        string[] forbidden =
        [
            "credential", "serviceToken", "decisionToken", "tenantId", "organizationId",
            "workspaceId", "prompt", "transcript", "raw", "localPath"
        ];

        foreach (var value in forbidden)
        {
            Assert.DoesNotContain(value, json, StringComparison.OrdinalIgnoreCase);
        }
    }
}

public sealed class ConsoleEnrollmentContractTests
{
    [Fact]
    public void EnrollmentContractRemainsBoundedAndTenantNeutral()
    {
        var response = new ConsoleEnrollmentDto(
            InstallationEnrollmentState.Pending,
            ConsoleEnrollmentAdapter.Fake,
            DateTimeOffset.Parse("2026-08-10T10:10:00Z"),
            "0123456789abcdef",
            ["console-login.v1"],
            "Luthn Cloud",
            "verify-enrollment",
            true);

        var json = JsonSerializer.Serialize(response);

        Assert.Contains("\"state\":\"Pending\"", json, StringComparison.Ordinal);
        Assert.Contains("\"installationFingerprint\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("organization", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("workspace", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("credential", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("prompt", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("transcript", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("localPath", json, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class ConsoleCloudLoginContractTests
{
    [Fact]
    public void LoginStatusExposesNoCallerSelectableAuthorityFields()
    {
        var response = new ConsoleCloudLoginDto(
            ConsoleCloudLoginProvider.Fake,
            true,
            ConsoleSessionState.LoginRequired,
            ConsoleMembershipState.Active,
            ConsoleEntitlementState.Active,
            [ConsoleCapability.AuditRead],
            "cloud-login",
            true);

        var json = JsonSerializer.Serialize(response);

        Assert.Contains("\"provider\":\"Fake\"", json, StringComparison.Ordinal);
        Assert.Contains("\"serverDerived\":true", json, StringComparison.Ordinal);
        Assert.DoesNotContain("organization", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("workspace", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("tenant", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("userId", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("credential", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("prompt", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("transcript", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("localPath", json, StringComparison.OrdinalIgnoreCase);
    }
}
