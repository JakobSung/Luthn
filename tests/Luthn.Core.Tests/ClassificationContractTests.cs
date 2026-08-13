using Luthn.Core.Classification;
using Luthn.Core.Common;
using Luthn.Core.Policy;

namespace Luthn.Core.Tests;

public sealed class ClassificationContractTests
{
    public static TheoryData<string, SensitivityLevel, string> KoreanFirstGoldenCases => new()
    {
        { "공개된 설치 안내입니다.", SensitivityLevel.Public, "" },
        { "홍길동 사원이 업무를 진행했다.", SensitivityLevel.Public, "" },
        { "홍길동 사원이 공개 견적을 발행했다.", SensitivityLevel.Public, "" },
        { "고객 계약서의 결제 조건입니다.", SensitivityLevel.Confidential, "contract" },
        { "홍길동 사원의 연봉은 5000만원입니다.", SensitivityLevel.Confidential, "finance" },
        { "Annual salary is USD 12000.", SensitivityLevel.Confidential, "finance" },
        { "회계 자료에 주민등록번호가 포함되어 있습니다.", SensitivityLevel.Confidential, "personal identifier" },
        { "Rotate the API 키가 포함된 운영 메모입니다.", SensitivityLevel.Restricted, "access key" },
        { "Customer 고객 원문을 보관합니다.", SensitivityLevel.Restricted, "customer original" }
    };

    public static TheoryData<string, double, string[], bool, SensitivityLevel> ContradictorySensitiveInputs => new()
    {
        { "source-category", 0.91, ["private key"], false, SensitivityLevel.Restricted },
        { "source-sensitive-boolean", 0.8, [], true, SensitivityLevel.Confidential }
    };

    public static TheoryData<string> LocalMonetaryNearMisses => new()
    {
        { "USD" },
        { "$" },
        { "const USDValue = 12000;" },
        { "echo $HOME" },
        { "홍길동 조원이 업무를 진행했다." },
        { "가격 계산 로직을 개선했다." },
        { "The budget includes 5 sections." },
        { "The price calculation handled 5 cases." }
    };

    [Theory]
    [MemberData(nameof(KoreanFirstGoldenCases))]
    public async Task LocalClassifierMatchesBoundedKoreanEnglishAndMixedGoldenCases(
        string content,
        SensitivityLevel expectedSensitivity,
        string expectedCategory)
    {
        var result = await new LocalContextualContentClassifier().ClassifyAsync(
            new PublicRecordId("golden-case"),
            content,
            "note");

        Assert.Equal(expectedSensitivity, result.Sensitivity);
        if (string.IsNullOrEmpty(expectedCategory))
        {
            Assert.Empty(result.Categories);
        }
        else
        {
            Assert.Contains(expectedCategory, result.Categories);
        }
    }

    [Fact]
    public void AgentVisibleInputIncludesEveryProjectionField()
    {
        var input = AgentVisibleClassificationInput.Compose(
            "source body",
            "projection title",
            "safe summary",
            ["tag-one", "tag-two"]);
        var normalizedInput = input.Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.Contains("content:\nsource body", normalizedInput, StringComparison.Ordinal);
        Assert.Contains("title:\nprojection title", normalizedInput, StringComparison.Ordinal);
        Assert.Contains("safeSummary:\nsafe summary", normalizedInput, StringComparison.Ordinal);
        Assert.Contains("coreTag:\ntag-one", normalizedInput, StringComparison.Ordinal);
        Assert.Contains("coreTag:\ntag-two", normalizedInput, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(ContradictorySensitiveInputs))]
    public void ContradictoryPublicSensitiveSignalsUseSafeStorage(
        string sourceId,
        double confidence,
        string[] categories,
        bool containsSensitiveMaterial,
        SensitivityLevel expectedSensitivity)
    {
        var result = ClassificationResultNormalizer.Normalize(new ClassificationResult(
            new PublicRecordId(sourceId),
            SensitivityLevel.Public,
            confidence,
            new HashSet<string>(categories, StringComparer.OrdinalIgnoreCase),
            containsSensitiveMaterial));

        Assert.Equal(expectedSensitivity, result.Sensitivity);
        Assert.True(result.ContainsSensitiveMaterial);
        Assert.Equal(StorageDecisionKind.SensitiveDbOnly, new PolicyEngine().Decide(result).Kind);
    }

    [Theory]
    [InlineData("Internal runbook steps.")]
    [InlineData("Internal implementation details.")]
    [InlineData("Internal architecture decision.")]
    public async Task LocalClassifierPreservesOperationalKnowledgeAsInternal(string content)
    {
        var result = await new LocalContextualContentClassifier().ClassifyAsync(
            new PublicRecordId("operational-note"),
            content,
            "note");

        Assert.Equal(SensitivityLevel.Internal, result.Sensitivity);
        Assert.False(result.ContainsSensitiveMaterial);
        Assert.False(new PolicyEngine().Decide(result).AllowsAgentContext);
    }

    [Fact]
    public void LocalClassifierDeclaresLocalOnlyBoundary()
    {
        var boundary = new LocalContextualContentClassifier().Boundary;

        Assert.Equal("LocalDeterministic", boundary.ProviderName);
        Assert.Equal("local-classification-input", boundary.PayloadClass);
        Assert.Equal("local-only", boundary.RedactionState);
    }

    [Theory]
    [MemberData(nameof(LocalMonetaryNearMisses))]
    public async Task LocalClassifierLeavesOrdinaryMonetaryNearMissesPublic(string content)
    {
        var result = await new LocalContextualContentClassifier().ClassifyAsync(
            new PublicRecordId("monetary-near-miss"),
            content,
            "note");

        Assert.Equal(SensitivityLevel.Public, result.Sensitivity);
        Assert.Empty(result.Categories);
        Assert.False(result.ContainsSensitiveMaterial);
        Assert.Equal(StorageDecisionKind.WikiCandidate, new PolicyEngine().Decide(result).Kind);
    }

    [Fact]
    public void KnownProviderCategoriesUseCanonicalTaxonomyNames()
    {
        var result = ClassificationResultNormalizer.Normalize(new ClassificationResult(
            new PublicRecordId("source-category-case"),
            SensitivityLevel.Public,
            0.9,
            new HashSet<string>(StringComparer.Ordinal) { "Private Key", "CONTRACT" },
            ContainsSensitiveMaterial: false));

        Assert.Contains("private key", result.Categories);
        Assert.Contains("contract", result.Categories);
        Assert.DoesNotContain("Private Key", result.Categories, StringComparer.Ordinal);
        Assert.DoesNotContain("CONTRACT", result.Categories, StringComparer.Ordinal);
        Assert.Equal(SensitivityLevel.Restricted, result.Sensitivity);
    }

    [Fact]
    public void SensitiveCategoryCannotBeIgnoredThroughContradictoryZeroConfidence()
    {
        var classification = new ClassificationResult(
            new PublicRecordId("source-zero-confidence"),
            SensitivityLevel.Public,
            0,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "contract" },
            ContainsSensitiveMaterial: false);

        Assert.Equal(StorageDecisionKind.SensitiveDbOnly, new PolicyEngine().Decide(classification).Kind);
    }
}
