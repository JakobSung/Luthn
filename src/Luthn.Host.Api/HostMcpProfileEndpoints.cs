using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Luthn.Host.Api;

public static partial class HostMcpProfileEndpoints
{
    private const int MaximumMcpEntries = 64;
    private static readonly TimeSpan ActionLifetime = TimeSpan.FromMinutes(10);

    public static IEndpointRouteBuilder MapHostMcpProfiles(this IEndpointRouteBuilder app)
    {
        var host = app.MapGroup("/api/host/mcp-profiles")
            .RequireServiceScope(ServiceScopes.AgentConnectionWrite);

        host.MapPost("/observations", ReportObservation)
            .WithName("ReportHostMcpProfileObservation");
        host.MapPost("/actions/claim", ClaimAction)
            .WithName("ClaimHostMcpProfileAction");
        host.MapPost("/actions/{actionId}/complete", CompleteAction)
            .WithName("CompleteHostMcpProfileAction");

        var console = app.MapGroup("/api/operator/mcp-profiles")
            .RequireServiceScope(ServiceScopes.ConfigWrite);

        console.MapGet("", ReadConsoleSnapshot)
            .WithName("ReadHostMcpProfiles");
        console.MapPost("/actions", CreateAction)
            .WithName("CreateHostMcpProfileAction");

        return app;
    }

    private static Results<Ok<HostMcpProfileConsoleSnapshot>, BadRequest<ProblemDetails>> ReportObservation(
        HostMcpProfileObservationRequest request,
        HostMcpProfileStore store,
        TimeProvider timeProvider)
    {
        var problem = ValidateObservation(request);
        if (problem is not null)
        {
            return TypedResults.BadRequest(problem);
        }

        return TypedResults.Ok(store.Report(request, timeProvider.GetUtcNow()));
    }

    private static Ok<HostMcpProfileConsoleSnapshot> ReadConsoleSnapshot(
        HostMcpProfileStore store,
        TimeProvider timeProvider) =>
        TypedResults.Ok(store.Read(timeProvider.GetUtcNow()));

    private static Results<Ok<HostMcpProfileActionResponse>, BadRequest<ProblemDetails>, Conflict<ProblemDetails>> CreateAction(
        CreateHostMcpProfileActionRequest request,
        HostMcpProfileStore store,
        TimeProvider timeProvider)
    {
        var problem = ValidateAction(request);
        if (problem is not null)
        {
            return TypedResults.BadRequest(problem);
        }

        var result = store.Create(request, timeProvider.GetUtcNow(), ActionLifetime);
        return result is null
            ? TypedResults.Conflict(Problem(
                "An MCP profile change is already pending.",
                "Wait for the current browser-confirmed action to finish or expire."))
            : TypedResults.Ok(result);
    }

    private static Ok<HostMcpProfileActionClaimResponse> ClaimAction(
        HostMcpProfileStore store,
        TimeProvider timeProvider) =>
        TypedResults.Ok(store.Claim(timeProvider.GetUtcNow()));

    private static Results<Ok<HostMcpProfileActionResponse>, BadRequest<ProblemDetails>, NotFound> CompleteAction(
        string actionId,
        CompleteHostMcpProfileActionRequest request,
        HostMcpProfileStore store,
        TimeProvider timeProvider)
    {
        if (!IsOpaqueId(actionId) ||
            request.Outcome is not ("succeeded" or "failed") ||
            request.FailureCode is not null && !IsToken(request.FailureCode, 64))
        {
            return TypedResults.BadRequest(Problem(
                "Invalid MCP profile result.",
                "The result must use a bounded action id, outcome, and failure code."));
        }

        var result = store.Complete(
            actionId,
            request.Outcome,
            request.FailureCode,
            timeProvider.GetUtcNow());
        return result is null ? TypedResults.NotFound() : TypedResults.Ok(result);
    }

    private static ProblemDetails? ValidateObservation(HostMcpProfileObservationRequest request)
    {
        if (!IsToken(request.HelperVersion, 64) ||
            request.Clients is null || request.Clients.Count is < 1 or > 4)
        {
            return Problem(
                "Invalid MCP profile observation.",
                "The helper version and bounded client list are required.");
        }

        foreach (var client in request.Clients)
        {
            if (client.AgentKind is not ("codex" or "claude") ||
                client.Mode is not ("none" or "local" or "remote" or "conflict") ||
                client.Entries is null || client.Entries.Count > MaximumMcpEntries)
            {
                return Problem(
                    "Invalid MCP profile observation.",
                    "Every client must use a supported Agent kind, mode, and bounded MCP list.");
            }

            foreach (var entry in client.Entries)
            {
                if (!IsToken(entry.Name, 128) ||
                    entry.Transport is not ("stdio" or "http") ||
                    entry.AuthStatus is not null && !IsToken(entry.AuthStatus, 64) ||
                    entry.EndpointHost is not null && !IsDnsName(entry.EndpointHost))
                {
                    return Problem(
                        "Invalid MCP profile observation.",
                        "MCP entries may contain only bounded names, transport state, authentication state, and endpoint hosts.");
                }
            }
        }

        return null;
    }

    private static ProblemDetails? ValidateAction(CreateHostMcpProfileActionRequest request)
    {
        if (request.AgentKind is not ("codex" or "claude") ||
            request.Operation is not ("activate-remote" or "restore-local") ||
            !IsDisplayName(request.DisplayName))
        {
            return Problem(
                "Invalid MCP profile action.",
                "The Agent kind, operation, and display name are invalid.");
        }

        if (request.Operation == "restore-local")
        {
            return request.RemoteUrl is null && request.OauthClientId is null && request.OauthResource is null
                ? null
                : Problem(
                    "Invalid MCP profile action.",
                    "A local restore cannot include remote connection values.");
        }

        if (!TryValidateRemoteUri(request.RemoteUrl, out _) ||
            request.OauthClientId is not null && !IsToken(request.OauthClientId, 256) ||
            request.OauthResource is not null && !TryValidateRemoteUri(request.OauthResource, out _))
        {
            return Problem(
                "Invalid MCP profile action.",
                "Remote activation requires bounded HTTPS connection values without credentials, query strings, or fragments.");
        }

        return null;
    }

    private static bool TryValidateRemoteUri(string? value, out Uri? uri)
    {
        uri = null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var candidate) ||
            candidate.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(candidate.UserInfo) ||
            !string.IsNullOrEmpty(candidate.Query) ||
            !string.IsNullOrEmpty(candidate.Fragment) ||
            !IsDnsName(candidate.Host) ||
            candidate.AbsolutePath.Length > 256)
        {
            return false;
        }

        uri = candidate;
        return true;
    }

    private static bool IsDisplayName(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Trim().Length <= 128 &&
        !value.Any(char.IsControl);

    private static bool IsOpaqueId(string value) =>
        value.Length is >= 16 and <= 128 && TokenPattern().IsMatch(value);

    private static bool IsToken(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximumLength && TokenPattern().IsMatch(value);

    private static bool IsDnsName(string value) =>
        value.Length <= 253 && value.Split('.').All(label =>
            label.Length is >= 1 and <= 63 &&
            char.IsAsciiLetterOrDigit(label[0]) &&
            char.IsAsciiLetterOrDigit(label[^1]) &&
            label.All(character => char.IsAsciiLetterOrDigit(character) || character == '-'));

    private static ProblemDetails Problem(string title, string detail) => new()
    {
        Title = title,
        Detail = detail,
    };

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._:@-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex TokenPattern();
}

public sealed class HostMcpProfileStore
{
    private readonly Lock _gate = new();
    private HostMcpProfileObservationRequest? _observation;
    private DateTimeOffset? _lastSeenAt;
    private HostMcpProfileActionResponse? _action;

    public HostMcpProfileConsoleSnapshot Report(
        HostMcpProfileObservationRequest observation,
        DateTimeOffset now)
    {
        lock (_gate)
        {
            _observation = observation;
            _lastSeenAt = now;
            ExpireAction(now);
            return Snapshot(now);
        }
    }

    public HostMcpProfileConsoleSnapshot Read(DateTimeOffset now)
    {
        lock (_gate)
        {
            ExpireAction(now);
            return Snapshot(now);
        }
    }

    public HostMcpProfileActionResponse? Create(
        CreateHostMcpProfileActionRequest request,
        DateTimeOffset now,
        TimeSpan lifetime)
    {
        lock (_gate)
        {
            ExpireAction(now);
            if (_action?.State is "pending" or "claimed")
            {
                return null;
            }

            _action = new HostMcpProfileActionResponse(
                $"mcp-action-{Guid.NewGuid():N}",
                request.AgentKind,
                request.Operation,
                request.DisplayName.Trim(),
                request.RemoteUrl,
                request.OauthClientId,
                request.OauthResource,
                "pending",
                null,
                now,
                now.Add(lifetime),
                null);
            return _action;
        }
    }

    public HostMcpProfileActionClaimResponse Claim(DateTimeOffset now)
    {
        lock (_gate)
        {
            ExpireAction(now);
            if (_action?.State != "pending")
            {
                return new HostMcpProfileActionClaimResponse(null);
            }

            _action = _action with { State = "claimed", UpdatedAt = now };
            return new HostMcpProfileActionClaimResponse(_action);
        }
    }

    public HostMcpProfileActionResponse? Complete(
        string actionId,
        string outcome,
        string? failureCode,
        DateTimeOffset now)
    {
        lock (_gate)
        {
            ExpireAction(now);
            if (_action is null || _action.Id != actionId || _action.State != "claimed")
            {
                return null;
            }

            _action = _action with
            {
                State = outcome,
                FailureCode = outcome == "failed" ? failureCode ?? "profile.change_failed" : null,
                UpdatedAt = now,
            };
            return _action;
        }
    }

    private HostMcpProfileConsoleSnapshot Snapshot(DateTimeOffset now) =>
        new(
            _observation?.HelperVersion,
            _lastSeenAt,
            _lastSeenAt is not null && now - _lastSeenAt <= TimeSpan.FromSeconds(30),
            _observation?.Clients ?? [],
            _action);

    private void ExpireAction(DateTimeOffset now)
    {
        if (_action is not null &&
            _action.State is ("pending" or "claimed") &&
            _action.ExpiresAt <= now)
        {
            _action = _action with
            {
                State = "expired",
                FailureCode = "profile.action_expired",
                UpdatedAt = now,
            };
        }
    }
}

public sealed record HostMcpProfileObservationRequest(
    string HelperVersion,
    IReadOnlyList<HostMcpClientObservation> Clients);

public sealed record HostMcpClientObservation(
    string AgentKind,
    string Mode,
    IReadOnlyList<HostMcpEntryObservation> Entries);

public sealed record HostMcpEntryObservation(
    string Name,
    string Transport,
    bool Enabled,
    string? AuthStatus,
    string? EndpointHost);

public sealed record HostMcpProfileConsoleSnapshot(
    string? HelperVersion,
    DateTimeOffset? LastSeenAt,
    bool HelperOnline,
    IReadOnlyList<HostMcpClientObservation> Clients,
    HostMcpProfileActionResponse? Action);

public sealed record CreateHostMcpProfileActionRequest(
    string AgentKind,
    string Operation,
    string DisplayName,
    string? RemoteUrl = null,
    string? OauthClientId = null,
    string? OauthResource = null);

public sealed record CompleteHostMcpProfileActionRequest(
    string Outcome,
    string? FailureCode = null);

public sealed record HostMcpProfileActionClaimResponse(
    HostMcpProfileActionResponse? Action);

public sealed record HostMcpProfileActionResponse(
    string Id,
    string AgentKind,
    string Operation,
    string DisplayName,
    string? RemoteUrl,
    string? OauthClientId,
    string? OauthResource,
    string State,
    string? FailureCode,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? UpdatedAt);
