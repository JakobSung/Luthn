using Luthn.Core.Policy;
using Luthn.Core.Classification;
using Luthn.Core.Common;

namespace Luthn.Core.Tests;

public sealed class PolicyEngineTests
{
    public static TheoryData<string> AmbiguousMonetaryExpressions => new()
    {
        { "예산은 약 5k입니다." },
        { "The amount is around five thousand." }
    };

    [Fact]
    public void SensitiveClassificationStaysBehindVaultBoundary()
    {
        var engine = new PolicyEngine();
        var classification = new ClassificationResult(
            new PublicRecordId("source-1"),
            SensitivityLevel.Confidential,
            0.9,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "contract" },
            ContainsSensitiveMaterial: true);

        var decision = engine.Decide(classification);

        Assert.Equal(StorageDecisionKind.SensitiveDbOnly, decision.Kind);
        Assert.False(decision.AllowsWikiProjection);
        Assert.False(decision.AllowsAgentContext);
    }

    [Fact]
    public void PublicClassificationCanBecomeWikiCandidate()
    {
        var engine = new PolicyEngine();
        var classification = new ClassificationResult(
            new PublicRecordId("source-2"),
            SensitivityLevel.Public,
            0.75,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            ContainsSensitiveMaterial: false);

        var decision = engine.Decide(classification);

        Assert.Equal(StorageDecisionKind.WikiCandidate, decision.Kind);
        Assert.True(decision.AllowsWikiProjection);
        Assert.True(decision.AllowsAgentContext);
    }

    [Theory]
    [MemberData(nameof(AmbiguousMonetaryExpressions))]
    public async Task AmbiguousMonetaryExpressionRequiresReview(string content)
    {
        var classification = await new LocalContextualContentClassifier().ClassifyAsync(
            new PublicRecordId("ambiguous-money"),
            content,
            "note");

        var decision = new PolicyEngine().Decide(classification);

        Assert.Equal(SensitivityLevel.Public, classification.Sensitivity);
        Assert.DoesNotContain("finance", classification.Categories);
        Assert.InRange(classification.Confidence, 0.01, 0.649999);
        Assert.Equal(StorageDecisionKind.NeedsReview, decision.Kind);
        Assert.True(decision.RequiresHumanReview);
        Assert.False(decision.AllowsAgentContext);
    }
}
