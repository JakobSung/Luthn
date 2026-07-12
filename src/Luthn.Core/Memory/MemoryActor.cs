using Luthn.Core.Common;

namespace Luthn.Core.Memory;

public sealed record MemoryActor(
    PublicRecordId Id,
    MemoryActorKind Kind,
    string DisplayName)
{
    public string DisplayName { get; init; } = RequiredText(DisplayName, nameof(DisplayName));

    private static string RequiredText(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Memory actor display name is required.", parameterName);
        }

        return value;
    }
}
