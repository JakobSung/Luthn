using System.Text.Json;
using Luthn.AgentConnector.Http;
using Luthn.Sdk.Telemetry;

namespace Luthn.McpServer.Tools;

public sealed class SubmitSearchFeedbackTool(ILuthnAgentClient client) : ILuthnMcpTool
{
    public string Name => "submit_search_feedback";

    public async Task<object> InvokeAsync(
        JsonElement arguments,
        CancellationToken cancellationToken = default)
    {
        McpToolArguments.RejectUnknownProperties(arguments, "retrievalId", "judgment");
        var request = new SearchFeedbackRequestDto(
            McpToolArguments.ReadRequiredString(arguments, "retrievalId"),
            McpToolArguments.ReadRequiredString(arguments, "judgment"));
        return await client.SubmitSearchFeedbackAsync(request, cancellationToken);
    }
}
