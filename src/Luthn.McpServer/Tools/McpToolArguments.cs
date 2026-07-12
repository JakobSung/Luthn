using System.Text.Json;

namespace Luthn.McpServer.Tools;

internal static class McpToolArguments
{
    public static string ReadRequiredString(JsonElement arguments, string propertyName)
    {
        if (!arguments.TryGetProperty(propertyName, out var element) ||
            element.ValueKind is not JsonValueKind.String ||
            string.IsNullOrWhiteSpace(element.GetString()))
        {
            throw new ArgumentException($"{propertyName} is required.", nameof(arguments));
        }

        return element.GetString()!;
    }

    public static string? ReadOptionalString(JsonElement arguments, string propertyName) =>
        arguments.TryGetProperty(propertyName, out var element) &&
        element.ValueKind is JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(element.GetString())
            ? element.GetString()
            : null;

    public static IReadOnlyList<string> ReadCoreTags(JsonElement arguments)
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

    public static int ReadMaxItems(JsonElement arguments) =>
        arguments.TryGetProperty("maxItems", out var maxItemsElement) &&
        maxItemsElement.TryGetInt32(out var value)
            ? value
            : 20;

    public static DateTimeOffset? ReadOptionalDateTimeOffset(JsonElement arguments, string propertyName)
    {
        if (!arguments.TryGetProperty(propertyName, out var element) ||
            element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        if (element.ValueKind is not JsonValueKind.String ||
            !element.TryGetDateTimeOffset(out var value))
        {
            throw new ArgumentException($"{propertyName} must be an ISO-8601 timestamp.", nameof(arguments));
        }

        return value;
    }
}
