using Luthn.Core.Classification;

namespace Luthn.Core.Policy;

public sealed class PolicyEngine : IPolicyEngine
{
    public StorageDecision Decide(ClassificationResult classification)
    {
        if (classification.Confidence <= 0)
        {
            return new StorageDecision(
                StorageDecisionKind.Ignore,
                ["No classifiable content was provided."],
                AllowsWikiProjection: false,
                AllowsAgentContext: false,
                RequiresHumanReview: false);
        }

        if (classification.Confidence < 0.65)
        {
            return new StorageDecision(
                StorageDecisionKind.NeedsReview,
                ["Classification confidence is below the automatic routing threshold."],
                AllowsWikiProjection: false,
                AllowsAgentContext: false,
                RequiresHumanReview: true);
        }

        if (classification.ContainsSensitiveMaterial
            || classification.Sensitivity is SensitivityLevel.Confidential or SensitivityLevel.Restricted)
        {
            return new StorageDecision(
                StorageDecisionKind.SensitiveDbOnly,
                ["Sensitive material is kept behind the Vault boundary."],
                AllowsWikiProjection: false,
                AllowsAgentContext: false,
                RequiresHumanReview: classification.Sensitivity == SensitivityLevel.Restricted);
        }

        return new StorageDecision(
            StorageDecisionKind.WikiCandidate,
            ["Content is eligible for wiki-safe review and Core projection."],
            AllowsWikiProjection: true,
            AllowsAgentContext: classification.Sensitivity == SensitivityLevel.Public,
            RequiresHumanReview: classification.Sensitivity == SensitivityLevel.Internal);
    }
}
