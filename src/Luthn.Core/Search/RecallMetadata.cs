namespace Luthn.Core.Search;

public static class RecallMetadata
{
    public const int MaximumKeyLength = 128;
    public const int MaximumTopicTags = 16;
    public const int MaximumTopicTagLength = 64;

    public static string? NormalizeKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim().ToLowerInvariant();
        if (normalized.Length > MaximumKeyLength ||
            normalized.Any(character => !char.IsLetterOrDigit(character) && character is not '-' and not '_' and not '.' and not ':'))
        {
            throw new ArgumentException(
                $"Recall keys must be {MaximumKeyLength} characters or fewer and contain only letters, digits, '-', '_', '.', or ':'.");
        }

        return normalized;
    }

    public static IReadOnlyList<string> NormalizeTopicTags(IEnumerable<string>? values)
    {
        var normalized = values is null
            ? []
            : values
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim().ToLowerInvariant())
                .Distinct(StringComparer.Ordinal)
                .ToArray();

        if (normalized.Length > MaximumTopicTags ||
            normalized.Any(value =>
                value.Length > MaximumTopicTagLength ||
                value.Any(character => !IsSafeKeyCharacter(character))))
        {
            throw new ArgumentException(
                $"topicTags must contain at most {MaximumTopicTags} values of {MaximumTopicTagLength} characters or fewer using only letters, digits, '-', '_', '.', or ':'.");
        }

        return normalized;
    }

    private static bool IsSafeKeyCharacter(char character) =>
        char.IsLetterOrDigit(character) || character is '-' or '_' or '.' or ':';
}
