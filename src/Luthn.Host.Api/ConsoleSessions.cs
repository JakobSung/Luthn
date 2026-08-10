using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using Luthn.Core.Common;
using Luthn.Core.Persistence;
using Luthn.Sdk.Console;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace Luthn.Host.Api;

public sealed class ConsoleAccessOptions
{
    public const string SectionName = "Luthn:Console";
    public const string CookieName = "LuthnConsoleSid";
    public const string AntiforgeryHeaderName = "X-Luthn-CSRF";

    public bool LocalOnly { get; set; }
    public int IdleMinutes { get; set; } = 15;
    public int AbsoluteMinutes { get; set; } = 120;

    public TimeSpan EffectiveIdleLifetime =>
        TimeSpan.FromMinutes(Math.Clamp(IdleMinutes, 1, 60));

    public TimeSpan EffectiveAbsoluteLifetime =>
        TimeSpan.FromMinutes(Math.Clamp(AbsoluteMinutes, 5, 480));
}

public sealed record ConsoleSessionIdentity(
    string SessionId,
    ConsoleAccessMode Mode,
    string UserId,
    string WorkspaceId,
    string ActorId,
    DateTimeOffset ExpiresAt,
    DateTimeOffset IdleExpiresAt,
    IReadOnlySet<string> Scopes,
    IReadOnlyList<ConsoleCapability> Capabilities,
    bool Restricted,
    string? CloudSubjectKey = null,
    string? OrganizationId = null,
    ConsoleMembershipState? Membership = null,
    ConsoleEntitlementState? Entitlement = null);

internal sealed class ConsoleSessionRecord
{
    public required string SessionId { get; init; }
    public required ConsoleAccessMode Mode { get; init; }
    public required string UserId { get; init; }
    public required string WorkspaceId { get; init; }
    public required string ActorId { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset LastSeenAt { get; set; }
    public required DateTimeOffset ExpiresAt { get; init; }
    public required HashSet<string> Scopes { get; init; }
    public required IReadOnlyList<ConsoleCapability> Capabilities { get; init; }
    public bool Restricted { get; set; }
    public bool Revoked { get; set; }
    public string? CloudSubjectKey { get; init; }
    public string? OrganizationId { get; init; }
    public ConsoleMembershipState? Membership { get; init; }
    public ConsoleEntitlementState? Entitlement { get; set; }
}

public interface IConsoleInstallationState
{
    bool IsEnrolled { get; }
}

public sealed class UnenrolledConsoleInstallationState : IConsoleInstallationState
{
    public bool IsEnrolled => false;
}

public interface IConsoleSessionStore
{
    ConsoleSessionIdentity? Authenticate(HttpContext context);
    ConsoleSessionIdentity CreateLocal(HttpContext context);
    ConsoleSessionIdentity CreateCloud(HttpContext context, AuthenticatedConsoleAuthority authority);
    void Revoke(HttpContext context);
    void RevokeAll(Func<ConsoleSessionIdentity, bool>? predicate = null);
}

public sealed class InMemoryConsoleSessionStore(
    TimeProvider timeProvider,
    IOptions<ConsoleAccessOptions> options,
    IOptions<LuthnIdentityOptions> identityOptions,
    IOptions<LuthnHostOperationalOptions> hostOptions,
    IConsoleInstallationState installationState,
    IHostEnvironment environment) : IConsoleSessionStore
{
    private static readonly string[] LocalScopes =
    [
        ServiceScopes.AgentConnectionRead,
        ServiceScopes.ClassificationPreview,
        ServiceScopes.SourceWrite,
        ServiceScopes.ExternalPublicationRead,
        ServiceScopes.ExternalPublicationWrite,
        ServiceScopes.AccessRequest,
        ServiceScopes.AccessDecide,
        ServiceScopes.AuditRead,
        ServiceScopes.ConfigWrite,
        ServiceScopes.MetricsRead
    ];

    private static readonly ConsoleCapability[] LocalCapabilities =
    [
        ConsoleCapability.AccessReview,
        ConsoleCapability.AccessDecision,
        ConsoleCapability.AuditRead,
        ConsoleCapability.ClassificationOperate,
        ConsoleCapability.SourceIntake,
        ConsoleCapability.PublicationOperate,
        ConsoleCapability.AgentConnectionRead,
        ConsoleCapability.ConfigurationWrite,
        ConsoleCapability.EnrollmentManage
    ];

    private readonly ConcurrentDictionary<string, ConsoleSessionRecord> _sessions =
        new(StringComparer.Ordinal);

    public ConsoleSessionIdentity? Authenticate(HttpContext context)
    {
        if (!context.Request.Cookies.TryGetValue(ConsoleAccessOptions.CookieName, out var sessionId) ||
            string.IsNullOrWhiteSpace(sessionId) ||
            !_sessions.TryGetValue(sessionId, out var session))
        {
            return null;
        }

        var now = timeProvider.GetUtcNow();
        var idleExpiry = session.LastSeenAt + options.Value.EffectiveIdleLifetime;
        if (session.Revoked || now >= session.ExpiresAt || now >= idleExpiry)
        {
            _sessions.TryRemove(sessionId, out _);
            DeleteCookie(context);
            return null;
        }

        if (session.Mode == ConsoleAccessMode.LocalAuto && !CanCreateLocal(context))
        {
            _sessions.TryRemove(sessionId, out _);
            DeleteCookie(context);
            return null;
        }

        session.LastSeenAt = now;
        return ToIdentity(session, now + options.Value.EffectiveIdleLifetime);
    }

    public ConsoleSessionIdentity CreateLocal(HttpContext context)
    {
        if (!CanCreateLocal(context))
        {
            throw new InvalidOperationException(
                "Local automatic console access requires an un-enrolled SingleOwner installation with explicit loopback-only exposure.");
        }

        var userId = ServiceTokenAuthorization.NormalizeUserId(identityOptions.Value.SingleOwnerUserId)
            ?? throw new InvalidOperationException("The configured single-owner identity is invalid.");
        var now = timeProvider.GetUtcNow();
        var sessionId = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var session = new ConsoleSessionRecord
        {
            SessionId = sessionId,
            Mode = ConsoleAccessMode.LocalAuto,
            UserId = userId,
            WorkspaceId = WorkspaceIds.ForLegacyUser(userId),
            ActorId = "console:local-owner",
            CreatedAt = now,
            LastSeenAt = now,
            ExpiresAt = now + options.Value.EffectiveAbsoluteLifetime,
            Scopes = new HashSet<string>(LocalScopes, StringComparer.OrdinalIgnoreCase),
            Capabilities = LocalCapabilities,
            Restricted = false
        };
        _sessions[sessionId] = session;
        AppendCookie(context, sessionId, session.ExpiresAt, secure: false);
        return ToIdentity(session, now + options.Value.EffectiveIdleLifetime);
    }

    public ConsoleSessionIdentity CreateCloud(
        HttpContext context,
        AuthenticatedConsoleAuthority authority)
    {
        if (!installationState.IsEnrolled ||
            authority.Membership != ConsoleMembershipState.Active)
        {
            throw new InvalidOperationException("An enrolled installation and active membership are required.");
        }

        var now = timeProvider.GetUtcNow();
        var sessionId = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var session = new ConsoleSessionRecord
        {
            SessionId = sessionId,
            Mode = ConsoleAccessMode.CloudAuthenticated,
            UserId = authority.UserId,
            WorkspaceId = authority.WorkspaceId,
            ActorId = "console:cloud-user",
            CreatedAt = now,
            LastSeenAt = now,
            ExpiresAt = now + options.Value.EffectiveAbsoluteLifetime,
            Scopes = new HashSet<string>(authority.Scopes, StringComparer.OrdinalIgnoreCase),
            Capabilities = authority.Capabilities,
            Restricted = authority.Entitlement == ConsoleEntitlementState.Restricted,
            CloudSubjectKey = authority.SubjectKey,
            OrganizationId = authority.OrganizationId,
            Membership = authority.Membership,
            Entitlement = authority.Entitlement
        };
        _sessions[sessionId] = session;
        AppendCookie(context, sessionId, session.ExpiresAt, secure: true);
        return ToIdentity(session, now + options.Value.EffectiveIdleLifetime);
    }

    public void Revoke(HttpContext context)
    {
        if (context.Request.Cookies.TryGetValue(ConsoleAccessOptions.CookieName, out var sessionId))
        {
            _sessions.TryRemove(sessionId, out _);
        }

        DeleteCookie(context);
    }

    public void RevokeAll(Func<ConsoleSessionIdentity, bool>? predicate = null)
    {
        foreach (var pair in _sessions)
        {
            var identity = ToIdentity(
                pair.Value,
                pair.Value.LastSeenAt + options.Value.EffectiveIdleLifetime);
            if (predicate is null || predicate(identity))
            {
                _sessions.TryRemove(pair.Key, out _);
            }
        }
    }

    private bool CanCreateLocal(HttpContext context)
    {
        if (!options.Value.LocalOnly ||
            identityOptions.Value.Mode != LuthnIdentityMode.SingleOwner ||
            installationState.IsEnrolled ||
            hostOptions.Value.EnableForwardedHeaders)
        {
            return false;
        }

        if (environment.IsEnvironment("Testing"))
        {
            return true;
        }

        return IsLoopback(context.Connection.LocalIpAddress) &&
            IsLoopback(context.Connection.RemoteIpAddress);
    }

    private static bool IsLoopback(IPAddress? address) =>
        address is not null && IPAddress.IsLoopback(address);

    internal static void AppendCookie(
        HttpContext context,
        string sessionId,
        DateTimeOffset expiresAt,
        bool secure)
    {
        context.Response.Cookies.Append(ConsoleAccessOptions.CookieName, sessionId, new CookieOptions
        {
            HttpOnly = true,
            Secure = secure,
            SameSite = SameSiteMode.Strict,
            Path = "/",
            Expires = expiresAt,
            IsEssential = true
        });
    }

    internal static void DeleteCookie(HttpContext context) =>
        context.Response.Cookies.Delete(ConsoleAccessOptions.CookieName, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Strict,
            Secure = context.Request.IsHttps,
            Path = "/"
        });

    private static ConsoleSessionIdentity ToIdentity(
        ConsoleSessionRecord session,
        DateTimeOffset idleExpiresAt) =>
        new(
            session.SessionId,
            session.Mode,
            session.UserId,
            session.WorkspaceId,
            session.ActorId,
            session.ExpiresAt,
            idleExpiresAt < session.ExpiresAt ? idleExpiresAt : session.ExpiresAt,
            session.Scopes,
            session.Capabilities,
            session.Restricted,
            session.CloudSubjectKey,
            session.OrganizationId,
            session.Membership,
            session.Entitlement);
}

public static class ConsoleSessionEndpoints
{
    public static IEndpointRouteBuilder MapConsoleSessions(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/operator/session");
        group.MapGet("", Read).WithName("ReadConsoleSession");
        group.MapPost("/local", CreateLocal).WithName("CreateLocalConsoleSession");
        group.MapPost("/logout", Logout).WithName("LogoutConsoleSession");
        return app;
    }

    private static Ok<ConsoleSessionDto> Read(
        HttpContext context,
        IConsoleSessionStore sessions,
        IAntiforgery antiforgery,
        IOptions<LuthnIdentityOptions> identityOptions,
        IOptions<ConsoleAccessOptions> consoleOptions,
        IConsoleInstallationState installationState)
    {
        var session = sessions.Authenticate(context);
        if (session is not null)
        {
            WriteAntiforgeryHeader(context, antiforgery);
            return TypedResults.Ok(ToDto(session));
        }

        var localEligible = consoleOptions.Value.LocalOnly &&
            identityOptions.Value.Mode == LuthnIdentityMode.SingleOwner &&
            !installationState.IsEnrolled;
        return TypedResults.Ok(new ConsoleSessionDto(
            installationState.IsEnrolled || !localEligible
                ? ConsoleAccessMode.CloudLoginRequired
                : ConsoleAccessMode.LocalAuto,
            installationState.IsEnrolled || !localEligible
                ? ConsoleSessionState.LoginRequired
                : ConsoleSessionState.Anonymous,
            null,
            null,
            [],
            installationState.IsEnrolled || !localEligible ? "cloud-login" : "create-local-session",
            true));
    }

    private static Results<Ok<ConsoleSessionDto>, ProblemHttpResult> CreateLocal(
        HttpContext context,
        IConsoleSessionStore sessions,
        IAntiforgery antiforgery)
    {
        if (!ConsoleRequestSecurity.IsSameOriginOrNonBrowser(context.Request))
        {
            return TypedResults.Problem(
                title: "Untrusted console origin.",
                detail: "Local console sessions can only be created from the same origin.",
                statusCode: StatusCodes.Status403Forbidden);
        }

        try
        {
            var session = sessions.CreateLocal(context);
            WriteAntiforgeryHeader(context, antiforgery);
            return TypedResults.Ok(ToDto(session));
        }
        catch (InvalidOperationException error)
        {
            return TypedResults.Problem(
                title: "Local console access is unavailable.",
                detail: error.Message,
                statusCode: StatusCodes.Status403Forbidden);
        }
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> Logout(
        HttpContext context,
        IConsoleSessionStore sessions,
        IAntiforgery antiforgery)
    {
        if (sessions.Authenticate(context) is null)
        {
            sessions.Revoke(context);
            return TypedResults.NoContent();
        }

        var csrfFailure = await ConsoleRequestSecurity.ValidateMutationAsync(context, antiforgery);
        if (csrfFailure is not null)
        {
            return csrfFailure;
        }

        sessions.Revoke(context);
        return TypedResults.NoContent();
    }

    internal static ConsoleSessionDto ToDto(ConsoleSessionIdentity session) =>
        new(
            session.Mode,
            session.Restricted ? ConsoleSessionState.Restricted : ConsoleSessionState.Active,
            session.ExpiresAt,
            session.IdleExpiresAt,
            session.Capabilities,
            session.Restricted ? "offboarding" : "continue",
            true);

    internal static void WriteAntiforgeryHeader(HttpContext context, IAntiforgery antiforgery)
    {
        var requestValue = antiforgery.GetAndStoreTokens(context).RequestToken;
        if (!string.IsNullOrWhiteSpace(requestValue))
        {
            context.Response.Headers[ConsoleAccessOptions.AntiforgeryHeaderName] = requestValue;
        }
    }
}

public static class ConsoleRequestSecurity
{
    public static bool IsSameOriginOrNonBrowser(HttpRequest request)
    {
        var origin = request.Headers.Origin.ToString();
        if (string.IsNullOrWhiteSpace(origin))
        {
            return true;
        }

        return Uri.TryCreate(origin, UriKind.Absolute, out var originUri) &&
            string.Equals(originUri.Scheme, request.Scheme, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(originUri.Authority, request.Host.Value, StringComparison.OrdinalIgnoreCase);
    }

    public static async Task<ProblemHttpResult?> ValidateMutationAsync(
        HttpContext context,
        IAntiforgery antiforgery)
    {
        if (!IsSameOriginOrNonBrowser(context.Request))
        {
            return TypedResults.Problem(
                title: "Untrusted console origin.",
                detail: "Cookie-authenticated changes require a same-origin request.",
                statusCode: StatusCodes.Status403Forbidden);
        }

        if (!await antiforgery.IsRequestValidAsync(context))
        {
            return TypedResults.Problem(
                title: "Console request verification failed.",
                detail: "Refresh the console session and retry the change.",
                statusCode: StatusCodes.Status403Forbidden);
        }

        return null;
    }
}
