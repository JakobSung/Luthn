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
        { "결제수단 4111 1111 1111 1111", "payment", SensitivityLevel.Confidential }
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
