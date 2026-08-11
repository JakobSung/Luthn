using System.Security.Cryptography;
using System.Text;
using Luthn.Core.Common;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;

namespace Luthn.Host.Api;

public static class ServiceScopes
{
    public const string AgentRead = "agent.read";
    public const string AgentSummaryWrite = "agent.write.summary";
    public const string AgentConnectionRead = "agent.connection.read";
    public const string AgentConnectionWrite = "agent.connection.write";
    public const string MemoryRead = "memory.read";
    public const string MemoryWrite = "memory.write";
    public const string ExternalPublicationRead = "external-publication.read";
    public const string ExternalPublicationWrite = "external-publication.write";
    public const string ClassificationPreview = "classification.preview";
    public const string SourceWrite = "source.write";
    public const string AccessRequest = "access.request";
    public const string AccessReview = "access.review";
    public const string AccessDecide = "access.decide";
    public const string AccessConfigure = "access.configure";
    public const string AuditRead = "audit.read";
    public const string ConfigWrite = "config.write";
    public const string MetricsRead = "metrics.read";
    public const string MetricsWrite = "metrics.write";
    public const string HubIngressWrite = "hub.ingress.write";
    public const string HubIngressOperate = "hub.ingress.operate";
    public const string All = "*";
}

public sealed class LuthnAuthOptions
{
    public bool RequireServiceToken { get; set; }
    public List<LuthnServiceTokenOptions> Tokens { get; set; } = [];
}

public sealed class LuthnServiceTokenOptions
{
    public string Name { get; set; } = "";
    public string Sha256Digest { get; set; } = "";
    public List<string> Scopes { get; set; } = [];
    public DateTimeOffset? ExpiresAt { get; set; }
    public string? UserId { get; set; }
    public string? WorkspaceId { get; set; }
    public string? HubOrganizationId { get; set; }
    public string? HubAgentConnectionId { get; set; }
    public string? HubAgentId { get; set; }
    public string? HubSessionId { get; set; }
    public LuthnActorKind ActorKind { get; set; } = LuthnActorKind.Service;
    public bool IsOperator { get; set; }
}

public enum LuthnActorKind
{
    User,
    Agent,
    Service,
    System
}

public enum LuthnIdentityMode
{
    SingleOwner,
    MultiUser
}

public sealed class LuthnIdentityOptions
{
    public const string DefaultSingleOwnerUserId = "local-owner";

    public LuthnIdentityMode Mode { get; set; } = LuthnIdentityMode.SingleOwner;
    public string SingleOwnerUserId { get; set; } = DefaultSingleOwnerUserId;
}

public sealed record LuthnRequestPrincipal(
    string UserId,
    string WorkspaceId,
    LuthnActorKind ActorKind,
    string ActorId,
    bool IsOperator,
    string? HubOrganizationId = null,
    string? HubAgentConnectionId = null,
    string? HubAgentId = null,
    string? HubSessionId = null)
{
    public bool CanAccessWorkspace(string workspaceId) =>
        string.Equals(WorkspaceId, workspaceId, StringComparison.Ordinal);

    public bool CanAccessPrivateUserData(string ownerUserId) =>
        string.Equals(UserId, ownerUserId, StringComparison.Ordinal);

    public bool HasTrustedHubBinding =>
        HubOrganizationId is not null && HubAgentConnectionId is not null &&
        HubAgentId is not null && HubSessionId is not null;
}

public static class ServiceTokenAuthorization
{
    public const string OperatorHeaderName = "X-Luthn-Operator";

    private const string ActorItemKey = "Luthn.ServiceActor";
    private const string ServiceTokenAuthenticatedItemKey = "Luthn.ServiceTokenAuthenticated";
    private const string PrincipalItemKey = "Luthn.RequestPrincipal";
    private const int MaxActorLength = 128;
    private const int MaxOperatorIdentityLength = 64;

    public static RouteGroupBuilder RequireServiceScope(
        this RouteGroupBuilder group,
        string requiredScope)
    {
        group.AddEndpointFilter((context, next) =>
            RequireScopeAsync(context, next, requiredScope));
        return group;
    }

    public static RouteHandlerBuilder RequireServiceScope(
        this RouteHandlerBuilder builder,
        string requiredScope)
    {
        builder.AddEndpointFilter((context, next) =>
            RequireScopeAsync(context, next, requiredScope));
        return builder;
    }

    public static string GetActor(HttpContext httpContext)
    {
        if (httpContext.Items.TryGetValue(ActorItemKey, out var value) &&
            value is string actor &&
            !string.IsNullOrWhiteSpace(actor))
        {
            return actor;
        }

        return "local-anonymous";
    }

    public static string GetActorKind(LuthnRequestPrincipal principal) =>
        principal.ActorKind.ToString().ToLowerInvariant();

    public static bool IsServiceTokenAuthenticated(HttpContext httpContext) =>
        httpContext.Items.TryGetValue(ServiceTokenAuthenticatedItemKey, out var value) &&
        value is true;

    public static LuthnRequestPrincipal GetPrincipal(HttpContext httpContext)
    {
        if (httpContext.Items.TryGetValue(PrincipalItemKey, out var value) &&
            value is LuthnRequestPrincipal principal)
        {
            return principal;
        }

        return new LuthnRequestPrincipal(
            LuthnIdentityOptions.DefaultSingleOwnerUserId,
            WorkspaceIds.Default,
            LuthnActorKind.System,
            "local-anonymous",
            IsOperator: false);
    }

    public static string? NormalizeUserId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim().ToLowerInvariant();
        if (normalized.Length > 128 ||
            !IsAsciiLetterOrDigit(normalized[0]) ||
            normalized.Any(character =>
                !IsAsciiLetterOrDigit(character) &&
                character is not '.' and not '_' and not ':' and not '@' and not '-'))
        {
            return null;
        }

        return normalized;
    }

    public static string? NormalizeWorkspaceId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim().ToLowerInvariant();
        if (normalized.Length > WorkspaceIds.MaxLength ||
            !IsAsciiLetterOrDigit(normalized[0]) ||
            normalized.Any(character =>
                !IsAsciiLetterOrDigit(character) &&
                character is not '.' and not '_' and not ':' and not '@' and not '-'))
        {
            return null;
        }

        return normalized;
    }

    public static string? NormalizeHubIdentity(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim().ToLowerInvariant();
        if (normalized.Length > 128 ||
            !IsAsciiLetterOrDigit(normalized[0]) ||
            normalized.Any(character =>
                !IsAsciiLetterOrDigit(character) &&
                character is not '.' and not '_' and not ':' and not '@' and not '-'))
        {
            return null;
        }

        return normalized;
    }

    private static async ValueTask<object?> RequireScopeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next,
        string requiredScope)
    {
        var httpContext = context.HttpContext;
        var environment = httpContext.RequestServices.GetRequiredService<IHostEnvironment>();
        var authOptions = httpContext.RequestServices
            .GetRequiredService<IOptions<LuthnAuthOptions>>()
            .Value;
        var identityOptions = httpContext.RequestServices
            .GetRequiredService<IOptions<LuthnIdentityOptions>>()
            .Value;
        if (!Enum.IsDefined(identityOptions.Mode))
        {
            return IdentityConfigurationProblem("Identity mode is invalid.");
        }
        var singleOwnerUserId = NormalizeUserId(identityOptions.SingleOwnerUserId);
        if (singleOwnerUserId is null)
        {
            return IdentityConfigurationProblem("Single-owner user identity is invalid.");
        }

        if (!RequiresServiceToken(environment, authOptions))
        {
            if (identityOptions.Mode == LuthnIdentityMode.MultiUser)
            {
                return IdentityConfigurationProblem("Multi-user mode requires authenticated service tokens.");
            }

            httpContext.Items[ServiceTokenAuthenticatedItemKey] = false;
            var localActor = ComposeActor(
                "local-anonymous",
                httpContext.Request.Headers[OperatorHeaderName].ToString());
            httpContext.Items[PrincipalItemKey] = new LuthnRequestPrincipal(
                singleOwnerUserId,
                WorkspaceIds.ForLegacyUser(singleOwnerUserId),
                LuthnActorKind.System,
                localActor,
                IsOperator: true);
            httpContext.Items[ActorItemKey] = localActor;
            return await ContinueWhenSensitiveMemoryProtectionIsReady(context, next);
        }

        var authorization = httpContext.Request.Headers.Authorization.ToString();
        if (!TryReadBearer(authorization, out var bearer))
        {
            var consoleResult = await TryAuthorizeConsoleSessionAsync(
                context,
                next,
                requiredScope);
            if (consoleResult.Handled)
            {
                return consoleResult.Result;
            }

            httpContext.Response.Headers.WWWAuthenticate = "Bearer";
            return Unauthorized();
        }

        var matchedToken = FindMatchingToken(authOptions.Tokens, bearer, DateTimeOffset.UtcNow);
        if (matchedToken is null)
        {
            httpContext.Response.Headers.WWWAuthenticate = "Bearer";
            return Unauthorized();
        }

        if (!HasScope(matchedToken, requiredScope))
        {
            return TypedResults.Problem(
                title: "Forbidden.",
                detail: "The service token is not authorized for this operation.",
                statusCode: StatusCodes.Status403Forbidden);
        }


        var configuredUserId = NormalizeUserId(matchedToken.UserId);
        if (identityOptions.Mode == LuthnIdentityMode.MultiUser &&
            configuredUserId is null &&
            !matchedToken.IsOperator)
        {
            return IdentityConfigurationProblem(
                "Multi-user mode requires a valid server-configured userId for every non-operator token.");
        }

        if (!Enum.IsDefined(matchedToken.ActorKind))
        {
            return IdentityConfigurationProblem("The service token actor kind is invalid.");
        }

        var resolvedUserId = identityOptions.Mode == LuthnIdentityMode.SingleOwner
            ? singleOwnerUserId
            : configuredUserId ?? singleOwnerUserId;
        var configuredWorkspaceId = NormalizeWorkspaceId(matchedToken.WorkspaceId);
        if (!string.IsNullOrWhiteSpace(matchedToken.WorkspaceId) && configuredWorkspaceId is null)
        {
            return IdentityConfigurationProblem("The service token workspaceId is invalid.");
        }
        if (identityOptions.Mode == LuthnIdentityMode.MultiUser &&
            matchedToken.IsOperator &&
            configuredUserId is null &&
            configuredWorkspaceId is null)
        {
            return IdentityConfigurationProblem(
                "Multi-user operator tokens require a workspaceId or userId binding.");
        }

        var actor = ComposeActor(
            matchedToken.Name.Trim(),
            httpContext.Request.Headers[OperatorHeaderName].ToString());

        httpContext.Items[ServiceTokenAuthenticatedItemKey] = true;
        httpContext.Items[PrincipalItemKey] = new LuthnRequestPrincipal(
            resolvedUserId,
            configuredWorkspaceId ?? WorkspaceIds.ForLegacyUser(resolvedUserId),
            matchedToken.ActorKind,
            actor,
            matchedToken.IsOperator,
            NormalizeHubIdentity(matchedToken.HubOrganizationId),
            NormalizeHubIdentity(matchedToken.HubAgentConnectionId),
            NormalizeHubIdentity(matchedToken.HubAgentId),
            NormalizeHubIdentity(matchedToken.HubSessionId));
        httpContext.Items[ActorItemKey] = actor;
        return await ContinueWhenSensitiveMemoryProtectionIsReady(context, next);
    }

    private static async ValueTask<(bool Handled, object? Result)> TryAuthorizeConsoleSessionAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next,
        string requiredScope)
    {
        var httpContext = context.HttpContext;
        var sessions = httpContext.RequestServices.GetRequiredService<IConsoleSessionStore>();
        var session = sessions.Authenticate(httpContext);
        if (session is null)
        {
            return (false, null);
        }

        var cloudValidation = await httpContext.RequestServices
            .GetRequiredService<IConsoleCloudSessionValidator>()
            .ValidateAsync(httpContext, session, httpContext.RequestAborted);
        session = cloudValidation.Session;
        if (session is null)
        {
            return (true, TypedResults.Problem(
                title: "Cloud console session expired.",
                detail: cloudValidation.Detail ?? "Cloud console authentication is no longer active. Sign in again; Local access will not be restored automatically.",
                statusCode: StatusCodes.Status401Unauthorized));
        }

        if (!HasConsoleScope(session.Scopes, requiredScope))
        {
            return (true, TypedResults.Problem(
                title: "Forbidden.",
                detail: "The console session is not authorized for this operation.",
                statusCode: StatusCodes.Status403Forbidden));
        }

        if (!HttpMethods.IsGet(httpContext.Request.Method) &&
            !HttpMethods.IsHead(httpContext.Request.Method) &&
            !HttpMethods.IsOptions(httpContext.Request.Method) &&
            !HttpMethods.IsTrace(httpContext.Request.Method))
        {
            var antiforgery = httpContext.RequestServices.GetRequiredService<IAntiforgery>();
            var csrfFailure = await ConsoleRequestSecurity.ValidateMutationAsync(httpContext, antiforgery);
            if (csrfFailure is not null)
            {
                return (true, csrfFailure);
            }
        }

        httpContext.Items[ServiceTokenAuthenticatedItemKey] = false;
        httpContext.Items[PrincipalItemKey] = new LuthnRequestPrincipal(
            session.UserId,
            session.WorkspaceId,
            LuthnActorKind.User,
            session.ActorId,
            IsOperator: true,
            HubOrganizationId: session.OrganizationId);
        httpContext.Items[ActorItemKey] = session.ActorId;
        return (true, await ContinueWhenSensitiveMemoryProtectionIsReady(context, next));
    }

    private static ValueTask<object?> ContinueWhenSensitiveMemoryProtectionIsReady(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        var protectionState = context.HttpContext.RequestServices
            .GetRequiredService<SensitiveMemoryProtectionState>();
        if (!protectionState.IsReady)
        {
            return ValueTask.FromResult<object?>(TypedResults.Problem(
                title: "Sensitive memory protection is not ready.",
                detail: "Product traffic is blocked until the protected payload store and key ring verify successfully.",
                statusCode: StatusCodes.Status503ServiceUnavailable));
        }

        return next(context);
    }

    public static string? GetProductionReadinessIssue(
        IHostEnvironment environment,
        LuthnAuthOptions options,
        LuthnIdentityOptions identityOptions,
        DateTimeOffset now)
    {
        var identityIssue = GetIdentityReadinessIssue(environment, options, identityOptions, now);
        if (identityIssue is not null)
        {
            return identityIssue;
        }

        if (!RequiresServiceToken(environment, options))
        {
            return null;
        }

        var activeTokens = options.Tokens
            .Where(token => !IsExpired(token, now))
            .ToArray();
        if (activeTokens.Length == 0)
        {
            return "No active service tokens are configured.";
        }

        if (activeTokens.Any(token => string.IsNullOrWhiteSpace(token.Name)))
        {
            return "Every active service token must have a name.";
        }

        if (activeTokens.Any(token => string.IsNullOrWhiteSpace(token.Sha256Digest)))
        {
            return "Every active service token must have a SHA-256 digest.";
        }

        if (activeTokens.Any(token => !IsValidSha256Digest(token.Sha256Digest)))
        {
            return "Every active service token must have a valid SHA-256 digest.";
        }

        if (activeTokens.Any(token => token.Scopes.Count == 0))
        {
            return "Every active service token must declare at least one scope.";
        }

        if (activeTokens.Any(token => !Enum.IsDefined(token.ActorKind)))
        {
            return "Every active service token must declare a valid actorKind.";
        }

        if (activeTokens.Any(token =>
                !string.IsNullOrWhiteSpace(token.WorkspaceId) &&
                NormalizeWorkspaceId(token.WorkspaceId) is null))
        {
            return "Every configured service token workspaceId must be valid.";
        }

        if (activeTokens.Any(token =>
                token.Scopes.Any(scope =>
                    string.Equals(scope, ServiceScopes.HubIngressWrite, StringComparison.OrdinalIgnoreCase)) &&
                (NormalizeHubIdentity(token.HubOrganizationId) is null ||
                    NormalizeHubIdentity(token.HubAgentConnectionId) is null ||
                    NormalizeHubIdentity(token.HubAgentId) is null ||
                    NormalizeHubIdentity(token.HubSessionId) is null)))
        {
            return "Every Hub ingress token requires valid server-configured organization, agent connection, agent, and session bindings.";
        }


        if (identityOptions.Mode == LuthnIdentityMode.MultiUser &&
            activeTokens.Any(token => !token.IsOperator && NormalizeUserId(token.UserId) is null))
        {
            return "Every active non-operator token requires a valid userId in multi-user mode.";
        }

        if (identityOptions.Mode == LuthnIdentityMode.MultiUser &&
            activeTokens.Any(token => token.IsOperator &&
                NormalizeUserId(token.UserId) is null &&
                NormalizeWorkspaceId(token.WorkspaceId) is null))
        {
            return "Every active operator token requires a workspaceId or userId binding in multi-user mode.";
        }

        return null;
    }

    public static string? GetIdentityReadinessIssue(
        IHostEnvironment environment,
        LuthnAuthOptions options,
        LuthnIdentityOptions identityOptions,
        DateTimeOffset now)
    {
        if (!Enum.IsDefined(identityOptions.Mode))
        {
            return "The configured identity mode is invalid.";
        }

        if (NormalizeUserId(identityOptions.SingleOwnerUserId) is null)
        {
            return "The configured single-owner user identity is invalid.";
        }

        if (identityOptions.Mode != LuthnIdentityMode.MultiUser)
        {
            return null;
        }

        if (!RequiresServiceToken(environment, options))
        {
            return "Multi-user identity mode requires service-token authentication.";
        }

        var activeTokens = options.Tokens.Where(token => !IsExpired(token, now));
        if (activeTokens.Any(token => !token.IsOperator && NormalizeUserId(token.UserId) is null))
        {
            return "Every active non-operator token requires a valid userId in multi-user mode.";
        }

        return activeTokens.Any(token => token.IsOperator &&
                NormalizeUserId(token.UserId) is null &&
                NormalizeWorkspaceId(token.WorkspaceId) is null)
            ? "Every active operator token requires a workspaceId or userId binding in multi-user mode."
            : null;
    }

    public static bool RequiresServiceToken(
        IHostEnvironment environment,
        LuthnAuthOptions options) =>
        environment.IsProduction() ||
        options.RequireServiceToken ||
        options.Tokens.Count > 0;

    private static bool TryReadBearer(string authorization, out string bearer)
    {
        const string prefix = "Bearer ";
        if (authorization.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            bearer = authorization[prefix.Length..].Trim();
            return !string.IsNullOrWhiteSpace(bearer);
        }

        bearer = "";
        return false;
    }

    private static LuthnServiceTokenOptions? FindMatchingToken(
        IReadOnlyCollection<LuthnServiceTokenOptions> tokens,
        string bearer,
        DateTimeOffset now)
    {
        var bearerDigest = ComputeSha256Digest(bearer);

        foreach (var token in tokens)
        {
            if (string.IsNullOrWhiteSpace(token.Name) ||
                string.IsNullOrWhiteSpace(token.Sha256Digest))
            {
                continue;
            }

            if (IsExpired(token, now))
            {
                continue;
            }

            if (FixedEquals(NormalizeDigest(token.Sha256Digest), bearerDigest))
            {
                return token;
            }
        }

        return null;
    }

    private static bool HasScope(
        LuthnServiceTokenOptions token,
        string requiredScope) => HasScope(token.Scopes, requiredScope);

    private static bool HasScope(
        IEnumerable<string> scopes,
        string requiredScope) =>
        scopes.Any(scope =>
            string.Equals(scope, ServiceScopes.All, StringComparison.Ordinal) ||
            string.Equals(scope, requiredScope, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(requiredScope, ServiceScopes.AccessReview, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(scope, ServiceScopes.AccessDecide, StringComparison.OrdinalIgnoreCase));

    private static bool HasConsoleScope(
        IEnumerable<string> scopes,
        string requiredScope) =>
        HasScope(scopes, requiredScope) ||
        string.Equals(requiredScope, ServiceScopes.AccessConfigure, StringComparison.OrdinalIgnoreCase) &&
        scopes.Any(scope =>
            string.Equals(scope, ServiceScopes.ConfigWrite, StringComparison.OrdinalIgnoreCase));

    private static bool IsExpired(LuthnServiceTokenOptions token, DateTimeOffset now) =>
        token.ExpiresAt is { } expiresAt && expiresAt <= now;

    private static string ComposeActor(string serviceActor, string operatorIdentity)
    {
        var sanitizedServiceActor = BoundActorPart(SanitizeActorPart(serviceActor), MaxActorLength);
        var sanitizedOperator = BoundActorPart(SanitizeActorPart(operatorIdentity), MaxOperatorIdentityLength);
        if (string.IsNullOrWhiteSpace(sanitizedOperator))
        {
            return sanitizedServiceActor;
        }

        var suffix = $" operator:{sanitizedOperator}";
        var serviceLength = Math.Max(0, MaxActorLength - suffix.Length);
        return $"{BoundActorPart(sanitizedServiceActor, serviceLength)}{suffix}";
    }

    private static string SanitizeActorPart(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "";
        }

        var builder = new StringBuilder(value.Length);
        var lastWasWhitespace = false;
        foreach (var character in value.Trim())
        {
            if (char.IsControl(character))
            {
                continue;
            }

            if (char.IsWhiteSpace(character))
            {
                if (!lastWasWhitespace)
                {
                    builder.Append(' ');
                    lastWasWhitespace = true;
                }

                continue;
            }

            builder.Append(character);
            lastWasWhitespace = false;
        }

        return builder.ToString().Trim();
    }

    private static string BoundActorPart(string value, int maxLength)
    {
        if (maxLength <= 0)
        {
            return "";
        }

        return value.Length <= maxLength
            ? value
            : value[..maxLength];
    }

    private static string ComputeSha256Digest(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string NormalizeDigest(string digest) =>
        digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
            ? digest["sha256:".Length..].Trim().ToLowerInvariant()
            : digest.Trim().ToLowerInvariant();

    private static bool IsValidSha256Digest(string digest)
    {
        var normalized = NormalizeDigest(digest);
        return normalized.Length == 64 && normalized.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');
    }

    private static bool FixedEquals(string left, string right)
    {
        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);

        return leftBytes.Length == rightBytes.Length &&
            CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    private static bool IsAsciiLetterOrDigit(char value) =>
        value is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9';

    private static ProblemHttpResult IdentityConfigurationProblem(string detail) =>
        TypedResults.Problem(
            title: "Identity configuration is not ready.",
            detail: detail,
            statusCode: StatusCodes.Status503ServiceUnavailable);

    private static ProblemHttpResult Unauthorized() =>
        TypedResults.Problem(
            title: "Authentication required.",
            detail: "A valid service bearer token is required.",
            statusCode: StatusCodes.Status401Unauthorized);
}
