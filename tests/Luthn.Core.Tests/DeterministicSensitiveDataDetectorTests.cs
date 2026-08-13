using Luthn.Core.Classification;
using Luthn.Core.Common;
using Luthn.Core.Policy;

namespace Luthn.Core.Tests;

public sealed class DeterministicSensitiveDataDetectorTests
{
    public static TheoryData<string, string, SensitivityLevel> SensitiveShapes => new()
    {
        { "-----BEGIN OPENSSH PRIVATE KEY-----", "private key", SensitivityLevel.Restricted },
        { "token ghp_1234567890abcdefghijklmnopqrstuvwxyz", "access key", SensitivityLevel.Restricted },
        { "AWS access AKIA1234567890ABCDEF", "access key", SensitivityLevel.Restricted },
        { "API 키 = abcdefghijklmnop12345678", "access key", SensitivityLevel.Restricted },
        { "비밀번호: correct-horse-battery-staple", "credential", SensitivityLevel.Restricted },
        { "담당자 person@example.com", "email", SensitivityLevel.Confidential },
        { "연락처 010-1234-5678", "personal identifier", SensitivityLevel.Confidential },
        { "식별값 900101-1234568", "personal identifier", SensitivityLevel.Confidential },
        { "결제수단 4111 1111 1111 1111", "payment", SensitivityLevel.Confidential },
        { "견적금액은 1,000원입니다.", "finance", SensitivityLevel.Confidential },
        { "홍길동 사원의 연봉은 5,000만원입니다.", "finance", SensitivityLevel.Confidential },
        { "Annual salary is USD 12000.", "finance", SensitivityLevel.Confidential },
        { "The invoice total is USD12000.", "finance", SensitivityLevel.Confidential },
        { "The invoice total is KRW5000.", "finance", SensitivityLevel.Confidential },
        { "Revenue was $12000.", "finance", SensitivityLevel.Confidential },
        { "프로젝트 비용은 오천만원입니다.", "finance", SensitivityLevel.Confidential },
        { "The fee was twelve thousand dollars.", "finance", SensitivityLevel.Confidential },
        { "예산은 KRW 2.5m입니다.", "finance", SensitivityLevel.Confidential },
        { "정산액은 3억 원입니다.", "finance", SensitivityLevel.Confidential },
        { "The contract value was EUR 3bn.", "finance", SensitivityLevel.Confidential },
        { "The bonus was £2.5m.", "finance", SensitivityLevel.Confidential },
        { "출장비는 20,000엔입니다.", "finance", SensitivityLevel.Confidential },
        { "지급액은 1200 JPY입니다.", "finance", SensitivityLevel.Confidential },
        { "보너스는 ₩750k입니다.", "finance", SensitivityLevel.Confidential },
        { "Balance: 50 cents.", "finance", SensitivityLevel.Confidential },
        { "잔액은 50센트입니다.", "finance", SensitivityLevel.Confidential },
        { "정산액은 5백원입니다.", "finance", SensitivityLevel.Confidential },
        { "정산액은 5백 원입니다.", "finance", SensitivityLevel.Confidential }
    };

    [Theory]
    [MemberData(nameof(SensitiveShapes))]
    public void DetectorReturnsOnlyCanonicalCategoryAndConservativeSensitivity(
        string content,
        string expectedCategory,
        SensitivityLevel expectedSensitivity)
    {
        var result = new DeterministicSensitiveDataDetector().Detect(
            new PublicRecordId("detector-positive"),
            content);

        Assert.Equal(expectedSensitivity, result.Sensitivity);
        Assert.Contains(expectedCategory, result.Categories);
        Assert.True(result.ContainsSensitiveMaterial);
        Assert.Equal(StorageDecisionKind.SensitiveDbOnly, new PolicyEngine().Decide(result).Kind);
        Assert.DoesNotContain(content, result.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ghp_short")]
    [InlineData("api key: <your-api-key>")]
    [InlineData("010-123-456")]
    [InlineData("900230-1234567")]
    [InlineData("4111 1111 1111 1112")]
    [InlineData("release@example")]
    [InlineData("public contributor guide")]
    [InlineData("홍길동 사원이 업무를 진행했다")]
    [InlineData("홍길동 사원이 공개 견적을 발행했다")]
    [InlineData("2026-08-04에 v1.2.3을 배포했고 3개 항목을 처리했다")]
    [InlineData("USD")]
    [InlineData("$")]
    [InlineData("const USDValue = 12000;")]
    [InlineData("echo $HOME")]
    [InlineData("홍길동 조원이 업무를 진행했다")]
    [InlineData("가격 계산 로직을 개선했다")]
    [InlineData("예산 문서를 검토했다")]
    public void DetectorRejectsBenignNearMisses(string content)
    {
        var result = new DeterministicSensitiveDataDetector().Detect(
            new PublicRecordId("detector-negative"),
            content);

        Assert.Equal(SensitivityLevel.Public, result.Sensitivity);
        Assert.Empty(result.Categories);
        Assert.False(result.ContainsSensitiveMaterial);
    }

    [Fact]
    public void RedactorRemovesHighConfidenceValuesWithoutRemovingPersonNameOrEvent()
    {
        const string content =
            "홍길동 사원이 person@example.com, 010-1234-5678, 900101-1234568, " +
            "4111 1111 1111 1111을 확인했고 비밀번호: correct-horse-battery-staple로 견적을 발행했다.";
        var detector = new DeterministicSensitiveDataDetector();

        var result = detector.Redact(content);

        Assert.True(result.Changed);
        Assert.True(result.IsComplete);
        Assert.Contains("홍길동 사원", result.Text, StringComparison.Ordinal);
        Assert.Contains("견적을 발행했다", result.Text, StringComparison.Ordinal);
        Assert.Contains(DeterministicSensitiveDataDetector.RedactionMarker, result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("person@example.com", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("010-1234-5678", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("900101-1234568", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("4111 1111 1111 1111", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("correct-horse-battery-staple", result.Text, StringComparison.Ordinal);
        Assert.Contains("email", result.Categories);
        Assert.Contains("personal identifier", result.Categories);
        Assert.Contains("payment", result.Categories);
        Assert.Contains("credential", result.Categories);
    }

    [Fact]
    public void RedactorRemovesMonetaryAmountWithoutRemovingPersonNameOrEvent()
    {
        const string content = "홍길동 사원이 견적금액 1,000원으로 신규 견적을 발행했다.";

        var result = new DeterministicSensitiveDataDetector().Redact(content);

        Assert.True(result.Changed);
        Assert.True(result.IsComplete);
        Assert.Contains("홍길동 사원", result.Text, StringComparison.Ordinal);
        Assert.Contains("신규 견적을 발행했다", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("1,000원", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("금액", result.Text, StringComparison.Ordinal);
        Assert.Contains(DeterministicSensitiveDataDetector.RedactionMarker, result.Text, StringComparison.Ordinal);
        Assert.Contains("finance", result.Categories);
    }

    [Fact]
    public void RedactorRemovesTextualMonetaryPhraseWithoutRemovingPersonNameOrEvent()
    {
        const string content = "홍길동 사원이 오천만원을 확인하고 신규 견적을 발행했다.";

        var result = new DeterministicSensitiveDataDetector().Redact(content);

        Assert.True(result.Changed);
        Assert.True(result.IsComplete);
        Assert.Contains("홍길동 사원", result.Text, StringComparison.Ordinal);
        Assert.Contains("신규 견적을 발행했다", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("오천만원", result.Text, StringComparison.Ordinal);
        Assert.Contains(DeterministicSensitiveDataDetector.RedactionMarker, result.Text, StringComparison.Ordinal);
        Assert.Contains("finance", result.Categories);
    }

    [Theory]
    [InlineData("USD12000")]
    [InlineData("KRW5000")]
    [InlineData("50 cents")]
    [InlineData("50센트")]
    [InlineData("5백원")]
    [InlineData("5백 원")]
    public void RedactorRemovesPreviouslySupportedCompactMonetaryFormats(string content)
    {
        var result = new DeterministicSensitiveDataDetector().Redact(content);

        Assert.True(result.Changed);
        Assert.True(result.IsComplete);
        Assert.DoesNotContain(content, result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(DeterministicSensitiveDataDetector.RedactionMarker, result.Text, StringComparison.Ordinal);
        Assert.Contains("finance", result.Categories);
    }

    [Fact]
    public void RedactorRemovesMonetaryContextWithoutNumericAmount()
    {
        const string content = "홍길동 사원이 연봉 협상을 완료했다.";

        var result = new DeterministicSensitiveDataDetector().Redact(content);

        Assert.True(result.Changed);
        Assert.True(result.IsComplete);
        Assert.Contains("홍길동 사원", result.Text, StringComparison.Ordinal);
        Assert.Contains("협상을 완료했다", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("연봉", result.Text, StringComparison.Ordinal);
        Assert.Contains(DeterministicSensitiveDataDetector.RedactionMarker, result.Text, StringComparison.Ordinal);
        Assert.Contains("finance", result.Categories);
    }

    [Theory]
    [InlineData("ghp_short")]
    [InlineData("010-123-456")]
    [InlineData("900230-1234567")]
    [InlineData("4111 1111 1111 1112")]
    [InlineData("release@example")]
    [InlineData("홍길동 사원이 공개 견적을 발행했다")]
    public void RedactorLeavesBenignNearMissesAndNamesUnchanged(string content)
    {
        var result = new DeterministicSensitiveDataDetector().Redact(content);

        Assert.False(result.Changed);
        Assert.True(result.IsComplete);
        Assert.Equal(content, result.Text);
        Assert.Empty(result.Categories);
    }

    [Fact]
    public void RedactorFailsClosedForIncompletePrivateKeyBlock()
    {
        const string content = "-----BEGIN OPENSSH PRIVATE KEY-----\nunterminated-private-key-body";

        var result = new DeterministicSensitiveDataDetector().Redact(content);

        Assert.False(result.Changed);
        Assert.False(result.IsComplete);
        Assert.Equal(content, result.Text);
    }

    [Fact]
    public async Task HybridClassifierOverridesPublicProviderFalseNegative()
    {
        var provider = new StaticClassifier(new ClassificationResult(
            new PublicRecordId("hybrid-source"),
            SensitivityLevel.Public,
            0.99,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            ContainsSensitiveMaterial: false));
        var classifier = new HybridContentClassifier(
            provider,
            new DeterministicSensitiveDataDetector());

        var result = await classifier.ClassifyAsync(
            new PublicRecordId("hybrid-source"),
            "연락처 010-1234-5678",
            "note");

        Assert.Equal(SensitivityLevel.Confidential, result.Sensitivity);
        Assert.Contains("personal identifier", result.Categories);
        Assert.True(result.ContainsSensitiveMaterial);
        Assert.False(new PolicyEngine().Decide(result).AllowsAgentContext);
        Assert.Equal(provider.Boundary, classifier.Boundary);
    }

    [Fact]
    public async Task HybridClassifierOverridesPublicProviderFalseNegativeForMonetaryData()
    {
        var provider = new StaticClassifier(new ClassificationResult(
            new PublicRecordId("hybrid-money-source"),
            SensitivityLevel.Public,
            0.99,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            ContainsSensitiveMaterial: false));
        var classifier = new HybridContentClassifier(
            provider,
            new DeterministicSensitiveDataDetector());

        var result = await classifier.ClassifyAsync(
            new PublicRecordId("hybrid-money-source"),
            "홍길동 사원의 연봉은 5,000만원입니다.",
            "note");

        Assert.Equal(SensitivityLevel.Confidential, result.Sensitivity);
        Assert.Contains("finance", result.Categories);
        Assert.True(result.ContainsSensitiveMaterial);
        Assert.False(new PolicyEngine().Decide(result).AllowsAgentContext);
    }

    [Fact]
    public async Task HybridClassifierDoesNotFallbackWhenProviderFails()
    {
        var classifier = new HybridContentClassifier(
            new ThrowingClassifier(),
            new DeterministicSensitiveDataDetector());

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => classifier.ClassifyAsync(
            new PublicRecordId("failed-source"),
            "person@example.com",
            "note").AsTask());

        Assert.Equal("provider failed", error.Message);
    }

    [Fact]
    public void MergerRejectsDifferentSourceIds()
    {
        var provider = PublicResult("provider-source");
        var local = PublicResult("local-source");

        var error = Assert.Throws<InvalidOperationException>(() =>
            ConservativeClassificationMerger.Merge(provider, local));

        Assert.Equal("Classification results must refer to the same source id.", error.Message);
    }

    private static ClassificationResult PublicResult(string sourceId) =>
        new(
            new PublicRecordId(sourceId),
            SensitivityLevel.Public,
            0.8,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            ContainsSensitiveMaterial: false);

    private sealed class StaticClassifier(ClassificationResult result) : IContentClassifier
    {
        public ClassificationProviderBoundary Boundary { get; } =
            new("self-hosted-test", "classification-input", "test-only");

        public ValueTask<ClassificationResult> ClassifyAsync(
            PublicRecordId sourceId,
            string content,
            string? sourceType,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(result);
    }

    private sealed class ThrowingClassifier : IContentClassifier
    {
        public ClassificationProviderBoundary Boundary { get; } =
            new("failed-test", "classification-input", "test-only");

        public ValueTask<ClassificationResult> ClassifyAsync(
            PublicRecordId sourceId,
            string content,
            string? sourceType,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<ClassificationResult>(new InvalidOperationException("provider failed"));
    }
}
