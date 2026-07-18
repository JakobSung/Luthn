using System.Text.Json;
using Luthn.AgentConnector.Http;

namespace Luthn.McpServer.Tools;

public sealed class SearchSafeContextTool(ILuthnAgentClient client) : ILuthnMcpTool
{
    public string Name => "search_safe_context";

    public async Task<object> InvokeAsync(
        JsonElement arguments,
        CancellationToken cancellationToken = default)
    {
        var query = arguments.TryGetProperty("query", out var queryElement) &&
            queryElement.ValueKind is JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(queryElement.GetString())
                ? queryElement.GetString()
                : null;
        var coreTags = ReadCoreTags(arguments);
        var maxItems = arguments.TryGetProperty("maxItems", out var maxItemsElement) &&
            maxItemsElement.TryGetInt32(out var value)
                ? value
                : 20;

        return await client.SearchAsync(query, coreTags, maxItems, cancellationToken);
    }

    private static IReadOnlyList<string> ReadCoreTags(JsonElement arguments)
    {
        if (!arguments.TryGetProperty("coreTags", out var tagsElement) ||
            tagsElement.ValueKind is not JsonValueKind.Array)
        {
            return [];
        }

        return tagsElement
            .EnumerateArray()
            .Where(tag => tag.ValueKind is JsonValueKind.String)
            .Select(tag => tag.GetString())
            .OfType<string>()
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .ToArray();
    }
}
