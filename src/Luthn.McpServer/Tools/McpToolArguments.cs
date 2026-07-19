using System.Text.Json;

namespace Luthn.McpServer.Tools;

internal static class McpToolArguments
{
    public static void RejectUnknownProperties(
        JsonElement arguments,
        params string[] allowedPropertyNames)
    {
        if (arguments.ValueKind is not JsonValueKind.Object)
        {
            throw new ArgumentException("Tool arguments must be an object.", nameof(arguments));
        }

        var allowed = allowedPropertyNames.ToHashSet(StringComparer.Ordinal);
        var unknown = arguments
            .EnumerateObject()
            .Select(property => property.Name)
            .FirstOrDefault(propertyName => !allowed.Contains(propertyName));
        if (unknown is not null)
        {
            throw new ArgumentException($"Unknown argument: {unknown}.", nameof(arguments));
        }
    }

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

    public static IReadOnlyList<string> ReadCoreTags(JsonElement arguments) =>
        ReadTags(arguments, "coreTags");

    public static IReadOnlyList<string> ReadTags(
        JsonElement arguments,
        string propertyName)
    {
        if (!arguments.TryGetProperty(propertyName, out var tagsElement) ||
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

    public static int ReadRequiredInt(JsonElement arguments, string propertyName)
    {
        if (!arguments.TryGetProperty(propertyName, out var element) || !element.TryGetInt32(out var value))
        {
            throw new ArgumentException($"{propertyName} is required and must be an integer.", nameof(arguments));
        }

        return value;
    }

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
