using System.Text.Json;
using Luthn.AgentConnector.Http;

namespace Luthn.McpServer.Tools;

public sealed class GetWikiProposalTool(ILuthnAgentClient client) : ILuthnMcpTool
{
    public string Name => "get_wiki_proposal";

    public async Task<object> InvokeAsync(
        JsonElement arguments,
        CancellationToken cancellationToken = default)
    {
        if (!arguments.TryGetProperty("id", out var idElement) ||
            idElement.ValueKind is not JsonValueKind.String ||
            string.IsNullOrWhiteSpace(idElement.GetString()))
        {
            throw new ArgumentException("id is required.", nameof(arguments));
        }

        return await client.GetWikiProposalAsync(idElement.GetString()!, cancellationToken);
    }
}
