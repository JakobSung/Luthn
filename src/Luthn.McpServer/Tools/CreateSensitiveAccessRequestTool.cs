using System.Text.Json;
using Luthn.AgentConnector.Http;
using Luthn.Sdk.Access;

namespace Luthn.McpServer.Tools;

public sealed class CreateSensitiveAccessRequestTool(ILuthnAgentClient client) : ILuthnMcpTool
{
    public string Name => "create_sensitive_access_request";

    public Task<object> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        var request = new SensitiveAccessCreateRequestDto(
            McpToolArguments.ReadRequiredString(arguments, "sensitiveReferenceId"),
            McpToolArguments.ReadRequiredString(arguments, "reason"),
            McpToolArguments.ReadRequiredString(arguments, "sessionId"),
            McpToolArguments.ReadRequiredInt(arguments, "expiresInSeconds"));
        return InvokeAsync(request, cancellationToken);
    }

    private async Task<object> InvokeAsync(SensitiveAccessCreateRequestDto request, CancellationToken cancellationToken) =>
        await client.CreateSensitiveAccessRequestAsync(request, cancellationToken);
}
