using System.Text.Json;
using Luthn.AgentConnector.Http;

namespace Luthn.McpServer.Tools;

public sealed class GetSensitiveAccessResultTool(ILuthnAgentClient client) : ILuthnMcpTool
{
    public string Name => "get_sensitive_access_result";

    public async Task<object> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken = default) =>
        await client.GetSensitiveAccessResultAsync(
            McpToolArguments.ReadRequiredString(arguments, "id"),
            cancellationToken);
}
