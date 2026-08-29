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
    public const string LocalCandidateCookieName = "LuthnConsoleCandidate";
    public const string AntiforgeryHeaderName = "X-Luthn-CSRF";

    public bool LocalOnly { get; set; }
    public bool TrustedLocalBridge { get; set; }
    public int IdleMinutes { get; set; } = 15;
    public int AbsoluteMinutes { get; set; } = 120;
    public int LocalArmSeconds { get; set; } = 30;

    public TimeSpan EffectiveIdleLifetime =>
        TimeSpan.FromMinutes(Math.Clamp(IdleMinutes, 1, 60));

    public TimeSpan EffectiveAbsoluteLifetime =>
        TimeSpan.FromMinutes(Math.Clamp(AbsoluteMinutes, 5, 480));

    public TimeSpan EffectiveLocalArmLifetime =>
        TimeSpan.FromSeconds(Math.Clamp(LocalArmSeconds, 5, 60));
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
    bool Restricted);

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
    public required IReadOnlyList<ConsoleCapability> Capabilities { get; set; }
    public bool Restricted { get; set; }
    public bool Revoked { get; set; }
}

public interface IConsoleLocalAccessArmStore
{
    void EnsureCandidate(HttpContext context);
    bool ArmSingleCandidate();
    bool RequestCandidateApproval(HttpContext context);
    bool ArmSingleRequestedCandidate();
    bool IsCandidateApprovalRequested(HttpContext context);
    bool IsCandidateApproved(HttpContext context);
    bool TryConsumeCandidate(HttpContext context);
}

public sealed class InMemoryConsoleLocalAccessArmStore(
    TimeProvider timeProvider,
    IOptions<ConsoleAccessOptions> options) : IConsoleLocalAccessArmStore
{
    private static readonly TimeSpan CandidateLifetime = TimeSpan.FromMinutes(2);
    private readonly ConcurrentDictionary<string, LocalConsoleCandidate> _candidates =
        new(StringComparer.Ordinal);

    public void EnsureCandidate(HttpContext context)
    {
        var now = timeProvider.GetUtcNow();
        Prune(now);
        if (TryReadCandidate(context, out var existingId) &&
            _candidates.TryGetValue(existingId, out var existing) &&
            now < existing.ExpiresAt)
        {
            return;
        }

        var candidateId = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        _candidates[candidateId] = new LocalConsoleCandidate(now + CandidateLifetime);
        context.Response.Cookies.Append(
            ConsoleAccessOptions.LocalCandidateCookieName,
            candidateId,
            CandidateCookieOptions(context.Request.IsHttps, now + CandidateLifetime));
    }

    public bool ArmSingleCandidate()
    {
        var now = timeProvider.GetUtcNow();
        Prune(now);
        var candidates = _candidates
            .Where(pair => now < pair.Value.ExpiresAt)
            .Take(2)
            .ToArray();
        if (candidates.Length != 1)
        {
            return false;
        }

        candidates[0].Value.ApprovedUntil = now + options.Value.EffectiveLocalArmLifetime;
        return true;
    }

    public bool RequestCandidateApproval(HttpContext context)
    {
        var now = timeProvider.GetUtcNow();
        Prune(now);
        if (!TryReadCandidate(context, out var candidateId) ||
            !_candidates.TryGetValue(candidateId, out var candidate) ||
            now >= candidate.ExpiresAt)
        {
            return false;
        }

        candidate.ApprovalRequested = true;
        return true;
    }

    public bool ArmSingleRequestedCandidate()
    {
        var now = timeProvider.GetUtcNow();
        Prune(now);
        var candidates = _candidates
            .Where(pair => now < pair.Value.ExpiresAt && pair.Value.ApprovalRequested)
            .Take(2)
            .ToArray();
        if (candidates.Length != 1)
        {
            return false;
        }

        candidates[0].Value.ApprovalRequested = false;
        candidates[0].Value.ApprovedUntil = now + options.Value.EffectiveLocalArmLifetime;
        return true;
    }

    public bool IsCandidateApprovalRequested(HttpContext context)
    {
        var now = timeProvider.GetUtcNow();
        return TryReadCandidate(context, out var candidateId) &&
            _candidates.TryGetValue(candidateId, out var candidate) &&
            now < candidate.ExpiresAt &&
            candidate.ApprovalRequested;
    }

    public bool IsCandidateApproved(HttpContext context)
    {
        var now = timeProvider.GetUtcNow();
        return TryReadCandidate(context, out var candidateId) &&
            _candidates.TryGetValue(candidateId, out var candidate) &&
            candidate.ApprovedUntil is { } approvedUntil &&
            now < approvedUntil;
    }

    public bool TryConsumeCandidate(HttpContext context)
    {
        if (!TryReadCandidate(context, out var candidateId) ||
            !_candidates.TryRemove(candidateId, out var candidate) ||
            candidate.ApprovedUntil is not { } approvedUntil ||
            timeProvider.GetUtcNow() >= approvedUntil)
        {
            return false;
        }

        context.Response.Cookies.Delete(
            ConsoleAccessOptions.LocalCandidateCookieName,
            CandidateCookieOptions(context.Request.IsHttps, null));
        return true;
    }

    private static bool TryReadCandidate(HttpContext context, out string candidateId) =>
        context.Request.Cookies.TryGetValue(
            ConsoleAccessOptions.LocalCandidateCookieName,
            out candidateId!) &&
        !string.IsNullOrWhiteSpace(candidateId);

    private void Prune(DateTimeOffset now)
    {
        foreach (var pair in _candidates.Where(pair => now >= pair.Value.ExpiresAt))
        {
            _candidates.TryRemove(pair.Key, out _);
        }
    }

    private static CookieOptions CandidateCookieOptions(
        bool secure,
        DateTimeOffset? expiresAt) => new()
        {
            HttpOnly = true,
            Secure = secure,
            SameSite = SameSiteMode.Strict,
            Path = "/",
            Expires = expiresAt,
            IsEssential = true
        };

    private sealed class LocalConsoleCandidate(DateTimeOffset expiresAt)
    {
        public DateTimeOffset ExpiresAt { get; } = expiresAt;
        public bool ApprovalRequested { get; set; }
        public DateTimeOffset? ApprovedUntil { get; set; }
    }
}

public interface IConsoleSessionStore
{
    ConsoleSessionIdentity? Authenticate(HttpContext context);
    bool IsLocalEligible(HttpContext context);
    ConsoleSessionIdentity CreateLocal(HttpContext context);
    void Revoke(HttpContext context);
    void RevokeAll(Func<ConsoleSessionIdentity, bool>? predicate = null);
}

public sealed class InMemoryConsoleSessionStore(
    TimeProvider timeProvider,
    IOptions<ConsoleAccessOptions> options,
    IOptions<LuthnIdentityOptions> identityOptions,
    IOptions<LuthnHostOperationalOptions> hostOptions,
    IConsoleLocalAccessArmStore localAccessArm,
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
        ServiceScopes.AccessReview,
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
        ConsoleCapability.ConfigurationWrite
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

        if (!IsLocalEligible(context))
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
        if (!IsLocalEligible(context))
        {
            throw new InvalidOperationException(
                "Local automatic console access requires a SingleOwner installation with explicit loopback-only exposure.");
        }

        if (!localAccessArm.TryConsumeCandidate(context))
        {
            throw new InvalidOperationException(
                "Local console access must first be authorized with the installed `luthn console` command.");
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
        AppendCookie(context, sessionId, session.ExpiresAt, secure: context.Request.IsHttps);
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

    public bool IsLocalEligible(HttpContext context)
    {
        if (identityOptions.Value.Mode != LuthnIdentityMode.SingleOwner)
        {
            return false;
        }

        return ConsoleRequestSecurity.IsTrustedLocalRequest(
            context,
            options.Value,
            hostOptions.Value,
            environment);
    }

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
            session.Restricted);
}

public static class ConsoleSessionEndpoints
{
    public static IEndpointRouteBuilder MapConsoleSessions(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/operator/session");
        group.MapGet("", Read).WithName("ReadConsoleSession");
        group.MapPost("/local/arm", ArmLocal)
            .RequireServiceScope(ServiceScopes.ConfigWrite)
            .WithName("ArmLocalConsoleSession");
        group.MapPost("/local/request", RequestLocal)
            .WithName("RequestLocalConsoleSession");
        group.MapPost("/local/arm-requested", ArmRequestedLocal)
            .RequireServiceScope(ServiceScopes.ConfigWrite)
            .WithName("ArmRequestedLocalConsoleSession");
        group.MapPost("/local/connect", ConnectLocal)
            .WithName("ConnectLocalConsoleSession");
        group.MapPost("/local", CreateLocal).WithName("CreateLocalConsoleSession");
        group.MapPost("/logout", Logout).WithName("LogoutConsoleSession");
        return app;
    }

    private static Ok<ConsoleSessionDto> Read(
        HttpContext context,
        IConsoleSessionStore sessions,
        IConsoleLocalAccessArmStore localAccessArm,
        IAntiforgery antiforgery)
    {
        var session = sessions.Authenticate(context);
        if (session is not null)
        {
            WriteAntiforgeryHeader(context, antiforgery);
            return TypedResults.Ok(ToDto(session));
        }

        var localEligible = sessions.IsLocalEligible(context);
        if (localEligible)
        {
            localAccessArm.EnsureCandidate(context);
        }
        var nextAction = localEligible
            ? localAccessArm.IsCandidateApproved(context)
                ? "create-local-session"
                : localAccessArm.IsCandidateApprovalRequested(context)
                    ? "await-host-helper"
                    : "arm-local-session"
            : "local-access-unavailable";

        return TypedResults.Ok(new ConsoleSessionDto(
            ConsoleAccessMode.LocalAuto,
            ConsoleSessionState.Anonymous,
            null,
            null,
            [],
            nextAction,
            true));
    }

    private static IResult ArmLocal(
        HttpContext context,
        IConsoleSessionStore sessions,
        IConsoleLocalAccessArmStore localAccessArm)
    {
        var principal = ServiceTokenAuthorization.GetPrincipal(context);
        if (!ServiceTokenAuthorization.IsServiceTokenAuthenticated(context) ||
            !principal.IsOperator ||
            !sessions.IsLocalEligible(context))
        {
            return TypedResults.Problem(
                title: "Local console access is unavailable.",
                detail: "The installed local operator credential and an eligible loopback-only installation are required.",
                statusCode: StatusCodes.Status403Forbidden);
        }

        if (!localAccessArm.ArmSingleCandidate())
        {
            return TypedResults.Problem(
                title: "Local console authorization could not continue.",
                detail: "Open one local console window and retry. Multiple or missing browser candidates fail closed.",
                statusCode: StatusCodes.Status409Conflict);
        }

        return TypedResults.NoContent();
    }

    private static IResult RequestLocal(
        HttpContext context,
        IConsoleSessionStore sessions,
        IConsoleLocalAccessArmStore localAccessArm)
    {
        if (!ConsoleRequestSecurity.IsSameOriginOrNonBrowser(context.Request) ||
            !sessions.IsLocalEligible(context) ||
            !localAccessArm.RequestCandidateApproval(context))
        {
            return TypedResults.Problem(
                title: "Local console authorization could not continue.",
                detail: "Open one eligible loopback console window and retry.",
                statusCode: StatusCodes.Status409Conflict);
        }

        return TypedResults.Ok(new { state = "requested" });
    }

    private static IResult ArmRequestedLocal(
        HttpContext context,
        IConsoleSessionStore sessions,
        IConsoleLocalAccessArmStore localAccessArm)
    {
        var principal = ServiceTokenAuthorization.GetPrincipal(context);
        if (!ServiceTokenAuthorization.IsServiceTokenAuthenticated(context) ||
            !principal.IsOperator ||
            !sessions.IsLocalEligible(context))
        {
            return TypedResults.Problem(
                title: "Local console access is unavailable.",
                detail: "The installed local operator credential and an eligible loopback-only installation are required.",
                statusCode: StatusCodes.Status403Forbidden);
        }

        if (!localAccessArm.ArmSingleRequestedCandidate())
        {
            return TypedResults.Problem(
                title: "Local browser authorization is not pending.",
                detail: "Exactly one explicit browser request is required.",
                statusCode: StatusCodes.Status409Conflict);
        }

        return TypedResults.NoContent();
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

    private static Results<Ok<ConsoleSessionDto>, ProblemHttpResult> ConnectLocal(
        HttpContext context,
        IConsoleSessionStore sessions,
        IConsoleLocalAccessArmStore localAccessArm,
        IAntiforgery antiforgery)
    {
        if (!ConsoleRequestSecurity.IsSameOriginOrNonBrowser(context.Request))
        {
            return TypedResults.Problem(
                title: "Untrusted console origin.",
                detail: "Local console access can only be connected from the console origin.",
                statusCode: StatusCodes.Status403Forbidden);
        }

        if (!sessions.IsLocalEligible(context))
        {
            return TypedResults.Problem(
                title: "Local console access is unavailable.",
                detail: "Local access requires a SingleOwner installation with explicit loopback-only exposure.",
                statusCode: StatusCodes.Status403Forbidden);
        }

        if (!localAccessArm.IsCandidateApproved(context))
        {
            return TypedResults.Problem(
                title: "Local console authorization could not continue.",
                detail: "Request authorization from the installed Host Helper, then retry. Multiple or missing browser candidates fail closed.",
                statusCode: StatusCodes.Status409Conflict);
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
            ConsoleSessionState.Active,
            session.ExpiresAt,
            session.IdleExpiresAt,
            session.Capabilities,
            "continue",
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

    public static bool IsTrustedLocalRequest(
        HttpContext context,
        ConsoleAccessOptions consoleOptions,
        LuthnHostOperationalOptions hostOptions,
        IHostEnvironment environment)
    {
        if (!consoleOptions.LocalOnly || hostOptions.EnableForwardedHeaders)
        {
            return false;
        }

        if (environment.IsEnvironment("Testing"))
        {
            return true;
        }

        var local = context.Connection.LocalIpAddress;
        var remote = context.Connection.RemoteIpAddress;
        if (local is null || remote is null)
        {
            return false;
        }

        if (IPAddress.IsLoopback(local) && IPAddress.IsLoopback(remote))
        {
            return true;
        }

        return consoleOptions.TrustedLocalBridge &&
            IsLoopbackHost(context.Request.Host.Host) &&
            IsPrivateOrLoopback(local) &&
            IsPrivateOrLoopback(remote);
    }

    private static bool IsLoopbackHost(string host) =>
        string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
        IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address);

    private static bool IsPrivateOrLoopback(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }
        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            return bytes[0] == 10 ||
                (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
                (bytes[0] == 192 && bytes[1] == 168);
        }

        return address.IsIPv6LinkLocal || (bytes[0] & 0xfe) == 0xfc;
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
