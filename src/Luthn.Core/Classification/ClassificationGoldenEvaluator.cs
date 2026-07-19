namespace Luthn.Core.Classification;

public sealed record ClassificationEvaluationObservation(
    ClassificationResult Classification,
    StorageDecision StorageDecision);

public sealed record ClassificationEvaluationSummary(
    int TotalCount,
    int PassedCount,
    int FalseNegativeCount,
    int FalsePositiveCount,
    int SensitivityMismatchCount,
    int CategoryMismatchCount,
    int SensitiveFlagMismatchCount,
    int RoutingMismatchCount);

public sealed record ClassificationEvaluationCaseResult(
    string Id,
    ClassificationGoldenLanguage Language,
    SensitivityLevel ExpectedSensitivity,
    SensitivityLevel ActualSensitivity,
    IReadOnlyList<string> ExpectedCategories,
    IReadOnlyList<string> ActualCategories,
    bool ExpectedContainsSensitiveMaterial,
    bool ActualContainsSensitiveMaterial,
    StorageDecisionKind ExpectedStorageDecision,
    StorageDecisionKind ActualStorageDecision,
    bool IsFalseNegative,
    bool IsFalsePositive,
    bool SensitivityMatches,
    bool CategoriesMatch,
    bool SensitiveFlagMatches,
    bool RoutingMatches,
    bool Passed);

public sealed record ClassificationEvaluationReport(
    int DatasetVersion,
    string TaxonomyVersion,
    string Provider,
    ClassificationEvaluationSummary Summary,
    IReadOnlyList<ClassificationEvaluationCaseResult> Cases);

public sealed class ClassificationGoldenEvaluator
{
    public async Task<ClassificationEvaluationReport> EvaluateAsync(
        ClassificationGoldenDataset dataset,
        string provider,
        Func<ClassificationGoldenCase, CancellationToken, ValueTask<ClassificationEvaluationObservation>> evaluateCase,
        CancellationToken cancellationToken = default)
    {
        ClassificationGoldenDatasetValidator.Validate(dataset);
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentNullException.ThrowIfNull(evaluateCase);

        var results = new List<ClassificationEvaluationCaseResult>(dataset.Cases.Count);
        foreach (var goldenCase in dataset.Cases)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var observation = await evaluateCase(goldenCase, cancellationToken);
            var classification = ClassificationResultNormalizer.Normalize(observation.Classification);
            var expectedCategories = goldenCase.ExpectedCategories
                .OrderBy(category => category, StringComparer.Ordinal)
                .ToArray();
            var actualCategories = classification.Categories
                .OrderBy(category => category, StringComparer.Ordinal)
                .ToArray();
            var expectedSensitive = goldenCase.ExpectedContainsSensitiveMaterial;
            var actualSensitive = classification.ContainsSensitiveMaterial;
            var sensitivityMatches = classification.Sensitivity == goldenCase.ExpectedSensitivity;
            var categoriesMatch = expectedCategories.SequenceEqual(actualCategories, StringComparer.Ordinal);
            var sensitiveFlagMatches = actualSensitive == expectedSensitive;
            var routingMatches = observation.StorageDecision.Kind == goldenCase.ExpectedStorageDecision;
            var isFalseNegative = expectedSensitive && !actualSensitive;
            var isFalsePositive = !expectedSensitive && actualSensitive;

            results.Add(new ClassificationEvaluationCaseResult(
                goldenCase.Id,
                goldenCase.Language,
                goldenCase.ExpectedSensitivity,
                classification.Sensitivity,
                expectedCategories,
                actualCategories,
                expectedSensitive,
                actualSensitive,
                goldenCase.ExpectedStorageDecision,
                observation.StorageDecision.Kind,
                isFalseNegative,
                isFalsePositive,
                sensitivityMatches,
                categoriesMatch,
                sensitiveFlagMatches,
                routingMatches,
                sensitivityMatches && categoriesMatch && sensitiveFlagMatches && routingMatches));
        }

        var summary = new ClassificationEvaluationSummary(
            results.Count,
            results.Count(result => result.Passed),
            results.Count(result => result.IsFalseNegative),
            results.Count(result => result.IsFalsePositive),
            results.Count(result => !result.SensitivityMatches),
            results.Count(result => !result.CategoriesMatch),
            results.Count(result => !result.SensitiveFlagMatches),
            results.Count(result => !result.RoutingMatches));

        return new ClassificationEvaluationReport(
            dataset.Version,
            dataset.TaxonomyVersion,
            provider.Trim(),
            summary,
            results);
    }
}
