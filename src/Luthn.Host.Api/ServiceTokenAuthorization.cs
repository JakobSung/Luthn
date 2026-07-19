using System.Security.Cryptography;
using System.Text;
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
    public const string AccessDecide = "access.decide";
    public const string AuditRead = "audit.read";
    public const string ConfigWrite = "config.write";
    public const string MetricsRead = "metrics.read";
    public const string MetricsWrite = "metrics.write";
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
}

public static class ServiceTokenAuthorization
{
    public const string OperatorHeaderName = "X-Luthn-Operator";

    private const string ActorItemKey = "Luthn.ServiceActor";
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

        if (!RequiresServiceToken(environment, authOptions))
        {
            httpContext.Items[ActorItemKey] = ComposeActor(
                "local-anonymous",
                httpContext.Request.Headers[OperatorHeaderName].ToString());
            return await next(context);
        }

        var authorization = httpContext.Request.Headers.Authorization.ToString();
        if (!TryReadBearer(authorization, out var bearer))
        {
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

        httpContext.Items[ActorItemKey] = ComposeActor(
            matchedToken.Name.Trim(),
            httpContext.Request.Headers[OperatorHeaderName].ToString());
        return await next(context);
    }

    public static string? GetProductionReadinessIssue(
        IHostEnvironment environment,
        LuthnAuthOptions options,
        DateTimeOffset now)
    {
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

        if (activeTokens.Any(token => token.Scopes.Count == 0))
        {
            return "Every active service token must declare at least one scope.";
        }

        return null;
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
        string requiredScope) =>
        token.Scopes.Any(scope =>
            string.Equals(scope, ServiceScopes.All, StringComparison.Ordinal) ||
            string.Equals(scope, requiredScope, StringComparison.OrdinalIgnoreCase));

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

    private static bool FixedEquals(string left, string right)
    {
        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);

        return leftBytes.Length == rightBytes.Length &&
            CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    private static ProblemHttpResult Unauthorized() =>
        TypedResults.Problem(
            title: "Authentication required.",
            detail: "A valid service bearer token is required.",
            statusCode: StatusCodes.Status401Unauthorized);
}
