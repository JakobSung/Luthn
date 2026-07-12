using Luthn.Core.Classification;
using Luthn.Core.Common;

namespace Luthn.Core.Memory;

public sealed class SharedMemoryItem
{
    public SharedMemoryItem(
        PublicRecordId id,
        string title,
        string safeSummary,
        SensitivityLevel sensitivity,
        IReadOnlyList<string>? coreTags,
        MemoryVisibility visibility,
        MemoryRetentionPolicy retention,
        PublicRecordId? sourceSessionId = null)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(retention);

        if (sensitivity == SensitivityLevel.Restricted && visibility != MemoryVisibility.PrivateToOwner)
        {
            throw new ArgumentException("Restricted memory cannot be shared by default.", nameof(visibility));
        }

        Id = id;
        Title = RequiredText(title, nameof(title));
        SafeSummary = RequiredText(safeSummary, nameof(safeSummary));
        Sensitivity = sensitivity;
        CoreTags = coreTags ?? [];
        Visibility = visibility;
        Retention = retention;
        SourceSessionId = sourceSessionId;
    }

    public PublicRecordId Id { get; }

    public string Title { get; }

    public string SafeSummary { get; }

    public SensitivityLevel Sensitivity { get; }

    public IReadOnlyList<string> CoreTags { get; }

    public MemoryVisibility Visibility { get; }

    public MemoryRetentionPolicy Retention { get; }

    public PublicRecordId? SourceSessionId { get; }

    private static string RequiredText(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Shared memory text is required.", parameterName);
        }

        return value;
    }
}
