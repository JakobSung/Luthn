using System.Text.Json;
using Luthn.AgentConnector.Http;
using Luthn.Sdk.Memory;

namespace Luthn.McpServer.Tools;

public sealed class CreateSharedMemoryTool(ILuthnAgentClient client) : ILuthnMcpTool
{
    public string Name => "create_shared_memory";

    public async Task<object> InvokeAsync(
        JsonElement arguments,
        CancellationToken cancellationToken = default)
    {
        var request = new CreateSharedMemoryItemRequestDto(
            McpToolArguments.ReadRequiredString(arguments, "title"),
            McpToolArguments.ReadRequiredString(arguments, "safeSummary"),
            McpToolArguments.ReadCoreTags(arguments),
            McpToolArguments.ReadOptionalString(arguments, "visibility") ?? "SharedAcrossAgents",
            McpToolArguments.ReadOptionalString(arguments, "retentionKind") ?? "Durable",
            McpToolArguments.ReadOptionalDateTimeOffset(arguments, "expiresAt"),
            McpToolArguments.ReadOptionalString(arguments, "sourceSessionId"),
            McpToolArguments.ReadOptionalString(arguments, "sensitivity") ?? "Public");

        return await client.CreateSharedMemoryItemAsync(request, cancellationToken);
    }
}
