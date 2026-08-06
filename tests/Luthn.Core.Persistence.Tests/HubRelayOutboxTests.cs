using System.Text.Json;
using System.Text.Json.Serialization;
using Luthn.Core.Classification;
using Luthn.Core.Memory;
using Luthn.Core.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Luthn.Core.Persistence.Tests;

public sealed class HubRelayOutboxTests
{
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    [Fact]
    public async Task DisabledHubRelayPreservesOutboxWithZeroOutbound()
    {
        await using var db = CreateDb();
        db.SafeProjectionSyncOutbox.Add(CreateRecord(CreateUpsert(2), DateTimeOffset.UtcNow));
        await db.SaveChangesAsync();
        var transport = new HubRelaySafeProjectionSyncTransport(new DisabledHubOutboundRelayTransport());
        var processor = new SafeProjectionOutboxProcessor(db, transport);

        var result = await processor.ProcessBatchAsync(DateTimeOffset.UtcNow);

        Assert.Equal(SafeProjectionSyncTransportState.Disabled, result.TransportState);
        Assert.Equal(0, result.ClaimedCount);
        Assert.Equal(SafeProjectionSyncOutboxState.Pending, (await db.SafeProjectionSyncOutbox.SingleAsync()).State);
        Assert.Empty(await db.SafeProjectionSyncCheckpoints.ToArrayAsync());
    }

    [Fact]
    public async Task HubRelayOutagePreservesLocalQueueAndReconnectStoresCheckpoint()
    {
        await using var db = CreateDb();
        db.SafeProjectionSyncOutbox.Add(CreateRecord(CreateUpsert(2), DateTimeOffset.UtcNow));
        await db.SaveChangesAsync();
        var relay = new MutableFakeHubRelay(HubRelayTransportState.Disconnected);
        var processor = new SafeProjectionOutboxProcessor(db, new HubRelaySafeProjectionSyncTransport(relay));

        var outage = await processor.ProcessBatchAsync(DateTimeOffset.UtcNow);
        relay.State = HubRelayTransportState.Ready;
        var recovered = await processor.ProcessBatchAsync(DateTimeOffset.UtcNow.AddSeconds(1));

        Assert.Equal(SafeProjectionSyncTransportState.NotConnected, outage.TransportState);
        Assert.Equal(0, outage.ClaimedCount);
        Assert.Equal(1, recovered.AcknowledgedCount);
        Assert.Single(relay.Sent);
        Assert.Equal(SafeProjectionSyncOutboxState.Acknowledged, (await db.SafeProjectionSyncOutbox.SingleAsync()).State);
        Assert.Equal("checkpoint-1", (await db.SafeProjectionSyncCheckpoints.SingleAsync()).Checkpoint);
    }

    [Fact]
    public async Task HubRelayRevokeSupersedesDelayedUpsertAndSendsNoBody()
    {
        await using var db = CreateDb();
        var now = DateTimeOffset.UtcNow;
        db.SafeProjectionSyncOutbox.AddRange(
            CreateRecord(CreateUpsert(2), now.AddSeconds(-1)),
            CreateRecord(CreateRevoke(3), now));
        await db.SaveChangesAsync();
        var relay = new MutableFakeHubRelay(HubRelayTransportState.Ready);
        var processor = new SafeProjectionOutboxProcessor(db, new HubRelaySafeProjectionSyncTransport(relay));

        var result = await processor.ProcessBatchAsync(now.AddSeconds(1));

        Assert.Equal(1, result.SupersededCount);
        Assert.Equal(1, result.AcknowledgedCount);
        var sent = Assert.Single(relay.Sent);
        Assert.Equal(SafeProjectionSyncOperation.Revoke, sent.Operation);
        Assert.Null(sent.Title);
        Assert.Null(sent.SafeSummary);
        Assert.Empty(sent.CoreTags);
        Assert.Null(sent.ExpiresAt);
        Assert.DoesNotContain("private", JsonSerializer.Serialize(sent), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HubRelayRejectsUnsafeEnvelopeAndBoundsTransportError()
    {
        var relay = new MutableFakeHubRelay(HubRelayTransportState.Ready)
        {
            Result = new HubRelayTransportResult(false, ErrorCode: new string('x', 1000))
        };
        var adapter = new HubRelaySafeProjectionSyncTransport(relay);
        var unsafeEnvelope = CreateUpsert(2) with { PayloadClass = "raw-capsule" };

        var rejected = await adapter.SendAsync(unsafeEnvelope, default);
        var safeFailure = await adapter.SendAsync(CreateUpsert(2), default);

        Assert.False(rejected.Accepted);
        Assert.Equal("relay.invalid_envelope", rejected.ErrorCode);
        Assert.Single(relay.Sent);
        Assert.False(safeFailure.Accepted);
        Assert.Equal("relay.failure", safeFailure.ErrorCode);
    }

    private static LuthnDbContext CreateDb() => new(
        new DbContextOptionsBuilder<LuthnDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options);

    private static SafeProjectionSyncEnvelope CreateUpsert(long revision) =>
        SafeProjectionSyncPolicy.CreateUpsert(
            "workspace-1",
            "instance-1",
            "memory-1",
            revision,
            "Safe operational summary.",
            ExternalPublicationState.ApprovedForExternal,
            SensitivityLevel.Public,
            MemoryVisibility.SharedAcrossAgents,
            DateTimeOffset.Parse("2026-08-06T00:00:00Z"),
            DateTimeOffset.Parse("2026-08-06T00:01:00Z"),
            DateTimeOffset.Parse("2026-08-06T00:01:00Z"),
            null);

    private static SafeProjectionSyncEnvelope CreateRevoke(long revision) =>
        SafeProjectionSyncPolicy.CreateRevoke(
            "workspace-1",
            "instance-1",
            "memory-1",
            revision,
            DateTimeOffset.Parse("2026-08-06T00:00:00Z"),
            DateTimeOffset.Parse("2026-08-06T00:02:00Z"),
            DateTimeOffset.Parse("2026-08-06T00:02:00Z"));

    private static SafeProjectionSyncOutboxRecord CreateRecord(
        SafeProjectionSyncEnvelope envelope,
        DateTimeOffset createdAt) => new()
    {
        Id = $"sync-{envelope.Revision}",
        IdempotencyKey = SafeProjectionSyncPolicy.CreateIdempotencyKey(envelope),
        OriginInstanceId = envelope.OriginInstanceId,
        LocalRecordId = envelope.LocalRecordId,
        WorkspaceId = envelope.WorkspaceId,
        OwnerUserId = "member-1",
        Revision = envelope.Revision,
        Operation = envelope.Operation,
        ContractVersion = envelope.ContractVersion,
        SafeEnvelopeJson = JsonSerializer.Serialize(envelope, SerializerOptions),
        State = SafeProjectionSyncOutboxState.Pending,
        CreatedAt = createdAt
    };

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private sealed class MutableFakeHubRelay(HubRelayTransportState state) : IHubOutboundRelayTransport
    {
        public string Name => "fake";
        public HubRelayTransportState State { get; set; } = state;
        public List<SafeProjectionSyncEnvelope> Sent { get; } = [];
        public HubRelayTransportResult Result { get; set; } =
            new(true, "checkpoint-1");

        public Task<HubRelayTransportResult> SendSafeProjectionAsync(
            SafeProjectionSyncEnvelope envelope,
            CancellationToken cancellationToken)
        {
            Sent.Add(envelope);
            return Task.FromResult(Result);
        }
    }
}
