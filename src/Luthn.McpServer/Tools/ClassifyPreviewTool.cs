using System.Text.Json;
using Luthn.AgentConnector.Http;
using Luthn.Sdk.Classification;

namespace Luthn.McpServer.Tools;

public sealed class ClassifyPreviewTool(ILuthnClient client) : ILuthnMcpTool
{
    public string Name => "classify_preview";

    public async Task<object> InvokeAsync(
        JsonElement arguments,
        CancellationToken cancellationToken = default)
    {
        var sourceId = ReadRequiredString(arguments, "sourceId");
        var content = ReadRequiredString(arguments, "content");
        var sourceType = arguments.TryGetProperty("sourceType", out var sourceTypeElement) &&
            sourceTypeElement.ValueKind is JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(sourceTypeElement.GetString())
                ? sourceTypeElement.GetString()!
                : "note";

        return await client.ClassifyPreviewAsync(
            new ClassificationPreviewRequestDto(sourceId, content, sourceType),
            cancellationToken);
    }

    private static string ReadRequiredString(JsonElement arguments, string propertyName)
    {
        if (!arguments.TryGetProperty(propertyName, out var element) ||
            element.ValueKind is not JsonValueKind.String ||
            string.IsNullOrWhiteSpace(element.GetString()))
        {
            throw new ArgumentException($"{propertyName} is required.", nameof(arguments));
        }

        return element.GetString()!;
    }
}
