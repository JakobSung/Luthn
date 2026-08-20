using System.Security.Cryptography;
using Luthn.Core.Persistence;
using Luthn.Sdk.Sync;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

namespace Luthn.Core.Persistence.Tests;

public sealed class CloudAgentDeviceConnectionTests
{
    [Fact]
    public async Task SeparatedAesKeyProtectsRestartAndRejectsWrongKey()
    {
        var directory = TemporaryDirectory();
        var options = Options.Create(new CloudAgentDeviceConnectionOptions
        {
            Enabled = true,
            BaseUrl = "https://cloud.example",
            StateDirectory = directory,
        });
        var key = RandomNumberGenerator.GetBytes(32);
        var wrongKey = RandomNumberGenerator.GetBytes(32);
        Guid deviceId;
        string authenticationKeyId;
        using (var store = new AesGcmCloudAgentDeviceStateStore(options, key))
        {
            var original = store.Read();
            authenticationKeyId = original.Key.AuthenticationKey.KeyId;
            deviceId = Guid.NewGuid();
            await store.UpdateAsync(
                (state, _) => Task.FromResult(new CloudAgentDeviceStateUpdate<bool>(
                    state with
                    {
                        Session = new AgentDeviceSession(
                            deviceId,
                            "access_token_private",
                            DateTimeOffset.UtcNow.AddMinutes(5),
                            "refresh_token_private",
                            DateTimeOffset.UtcNow.AddDays(30),
                            state.Key.AuthenticationKey.KeyId,
                            [AgentDeviceProtocolClient.ConnectionWriteScope]),
                    },
                    true)),
                CancellationToken.None);
        }

        var persisted = await File.ReadAllTextAsync(
            Path.Combine(directory, "cloud-agent-device-state.json"));
        Assert.DoesNotContain("access_token_private", persisted, StringComparison.Ordinal);
        Assert.DoesNotContain("refresh_token_private", persisted, StringComparison.Ordinal);
        using (var restarted = new AesGcmCloudAgentDeviceStateStore(options, key))
        {
            Assert.Equal(deviceId, restarted.Read().Session!.AgentDeviceId);
            Assert.Equal(authenticationKeyId, restarted.Read().Key.AuthenticationKey.KeyId);
        }
        using (var rejected = new AesGcmCloudAgentDeviceStateStore(options, wrongKey))
        {
            Assert.Throws<InvalidOperationException>(() => rejected.Read());
        }
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                File.GetUnixFileMode(directory));
        }
        CryptographicOperations.ZeroMemory(key);
        CryptographicOperations.ZeroMemory(wrongKey);
    }

    [Fact]
    public async Task DeviceKeysAndCredentialsAreProtectedAndRestartable()
    {
        var directory = TemporaryDirectory();
        var keyDirectory = Path.Combine(directory, "keys");
        var options = Options.Create(new CloudAgentDeviceConnectionOptions
        {
            Enabled = true,
            BaseUrl = "https://cloud.example",
            StateDirectory = directory,
        });
        var provider = DataProtectionProvider.Create(
            new DirectoryInfo(keyDirectory),
            builder => builder.SetApplicationName("Luthn.Cloud.AgentDeviceState.v1"));
        var store = new DataProtectionCloudAgentDeviceStateStore(options, provider);
        var deviceId = Guid.NewGuid();
        var original = store.Read();
        await store.UpdateAsync(
            (state, _) => Task.FromResult(new CloudAgentDeviceStateUpdate<bool>(
                state with
                {
                    Session = new AgentDeviceSession(
                        deviceId,
                        "access_token_private",
                        DateTimeOffset.UtcNow.AddMinutes(5),
                        "refresh_token_private",
                        DateTimeOffset.UtcNow.AddDays(30),
                        state.Key.AuthenticationKey.KeyId,
                        [AgentDeviceProtocolClient.ConnectionWriteScope]),
                },
                true)),
            CancellationToken.None);

        var persisted = await File.ReadAllTextAsync(
            Path.Combine(directory, "cloud-agent-device-state.json"));
        Assert.DoesNotContain("access_token_private", persisted, StringComparison.Ordinal);
        Assert.DoesNotContain("refresh_token_private", persisted, StringComparison.Ordinal);
        Assert.DoesNotContain(original.Key.AuthenticationKey.PrivateKeyPkcs8, persisted, StringComparison.Ordinal);
        Assert.DoesNotContain(original.Key.RelaySenderKey.PrivateKey, persisted, StringComparison.Ordinal);
        Assert.DoesNotContain(original.Key.SensitiveRecipientKey.PrivateKey, persisted, StringComparison.Ordinal);

        var restarted = new DataProtectionCloudAgentDeviceStateStore(options, provider).Read();
        Assert.Equal(deviceId, restarted.Session!.AgentDeviceId);
        Assert.Equal(original.Key.AuthenticationKey.KeyId, restarted.Key.AuthenticationKey.KeyId);
        Assert.Equal(original.Key.RelaySenderKey.KeyId, restarted.Key.RelaySenderKey.KeyId);
        Assert.Equal(original.Key.SensitiveRecipientKey.KeyId, restarted.Key.SensitiveRecipientKey.KeyId);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite,
                File.GetUnixFileMode(Path.Combine(directory, "cloud-agent-device-state.json")));
        }
    }

    [Fact]
    public async Task FailedCloudMutationDoesNotReplaceTheLastUsableLocalState()
    {
        var directory = TemporaryDirectory();
        var options = Options.Create(new CloudAgentDeviceConnectionOptions
        {
            Enabled = true,
            BaseUrl = "https://cloud.example",
            StateDirectory = directory,
        });
        var store = new DataProtectionCloudAgentDeviceStateStore(
            options,
            new EphemeralDataProtectionProvider());
        var before = store.Read();

        await Assert.ThrowsAsync<HttpRequestException>(() => store.UpdateAsync<bool>(
            (_, _) => throw new HttpRequestException("simulated outage"),
            CancellationToken.None));

        var after = store.Read();
        Assert.Equal(before.Key.AuthenticationKey.KeyId, after.Key.AuthenticationKey.KeyId);
        Assert.Null(after.PendingEnrollment);
        Assert.Null(after.Session);
    }

    [Fact]
    public void DisabledOptionsNeverResolveACloudEndpoint()
    {
        var options = new CloudAgentDeviceConnectionOptions();

        var protocol = options.ToProtocolOptions();

        Assert.False(protocol.IsEnabled);
        Assert.Throws<InvalidOperationException>(() => protocol.Resolve("mcp"));
    }

    private static string TemporaryDirectory() =>
        Path.Combine(
            Path.GetTempPath(),
            "luthn-cloud-agent-device-tests",
            Guid.NewGuid().ToString("N"));
}
