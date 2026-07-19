namespace Luthn.Core.Classification;

public enum ClassificationGoldenLanguage
{
    Korean,
    English,
    Mixed
}

public sealed record ClassificationGoldenDataset(
    int Version,
    string TaxonomyVersion,
    IReadOnlyList<ClassificationGoldenCase> Cases);

public sealed record ClassificationGoldenCase(
    string Id,
    ClassificationGoldenLanguage Language,
    string SourceType,
    string? Content,
    string? Title,
    string? SafeSummary,
    IReadOnlyList<string> CoreTags,
    SensitivityLevel ExpectedSensitivity,
    IReadOnlyList<string> ExpectedCategories,
    bool ExpectedContainsSensitiveMaterial,
    StorageDecisionKind ExpectedStorageDecision);

public static class ClassificationGoldenDatasetValidator
{
    public const int CurrentDatasetVersion = 1;

    public static void Validate(ClassificationGoldenDataset dataset)
    {
        ArgumentNullException.ThrowIfNull(dataset);

        if (dataset.Version != CurrentDatasetVersion)
        {
            throw new InvalidOperationException(
                $"Unsupported classification golden dataset version '{dataset.Version}'.");
        }

        if (!string.Equals(
            dataset.TaxonomyVersion,
            ClassificationTaxonomy.Version,
            StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Dataset taxonomy version '{dataset.TaxonomyVersion}' does not match supported version '{ClassificationTaxonomy.Version}'.");
        }

        if (dataset.Cases is null)
        {
            throw new InvalidOperationException("Classification golden dataset requires cases.");
        }

        if (dataset.Cases.Count == 0)
        {
            throw new InvalidOperationException("Classification golden dataset must include at least one case.");
        }

        if (dataset.Cases.Any(goldenCase => goldenCase is null))
        {
            throw new InvalidOperationException("Classification golden dataset cannot contain a null case.");
        }

        var duplicateId = dataset.Cases
            .GroupBy(goldenCase => goldenCase.Id, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1)
            ?.Key;
        if (duplicateId is not null)
        {
            throw new InvalidOperationException($"Classification golden dataset contains duplicate case id '{duplicateId}'.");
        }

        foreach (var goldenCase in dataset.Cases)
        {
            ValidateCase(goldenCase);
        }

        var koreanCount = dataset.Cases.Count(goldenCase => goldenCase.Language == ClassificationGoldenLanguage.Korean);
        var englishCount = dataset.Cases.Count(goldenCase => goldenCase.Language == ClassificationGoldenLanguage.English);
        var mixedCount = dataset.Cases.Count(goldenCase => goldenCase.Language == ClassificationGoldenLanguage.Mixed);
        if (koreanCount <= dataset.Cases.Count / 2 || englishCount == 0 || mixedCount == 0)
        {
            throw new InvalidOperationException(
                "Classification golden dataset must be Korean-majority and include English and mixed-language cases.");
        }

        if (!dataset.Cases.Any(goldenCase => goldenCase.ExpectedContainsSensitiveMaterial) ||
            !dataset.Cases.Any(goldenCase => !goldenCase.ExpectedContainsSensitiveMaterial))
        {
            throw new InvalidOperationException(
                "Classification golden dataset must include sensitive and non-sensitive cases.");
        }

        if (!dataset.Cases.Any(IsTitleOnlyCase) ||
            !dataset.Cases.Any(IsSafeSummaryOnlyCase) ||
            !dataset.Cases.Any(IsCoreTagsOnlyCase))
        {
            throw new InvalidOperationException(
                "Classification golden dataset must include title-only, safeSummary-only, and coreTags-only signals.");
        }
    }

    private static void ValidateCase(ClassificationGoldenCase goldenCase)
    {
        if (string.IsNullOrWhiteSpace(goldenCase.Id) ||
            goldenCase.Id.Length > 80 ||
            goldenCase.Id.Any(character =>
                !(character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-')))
        {
            throw new InvalidOperationException(
                "Classification golden case ids must be 1-80 lowercase ASCII letters, digits, or hyphens.");
        }

        if (string.IsNullOrWhiteSpace(goldenCase.SourceType))
        {
            throw new InvalidOperationException($"Classification golden case '{goldenCase.Id}' requires sourceType.");
        }

        if (!Enum.IsDefined(goldenCase.Language))
        {
            throw new InvalidOperationException(
                $"Classification golden case '{goldenCase.Id}' contains unsupported language '{goldenCase.Language}'.");
        }

        if (!Enum.IsDefined(goldenCase.ExpectedSensitivity))
        {
            throw new InvalidOperationException(
                $"Classification golden case '{goldenCase.Id}' contains unsupported expected sensitivity '{goldenCase.ExpectedSensitivity}'.");
        }

        if (!Enum.IsDefined(goldenCase.ExpectedStorageDecision))
        {
            throw new InvalidOperationException(
                $"Classification golden case '{goldenCase.Id}' contains unsupported expected storage decision '{goldenCase.ExpectedStorageDecision}'.");
        }

        if (goldenCase.CoreTags is null)
        {
            throw new InvalidOperationException($"Classification golden case '{goldenCase.Id}' requires coreTags.");
        }

        if (goldenCase.ExpectedCategories is null)
        {
            throw new InvalidOperationException($"Classification golden case '{goldenCase.Id}' requires expectedCategories.");
        }

        var input = AgentVisibleClassificationInput.Compose(
            goldenCase.Content,
            goldenCase.Title,
            goldenCase.SafeSummary,
            goldenCase.CoreTags);
        if (string.IsNullOrWhiteSpace(input))
        {
            throw new InvalidOperationException($"Classification golden case '{goldenCase.Id}' has no classifiable projection fields.");
        }

        foreach (var category in goldenCase.ExpectedCategories)
        {
            var canonical = ClassificationTaxonomy.CanonicalNameFor(category);
            if (canonical is null)
            {
                throw new InvalidOperationException(
                    $"Classification golden case '{goldenCase.Id}' contains unknown category '{category}'.");
            }

            if (!string.Equals(canonical, category, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Classification golden case '{goldenCase.Id}' category '{category}' is not canonical; use '{canonical}'.");
            }

            var minimum = ClassificationTaxonomy.MinimumSensitivityFor(category);
            if (minimum is not null && minimum.Value > goldenCase.ExpectedSensitivity)
            {
                throw new InvalidOperationException(
                    $"Classification golden case '{goldenCase.Id}' sensitivity is lower than category '{category}' allows.");
            }
        }

        var expectedSensitive = goldenCase.ExpectedSensitivity is
            SensitivityLevel.Confidential or SensitivityLevel.Restricted;
        if (goldenCase.ExpectedContainsSensitiveMaterial != expectedSensitive)
        {
            throw new InvalidOperationException(
                $"Classification golden case '{goldenCase.Id}' has inconsistent sensitivity and contains-sensitive expectation.");
        }

        var expectedDecision = expectedSensitive
            ? StorageDecisionKind.SensitiveDbOnly
            : StorageDecisionKind.WikiCandidate;
        if (goldenCase.ExpectedStorageDecision != expectedDecision)
        {
            throw new InvalidOperationException(
                $"Classification golden case '{goldenCase.Id}' has inconsistent sensitivity and storage decision.");
        }
    }

    private static bool IsTitleOnlyCase(ClassificationGoldenCase goldenCase) =>
        string.IsNullOrWhiteSpace(goldenCase.Content) &&
        !string.IsNullOrWhiteSpace(goldenCase.Title) &&
        string.IsNullOrWhiteSpace(goldenCase.SafeSummary) &&
        goldenCase.CoreTags.Count == 0;

    private static bool IsSafeSummaryOnlyCase(ClassificationGoldenCase goldenCase) =>
        string.IsNullOrWhiteSpace(goldenCase.Content) &&
        string.IsNullOrWhiteSpace(goldenCase.Title) &&
        !string.IsNullOrWhiteSpace(goldenCase.SafeSummary) &&
        goldenCase.CoreTags.Count == 0;

    private static bool IsCoreTagsOnlyCase(ClassificationGoldenCase goldenCase) =>
        string.IsNullOrWhiteSpace(goldenCase.Content) &&
        string.IsNullOrWhiteSpace(goldenCase.Title) &&
        string.IsNullOrWhiteSpace(goldenCase.SafeSummary) &&
        goldenCase.CoreTags.Count > 0;
}
