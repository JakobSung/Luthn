namespace Luthn.Core.Access;

/// <summary>Metadata-only boundary for a trusted human decision channel.</summary>
public interface ITrustedSensitiveAccessDecisionAdapter
{
    string AdapterId { get; }

    Task<SensitiveAccessDecisionIntent?> GetDecisionAsync(
        SensitiveAccessDecisionRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record SensitiveAccessDecisionRequest(
    string RequestId,
    string SensitiveReferenceId,
    string SessionId,
    string RequestedBy,
    string Purpose,
    DateTimeOffset ExpiresAt);

public sealed record SensitiveAccessDecisionIntent(
    bool Approved,
    string? Reason = null,
    string? RedactedSummary = null);
