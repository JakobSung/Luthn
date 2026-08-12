using Luthn.Core.Common;

namespace Luthn.Core.Classification;

public sealed class LocalContextualContentClassifier : IContentClassifier
{
    public ClassificationProviderBoundary Boundary { get; } =
        new("local-contextual", "local-classification-input", "local-only");

    public ValueTask<ClassificationResult> ClassifyAsync(
        PublicRecordId sourceId,
        string content,
        string? sourceType,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var categories = ClassificationTaxonomy.DetectCategories(content)
            .Where(category => !string.Equals(category, "finance", StringComparison.OrdinalIgnoreCase))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var monetary = BoundedMonetaryAnalyzer.Analyze(content);
        if (monetary.HasSensitiveExpression)
        {
            categories.Add("finance");
        }

        var sensitivity = categories
            .Select(ClassificationTaxonomy.MinimumSensitivityFor)
            .Where(level => level is not null)
            .Select(level => level!.Value)
            .DefaultIfEmpty(IsLikelyOperationalKnowledge(content, sourceType)
                ? SensitivityLevel.Internal
                : SensitivityLevel.Public)
            .Max();
        var confidence = DetermineConfidence(
            content,
            categories.Count > 0,
            monetary.HasAmbiguousExpression);

        return ValueTask.FromResult(ClassificationResultNormalizer.Normalize(new ClassificationResult(
            sourceId,
            sensitivity,
            confidence,
            categories,
            sensitivity is SensitivityLevel.Confidential or SensitivityLevel.Restricted)));
    }

    private static double DetermineConfidence(
        string content,
        bool hasCategories,
        bool hasAmbiguousMonetaryExpression)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return 0;
        }

        if (hasAmbiguousMonetaryExpression && !hasCategories)
        {
            return 0.5;
        }

        return hasCategories ? 0.9 : 0.75;
    }

    private static bool IsLikelyOperationalKnowledge(string content, string? sourceType) =>
        string.Equals(sourceType, "runbook", StringComparison.OrdinalIgnoreCase)
        || content.Contains("runbook", StringComparison.OrdinalIgnoreCase)
        || content.Contains("implementation", StringComparison.OrdinalIgnoreCase)
        || content.Contains("decision", StringComparison.OrdinalIgnoreCase);
}
