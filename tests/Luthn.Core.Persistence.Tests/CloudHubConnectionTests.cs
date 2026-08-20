using System.Net;
using System.Text;
using System.Text.Json;
using Luthn.Core.Classification;
using Luthn.Core.Memory;
using Luthn.Core.Persistence;
using Luthn.Sdk.Sync;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Luthn.Core.Persistence.Tests;

public sealed class CloudHubConnectionTests
{
    [Fact]
    public async Task StateFileProtectsPrivateKeyAndCredentialsAtRest()
    {
        var directory = NewDirectory();
        var provider = new EphemeralDataProtectionProvider();
        var options = Options.Create(new CloudHubConnectionOptions
        {
            StateDirectory = directory,
        });
        var store = new DataProtectionCloudHubStateStore(
            options,
            new DataProtectionCloudHubStateProtector(provider));
        var original = store.Read();
        var session = Session(original.Key.KeyId);

        await store.UpdateAsync(
            (state, _) => Task.FromResult(
                new CloudHubStateUpdate<bool>(state with { Session = session }, true)),
            CancellationToken.None);

        var file = await File.ReadAllTextAsync(Path.Combine(directory, "cloud-hub-state.json"));
        Assert.DoesNotContain(original.Key.PrivateKeyPkcs8, file, StringComparison.Ordinal);
        Assert.DoesNotContain(session.AccessToken, file, StringComparison.Ordinal);
        Assert.DoesNotContain(session.RefreshCredential, file, StringComparison.Ordinal);
        var restarted = new DataProtectionCloudHubStateStore(
            options,
            new DataProtectionCloudHubStateProtector(provider)).Read();
        Assert.Equal(session.RefreshCredential, restarted.Session!.RefreshCredential);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite,
                File.GetUnixFileMode(Path.Combine(directory, "cloud-hub-state.json")));
        }
    }

    [Fact]
    public async Task SeparateHostsWithSharedKeyDirectoryCanReadTheSameCloudState()
    {
        var directory = NewDirectory();
        var stateDirectory = Path.Combine(directory, "state");
        var keyDirectory = Path.Combine(directory, "keys");
        var options = Options.Create(new CloudHubConnectionOptions
        {
            StateDirectory = stateDirectory,
        });
        var producerProvider = DataProtectionProvider.Create(
            new DirectoryInfo(keyDirectory),
            builder => builder.SetApplicationName("Luthn.Cloud.HubState.v1"));
        var producer = new DataProtectionCloudHubStateStore(
            options,
            new DataProtectionCloudHubStateProtector(producerProvider));
        var initial = producer.Read();
        await SeedSessionAsync(producer, initial.Key.KeyId);

        var consumerProvider = DataProtectionProvider.Create(
            new DirectoryInfo(keyDirectory),
            builder => builder.SetApplicationName("Luthn.Cloud.HubState.v1"));
        var consumer = new DataProtectionCloudHubStateStore(
            options,
            new DataProtectionCloudHubStateProtector(consumerProvider));

        Assert.Equal("refresh_token_1", consumer.Read().Session!.RefreshCredential);
    }

    [Fact]
    public async Task RealTransportHashesLocalIdentityBeforeCloudAndAcknowledgesReceipt()
    {
        var directory = NewDirectory();
        var options = Options.Create(EnabledOptions(directory));
        var store = new DataProtectionCloudHubStateStore(
            options,
            new DataProtectionCloudHubStateProtector(new EphemeralDataProtectionProvider()));
        var initial = store.Read();
        await SeedSessionAsync(store, initial.Key.KeyId);
        JsonElement sentItem = default;
        var handler = new CallbackHandler(request =>
        {
            var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            using var document = JsonDocument.Parse(body);
            var batchId = document.RootElement.GetProperty("batchId").GetString();
            sentItem = document.RootElement.GetProperty("items")[0].Clone();
            var operationId = sentItem.GetProperty("operationId").GetString();
            var localRecordId = sentItem.GetProperty("localRecordId").GetString();
            return Json(
                $$"""
                {
                  "batchId":"{{batchId}}",
                  "receipts":[{
                    "operationId":"{{operationId}}",
                    "localRecordId":"{{localRecordId}}",
                    "revision":2,
                    "outcome":"Accepted",
                    "retryable":false,
                    "acknowledgedAt":"{{DateTimeOffset.UtcNow:O}}"
                  }],
                  "checkpoint":{
                    "checkpoint":"checkpoint_1",
                    "lastAcknowledgedOperationId":"{{operationId}}",
                    "updatedAt":"{{DateTimeOffset.UtcNow:O}}"
                  }
                }
                """);
        });
        using var httpClient = new HttpClient(handler);
        var protocol = new CloudHubProtocolClient(httpClient, TimeProvider.System);
        var transport = new CloudHubOutboundRelayTransport(options, store, protocol);
        await using var db = CreateDb();
        var envelope = SafeProjectionSyncPolicy.CreateUpsert(
            "default",
            "instance-1",
            "/Users/alice/private/memory.json",
            2,
            "Approved safe summary.",
            ExternalPublicationState.ApprovedForExternal,
            SensitivityLevel.Public,
            MemoryVisibility.SharedAcrossAgents,
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddMinutes(-2),
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddDays(1));
        db.SafeProjectionSyncOutbox.Add(CreateRecord(envelope));
        await db.SaveChangesAsync();
        var processor = new SafeProjectionOutboxProcessor(
            db,
            new HubRelaySafeProjectionSyncTransport(transport));

        var result = await processor.ProcessBatchAsync(DateTimeOffset.UtcNow);

        Assert.Equal(1, result.AcknowledgedCount);
        Assert.Equal(SafeProjectionSyncOutboxState.Acknowledged, (await db.SafeProjectionSyncOutbox.SingleAsync()).State);
        var cloudRecordId = sentItem.GetProperty("localRecordId").GetString()!;
        Assert.StartsWith("record_", cloudRecordId, StringComparison.Ordinal);
        Assert.DoesNotContain("Users", cloudRecordId, StringComparison.Ordinal);
        Assert.DoesNotContain("/", cloudRecordId, StringComparison.Ordinal);
        Assert.DoesNotContain("originInstanceId", sentItem.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("workspaceId", sentItem.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task NetworkFailureKeepsOutboxForLocalOnlyRetry()
    {
        var directory = NewDirectory();
        var options = Options.Create(EnabledOptions(directory));
        var store = new DataProtectionCloudHubStateStore(
            options,
            new DataProtectionCloudHubStateProtector(new EphemeralDataProtectionProvider()));
        var initial = store.Read();
        await SeedSessionAsync(store, initial.Key.KeyId);
        using var httpClient = new HttpClient(new CallbackHandler(_ =>
            throw new HttpRequestException("offline")));
        var transport = new CloudHubOutboundRelayTransport(
            options,
            store,
            new CloudHubProtocolClient(httpClient, TimeProvider.System));
        await using var db = CreateDb();
        var now = DateTimeOffset.UtcNow;
        var envelope = SafeProjectionSyncPolicy.CreateUpsert(
            "default",
            "instance-1",
            "memory-1",
            2,
            "Approved safe summary.",
            ExternalPublicationState.ApprovedForExternal,
            SensitivityLevel.Public,
            MemoryVisibility.SharedAcrossAgents,
            now.AddDays(-1),
            now.AddMinutes(-2),
            now.AddMinutes(-1),
            now.AddDays(1));
        db.SafeProjectionSyncOutbox.Add(CreateRecord(envelope));
        await db.SaveChangesAsync();
        var processor = new SafeProjectionOutboxProcessor(
            db,
            new HubRelaySafeProjectionSyncTransport(transport));

        var result = await processor.ProcessBatchAsync(now);

        Assert.Equal(1, result.FailedCount);
        var record = await db.SafeProjectionSyncOutbox.SingleAsync();
        Assert.Equal(SafeProjectionSyncOutboxState.Failed, record.State);
        Assert.Equal("relay.disconnected", record.LastErrorCode);
        Assert.NotNull(record.NextAttemptAt);
    }

    [Fact]
    public async Task RefreshCredentialIsCheckpointedBeforeProjectionRetryCanFail()
    {
        var directory = NewDirectory();
        var options = Options.Create(EnabledOptions(directory));
        var store = new DataProtectionCloudHubStateStore(
            options,
            new DataProtectionCloudHubStateProtector(new EphemeralDataProtectionProvider()));
        var initial = store.Read();
        var now = DateTimeOffset.UtcNow;
        await store.UpdateAsync(
            (state, _) => Task.FromResult(
                new CloudHubStateUpdate<bool>(
                    state with
                    {
                        Session = new CloudHubSession(
                            "installation_1",
                            "expired_access",
                            now.AddMinutes(-1),
                            "refresh_token_1",
                            now.AddDays(1),
                            initial.Key.KeyId,
                            [CloudHubProtocolClient.ProjectionWriteScope]),
                    },
                    true)),
            CancellationToken.None);
        var requestCount = 0;
        using var httpClient = new HttpClient(new CallbackHandler(_ =>
        {
            requestCount++;
            if (requestCount == 1)
            {
                return Json(
                    $$"""
                    {
                      "installationId":"installation_1",
                      "tokenType":"DPoP",
                      "accessToken":"access_token_2",
                      "expiresInSeconds":300,
                      "refreshCredential":"refresh_token_2",
                      "refreshExpiresAt":"{{now.AddDays(30):O}}",
                      "confirmationJwkThumbprint":"{{initial.Key.KeyId}}",
                      "scopes":["safe-projection.write"]
                    }
                    """);
            }

            throw new HttpRequestException("projection offline");
        }));
        var transport = new CloudHubOutboundRelayTransport(
            options,
            store,
            new CloudHubProtocolClient(httpClient, TimeProvider.System));
        var envelope = SafeProjectionSyncPolicy.CreateUpsert(
            "default",
            "instance-1",
            "memory-1",
            2,
            "Approved safe summary.",
            ExternalPublicationState.ApprovedForExternal,
            SensitivityLevel.Public,
            MemoryVisibility.SharedAcrossAgents,
            now.AddDays(-1),
            now.AddMinutes(-2),
            now.AddMinutes(-1),
            now.AddDays(1));

        var result = await transport.SendSafeProjectionAsync(envelope, CancellationToken.None);

        Assert.False(result.Accepted);
        Assert.Equal("relay.disconnected", result.ErrorCode);
        Assert.Equal(2, requestCount);
        Assert.Equal("refresh_token_2", store.Read().Session!.RefreshCredential);
    }

    private static CloudHubConnectionOptions EnabledOptions(string directory) => new()
    {
        Enabled = true,
        BaseUrl = "https://cloud.example",
        Audience = "luthn-cloud",
        StateDirectory = directory,
    };

    private static CloudHubSession Session(string keyId) => new(
        "installation_1",
        "access_token_1",
        DateTimeOffset.UtcNow.AddMinutes(5),
        "refresh_token_1",
        DateTimeOffset.UtcNow.AddDays(30),
        keyId,
        [CloudHubProtocolClient.ProjectionWriteScope]);

    private static Task SeedSessionAsync(ICloudHubStateStore store, string keyId) =>
        store.UpdateAsync(
            (state, _) => Task.FromResult(
                new CloudHubStateUpdate<bool>(state with { Session = Session(keyId) }, true)),
            CancellationToken.None);

    private static LuthnDbContext CreateDb() => new(
        new DbContextOptionsBuilder<LuthnDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options);

    private static SafeProjectionSyncOutboxRecord CreateRecord(SafeProjectionSyncEnvelope envelope) => new()
    {
        Id = $"sync-{Guid.NewGuid():N}",
        IdempotencyKey = SafeProjectionSyncPolicy.CreateIdempotencyKey(envelope),
        OriginInstanceId = envelope.OriginInstanceId,
        LocalRecordId = envelope.LocalRecordId,
        WorkspaceId = envelope.WorkspaceId,
        OwnerUserId = "member-1",
        Revision = envelope.Revision,
        Operation = envelope.Operation,
        ContractVersion = envelope.ContractVersion,
        SafeEnvelopeJson = JsonSerializer.Serialize(envelope),
        State = SafeProjectionSyncOutboxState.Pending,
        CreatedAt = DateTimeOffset.UtcNow.AddSeconds(-1),
    };

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private static string NewDirectory() =>
        Path.Combine(Path.GetTempPath(), "luthn-cloud-hub-tests", Guid.NewGuid().ToString("N"));

    private sealed class CallbackHandler(Func<HttpRequestMessage, HttpResponseMessage> callback)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(callback(request));
    }
}
