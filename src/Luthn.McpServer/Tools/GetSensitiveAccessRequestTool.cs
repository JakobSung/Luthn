using System.Text.Json;
using Luthn.AgentConnector.Http;

namespace Luthn.McpServer.Tools;

public sealed class GetSensitiveAccessRequestTool(ILuthnClient client) : ILuthnMcpTool
{
    public string Name => "get_sensitive_access_request";

    public async Task<object> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken = default) =>
        await client.GetSensitiveAccessRequestAsync(
            McpToolArguments.ReadRequiredString(arguments, "id"),
            cancellationToken);
}
