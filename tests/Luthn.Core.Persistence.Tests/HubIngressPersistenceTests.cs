using Luthn.Core.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Luthn.Core.Persistence.Tests;

public sealed class HubIngressPersistenceTests
{
    [Fact]
    public async Task DurableIngressQueueSurvivesContextRestartWithProtectedPayloadOnly()
    {
        var databaseName = Guid.NewGuid().ToString("N");
        var options = new DbContextOptionsBuilder<LuthnDbContext>()
            .UseInMemoryDatabase(databaseName)
            .Options;
        await using (var first = new LuthnDbContext(options))
        {
            first.HubIngressQueue.Add(CreateRecord());
            await first.SaveChangesAsync();
        }

        await using var restarted = new LuthnDbContext(options);
        var pending = await restarted.HubIngressQueue.SingleAsync();

        Assert.Equal(HubIngressQueueState.Pending, pending.State);
        Assert.Equal("protected:ciphertext", pending.ProtectedCapsule);
        Assert.DoesNotContain("private capsule", pending.ProtectedCapsule, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IngressIdempotencyIndexIsWorkspaceAndAgentConnectionScoped()
    {
        var options = new DbContextOptionsBuilder<LuthnDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        await using var db = new LuthnDbContext(options);
        var first = CreateRecord();
        var otherConnection = CreateRecord();
        otherConnection.Id = "hub-ingress-2";
        otherConnection.ReceiptId = "hub-receipt-2";
        otherConnection.AgentConnectionId = "connection-2";
        db.HubIngressQueue.AddRange(first, otherConnection);

        await db.SaveChangesAsync();

        Assert.Equal(2, await db.HubIngressQueue.CountAsync());
    }

    private static HubIngressQueueRecord CreateRecord() => new()
    {
        Id = "hub-ingress-1",
        ReceiptId = "hub-receipt-1",
        OrganizationId = "organization-1",
        WorkspaceId = "workspace-1",
        MemberUserId = "member-1",
        AgentConnectionId = "connection-1",
        AgentId = "codex",
        SessionId = "session-1",
        TurnId = "turn-1",
        IdempotencyKey = "event-1",
        ContentDigest = $"sha256:{new string('a', 64)}",
        CapsuleSizeBytes = 15,
        ProtectionScheme = "test:v1",
        ProtectedCapsule = "protected:ciphertext",
        State = HubIngressQueueState.Pending,
        AcceptedAt = DateTimeOffset.Parse("2026-08-06T00:00:00Z")
    };
}
