using Luthn.Core.Persistence;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.RegularExpressions;

namespace Luthn.Host.Api;

public static partial class AgentConnectionEndpoints
{
    private const int MaxChannelsPerObservation = 8;
    private const string ConfigurationOwner = "luthn";

    public static IEndpointRouteBuilder MapAgentConnections(this IEndpointRouteBuilder app)
    {
        var connections = app.MapGroup("/api/agent-connections");

        connections.MapGet("", ListConnections)
            .RequireServiceScope(ServiceScopes.AgentConnectionRead)
            .WithName("ListAgentConnections");

        connections.MapPost("/{agentId}/observations", ReportObservation)
            .RequireServiceScope(ServiceScopes.AgentConnectionWrite)
            .WithName("ReportAgentConnectionObservation");

        return app;
    }

    public static async Task<Ok<AgentConnectionListResponse>> ListConnections(
        LuthnDbContext db,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var principal = ServiceTokenAuthorization.GetPrincipal(httpContext);
        var query = db.AgentConnectionChannels.AsNoTracking();
        if (!principal.IsOperator)
        {
            query = query.Where(record => record.OwnerUserId == principal.UserId);
        }

        var records = await query
            .OrderBy(record => record.OwnerUserId)
            .ThenBy(record => record.AgentId)
            .ThenBy(record => record.Channel)
            .ToListAsync(cancellationToken);

        var connections = records
            .GroupBy(
                record => new { record.OwnerUserId, record.AgentId })
            .Select(ToConnectionResponse)
            .ToArray();

        return TypedResults.Ok(new AgentConnectionListResponse(connections));
    }

    public static async Task<Results<Ok<AgentConnectionResponse>, BadRequest<ProblemDetails>>> ReportObservation(
        string agentId,
        AgentConnectionObservationRequest request,
        LuthnDbContext db,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var validationError = Validate(agentId, request);
        if (validationError is not null)
        {
            return TypedResults.BadRequest(validationError);
        }

        var ownerUserId = ServiceTokenAuthorization.GetPrincipal(httpContext).UserId;
        var normalizedAgentId = agentId.Trim();
        var observedAt = DateTimeOffset.UtcNow;
        var requestedChannels = request.Channels!
            .Select(channel => channel! with { Channel = channel.Channel.Trim() })
            .ToArray();
        var channelNames = requestedChannels
            .Select(channel => channel.Channel)
            .ToArray();
        try
        {
            await UpsertAsync(
                db,
                ownerUserId,
                normalizedAgentId,
                request,
                requestedChannels,
                channelNames,
                observedAt,
                cancellationToken);
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            await UpsertAsync(
                db,
                ownerUserId,
                normalizedAgentId,
                request,
                requestedChannels,
                channelNames,
                observedAt,
                cancellationToken);
        }

        var records = await db.AgentConnectionChannels
            .AsNoTracking()
            .Where(record =>
                record.OwnerUserId == ownerUserId &&
                record.AgentId == normalizedAgentId)
            .OrderBy(record => record.AgentId)
            .ThenBy(record => record.Channel)
            .ToListAsync(cancellationToken);

        return TypedResults.Ok(ToConnectionResponse(records));
    }

    private static async Task UpsertAsync(
        LuthnDbContext db,
        string ownerUserId,
        string agentId,
        AgentConnectionObservationRequest request,
        IReadOnlyList<AgentConnectionChannelObservation> requestedChannels,
        IReadOnlyList<string> channelNames,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken)
    {
        var existing = await db.AgentConnectionChannels
            .Where(record =>
                record.OwnerUserId == ownerUserId &&
                record.AgentId == agentId &&
                channelNames.Contains(record.Channel))
            .ToDictionaryAsync(record => record.Channel, StringComparer.Ordinal, cancellationToken);

        foreach (var channel in requestedChannels)
        {
            if (!existing.TryGetValue(channel.Channel, out var record))
            {
                record = new AgentConnectionChannelRecord
                {
                    Id = $"agent-connection:{Guid.NewGuid():N}",
                    OwnerUserId = ownerUserId,
                    AgentId = agentId,
                    Channel = channel.Channel,
                    ConfigurationOwner = ConfigurationOwner,
                    FirstObservedAt = observedAt
                };
                db.AgentConnectionChannels.Add(record);
            }

            ApplyObservation(record, request, channel, observedAt);
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private static void ApplyObservation(
        AgentConnectionChannelRecord record,
        AgentConnectionObservationRequest request,
        AgentConnectionChannelObservation channel,
        DateTimeOffset observedAt)
    {
        record.AgentName = request.AgentName.Trim();
        record.IntegrationKind = request.IntegrationKind.Trim();
        record.ConnectorVersion = request.ConnectorVersion.Trim();
        record.IsConfigured = channel.Configured;
        record.UpdatedAt = observedAt;

        if (!channel.Configured)
        {
            record.VerificationState = AgentConnectionVerificationState.Unknown;
            record.ActivityState = AgentConnectionActivityState.Unknown;
            record.FailureCode = null;
            return;
        }

        if (channel.VerificationState != AgentConnectionVerificationState.Unknown)
        {
            record.VerificationState = channel.VerificationState;
            record.LastVerifiedAt = observedAt;
        }

        if (channel.ActivityState != AgentConnectionActivityState.Unknown)
        {
            record.ActivityState = channel.ActivityState;
            record.LastActivityAt = observedAt;
        }

        if (channel.VerificationState == AgentConnectionVerificationState.Failed ||
            channel.ActivityState == AgentConnectionActivityState.Failed)
        {
            record.FailureCode = NormalizeFailureCode(channel.FailureCode);
        }
        else if ((channel.VerificationState != AgentConnectionVerificationState.Unknown ||
            channel.ActivityState != AgentConnectionActivityState.Unknown) &&
            record.VerificationState != AgentConnectionVerificationState.Failed &&
            record.ActivityState != AgentConnectionActivityState.Failed)
        {
            record.FailureCode = null;
        }

        if (channel.ActivityState == AgentConnectionActivityState.Succeeded)
        {
            record.LastSuccessfulActivityAt = observedAt;
        }
    }

    private static AgentConnectionResponse ToConnectionResponse(
        IEnumerable<AgentConnectionChannelRecord> source)
    {
        var records = source.OrderBy(record => record.Channel, StringComparer.Ordinal).ToArray();
        var latest = records.MaxBy(record => record.UpdatedAt)!;
        var channels = records.Select(record => new AgentConnectionChannelResponse(
            record.Channel,
            record.IsConfigured,
            ResolveChannelState(record),
            record.VerificationState,
            record.ActivityState,
            record.LastVerifiedAt,
            record.LastActivityAt,
            record.LastSuccessfulActivityAt,
            record.FailureCode,
            record.UpdatedAt)).ToArray();

        return new AgentConnectionResponse(
            latest.OwnerUserId,
            latest.AgentId,
            latest.AgentName,
            latest.IntegrationKind,
            latest.ConnectorVersion,
            ResolveOverallState(records),
            channels.Max(channel => channel.LastSuccessfulActivityAt),
            channels.Max(channel => channel.UpdatedAt),
            channels);
    }

    private static AgentConnectionState ResolveOverallState(
        IReadOnlyCollection<AgentConnectionChannelRecord> records)
    {
        if (records.Count == 0)
        {
            return AgentConnectionState.Unknown;
        }

        if (records.All(record => !record.IsConfigured))
        {
            return AgentConnectionState.Disconnected;
        }

        if (records.Any(record => record.IsConfigured) &&
            records.Any(record => !record.IsConfigured))
        {
            return AgentConnectionState.Degraded;
        }

        if (records.Any(record =>
            record.VerificationState == AgentConnectionVerificationState.Failed ||
            record.ActivityState == AgentConnectionActivityState.Failed))
        {
            return AgentConnectionState.Degraded;
        }

        if (records.Any(record => record.ActivityState == AgentConnectionActivityState.Succeeded))
        {
            return AgentConnectionState.Active;
        }

        if (records.Where(record => record.IsConfigured)
            .All(record => record.VerificationState == AgentConnectionVerificationState.Verified))
        {
            return AgentConnectionState.Verified;
        }

        return AgentConnectionState.Configured;
    }

    private static AgentConnectionState ResolveChannelState(AgentConnectionChannelRecord record)
    {
        if (!record.IsConfigured)
        {
            return AgentConnectionState.Disconnected;
        }

        if (record.VerificationState == AgentConnectionVerificationState.Failed ||
            record.ActivityState == AgentConnectionActivityState.Failed)
        {
            return AgentConnectionState.Degraded;
        }

        if (record.ActivityState == AgentConnectionActivityState.Succeeded)
        {
            return AgentConnectionState.Active;
        }

        return record.VerificationState == AgentConnectionVerificationState.Verified
            ? AgentConnectionState.Verified
            : AgentConnectionState.Configured;
    }

    private static ProblemDetails? Validate(
        string agentId,
        AgentConnectionObservationRequest request)
    {
        const string title = "Invalid agent connection observation.";
        var credentialError = ValidateCredentialFreeText(
            title,
            ("agentId", agentId),
            ("agentName", request.AgentName),
            ("integrationKind", request.IntegrationKind),
            ("connectorVersion", request.ConnectorVersion));
        if (credentialError is not null)
        {
            return credentialError;
        }

        var agentIdError = ValidateIdentifier(agentId, "agentId", title);
        if (agentIdError is not null)
        {
            return agentIdError;
        }

        var nameError = ApiValidation.ValidateRequiredText(
            request.AgentName,
            "agentName",
            128,
            title);
        if (nameError is not null)
        {
            return nameError;
        }
        if (request.AgentName.Any(char.IsControl))
        {
            return ApiValidation.CreateProblem(title, "agentName cannot contain control characters.");
        }

        var integrationError = ValidateIdentifier(
            request.IntegrationKind,
            "integrationKind",
            title);
        if (integrationError is not null)
        {
            return integrationError;
        }

        var versionError = ValidateIdentifier(
            request.ConnectorVersion,
            "connectorVersion",
            title);
        if (versionError is not null)
        {
            return versionError;
        }

        if (request.Channels is null || request.Channels.Count == 0)
        {
            return ApiValidation.CreateProblem(title, "channels must include at least one channel.");
        }

        if (request.Channels.Count > MaxChannelsPerObservation)
        {
            return ApiValidation.CreateProblem(
                title,
                $"channels must include {MaxChannelsPerObservation} channels or fewer.");
        }

        var channelNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var channel in request.Channels)
        {
            if (channel is null)
            {
                return ApiValidation.CreateProblem(title, "channels cannot contain null elements.");
            }

            credentialError = ValidateCredentialFreeText(
                title,
                ("channels.channel", channel.Channel),
                ("channels.failureCode", channel.FailureCode));
            if (credentialError is not null)
            {
                return credentialError;
            }

            var channelError = ValidateIdentifier(channel.Channel, "channels.channel", title);
            if (channelError is not null)
            {
                return channelError;
            }

            if (!channelNames.Add(channel.Channel.Trim()))
            {
                return ApiValidation.CreateProblem(title, "channels cannot contain duplicate channel names.");
            }

            if (!Enum.IsDefined(channel.VerificationState))
            {
                return ApiValidation.CreateProblem(
                    title,
                    "channels.verificationState must be a defined value.");
            }

            if (!Enum.IsDefined(channel.ActivityState))
            {
                return ApiValidation.CreateProblem(
                    title,
                    "channels.activityState must be a defined value.");
            }

            if (!channel.Configured &&
                (channel.VerificationState != AgentConnectionVerificationState.Unknown ||
                    channel.ActivityState != AgentConnectionActivityState.Unknown))
            {
                return ApiValidation.CreateProblem(
                    title,
                    "unconfigured channels must use Unknown verification and activity states.");
            }

            if (channel.FailureCode is not null)
            {
                var failureError = ValidateIdentifier(
                    channel.FailureCode,
                    "channels.failureCode",
                    title);
                if (failureError is not null)
                {
                    return failureError;
                }
            }

            var failed = channel.VerificationState == AgentConnectionVerificationState.Failed ||
                channel.ActivityState == AgentConnectionActivityState.Failed;
            if (failed != !string.IsNullOrWhiteSpace(channel.FailureCode))
            {
                return ApiValidation.CreateProblem(
                    title,
                    "channels.failureCode is required only for failed channel observations.");
            }
        }

        return null;
    }

    private static ProblemDetails? ValidateCredentialFreeText(
        string title,
        params (string FieldName, string? Value)[] fields)
    {
        foreach (var (fieldName, value) in fields)
        {
            if (!string.IsNullOrEmpty(value) && CredentialPattern().IsMatch(value))
            {
                return ApiValidation.CreateProblem(
                    title,
                    $"{fieldName} cannot contain credential-like content.");
            }
        }

        return null;
    }

    [GeneratedRegex(
        "-----BEGIN [A-Z0-9 ]*PRIVATE KEY-----|" +
        @"\b(?:sk-[A-Za-z0-9_-]{16,}|ghp_[A-Za-z0-9]{16,}|github_pat_[A-Za-z0-9_]{16,}|AKIA[A-Z0-9]{16})\b|" +
        @"\bBearer\s+[A-Za-z0-9._~+/=-]{16,}|" +
        @"\b(?:Authorization\s*[:=]\s*)?Basic\s+[A-Za-z0-9+/]{8,}={0,2}|" +
        @"\b[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\b|" +
        @"\b[A-Za-z][A-Za-z0-9+.-]*://[^\s/@:]+:[^\s/@]+@|" +
        @"(?<![A-Za-z0-9_])(?:[A-Za-z][A-Za-z0-9]*[_-])*(?:api[_-]?key|access[_-]?key(?:[_-]?id)?|secret[_-]?access[_-]?key|access[_-]?token|refresh[_-]?token|session[_-]?token|client[_-]?secret|token|password|passwd|secret|private[_-]?key|database[_-]?url|connection[_-]?string)\s*[:=]\s*\S",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CredentialPattern();

    private static ProblemDetails? ValidateIdentifier(
        string? value,
        string fieldName,
        string title)
    {
        var textError = ApiValidation.ValidateRequiredText(value, fieldName, 64, title);
        if (textError is not null)
        {
            return textError;
        }

        return value!.Trim().All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or '+')
                ? null
                : ApiValidation.CreateProblem(
                    title,
                    $"{fieldName} may contain only ASCII letters, digits, '.', '_', '-', or '+'.");
    }

    private static string? NormalizeFailureCode(string? failureCode) =>
        string.IsNullOrWhiteSpace(failureCode) ? null : failureCode.Trim();
}

public enum AgentConnectionState
{
    Unknown,
    Configured,
    Verified,
    Active,
    Degraded,
    Disconnected
}

public sealed record AgentConnectionObservationRequest
{
    public string AgentName { get; init; } = "";
    public string IntegrationKind { get; init; } = "";
    public string ConnectorVersion { get; init; } = "";
    public IReadOnlyList<AgentConnectionChannelObservation?>? Channels { get; init; }
}

public sealed record AgentConnectionChannelObservation
{
    public string Channel { get; init; } = "";
    public bool Configured { get; init; }
    public AgentConnectionVerificationState VerificationState { get; init; }
    public AgentConnectionActivityState ActivityState { get; init; }
    public string? FailureCode { get; init; }
}

public sealed record AgentConnectionListResponse(
    IReadOnlyList<AgentConnectionResponse> Connections);

public sealed record AgentConnectionResponse(
    string OwnerUserId,
    string AgentId,
    string AgentName,
    string IntegrationKind,
    string ConnectorVersion,
    AgentConnectionState State,
    DateTimeOffset? LastSuccessfulActivityAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<AgentConnectionChannelResponse> Channels);

public sealed record AgentConnectionChannelResponse(
    string Channel,
    bool Configured,
    AgentConnectionState State,
    AgentConnectionVerificationState VerificationState,
    AgentConnectionActivityState ActivityState,
    DateTimeOffset? LastVerifiedAt,
    DateTimeOffset? LastActivityAt,
    DateTimeOffset? LastSuccessfulActivityAt,
    string? FailureCode,
    DateTimeOffset UpdatedAt);
