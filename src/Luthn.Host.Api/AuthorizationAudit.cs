using Luthn.Core.Persistence;

namespace Luthn.Host.Api;

/// <summary>
/// Records bounded authentication and authorization denials without retaining
/// credentials, request content, headers, paths, or network identifiers.
/// Recording is best effort so a telemetry failure can never change an access
/// decision.
/// </summary>
internal static class AuthorizationAudit
{
    public static Task RecordCredentialRejectedAsync(
        HttpContext httpContext,
        string requiredScope) =>
        RecordAsync(
            httpContext,
            AuditEventFactory.ForInstallation(
                "unauthenticated",
                "authorization.credential_rejected",
                requiredScope,
                "metadata-only",
                "credential-not-retained",
                DateTimeOffset.UtcNow,
                actorKind: "system",
                subjectType: "required_scope",
                outcome: AuditOutcomes.Denied,
                correlationId: AuditCorrelationIds.CreateOperationId()));

    public static Task RecordScopeDeniedAsync(
        HttpContext httpContext,
        string workspaceId,
        string? actorUserId,
        string actorKind,
        string actor,
        string requiredScope,
        string action = "authorization.scope_denied") =>
        RecordAsync(
            httpContext,
            AuditEventFactory.ForWorkspace(
                workspaceId,
                actorUserId,
                actorKind,
                actor,
                action,
                requiredScope,
                "metadata-only",
                "authorization-metadata-only",
                DateTimeOffset.UtcNow,
                subjectType: "required_scope",
                outcome: AuditOutcomes.Denied,
                correlationId: AuditCorrelationIds.CreateOperationId()));

    private static async Task RecordAsync(HttpContext httpContext, AuditEventRecord auditEvent)
    {
        try
        {
            var db = httpContext.RequestServices.GetService<LuthnDbContext>();
            if (db is null)
            {
                return;
            }

            db.AuditEvents.Add(auditEvent);
            await db.SaveChangesAsync(httpContext.RequestAborted);
        }
        catch
        {
            // Authentication and authorization responses must remain fail-closed
            // even when best-effort audit persistence is unavailable.
        }
    }
}
