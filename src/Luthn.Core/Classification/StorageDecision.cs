namespace Luthn.Core.Classification;

public sealed record StorageDecision(
    StorageDecisionKind Kind,
    IReadOnlyList<string> Reasons,
    bool AllowsWikiProjection,
    bool AllowsAgentContext,
    bool RequiresHumanReview);
