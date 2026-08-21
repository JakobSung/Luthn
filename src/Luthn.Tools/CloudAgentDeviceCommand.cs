using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Luthn.Core.Persistence;
using Luthn.Sdk.Sync;
using Microsoft.Extensions.Options;

namespace Luthn.Tools;

public sealed class CloudAgentDeviceCommand(
    HttpClient? httpClient = null,
    TimeProvider? timeProvider = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _httpClient = httpClient ?? new HttpClient
    {
        Timeout = TimeSpan.FromSeconds(30),
    };
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<int> ExecuteAsync(
        string[] args,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        CloudAgentArguments parsed;
        try
        {
            parsed = CloudAgentArguments.Parse(args);
        }
        catch (ArgumentException exception)
        {
            await error.WriteLineAsync(exception.Message);
            await error.WriteLineAsync(CloudAgentArguments.Usage);
            return 2;
        }

        try
        {
            var options = new CloudAgentDeviceConnectionOptions
            {
                Enabled = true,
                BaseUrl = parsed.BaseUri.AbsoluteUri,
                Audience = parsed.Audience,
                StateDirectory = parsed.StateDirectory,
            };
            var protocolOptions = options.ToProtocolOptions();
            var stateKey = ReadStateKey(parsed.StateKeyFile);
            AesGcmCloudAgentDeviceStateStore store;
            try
            {
                store = new AesGcmCloudAgentDeviceStateStore(
                    Options.Create(options),
                    stateKey);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(stateKey);
            }
            using (store)
            {
                var client = new AgentDeviceProtocolClient(_httpClient, _timeProvider);
                var result = await store.UpdateAsync(
                    async (state, token) => await AdvanceAsync(
                        client,
                        state,
                        protocolOptions,
                        parsed,
                        token),
                    cancellationToken);
                await output.WriteLineAsync(JsonSerializer.Serialize(result, JsonOptions));
                return 0;
            }
        }
        catch (AgentDeviceProtocolException exception)
        {
            await error.WriteLineAsync(exception.ErrorCode);
            return 1;
        }
        catch (Exception exception) when (
            exception is HttpRequestException or TaskCanceledException or InvalidOperationException or
                IOException or UnauthorizedAccessException)
        {
            await error.WriteLineAsync("cloud_agent.connection_failed");
            return 1;
        }
    }

    private static byte[] ReadStateKey(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.Length is < 40 or > 128)
        {
            throw new InvalidOperationException("Cloud AgentDevice state key file is invalid.");
        }
        var encoded = File.ReadAllText(path).Trim();
        byte[] key;
        try
        {
            key = Convert.FromBase64String(encoded);
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException("Cloud AgentDevice state key file is invalid.", exception);
        }
        if (key.Length != 32)
        {
            CryptographicOperations.ZeroMemory(key);
            throw new InvalidOperationException("Cloud AgentDevice state key file is invalid.");
        }
        return key;
    }

    private static async Task<CloudAgentDeviceStateUpdate<CloudAgentCommandResult>> AdvanceAsync(
        AgentDeviceProtocolClient client,
        AgentDeviceLocalState state,
        AgentDeviceProtocolOptions options,
        CloudAgentArguments arguments,
        CancellationToken cancellationToken)
    {
        if (FindConnection(state, arguments) is { } existing)
        {
            var remote = await client.GetConnectionAsync(
                state,
                options,
                existing.Id,
                cancellationToken);
            if (remote.Connection is { } current)
            {
                EnsureConnectionMatches(current, existing, arguments);
                if (current.Status == "active")
                {
                    return new CloudAgentDeviceStateUpdate<CloudAgentCommandResult>(
                        remote.State,
                        Connected(current, options));
                }
            }

            state = remote.State with
            {
                Connections = (remote.State.Connections ?? [])
                    .Where(connection => connection.Id != existing.Id)
                    .ToArray(),
            };
            return await CreateConnectionAsync(
                client,
                state,
                options,
                arguments,
                existing.Id,
                cancellationToken);
        }

        if (state.Session is null && state.PendingEnrollment is null)
        {
            var begin = await client.BeginEnrollmentAsync(
                state,
                options,
                arguments.DeviceName,
                cancellationToken);
            return new CloudAgentDeviceStateUpdate<CloudAgentCommandResult>(
                begin.State,
                new CloudAgentCommandResult(
                    "approval-required",
                    VerificationUri: begin.Challenge.VerificationUri.AbsoluteUri,
                    UserCode: begin.Challenge.UserCode,
                    ExpiresAt: begin.Challenge.ExpiresAt,
                    RetryAfterSeconds: begin.Challenge.PollIntervalSeconds));
        }

        if (state.Session is null)
        {
            var poll = await client.PollEnrollmentAsync(state, options, cancellationToken);
            if (poll.StateValue != AgentDeviceEnrollmentState.Approved)
            {
                return new CloudAgentDeviceStateUpdate<CloudAgentCommandResult>(
                    poll.State,
                    new CloudAgentCommandResult(
                        poll.StateValue.ToString().ToLowerInvariant(),
                        RetryAfterSeconds: poll.RetryAfterSeconds));
            }

            // Persist the one-time session grant before attempting a connection mutation.
            // If connection creation fails, a subsequent invocation can retry without
            // losing the only grant the server will issue for this enrollment.
            return new CloudAgentDeviceStateUpdate<CloudAgentCommandResult>(
                poll.State,
                new CloudAgentCommandResult("pending", RetryAfterSeconds: 0));
        }

        return await CreateConnectionAsync(
            client,
            state,
            options,
            arguments,
            previousConnectionId: null,
            cancellationToken);
    }

    private static async Task<CloudAgentDeviceStateUpdate<CloudAgentCommandResult>> CreateConnectionAsync(
        AgentDeviceProtocolClient client,
        AgentDeviceLocalState state,
        AgentDeviceProtocolOptions options,
        CloudAgentArguments arguments,
        Guid? previousConnectionId,
        CancellationToken cancellationToken)
    {
        var created = await client.CreateConnectionAsync(
            state,
            options,
            arguments.WorkspaceId,
            arguments.AgentKind,
            arguments.CapabilityPreset,
            CreateIdempotencyKey(state.Session!.AgentDeviceId, arguments, previousConnectionId),
            cancellationToken);
        return new CloudAgentDeviceStateUpdate<CloudAgentCommandResult>(
            created.State,
            Connected(created.Connection, options));
    }

    private static void EnsureConnectionMatches(
        CloudAgentConnectionDto remote,
        CloudAgentConnectionDto local,
        CloudAgentArguments arguments)
    {
        if (remote.OrganizationId != local.OrganizationId ||
            remote.WorkspaceId != arguments.WorkspaceId ||
            !string.Equals(remote.AgentKind, arguments.AgentKind, StringComparison.Ordinal) ||
            !string.Equals(
                remote.CapabilityPreset,
                arguments.CapabilityPreset,
                StringComparison.Ordinal))
        {
            throw new AgentDeviceProtocolException("agent_connection.invalid_response");
        }
    }

    private static CloudAgentConnectionDto? FindConnection(
        AgentDeviceLocalState state,
        CloudAgentArguments arguments) =>
        (state.Connections ?? []).SingleOrDefault(connection =>
            connection.WorkspaceId == arguments.WorkspaceId &&
            string.Equals(connection.AgentKind, arguments.AgentKind, StringComparison.Ordinal) &&
            string.Equals(connection.CapabilityPreset, arguments.CapabilityPreset, StringComparison.Ordinal) &&
            string.Equals(connection.Status, "active", StringComparison.Ordinal));

    private static CloudAgentCommandResult Connected(
        CloudAgentConnectionDto connection,
        AgentDeviceProtocolOptions options) =>
        new(
            "connected",
            AgentConnectionId: connection.Id,
            OrganizationId: connection.OrganizationId,
            WorkspaceId: connection.WorkspaceId,
            AgentKind: connection.AgentKind,
            CapabilityPreset: connection.CapabilityPreset,
            RemoteMcpUrl: options.RemoteMcpUri.AbsoluteUri);

    private static string CreateIdempotencyKey(
        Guid agentDeviceId,
        CloudAgentArguments arguments,
        Guid? previousConnectionId)
    {
        var input = Encoding.UTF8.GetBytes(
            $"{agentDeviceId:D}|{arguments.WorkspaceId:D}|{arguments.AgentKind}|{arguments.CapabilityPreset}|{previousConnectionId?.ToString("D") ?? "initial"}");
        return $"m4-{Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant()}";
    }

    private sealed record CloudAgentArguments(
        Uri BaseUri,
        string StateDirectory,
        string StateKeyFile,
        Guid WorkspaceId,
        string AgentKind,
        string CapabilityPreset,
        string DeviceName,
        string Audience)
    {
        public const string Usage =
            "usage: cloud-agent --base-url https://cloud.example --state-dir path --state-key-file path --workspace uuid --agent codex|claude [--capability reader|contributor|sensitive-requester] [--device-name name] [--audience value]";

        public static CloudAgentArguments Parse(string[] args)
        {
            string? baseUrl = null;
            string? stateDirectory = null;
            string? stateKeyFile = null;
            string? workspace = null;
            string? agent = null;
            var capability = "reader";
            var deviceName = Environment.MachineName;
            var audience = "luthn-cloud-agent";

            for (var index = 0; index < args.Length; index += 2)
            {
                if (index + 1 >= args.Length)
                {
                    throw new ArgumentException("Every Cloud AgentDevice option requires a value.");
                }
                var value = args[index + 1];
                switch (args[index])
                {
                    case "--base-url": baseUrl = value; break;
                    case "--state-dir": stateDirectory = value; break;
                    case "--state-key-file": stateKeyFile = value; break;
                    case "--workspace": workspace = value; break;
                    case "--agent": agent = value; break;
                    case "--capability": capability = value; break;
                    case "--device-name": deviceName = value; break;
                    case "--audience": audience = value; break;
                    default: throw new ArgumentException($"Unknown Cloud AgentDevice option: {args[index]}");
                }
            }

            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri) ||
                (baseUri.Scheme != Uri.UriSchemeHttps && !baseUri.IsLoopback) ||
                string.IsNullOrWhiteSpace(stateDirectory) ||
                string.IsNullOrWhiteSpace(stateKeyFile) ||
                !Guid.TryParseExact(workspace, "D", out var workspaceId) ||
                workspaceId == Guid.Empty ||
                agent is not ("codex" or "claude") ||
                capability is not ("reader" or "contributor" or "sensitive-requester") ||
                string.IsNullOrWhiteSpace(deviceName) ||
                deviceName.Length > 160 ||
                !string.Equals(deviceName, deviceName.Trim(), StringComparison.Ordinal))
            {
                throw new ArgumentException("Cloud AgentDevice options are invalid.");
            }

            return new CloudAgentArguments(
                baseUri,
                Path.GetFullPath(stateDirectory),
                Path.GetFullPath(stateKeyFile),
                workspaceId,
                agent,
                capability,
                deviceName,
                audience);
        }
    }
}

public sealed record CloudAgentCommandResult(
    string State,
    string? VerificationUri = null,
    string? UserCode = null,
    DateTimeOffset? ExpiresAt = null,
    int? RetryAfterSeconds = null,
    Guid? AgentConnectionId = null,
    Guid? OrganizationId = null,
    Guid? WorkspaceId = null,
    string? AgentKind = null,
    string? CapabilityPreset = null,
    string? RemoteMcpUrl = null);
