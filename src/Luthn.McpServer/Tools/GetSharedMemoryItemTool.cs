using System.Text.Json;
using Luthn.AgentConnector.Http;

namespace Luthn.McpServer.Tools;

public sealed class GetSharedMemoryItemTool(ILuthnAgentClient client) : ILuthnMcpTool
{
    public string Name => "get_shared_memory_item";

    public async Task<object> InvokeAsync(
        JsonElement arguments,
        CancellationToken cancellationToken = default)
    {
        var id = McpToolArguments.ReadRequiredString(arguments, "id");

        return await client.GetSharedMemoryItemAsync(id, cancellationToken);
    }
}
