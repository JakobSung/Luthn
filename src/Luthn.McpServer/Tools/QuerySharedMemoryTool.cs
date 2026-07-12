using System.Text.Json;
using Luthn.AgentConnector.Http;
using Luthn.Sdk.Memory;

namespace Luthn.McpServer.Tools;

public sealed class QuerySharedMemoryTool(ILuthnClient client) : ILuthnMcpTool
{
    public string Name => "query_shared_memory";

    public async Task<object> InvokeAsync(
        JsonElement arguments,
        CancellationToken cancellationToken = default)
    {
        var request = new SharedMemoryQueryRequestDto(
            McpToolArguments.ReadOptionalString(arguments, "query"),
            McpToolArguments.ReadCoreTags(arguments),
            McpToolArguments.ReadMaxItems(arguments));

        return await client.QuerySharedMemoryAsync(request, cancellationToken);
    }
}
