using Luthn.Core.Persistence;
using Luthn.Sdk.Console;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;

namespace Luthn.Host.Api;

public sealed class ConsoleCloudLoginOptions
{
    public const string SectionName = "Luthn:Console:CloudLogin";

    public ConsoleCloudLoginProvider Provider { get; set; } = ConsoleCloudLoginProvider.Disabled;
    public string UserId { get; set; } = "cloud-owner";
    public string OrganizationId { get; set; } = "fake-organization";
    public string WorkspaceId { get; set; } = "fake-workspace";
    public bool Owner { get; set; } = true;
    public bool MembershipActive { get; set; } = true;
    public bool EntitlementActive { get; set; } = true;
    public DateTimeOffset? ExpiresAt { get; set; }
}

public sealed record AuthenticatedConsoleAuthority(
    string SubjectKey,
    string UserId,
    string OrganizationId,
    string WorkspaceId,
    bool Owner,
    ConsoleMembershipState Membership,
    ConsoleEntitlementState Entitlement,
    IReadOnlyList<ConsoleCapability> Capabilities,
    IReadOnlySet<string> Scopes);

public interface IConsoleCloudLoginProvider
{
    ConsoleCloudLoginProvider Kind { get; }
    bool Available { get; }
    ValueTask<AuthenticatedConsoleAuthority> AuthenticateAsync(CancellationToken cancellationToken);
    ValueTask<AuthenticatedConsoleAuthority?> ValidateAsync(
        string subjectKey,
        CancellationToken cancellationToken);
}

public sealed class DisabledConsoleCloudLoginProvider : IConsoleCloudLoginProvider
{
    public ConsoleCloudLoginProvider Kind => ConsoleCloudLoginProvider.Disabled;
    public bool Available => false;

    public ValueTask<AuthenticatedConsoleAuthority> AuthenticateAsync(CancellationToken cancellationToken) =>
        ValueTask.FromException<AuthenticatedConsoleAuthority>(
            new InvalidOperationException("A live Luthn Cloud login provider is not configured."));

    public ValueTask<AuthenticatedConsoleAuthority?> ValidateAsync(
        string subjectKey,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult<AuthenticatedConsoleAuthority?>(null);
}

public sealed class FakeConsoleCloudLoginProvider(
    IOptions<ConsoleCloudLoginOptions> options,
    IHostEnvironment environment,
    TimeProvider timeProvider)
    : IConsoleCloudLoginProvider
{
    private static readonly ConsoleCapability[] OwnerCapabilities =
    [
        ConsoleCapability.AccessReview,
        ConsoleCapability.AccessDecision,
        ConsoleCapability.AuditRead,
        ConsoleCapability.ClassificationOperate,
        ConsoleCapability.SourceIntake,
        ConsoleCapability.PublicationOperate,
        ConsoleCapability.AgentConnectionRead,
        ConsoleCapability.ConfigurationWrite,
        ConsoleCapability.OffboardingExport,
        ConsoleCapability.InstallationDetach
    ];

    private static readonly ConsoleCapability[] MemberCapabilities =
    [
        ConsoleCapability.AccessReview,
        ConsoleCapability.AuditRead,
        ConsoleCapability.AgentConnectionRead,
        ConsoleCapability.OffboardingExport
    ];

    public ConsoleCloudLoginProvider Kind => ConsoleCloudLoginProvider.Fake;
    public bool Available => !environment.IsProduction();

    public ValueTask<AuthenticatedConsoleAuthority> AuthenticateAsync(CancellationToken cancellationToken)
    {
        if (!Available)
        {
            return ValueTask.FromException<AuthenticatedConsoleAuthority>(
                new InvalidOperationException("The fake Cloud login provider is disabled in Production."));
        }

        var configured = options.Value;
        if (configured.ExpiresAt is { } expiresAt && timeProvider.GetUtcNow() >= expiresAt)
        {
            return ValueTask.FromException<AuthenticatedConsoleAuthority>(
                new InvalidOperationException("Cloud account authentication has expired."));
        }

        var userId = ServiceTokenAuthorization.NormalizeUserId(configured.UserId);
        var organizationId = ServiceTokenAuthorization.NormalizeHubIdentity(configured.OrganizationId);
        var workspaceId = ServiceTokenAuthorization.NormalizeWorkspaceId(configured.WorkspaceId);
        if (userId is null || organizationId is null || workspaceId is null)
        {
            return ValueTask.FromException<AuthenticatedConsoleAuthority>(
                new InvalidOperationException("The fake Cloud authority configuration is invalid."));
        }

        var membership = configured.MembershipActive
            ? ConsoleMembershipState.Active
            : ConsoleMembershipState.Removed;
        var entitlement = configured.EntitlementActive
            ? ConsoleEntitlementState.Active
            : ConsoleEntitlementState.Restricted;
        if (membership != ConsoleMembershipState.Active)
        {
            return ValueTask.FromException<AuthenticatedConsoleAuthority>(
                new InvalidOperationException("Cloud membership is not active."));
        }

        var capabilities = configured.Owner ? OwnerCapabilities : MemberCapabilities;
        var scopes = MapScopes(capabilities);
        return ValueTask.FromResult(new AuthenticatedConsoleAuthority(
            $"{organizationId}:{userId}",
            userId,
            organizationId,
            workspaceId,
            configured.Owner,
            membership,
            entitlement,
            capabilities,
            scopes));
    }

    public async ValueTask<AuthenticatedConsoleAuthority?> ValidateAsync(
        string subjectKey,
        CancellationToken cancellationToken)
    {
        try
        {
            var authority = await AuthenticateAsync(cancellationToken);
            return string.Equals(authority.SubjectKey, subjectKey, StringComparison.Ordinal)
                ? authority
                : null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static IReadOnlySet<string> MapScopes(IReadOnlyList<ConsoleCapability> capabilities)
    {
        var scopes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var capability in capabilities)
        {
            switch (capability)
            {
                case ConsoleCapability.AccessReview:
                    scopes.Add(ServiceScopes.AccessReview);
                    break;
                case ConsoleCapability.AccessDecision:
                    scopes.Add(ServiceScopes.AccessDecide);
                    break;
                case ConsoleCapability.AuditRead:
                    scopes.Add(ServiceScopes.AuditRead);
                    break;
                case ConsoleCapability.ClassificationOperate:
                    scopes.Add(ServiceScopes.ClassificationPreview);
                    break;
                case ConsoleCapability.SourceIntake:
                    scopes.Add(ServiceScopes.SourceWrite);
                    break;
                case ConsoleCapability.PublicationOperate:
                    scopes.Add(ServiceScopes.ExternalPublicationRead);
                    scopes.Add(ServiceScopes.ExternalPublicationWrite);
                    break;
                case ConsoleCapability.AgentConnectionRead:
                    scopes.Add(ServiceScopes.AgentConnectionRead);
                    break;
                case ConsoleCapability.ConfigurationWrite:
                    scopes.Add(ServiceScopes.ConfigWrite);
                    break;
            }
        }

        return scopes;
    }
}

public sealed record ConsoleCloudSessionValidation(
    ConsoleSessionIdentity? Session,
    string? Reason,
    string? Detail);

public interface IConsoleCloudSessionValidator
{
    ValueTask<ConsoleCloudSessionValidation> ValidateAsync(
        HttpContext context,
        ConsoleSessionIdentity? session,
        CancellationToken cancellationToken);
}

public sealed class ConsoleCloudSessionValidator(
    IConsoleCloudLoginProvider provider,
    IConsoleSessionStore sessions,
    IConsoleLifecycleStore lifecycle,
    TimeProvider timeProvider) : IConsoleCloudSessionValidator
{
    private const string ExpiredReason = "cloud-account-expired";
    private const string ExpiredDetail =
        "Cloud account authentication expired or was revoked. Sign in again; Local access will not be restored automatically.";

    public async ValueTask<ConsoleCloudSessionValidation> ValidateAsync(
        HttpContext context,
        ConsoleSessionIdentity? session,
        CancellationToken cancellationToken)
    {
        if (session is null || session.Mode == ConsoleAccessMode.LocalAuto)
        {
            return new(session, null, null);
        }

        var subjectKey = session.CloudSubjectKey;
        if (string.IsNullOrWhiteSpace(subjectKey) || lifecycle.IsSubjectRemoved(subjectKey))
        {
            return Revoke(context, subjectKey);
        }

        AuthenticatedConsoleAuthority? authority;
        try
        {
            authority = await provider.ValidateAsync(subjectKey, cancellationToken);
        }
        catch (InvalidOperationException)
        {
            authority = null;
        }

        if (authority is null || !string.Equals(authority.SubjectKey, subjectKey, StringComparison.Ordinal))
        {
            return Revoke(context, subjectKey);
        }

        if (authority.Membership != ConsoleMembershipState.Active)
        {
            return Revoke(context, subjectKey);
        }

        if (authority.Entitlement == ConsoleEntitlementState.Restricted)
        {
            if (lifecycle.Current.OrganizationState != ConsoleOrganizationState.RestrictedOffboarding)
            {
                var now = timeProvider.GetUtcNow();
                await lifecycle.RevokeConnectionAuthorityAsync(now, cancellationToken);
                await lifecycle.RestrictOrganizationAsync(cancellationToken);
                sessions.RestrictCloudSessions();
            }
            else
            {
                sessions.RestrictSubject(subjectKey);
            }

            return new(sessions.Authenticate(context), null, null);
        }

        // A restricted session must not regain authority silently after the
        // provider becomes active again. Explicit Cloud reauthentication is
        // required to create a fresh session with the restored capabilities.
        if (session.Restricted || session.Entitlement == ConsoleEntitlementState.Restricted)
        {
            return new(session, null, null);
        }

        if (!MatchesActiveAuthority(session, authority))
        {
            return Revoke(context, subjectKey);
        }

        return new(session, null, null);
    }

    private static bool MatchesActiveAuthority(
        ConsoleSessionIdentity session,
        AuthenticatedConsoleAuthority authority) =>
        string.Equals(session.UserId, authority.UserId, StringComparison.Ordinal) &&
        string.Equals(session.OrganizationId, authority.OrganizationId, StringComparison.Ordinal) &&
        string.Equals(session.WorkspaceId, authority.WorkspaceId, StringComparison.Ordinal) &&
        session.CloudOwner == authority.Owner &&
        session.Membership == authority.Membership &&
        session.Entitlement == authority.Entitlement &&
        HasSameValues(session.Scopes, authority.Scopes) &&
        HasSameValues(session.Capabilities, authority.Capabilities);

    private static bool HasSameValues<T>(
        IEnumerable<T> left,
        IEnumerable<T> right)
    {
        var rightValues = right.ToArray();
        return left.Count() == rightValues.Length && left.All(rightValues.Contains);
    }

    private ConsoleCloudSessionValidation Revoke(
        HttpContext context,
        string? subjectKey)
    {
        if (!string.IsNullOrWhiteSpace(subjectKey))
        {
            sessions.RevokeSubject(subjectKey);
        }

        sessions.Revoke(context);
        return new(null, ExpiredReason, ExpiredDetail);
    }
}

public static class ConsoleCloudLoginEndpoints
{
    public static IEndpointRouteBuilder MapConsoleCloudLogin(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/operator/cloud-login");
        group.MapGet("", Read).WithName("ReadConsoleCloudLogin");
        group.MapPost("", Login).WithName("CreateCloudConsoleSession");
        return app;
    }

    private static async Task<Ok<ConsoleCloudLoginDto>> Read(
        HttpContext context,
        IConsoleCloudLoginProvider provider,
        IConsoleSessionStore sessions,
        IConsoleLifecycleStore lifecycle,
        IConsoleCloudSessionValidator cloudSessionValidator,
        CancellationToken cancellationToken)
    {
        var validation = await cloudSessionValidator.ValidateAsync(
            context,
            sessions.Authenticate(context),
            cancellationToken);
        var session = validation.Session;
        return TypedResults.Ok(ToDto(provider, lifecycle.IsEnrolled, session));
    }

    private static async Task<Results<Ok<ConsoleSessionDto>, ProblemHttpResult>> Login(
        HttpContext context,
        IConsoleCloudLoginProvider provider,
        IConsoleLifecycleStore lifecycle,
        IConsoleSessionStore sessions,
        IAntiforgery antiforgery,
        LuthnDbContext db,
        TimeProvider timeProvider,
        IHostEnvironment environment,
        IOptions<ConsoleAccessOptions> consoleOptions,
        IOptions<LuthnHostOperationalOptions> hostOptions,
        CancellationToken cancellationToken)
    {
        if (!ConsoleRequestSecurity.IsSameOriginOrNonBrowser(context.Request))
        {
            return LoginProblem("Cloud login must begin from the same console origin.", StatusCodes.Status403Forbidden);
        }

        if (!lifecycle.IsEnrolled)
        {
            return LoginProblem("This installation is not enrolled.", StatusCodes.Status409Conflict);
        }

        if (!context.Request.IsHttps &&
            !ConsoleRequestSecurity.IsTrustedLocalRequest(
                context,
                consoleOptions.Value,
                hostOptions.Value,
                environment))
        {
            return LoginProblem("Cloud console sessions require HTTPS.", StatusCodes.Status400BadRequest);
        }

        try
        {
            var authority = await provider.AuthenticateAsync(cancellationToken);
            if (lifecycle.IsSubjectRemoved(authority.SubjectKey))
            {
                throw new InvalidOperationException("Cloud membership is no longer active for this account.");
            }
            if (authority.Entitlement == ConsoleEntitlementState.Restricted)
            {
                var now = timeProvider.GetUtcNow();
                await lifecycle.RevokeConnectionAuthorityAsync(now, cancellationToken);
                await lifecycle.RestrictOrganizationAsync(cancellationToken);
            }
            sessions.RevokeAll(session => session.Mode == ConsoleAccessMode.LocalAuto);
            var session = sessions.CreateCloud(context, authority);
            db.AuditEvents.Add(AuditEventFactory.ForInstallation(
                "console:cloud-user",
                "console.cloud_login.succeeded",
                "console-installation",
                "metadata-only",
                "no-content",
                timeProvider.GetUtcNow(),
                actorKind: "user",
                subjectType: "console_session",
                outcome: "authenticated",
                actorUserId: authority.UserId));
            await db.SaveChangesAsync(cancellationToken);
            ConsoleSessionEndpoints.WriteAntiforgeryHeader(context, antiforgery);
            return TypedResults.Ok(ConsoleSessionEndpoints.ToDto(session));
        }
        catch (InvalidOperationException error)
        {
            return LoginProblem(error.Message, StatusCodes.Status403Forbidden);
        }
    }

    private static ConsoleCloudLoginDto ToDto(
        IConsoleCloudLoginProvider provider,
        bool enrolled,
        ConsoleSessionIdentity? session) =>
        new(
            provider.Kind,
            provider.Available,
            session is null
                ? enrolled ? ConsoleSessionState.LoginRequired : ConsoleSessionState.Anonymous
                : session.Restricted ? ConsoleSessionState.Restricted : ConsoleSessionState.Active,
            session?.Membership,
            session?.Entitlement,
            session?.Capabilities ?? [],
            session is not null
                ? "continue"
                : !enrolled
                    ? "enroll-installation"
                    : provider.Available ? "cloud-login" : "configure-cloud-provider",
            true);

    private static ProblemHttpResult LoginProblem(string detail, int statusCode) =>
        TypedResults.Problem(
            title: "Cloud login could not continue.",
            detail: detail,
            statusCode: statusCode);
}
