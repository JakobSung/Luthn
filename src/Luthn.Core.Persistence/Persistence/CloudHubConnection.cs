using System.Security.Cryptography;
using System.Text.Json;
using Luthn.Core.Memory;
using Luthn.Sdk.Sync;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace Luthn.Core.Persistence;

public sealed class CloudHubConnectionOptions
{
    public const string SectionName = "Luthn:Cloud";

    public bool Enabled { get; set; }
    public string BaseUrl { get; set; } = string.Empty;
    public string Audience { get; set; } = "luthn-cloud";
    public string StateDirectory { get; set; } = string.Empty;

    public CloudHubProtocolOptions ToProtocolOptions()
    {
        if (!Enabled)
        {
            return new CloudHubProtocolOptions();
        }

        if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out var baseUri))
        {
            throw new InvalidOperationException("Luthn Cloud BaseUrl must be an absolute URI.");
        }

        var options = new CloudHubProtocolOptions
        {
            BaseUri = baseUri,
            Audience = Audience,
        };
        _ = options.Resolve("api/v2/hub/projections");
        return options;
    }
}

public sealed record CloudHubStoragePaths(
    string OperatorConfigDirectory,
    string StateDirectory,
    string KeyDirectory)
{
    public static CloudHubStoragePaths Resolve(
        IConfiguration configuration,
        string defaultOperatorConfigDirectory = ".luthn/operator")
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var configuredOperatorDirectory = configuration["Luthn:OperatorConfig:Directory"];
        var operatorDirectory = string.IsNullOrWhiteSpace(configuredOperatorDirectory)
            ? defaultOperatorConfigDirectory
            : configuredOperatorDirectory;
        var configuredStateDirectory = configuration[$"{CloudHubConnectionOptions.SectionName}:StateDirectory"];
        var stateDirectory = string.IsNullOrWhiteSpace(configuredStateDirectory)
            ? operatorDirectory
            : configuredStateDirectory;
        return new CloudHubStoragePaths(
            operatorDirectory,
            stateDirectory,
            Path.Combine(operatorDirectory, "keys"));
    }
}

public interface ICloudHubStateStore
{
    CloudHubLocalState Read();
    Task<TResult> UpdateAsync<TResult>(
        Func<CloudHubLocalState, CancellationToken, Task<CloudHubStateUpdate<TResult>>> update,
        CancellationToken cancellationToken);
    Task<TResult> UpdateWithCheckpointAsync<TResult>(
        Func<
            CloudHubLocalState,
            Action<CloudHubLocalState>,
            CancellationToken,
            Task<CloudHubStateUpdate<TResult>>> update,
        CancellationToken cancellationToken);
}

public sealed record CloudHubStateUpdate<TResult>(CloudHubLocalState State, TResult Result);

public interface ICloudHubStateProtector
{
    string Protect(string plaintext);
    string Unprotect(string protectedData);
}

public sealed class DataProtectionCloudHubStateProtector(IDataProtectionProvider provider)
    : ICloudHubStateProtector
{
    private readonly IDataProtector _protector = provider.CreateProtector("Luthn.Cloud.HubState.Payload.v1");

    public string Protect(string plaintext) => _protector.Protect(plaintext);

    public string Unprotect(string protectedData) => _protector.Unprotect(protectedData);
}

public sealed class DataProtectionCloudHubStateStore : ICloudHubStateStore
{
    private const int CurrentVersion = 1;
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(5);
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly CloudHubConnectionOptions _options;
    private readonly ICloudHubStateProtector _protector;

    public DataProtectionCloudHubStateStore(
        IOptions<CloudHubConnectionOptions> options,
        ICloudHubStateProtector protector)
    {
        _options = options.Value;
        _protector = protector;
    }

    public CloudHubLocalState Read()
    {
        using var stateLock = AcquireLock(CancellationToken.None);
        return ReadOrCreate();
    }

    public async Task<TResult> UpdateAsync<TResult>(
        Func<CloudHubLocalState, CancellationToken, Task<CloudHubStateUpdate<TResult>>> update,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);
        return await UpdateWithCheckpointAsync(
            (state, _, updateCancellationToken) => update(state, updateCancellationToken),
            cancellationToken);
    }

    public async Task<TResult> UpdateWithCheckpointAsync<TResult>(
        Func<
            CloudHubLocalState,
            Action<CloudHubLocalState>,
            CancellationToken,
            Task<CloudHubStateUpdate<TResult>>> update,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);
        await using var stateLock = await AcquireLockAsync(cancellationToken);
        var current = ReadOrCreate();
        var next = await update(current, Persist, cancellationToken);
        Persist(next.State);
        return next.Result;
    }

    private CloudHubLocalState ReadOrCreate()
    {
        if (!File.Exists(StatePath))
        {
            var created = CloudHubProtocolClient.CreateLocalState();
            Persist(created);
            return created;
        }

        using var stream = File.OpenRead(StatePath);
        var envelope = JsonSerializer.Deserialize<ProtectedCloudHubState>(stream, SerializerOptions);
        if (envelope is null || envelope.Version != CurrentVersion ||
            string.IsNullOrWhiteSpace(envelope.ProtectedPayload))
        {
            throw new InvalidOperationException("The protected Cloud Hub state is invalid.");
        }

        var json = _protector.Unprotect(envelope.ProtectedPayload);
        return JsonSerializer.Deserialize<CloudHubLocalState>(json, SerializerOptions) ??
            throw new InvalidOperationException("The protected Cloud Hub payload is invalid.");
    }

    private void Persist(CloudHubLocalState state)
    {
        Directory.CreateDirectory(StateDirectory);
        var payload = JsonSerializer.Serialize(state, SerializerOptions);
        var envelope = new ProtectedCloudHubState(
            CurrentVersion,
            _protector.Protect(payload));
        var temporaryPath = Path.Combine(
            StateDirectory,
            $".cloud-hub-state.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None))
            {
                JsonSerializer.Serialize(stream, envelope, SerializerOptions);
                stream.Flush(flushToDisk: true);
            }
            SetOwnerOnly(temporaryPath);
            File.Move(temporaryPath, StatePath, overwrite: true);
            SetOwnerOnly(StatePath);
        }
        catch
        {
            File.Delete(temporaryPath);
            throw;
        }
    }

    private FileStream AcquireLock(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(StateDirectory);
        var startedAt = DateTimeOffset.UtcNow;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var stream = new FileStream(
                    LockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None);
                SetOwnerOnly(LockPath);
                return stream;
            }
            catch (IOException) when (DateTimeOffset.UtcNow - startedAt < LockTimeout)
            {
                Thread.Sleep(50);
            }
        }
    }

    private async Task<FileStream> AcquireLockAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(StateDirectory);
        var startedAt = DateTimeOffset.UtcNow;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var stream = new FileStream(
                    LockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None);
                SetOwnerOnly(LockPath);
                return stream;
            }
            catch (IOException) when (DateTimeOffset.UtcNow - startedAt < LockTimeout)
            {
                await Task.Delay(50, cancellationToken);
            }
        }
    }

    private string StateDirectory => Path.GetFullPath(_options.StateDirectory);
    private string StatePath => Path.Combine(StateDirectory, "cloud-hub-state.json");
    private string LockPath => Path.Combine(StateDirectory, ".cloud-hub-state.lock");

    private static void SetOwnerOnly(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    private sealed record ProtectedCloudHubState(int Version, string ProtectedPayload);
}

public sealed class CloudHubOutboundRelayTransport(
    IOptions<CloudHubConnectionOptions> options,
    ICloudHubStateStore stateStore,
    CloudHubProtocolClient protocolClient) : IHubOutboundRelayTransport
{
    private readonly CloudHubConnectionOptions _options = options.Value;

    public string Name => "luthn-cloud-v2";

    public HubRelayTransportState State
    {
        get
        {
            if (!_options.Enabled)
            {
                return HubRelayTransportState.Disabled;
            }

            try
            {
                var state = stateStore.Read();
                if (state.Session is null)
                {
                    return HubRelayTransportState.Disconnected;
                }

                return state.Session.RefreshExpiresAt > DateTimeOffset.UtcNow
                    ? HubRelayTransportState.Ready
                    : HubRelayTransportState.Stale;
            }
            catch (Exception exception) when (exception is IOException or CryptographicException or InvalidOperationException)
            {
                return HubRelayTransportState.Disconnected;
            }
        }
    }

    public async Task<HubRelayTransportResult> SendSafeProjectionAsync(
        SafeProjectionSyncEnvelope envelope,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (!_options.Enabled)
        {
            return new HubRelayTransportResult(false, ErrorCode: "relay.disabled");
        }

        try
        {
            return await stateStore.UpdateWithCheckpointAsync(
                async (state, persistCheckpoint, updateCancellationToken) =>
                {
                    var result = await protocolClient.SendProjectionAsync(
                        state,
                        _options.ToProtocolOptions(),
                        ToDto(envelope),
                        persistCheckpoint,
                        updateCancellationToken);
                    return new CloudHubStateUpdate<HubRelayTransportResult>(
                        result.State,
                        new HubRelayTransportResult(
                            result.Accepted,
                            result.Checkpoint,
                            result.ErrorCode));
                },
                cancellationToken);
        }
        catch (Exception exception) when (
            exception is HttpRequestException or IOException or CloudHubProtocolException or
            CryptographicException or InvalidOperationException)
        {
            return new HubRelayTransportResult(
                false,
                ErrorCode: exception is CloudHubProtocolException protocolError
                    ? BoundErrorCode(protocolError.ErrorCode)
                    : "relay.disconnected");
        }
    }

    private static SafeProjectionSyncEnvelopeV2Dto ToDto(SafeProjectionSyncEnvelope envelope)
    {
        var idempotencyKey = SafeProjectionSyncPolicy.CreateIdempotencyKey(envelope);
        var operationId = $"op_{Base64Url(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(idempotencyKey)))}";
        var cloudRecordId = $"record_{Base64Url(SHA256.HashData(
            JsonSerializer.SerializeToUtf8Bytes(new[] { envelope.OriginInstanceId, envelope.LocalRecordId })))}";
        return new SafeProjectionSyncEnvelopeV2Dto(
            operationId,
            cloudRecordId,
            envelope.Revision,
            envelope.Operation.ToString(),
            envelope.Title,
            envelope.SafeSummary,
            envelope.CoreTags,
            envelope.ProjectionKind,
            envelope.PayloadClass,
            envelope.RedactionState,
            envelope.CreatedAt,
            envelope.UpdatedAt,
            envelope.DecidedAt,
            envelope.ExpiresAt);
    }

    private static string Base64Url(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string BoundErrorCode(string value) =>
        value.Length <= 64 && value.All(character =>
            character is >= 'a' and <= 'z' or
            >= '0' and <= '9' or '.' or '_' or '-')
            ? value
            : "relay.failure";
}
