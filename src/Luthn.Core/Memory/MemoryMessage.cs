using Luthn.Core.Common;
using Luthn.Core.Vault;

namespace Luthn.Core.Memory;

public sealed record MemoryMessage(
    PublicRecordId Id,
    PublicRecordId SessionId,
    PublicRecordId AuthorActorId,
    DateTimeOffset CreatedAt,
    string SafeText,
    IReadOnlyList<SourceReference>? SourceReferences = null)
{
    public string SafeText { get; init; } = RequiredText(SafeText, nameof(SafeText));

    public IReadOnlyList<SourceReference> SourceReferences { get; init; } =
        SourceReferences ?? [];

    private static string RequiredText(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Memory message safe text is required.", parameterName);
        }

        return value;
    }
}
