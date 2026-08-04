using Luthn.Core.Persistence;

namespace Luthn.Host.Api;

/// <summary>
/// Creates audit records with the request scope and actor identity attached.
/// Keeping this at the host boundary prevents individual endpoints from
/// accidentally writing an unscoped event when the identity model evolves.
/// </summary>
public static class AuditEventFactory
{
    public static AuditEventRecord ForWorkspace(
        LuthnRequestPrincipal principal,
        string actor,
        string action,
        string subjectId,
        string payloadClass,
        string redactionState,
        DateTimeOffset occurredAt,
        string subjectType = "unknown",
        string outcome = "unspecified",
        string? correlationId = null,
        string? id = null) =>
        new()
        {
            Id = id ?? $"audit-{Guid.NewGuid():N}",
            OccurredAt = occurredAt,
            ScopeKind = AuditEventScopeKind.Workspace,
            WorkspaceId = principal.WorkspaceId,
            Actor = actor,
            ActorUserId = principal.UserId,
            ActorKind = ServiceTokenAuthorization.GetActorKind(principal),
            Action = action,
            SubjectId = subjectId,
            SubjectType = subjectType,
            Outcome = outcome,
            CorrelationId = correlationId,
            PayloadClass = payloadClass,
            RedactionState = redactionState
        };

    public static AuditEventRecord ForWorkspace(
        string workspaceId,
        string? actorUserId,
        string actorKind,
        string actor,
        string action,
        string subjectId,
        string payloadClass,
        string redactionState,
        DateTimeOffset occurredAt,
        string subjectType = "unknown",
        string outcome = "unspecified",
        string? correlationId = null,
        string? id = null) =>
        new()
        {
            Id = id ?? $"audit-{Guid.NewGuid():N}",
            OccurredAt = occurredAt,
            ScopeKind = AuditEventScopeKind.Workspace,
            WorkspaceId = workspaceId,
            Actor = actor,
            ActorUserId = actorUserId,
            ActorKind = actorKind,
            Action = action,
            SubjectId = subjectId,
            SubjectType = subjectType,
            Outcome = outcome,
            CorrelationId = correlationId,
            PayloadClass = payloadClass,
            RedactionState = redactionState
        };

    public static AuditEventRecord ForInstallation(
        string actor,
        string action,
        string subjectId,
        string payloadClass,
        string redactionState,
        DateTimeOffset occurredAt,
        string actorKind = "system",
        string subjectType = "installation",
        string outcome = "unspecified",
        string? correlationId = null,
        string? id = null,
        string? actorUserId = null) =>
        new()
        {
            Id = id ?? $"audit-{Guid.NewGuid():N}",
            OccurredAt = occurredAt,
            ScopeKind = AuditEventScopeKind.Installation,
            WorkspaceId = "",
            Actor = actor,
            ActorUserId = actorUserId,
            ActorKind = actorKind,
            Action = action,
            SubjectId = subjectId,
            SubjectType = subjectType,
            Outcome = outcome,
            CorrelationId = correlationId,
            PayloadClass = payloadClass,
            RedactionState = redactionState
        };
}
