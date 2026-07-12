using Luthn.Core.Classification;

namespace Luthn.Core.Memory;

public enum ExternalMemoryAdapterKind
{
    CustomService
}

public sealed record ExternalMemoryAdapterDescriptor(
    ExternalMemoryAdapterKind Kind,
    bool IsDefault,
    string ExportedCorpus,
    string PayloadClass,
    string RedactionState,
    string OperationalBoundary);

public sealed record ExternalMemoryProjection(
    string Id,
    string Title,
    string SafeSummary,
    IReadOnlyList<string> CoreTags,
    string ProjectionKind,
    string PayloadClass,
    string RedactionState,
    DateTimeOffset? ExpiresAt);

public sealed record ExternalMemoryExportBatch(
    string ExportedCorpus,
    IReadOnlyList<ExternalMemoryProjection> Projections);

public sealed record ExternalMemoryExportResult(
    int AcceptedCount,
    int SkippedCount,
    string? Checkpoint = null);

public interface IExternalMemoryServiceAdapter
{
    ExternalMemoryAdapterKind Kind { get; }

    Task<ExternalMemoryExportResult> UpsertSafeProjectionsAsync(
        ExternalMemoryExportBatch batch,
        CancellationToken cancellationToken);
}

public static class ExternalMemoryAdapterCatalog
{
    public const string PublicAgentAllowedProjection =
        "public-agent-allowed-safe-projections";
    public const string MetadataOnlyPayload = "metadata-only";
    public const string SafeProjectionOnly = "safe-projection-only";
    public const string SharedMemoryProjection = "shared-memory-safe-projection";

    public static readonly ExternalMemoryAdapterDescriptor CustomService = new(
        ExternalMemoryAdapterKind.CustomService,
        IsDefault: false,
        PublicAgentAllowedProjection,
        MetadataOnlyPayload,
        SafeProjectionOnly,
        "external memory services receive policy-approved safe projections only");

    public static IReadOnlyList<ExternalMemoryAdapterDescriptor> Supported { get; } =
    [
        CustomService
    ];
}

public static class ExternalMemoryProjectionPolicy
{
    public static bool AllowsExternalMemoryExport(
        SensitivityLevel sensitivity,
        MemoryVisibility visibility,
        DateTimeOffset? expiresAt,
        DateTimeOffset now) =>
        sensitivity == SensitivityLevel.Public &&
        (visibility == MemoryVisibility.PublicSafe ||
            visibility == MemoryVisibility.SharedAcrossAgents) &&
        (expiresAt is null || expiresAt > now);

    public static ExternalMemoryProjection CreateProjection(
        string id,
        string title,
        string safeSummary,
        SensitivityLevel sensitivity,
        IReadOnlyList<string> coreTags,
        MemoryVisibility visibility,
        DateTimeOffset? expiresAt,
        DateTimeOffset now)
    {
        if (!AllowsExternalMemoryExport(sensitivity, visibility, expiresAt, now))
        {
            throw new ArgumentException(
                "External memory export requires a public, agent-visible, non-expired safe projection.",
                nameof(sensitivity));
        }

        return new ExternalMemoryProjection(
            RequiredText(id, nameof(id)),
            RequiredText(title, nameof(title)),
            RequiredText(safeSummary, nameof(safeSummary)),
            NormalizeTags(coreTags),
            ExternalMemoryAdapterCatalog.SharedMemoryProjection,
            ExternalMemoryAdapterCatalog.MetadataOnlyPayload,
            ExternalMemoryAdapterCatalog.SafeProjectionOnly,
            expiresAt);
    }

    public static ExternalMemoryExportBatch CreateBatch(
        IEnumerable<ExternalMemoryProjection> projections) =>
        new(
            ExternalMemoryAdapterCatalog.PublicAgentAllowedProjection,
            projections.ToArray());

    private static IReadOnlyList<string> NormalizeTags(IEnumerable<string> coreTags) =>
        coreTags
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Select(tag => tag.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static string RequiredText(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("External memory projection text is required.", parameterName);
        }

        return value.Trim();
    }
}
