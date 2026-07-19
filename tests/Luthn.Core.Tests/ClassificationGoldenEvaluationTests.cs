using Luthn.Core.Classification;
using Luthn.Core.Common;
using Luthn.Core.Policy;

namespace Luthn.Core.Tests;

public sealed class ClassificationGoldenEvaluationTests
{
    [Fact]
    public void ValidateAcceptsKoreanMajorityVersionedDataset()
    {
        var dataset = CreateDataset();

        ClassificationGoldenDatasetValidator.Validate(dataset);
    }

    [Fact]
    public void ValidateRejectsDuplicateCaseIds()
    {
        var dataset = CreateDataset();
        dataset = dataset with { Cases = [.. dataset.Cases, dataset.Cases[0]] };

        var error = Assert.Throws<InvalidOperationException>(
            () => ClassificationGoldenDatasetValidator.Validate(dataset));

        Assert.Contains("duplicate case id 'ko-sensitive'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateRejectsNullCaseBeforeDuplicateIdInspection()
    {
        var dataset = CreateDataset() with { Cases = [null!] };

        var error = Assert.Throws<InvalidOperationException>(
            () => ClassificationGoldenDatasetValidator.Validate(dataset));

        Assert.Contains("cannot contain a null case", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateRejectsUnsupportedTaxonomyVersion()
    {
        var dataset = CreateDataset() with { TaxonomyVersion = "unsupported" };

        var error = Assert.Throws<InvalidOperationException>(
            () => ClassificationGoldenDatasetValidator.Validate(dataset));

        Assert.Contains("does not match supported version", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateRejectsUnknownCategory()
    {
        var dataset = ReplaceFirstCase(CreateDataset(), goldenCase => goldenCase with
        {
            ExpectedCategories = ["unknown-secret"]
        });

        var error = Assert.Throws<InvalidOperationException>(
            () => ClassificationGoldenDatasetValidator.Validate(dataset));

        Assert.Contains("unknown category 'unknown-secret'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateRejectsCorpusThatIsNotKoreanMajority()
    {
        var dataset = CreateDataset();
        var mixedCase = dataset.Cases.Single(goldenCase => goldenCase.Id == "mixed-public");
        dataset = dataset with
        {
            Cases =
            [
                dataset.Cases.Single(goldenCase => goldenCase.Id == "ko-sensitive"),
                dataset.Cases.Single(goldenCase => goldenCase.Id == "ko-public"),
                mixedCase,
                mixedCase with { Id = "mixed-public-two" },
                dataset.Cases.Single(goldenCase => goldenCase.Id == "en-public")
            ]
        };

        var error = Assert.Throws<InvalidOperationException>(
            () => ClassificationGoldenDatasetValidator.Validate(dataset));

        Assert.Contains("must be Korean-majority", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("language")]
    [InlineData("sensitivity")]
    [InlineData("storage")]
    public void ValidateRejectsUndefinedEnumValues(string field)
    {
        var dataset = ReplaceFirstCase(CreateDataset(), goldenCase => field switch
        {
            "language" => goldenCase with { Language = (ClassificationGoldenLanguage)99 },
            "sensitivity" => goldenCase with { ExpectedSensitivity = (SensitivityLevel)99 },
            "storage" => goldenCase with { ExpectedStorageDecision = (StorageDecisionKind)99 },
            _ => throw new InvalidOperationException("Unknown test field.")
        });

        var error = Assert.Throws<InvalidOperationException>(
            () => ClassificationGoldenDatasetValidator.Validate(dataset));

        Assert.Contains("unsupported", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateRejectsInconsistentSensitivityAndRoutingExpectations()
    {
        var dataset = ReplaceFirstCase(CreateDataset(), goldenCase => goldenCase with
        {
            ExpectedSensitivity = SensitivityLevel.Public,
            ExpectedCategories = [],
            ExpectedContainsSensitiveMaterial = false,
            ExpectedStorageDecision = StorageDecisionKind.SensitiveDbOnly
        });

        var error = Assert.Throws<InvalidOperationException>(
            () => ClassificationGoldenDatasetValidator.Validate(dataset));

        Assert.Contains("inconsistent sensitivity and storage decision", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateRejectsInconsistentSensitivityAndSensitiveFlagExpectations()
    {
        var dataset = ReplaceFirstCase(CreateDataset(), goldenCase => goldenCase with
        {
            ExpectedCategories = [],
            ExpectedContainsSensitiveMaterial = false,
            ExpectedStorageDecision = StorageDecisionKind.WikiCandidate
        });

        var error = Assert.Throws<InvalidOperationException>(
            () => ClassificationGoldenDatasetValidator.Validate(dataset));

        Assert.Contains("inconsistent sensitivity and contains-sensitive expectation", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EvaluateReportsDeterministicFalseNegativeAndMismatchCounts()
    {
        var dataset = CreateDataset();
        var evaluator = new ClassificationGoldenEvaluator();
        var policy = new PolicyEngine();

        var report = await evaluator.EvaluateAsync(
            dataset,
            "test-provider",
            (goldenCase, _) =>
            {
                var classification = goldenCase.Id == "ko-sensitive"
                    ? CreateClassification(goldenCase.Id, SensitivityLevel.Public, [], false)
                    : CreateClassification(
                        goldenCase.Id,
                        goldenCase.ExpectedSensitivity,
                        goldenCase.ExpectedCategories,
                        goldenCase.ExpectedContainsSensitiveMaterial);
                return ValueTask.FromResult(new ClassificationEvaluationObservation(
                    classification,
                    policy.Decide(classification)));
            });

        Assert.Equal("test-provider", report.Provider);
        Assert.Equal(5, report.Summary.TotalCount);
        Assert.Equal(4, report.Summary.PassedCount);
        Assert.Equal(1, report.Summary.FalseNegativeCount);
        Assert.Equal(0, report.Summary.FalsePositiveCount);
        Assert.Equal(1, report.Summary.SensitivityMismatchCount);
        Assert.Equal(1, report.Summary.CategoryMismatchCount);
        Assert.Equal(1, report.Summary.SensitiveFlagMismatchCount);
        Assert.Equal(1, report.Summary.RoutingMismatchCount);
        Assert.True(report.Cases.Single(result => result.Id == "ko-sensitive").IsFalseNegative);
    }

    private static ClassificationGoldenDataset ReplaceFirstCase(
        ClassificationGoldenDataset dataset,
        Func<ClassificationGoldenCase, ClassificationGoldenCase> replace) =>
        dataset with { Cases = [replace(dataset.Cases[0]), .. dataset.Cases.Skip(1)] };

    private static ClassificationGoldenDataset CreateDataset() => new(
        ClassificationGoldenDatasetValidator.CurrentDatasetVersion,
        ClassificationTaxonomy.Version,
        [
            new ClassificationGoldenCase(
                "ko-sensitive",
                ClassificationGoldenLanguage.Korean,
                "note",
                null,
                "고객 계약서입니다.",
                null,
                [],
                SensitivityLevel.Confidential,
                ["contract", "customer"],
                true,
                StorageDecisionKind.SensitiveDbOnly),
            new ClassificationGoldenCase(
                "ko-public",
                ClassificationGoldenLanguage.Korean,
                "note",
                null,
                null,
                "공개 문서입니다.",
                [],
                SensitivityLevel.Public,
                [],
                false,
                StorageDecisionKind.WikiCandidate),
            new ClassificationGoldenCase(
                "mixed-public",
                ClassificationGoldenLanguage.Mixed,
                "note",
                null,
                null,
                null,
                ["공개"],
                SensitivityLevel.Public,
                [],
                false,
                StorageDecisionKind.WikiCandidate),
            new ClassificationGoldenCase(
                "ko-public-two",
                ClassificationGoldenLanguage.Korean,
                "note",
                "공개 안내입니다.",
                null,
                null,
                [],
                SensitivityLevel.Public,
                [],
                false,
                StorageDecisionKind.WikiCandidate),
            new ClassificationGoldenCase(
                "en-public",
                ClassificationGoldenLanguage.English,
                "note",
                "Published guide.",
                null,
                null,
                [],
                SensitivityLevel.Public,
                [],
                false,
                StorageDecisionKind.WikiCandidate)
        ]);

    private static ClassificationResult CreateClassification(
        string id,
        SensitivityLevel sensitivity,
        IEnumerable<string> categories,
        bool containsSensitiveMaterial) =>
        new(
            new PublicRecordId($"golden-{id}"),
            sensitivity,
            1,
            categories.ToHashSet(StringComparer.OrdinalIgnoreCase),
            containsSensitiveMaterial);
}
