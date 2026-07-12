using Luthn.Core.Common;

namespace Luthn.Core.Memory;

public sealed class MemoryConclusion
{
    public MemoryConclusion(
        PublicRecordId id,
        PublicRecordId memoryItemId,
        string statement,
        IReadOnlyList<PublicRecordId>? sourceMessageIds = null)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(memoryItemId);

        Id = id;
        MemoryItemId = memoryItemId;
        Statement = RequiredText(statement, nameof(statement));
        SourceMessageIds = sourceMessageIds ?? [];
    }

    public PublicRecordId Id { get; }

    public PublicRecordId MemoryItemId { get; }

    public string Statement { get; }

    public IReadOnlyList<PublicRecordId> SourceMessageIds { get; }

    private static string RequiredText(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Memory conclusion statement is required.", parameterName);
        }

        return value;
    }
}
