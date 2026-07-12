using System.Text.Json;

namespace Luthn.McpServer.Tools;

public interface ILuthnMcpTool
{
    string Name { get; }

    Task<object> InvokeAsync(
        JsonElement arguments,
        CancellationToken cancellationToken = default);
}
