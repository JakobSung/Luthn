using System.Text.Json;
using Luthn.AgentConnector.Http;
using Luthn.Sdk.Memory;
using Luthn.Sdk.Provenance;

namespace Luthn.McpServer.Tools;

public sealed class CreateSharedMemoryTool(ILuthnAgentClient client) : ILuthnMcpTool
{
    public string Name => "create_shared_memory";

    public async Task<object> InvokeAsync(
        JsonElement arguments,
        CancellationToken cancellationToken = default)
    {
        CollectionProvenanceClaimsDto? provenance = null;
        if (arguments.TryGetProperty("provenance", out var provenanceElement) &&
            provenanceElement.ValueKind is JsonValueKind.Object)
        {
            McpToolArguments.RejectUnknownProperties(
                provenanceElement,
                "userId",
                "agentId",
                "applicationId",
                "pluginId",
                "connectorId",
                "connectorVersion",
                "collectedAt");
            provenance = new CollectionProvenanceClaimsDto(
                McpToolArguments.ReadOptionalString(provenanceElement, "userId"),
                McpToolArguments.ReadOptionalString(provenanceElement, "agentId"),
                McpToolArguments.ReadOptionalString(provenanceElement, "applicationId") ?? "luthn-mcp-server",
                McpToolArguments.ReadOptionalString(provenanceElement, "pluginId"),
                McpToolArguments.ReadOptionalString(provenanceElement, "connectorId") ?? "luthn-mcp-server",
                McpToolArguments.ReadOptionalString(provenanceElement, "connectorVersion"),
                McpToolArguments.ReadOptionalDateTimeOffset(provenanceElement, "collectedAt"));
        }
        else if (arguments.TryGetProperty("provenance", out provenanceElement) &&
                 provenanceElement.ValueKind is not JsonValueKind.Null)
        {
            throw new ArgumentException("provenance must be an object.", nameof(arguments));
        }
        else
        {
            provenance = new CollectionProvenanceClaimsDto(
                ApplicationId: "luthn-mcp-server",
                ConnectorId: "luthn-mcp-server");
        }

        var request = new CreateSharedMemoryItemRequestDto(
            McpToolArguments.ReadRequiredString(arguments, "title"),
            McpToolArguments.ReadRequiredString(arguments, "safeSummary"),
            McpToolArguments.ReadCoreTags(arguments),
            McpToolArguments.ReadOptionalString(arguments, "visibility") ?? "SharedAcrossAgents",
            McpToolArguments.ReadOptionalString(arguments, "retentionKind") ?? "Durable",
            McpToolArguments.ReadOptionalDateTimeOffset(arguments, "expiresAt"),
            McpToolArguments.ReadOptionalString(arguments, "sourceSessionId"),
            McpToolArguments.ReadOptionalString(arguments, "sensitivity") ?? "Public",
            McpToolArguments.ReadOptionalString(arguments, "projectKey"),
            McpToolArguments.ReadOptionalString(arguments, "taskKey"),
            McpToolArguments.ReadTags(arguments, "topicTags"),
            provenance);

        return await client.CreateSharedMemoryItemAsync(request, cancellationToken);
    }
}
