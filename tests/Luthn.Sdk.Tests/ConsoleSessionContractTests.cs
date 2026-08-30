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
              "mode": "LocalAuto",
              "state": "Anonymous",
              "expiresAt": null,
              "idleExpiresAt": null,
              "capabilities": [],
              "nextAction": "arm-local-session",
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
