using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Luthn.Sdk.Sync;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

namespace Luthn.Core.Persistence;

public sealed class CloudAgentDeviceConnectionOptions
{
    public const string SectionName = "Luthn:Cloud:AgentDevice";

    public bool Enabled { get; set; }
    public string BaseUrl { get; set; } = string.Empty;
    public string Audience { get; set; } = "luthn-cloud-agent";
    public string StateDirectory { get; set; } = string.Empty;

    public AgentDeviceProtocolOptions ToProtocolOptions()
    {
        if (!Enabled)
        {
            return new AgentDeviceProtocolOptions();
        }
        if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out var baseUri))
        {
            throw new InvalidOperationException("Luthn Cloud AgentDevice BaseUrl must be an absolute URI.");
        }
        var options = new AgentDeviceProtocolOptions
        {
            BaseUri = baseUri,
            Audience = Audience,
        };
        _ = options.RemoteMcpUri;
        return options;
    }
}

public interface ICloudAgentDeviceStateStore
{
    AgentDeviceLocalState Read();
    Task<TResult> UpdateAsync<TResult>(
        Func<AgentDeviceLocalState, CancellationToken, Task<CloudAgentDeviceStateUpdate<TResult>>> update,
        CancellationToken cancellationToken);
}

public sealed record CloudAgentDeviceStateUpdate<TResult>(
    AgentDeviceLocalState State,
    TResult Result);

public sealed class DataProtectionCloudAgentDeviceStateStore
    : ICloudAgentDeviceStateStore
{
    private const int CurrentVersion = 1;
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(5);
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };
    private readonly CloudAgentDeviceConnectionOptions _options;
    private readonly IDataProtector _protector;

    public DataProtectionCloudAgentDeviceStateStore(
        IOptions<CloudAgentDeviceConnectionOptions> options,
        IDataProtectionProvider provider)
    {
        _options = options.Value;
        _protector = provider.CreateProtector("Luthn.Cloud.AgentDeviceState.Payload.v1");
    }

    public AgentDeviceLocalState Read()
    {
        using var stateLock = AcquireLock(CancellationToken.None);
        return ReadOrCreate();
    }

    public async Task<TResult> UpdateAsync<TResult>(
        Func<AgentDeviceLocalState, CancellationToken, Task<CloudAgentDeviceStateUpdate<TResult>>> update,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);
        await using var stateLock = await AcquireLockAsync(cancellationToken);
        var next = await update(ReadOrCreate(), cancellationToken);
        Persist(next.State);
        return next.Result;
    }

    private AgentDeviceLocalState ReadOrCreate()
    {
        if (!File.Exists(StatePath))
        {
            var created = AgentDeviceProtocolClient.CreateLocalState();
            Persist(created);
            return created;
        }

        using var stream = File.OpenRead(StatePath);
        var envelope = JsonSerializer.Deserialize<ProtectedState>(stream, SerializerOptions);
        if (envelope is null || envelope.Version != CurrentVersion ||
            string.IsNullOrWhiteSpace(envelope.ProtectedPayload))
        {
            throw new InvalidOperationException("The protected Cloud AgentDevice state is invalid.");
        }
        var json = _protector.Unprotect(envelope.ProtectedPayload);
        return JsonSerializer.Deserialize<AgentDeviceLocalState>(json, SerializerOptions) ??
            throw new InvalidOperationException("The protected Cloud AgentDevice payload is invalid.");
    }

    private void Persist(AgentDeviceLocalState state)
    {
        Directory.CreateDirectory(StateDirectory);
        SetOwnerOnlyDirectory(StateDirectory);
        var payload = JsonSerializer.Serialize(state, SerializerOptions);
        var envelope = new ProtectedState(CurrentVersion, _protector.Protect(payload));
        var temporaryPath = Path.Combine(
            StateDirectory,
            $".cloud-agent-device-state.{Guid.NewGuid():N}.tmp");
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
    private string StatePath => Path.Combine(StateDirectory, "cloud-agent-device-state.json");
    private string LockPath => Path.Combine(StateDirectory, ".cloud-agent-device-state.lock");

    private static void SetOwnerOnly(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    private static void SetOwnerOnlyDirectory(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private sealed record ProtectedState(int Version, string ProtectedPayload);
}

public sealed class AesGcmCloudAgentDeviceStateStore
    : ICloudAgentDeviceStateStore, IDisposable
{
    private const int CurrentVersion = 1;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private static readonly byte[] AssociatedData =
        Encoding.UTF8.GetBytes("Luthn.Cloud.AgentDeviceState.Payload.v1");
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(5);
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };
    private readonly CloudAgentDeviceConnectionOptions _options;
    private readonly byte[] _key;

    public AesGcmCloudAgentDeviceStateStore(
        IOptions<CloudAgentDeviceConnectionOptions> options,
        ReadOnlySpan<byte> key)
    {
        if (key.Length != 32)
        {
            throw new ArgumentException("Cloud AgentDevice state key must be 256 bits.", nameof(key));
        }
        _options = options.Value;
        _key = key.ToArray();
    }

    public AgentDeviceLocalState Read()
    {
        using var stateLock = AcquireLock(CancellationToken.None);
        return ReadOrCreate();
    }

    public async Task<TResult> UpdateAsync<TResult>(
        Func<AgentDeviceLocalState, CancellationToken, Task<CloudAgentDeviceStateUpdate<TResult>>> update,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);
        await using var stateLock = await AcquireLockAsync(cancellationToken);
        var next = await update(ReadOrCreate(), cancellationToken);
        Persist(next.State);
        return next.Result;
    }

    public void Dispose() => CryptographicOperations.ZeroMemory(_key);

    private AgentDeviceLocalState ReadOrCreate()
    {
        if (!File.Exists(StatePath))
        {
            var created = AgentDeviceProtocolClient.CreateLocalState();
            Persist(created);
            return created;
        }

        try
        {
            using var stream = File.OpenRead(StatePath);
            var envelope = JsonSerializer.Deserialize<EncryptedState>(stream, SerializerOptions);
            if (envelope is null || envelope.Version != CurrentVersion)
            {
                throw new InvalidOperationException();
            }
            var encrypted = Convert.FromBase64String(envelope.EncryptedPayload);
            if (encrypted.Length <= NonceSize + TagSize)
            {
                throw new InvalidOperationException();
            }
            var nonce = encrypted.AsSpan(0, NonceSize);
            var tag = encrypted.AsSpan(NonceSize, TagSize);
            var ciphertext = encrypted.AsSpan(NonceSize + TagSize);
            var plaintext = new byte[ciphertext.Length];
            try
            {
                using var aes = new AesGcm(_key, TagSize);
                aes.Decrypt(nonce, ciphertext, tag, plaintext, AssociatedData);
                return JsonSerializer.Deserialize<AgentDeviceLocalState>(plaintext, SerializerOptions) ??
                    throw new InvalidOperationException();
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
                CryptographicOperations.ZeroMemory(encrypted);
            }
        }
        catch (Exception exception) when (
            exception is JsonException or FormatException or CryptographicException or InvalidOperationException)
        {
            throw new InvalidOperationException("The encrypted Cloud AgentDevice state is invalid.", exception);
        }
    }

    private void Persist(AgentDeviceLocalState state)
    {
        Directory.CreateDirectory(StateDirectory);
        SetOwnerOnlyDirectory(StateDirectory);
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(state, SerializerOptions);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];
        try
        {
            using var aes = new AesGcm(_key, TagSize);
            aes.Encrypt(nonce, plaintext, ciphertext, tag, AssociatedData);
            var encrypted = new byte[nonce.Length + tag.Length + ciphertext.Length];
            try
            {
                nonce.CopyTo(encrypted, 0);
                tag.CopyTo(encrypted, nonce.Length);
                ciphertext.CopyTo(encrypted, nonce.Length + tag.Length);
                var envelope = new EncryptedState(CurrentVersion, Convert.ToBase64String(encrypted));
                PersistEnvelope(envelope);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(encrypted);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            CryptographicOperations.ZeroMemory(nonce);
            CryptographicOperations.ZeroMemory(ciphertext);
            CryptographicOperations.ZeroMemory(tag);
        }
    }

    private void PersistEnvelope(EncryptedState envelope)
    {
        var temporaryPath = Path.Combine(
            StateDirectory,
            $".cloud-agent-device-state.{Guid.NewGuid():N}.tmp");
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
            SetOwnerOnlyFile(temporaryPath);
            File.Move(temporaryPath, StatePath, overwrite: true);
            SetOwnerOnlyFile(StatePath);
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
        SetOwnerOnlyDirectory(StateDirectory);
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
                SetOwnerOnlyFile(LockPath);
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
        SetOwnerOnlyDirectory(StateDirectory);
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
                SetOwnerOnlyFile(LockPath);
                return stream;
            }
            catch (IOException) when (DateTimeOffset.UtcNow - startedAt < LockTimeout)
            {
                await Task.Delay(50, cancellationToken);
            }
        }
    }

    private string StateDirectory => Path.GetFullPath(_options.StateDirectory);
    private string StatePath => Path.Combine(StateDirectory, "cloud-agent-device-state.json");
    private string LockPath => Path.Combine(StateDirectory, ".cloud-agent-device-state.lock");

    private static void SetOwnerOnlyFile(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    private static void SetOwnerOnlyDirectory(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private sealed record EncryptedState(int Version, string EncryptedPayload);
}
