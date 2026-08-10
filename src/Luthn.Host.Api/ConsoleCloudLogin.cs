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
}

public sealed class DisabledConsoleCloudLoginProvider : IConsoleCloudLoginProvider
{
    public ConsoleCloudLoginProvider Kind => ConsoleCloudLoginProvider.Disabled;
    public bool Available => false;

    public ValueTask<AuthenticatedConsoleAuthority> AuthenticateAsync(CancellationToken cancellationToken) =>
        ValueTask.FromException<AuthenticatedConsoleAuthority>(
            new InvalidOperationException("A live Luthn Cloud login provider is not configured."));
}

public sealed class FakeConsoleCloudLoginProvider(
    IOptions<ConsoleCloudLoginOptions> options,
    IHostEnvironment environment)
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

    private static IReadOnlySet<string> MapScopes(IReadOnlyList<ConsoleCapability> capabilities)
    {
        var scopes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var capability in capabilities)
        {
            switch (capability)
            {
                case ConsoleCapability.AccessReview:
                    scopes.Add(ServiceScopes.AccessDecide);
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

public static class ConsoleCloudLoginEndpoints
{
    public static IEndpointRouteBuilder MapConsoleCloudLogin(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/operator/cloud-login");
        group.MapGet("", Read).WithName("ReadConsoleCloudLogin");
        group.MapPost("", Login).WithName("CreateCloudConsoleSession");
        return app;
    }

    private static Ok<ConsoleCloudLoginDto> Read(
        HttpContext context,
        IConsoleCloudLoginProvider provider,
        IConsoleSessionStore sessions,
        IConsoleLifecycleStore lifecycle)
    {
        var session = sessions.Authenticate(context);
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

        if (!context.Request.IsHttps && !environment.IsEnvironment("Testing"))
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
            sessions.RevokeAll();
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
                outcome: "authenticated"));
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
