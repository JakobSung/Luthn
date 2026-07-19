using Luthn.Core.Classification;
using Luthn.Core.Common;

namespace Luthn.Core.Classification;

public sealed class MockContentClassifier : IContentClassifier
{
    public const string UsageNotice =
        "MockContentClassifier is test and experiment only; production classification requires an external provider.";

    public ClassificationProviderBoundary Boundary { get; } =
        new("mock", "local-classification-input", "local-only");

    public ValueTask<ClassificationResult> ClassifyAsync(
        PublicRecordId sourceId,
        string content,
        string? sourceType,
        CancellationToken cancellationToken = default)
    {
        var categories = ClassificationTaxonomy.DetectCategories(content);
        var sensitivity = categories
            .Select(ClassificationTaxonomy.MinimumSensitivityFor)
            .Where(level => level is not null)
            .Select(level => level!.Value)
            .DefaultIfEmpty(IsLikelyOperationalKnowledge(content, sourceType)
                ? SensitivityLevel.Internal
                : SensitivityLevel.Public)
            .Max();

        var confidence = string.IsNullOrWhiteSpace(content)
            ? 0
            : categories.Count > 0
                ? 0.9
                : 0.75;

        return ValueTask.FromResult(ClassificationResultNormalizer.Normalize(new ClassificationResult(
            sourceId,
            sensitivity,
            confidence,
            categories,
            sensitivity is SensitivityLevel.Confidential or SensitivityLevel.Restricted)));
    }

    private static bool IsLikelyOperationalKnowledge(string content, string? sourceType) =>
        string.Equals(sourceType, "runbook", StringComparison.OrdinalIgnoreCase)
        || content.Contains("runbook", StringComparison.OrdinalIgnoreCase)
        || content.Contains("implementation", StringComparison.OrdinalIgnoreCase)
        || content.Contains("decision", StringComparison.OrdinalIgnoreCase);
}
