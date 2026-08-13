using System.Text.Json;
using Luthn.AgentConnector.Http;
using Luthn.Sdk.Access;

namespace Luthn.McpServer.Tools;

public sealed class RequestProtectedInformationAccessTool(ILuthnAgentClient client) : ILuthnMcpTool
{
    public string Name => "request_protected_information_access";

    public Task<object> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        var request = new ProtectedInformationAccessRequestDto(
            McpToolArguments.ReadRequiredString(arguments, "memoryItemId"),
            McpToolArguments.ReadOptionalString(arguments, "reason"));
        return InvokeAsync(request, cancellationToken);
    }

    private async Task<object> InvokeAsync(
        ProtectedInformationAccessRequestDto request,
        CancellationToken cancellationToken) =>
        await client.RequestProtectedInformationAccessAsync(request, cancellationToken);
}
