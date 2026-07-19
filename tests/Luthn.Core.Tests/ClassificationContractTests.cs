using Luthn.Core.Classification;
using Luthn.Core.Common;
using Luthn.Core.Policy;

namespace Luthn.Core.Tests;

public sealed class ClassificationContractTests
{
    public static TheoryData<string, SensitivityLevel, string> KoreanFirstGoldenCases => new()
    {
        { "공개된 설치 안내입니다.", SensitivityLevel.Public, "" },
        { "고객 계약서의 결제 조건입니다.", SensitivityLevel.Confidential, "contract" },
        { "회계 자료에 주민등록번호가 포함되어 있습니다.", SensitivityLevel.Confidential, "personal identifier" },
        { "Rotate the API 키가 포함된 운영 메모입니다.", SensitivityLevel.Restricted, "access key" },
        { "Customer 고객 원문을 보관합니다.", SensitivityLevel.Restricted, "customer original" }
    };

    [Theory]
    [MemberData(nameof(KoreanFirstGoldenCases))]
    public async Task MockClassifierMatchesBoundedKoreanEnglishAndMixedGoldenCases(
        string content,
        SensitivityLevel expectedSensitivity,
        string expectedCategory)
    {
        var result = await new MockContentClassifier().ClassifyAsync(
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

        Assert.Contains("content:\nsource body", input, StringComparison.Ordinal);
        Assert.Contains("title:\nprojection title", input, StringComparison.Ordinal);
        Assert.Contains("safeSummary:\nsafe summary", input, StringComparison.Ordinal);
        Assert.Contains("coreTag:\ntag-one", input, StringComparison.Ordinal);
        Assert.Contains("coreTag:\ntag-two", input, StringComparison.Ordinal);
    }

    [Fact]
    public void SensitiveCategoryOverridesContradictoryPublicProviderFields()
    {
        var result = ClassificationResultNormalizer.Normalize(new ClassificationResult(
            new PublicRecordId("source-contradictory"),
            SensitivityLevel.Public,
            0.91,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "private key" },
            ContainsSensitiveMaterial: false));

        Assert.Equal(SensitivityLevel.Restricted, result.Sensitivity);
        Assert.True(result.ContainsSensitiveMaterial);
        Assert.Equal(StorageDecisionKind.SensitiveDbOnly, new PolicyEngine().Decide(result).Kind);
    }

    [Theory]
    [InlineData("Internal runbook steps.")]
    [InlineData("Internal implementation details.")]
    [InlineData("Internal architecture decision.")]
    public async Task MockClassifierPreservesOperationalKnowledgeAsInternal(string content)
    {
        var result = await new MockContentClassifier().ClassifyAsync(
            new PublicRecordId("operational-note"),
            content,
            "note");

        Assert.Equal(SensitivityLevel.Internal, result.Sensitivity);
        Assert.False(result.ContainsSensitiveMaterial);
        Assert.False(new PolicyEngine().Decide(result).AllowsAgentContext);
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
    public void SensitiveBooleanOverridesContradictoryPublicSensitivity()
    {
        var result = ClassificationResultNormalizer.Normalize(new ClassificationResult(
            new PublicRecordId("source-sensitive-boolean"),
            SensitivityLevel.Public,
            0.8,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            ContainsSensitiveMaterial: true));

        Assert.Equal(SensitivityLevel.Confidential, result.Sensitivity);
        Assert.True(result.ContainsSensitiveMaterial);
        Assert.Equal(StorageDecisionKind.SensitiveDbOnly, new PolicyEngine().Decide(result).Kind);
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
