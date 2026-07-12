using Luthn.Core.Policy;
using Luthn.Core.Classification;
using Luthn.Core.Common;

namespace Luthn.Core.Tests;

public sealed class PolicyEngineTests
{
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
}
