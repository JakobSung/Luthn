using Luthn.Core.Classification;
using Luthn.Core.Common;
using Luthn.Core.Memory;

namespace Luthn.Core.Tests;

public sealed class SharedMemoryModelTests
{
    [Fact]
    public void SessionRequiresOwnerParticipant()
    {
        var ownerId = new PublicRecordId("user-1");

        Assert.Throws<ArgumentException>(() =>
            new MemorySession(
                new PublicRecordId("session-1"),
                ownerId,
                DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
                [new MemoryParticipant(new PublicRecordId("agent-1"), MemoryParticipantRole.Participant)]));
    }

    [Fact]
    public void RetentionPolicyRequiresExpirationForNonDurableMemory()
    {
        Assert.Throws<ArgumentException>(() => new MemoryRetentionPolicy(MemoryRetentionKind.Session));
    }

    [Fact]
    public void SessionRequiresParticipants()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new MemorySession(
                new PublicRecordId("session-1"),
                new PublicRecordId("user-1"),
                DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
                null!));
    }

    [Fact]
    public void DurableRetentionRejectsExpiration()
    {
        Assert.Throws<ArgumentException>(() =>
            new MemoryRetentionPolicy(
                MemoryRetentionKind.Durable,
                DateTimeOffset.Parse("2026-01-01T00:00:00Z")));
    }

    [Fact]
    public void RestrictedMemoryCannotBeSharedAcrossAgentsByDefault()
    {
        Assert.Throws<ArgumentException>(() =>
            new SharedMemoryItem(
                new PublicRecordId("memory-1"),
                "Release credential handling",
                "Keep release credentials behind policy-approved access.",
                SensitivityLevel.Restricted,
                ["release", "credentials"],
                MemoryVisibility.SharedAcrossAgents,
                MemoryRetentionPolicy.Durable()));
    }

    [Fact]
    public void SharedMemoryCarriesSafeSummaryVisibilityRetentionAndTags()
    {
        var ownerId = new PublicRecordId("user-1");
        var session = new MemorySession(
            new PublicRecordId("session-1"),
            ownerId,
            DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
            [
                new MemoryParticipant(ownerId, MemoryParticipantRole.Owner),
                new MemoryParticipant(new PublicRecordId("agent-1"), MemoryParticipantRole.Participant)
            ],
            "Release planning");

        var item = new SharedMemoryItem(
            new PublicRecordId("memory-1"),
            "Release runbook preference",
            "Prefer policy-approved context packs over raw source reads.",
            SensitivityLevel.Internal,
            ["release", "policy"],
            MemoryVisibility.SharedWithParticipants,
            MemoryRetentionPolicy.Session(DateTimeOffset.Parse("2026-01-02T00:00:00Z")),
            session.Id);

        var conclusion = new MemoryConclusion(
            new PublicRecordId("conclusion-1"),
            item.Id,
            "Agents should use safe context packs for release planning.",
            [new PublicRecordId("message-1")]);

        Assert.Equal(session.Id, item.SourceSessionId);
        Assert.Equal(MemoryVisibility.SharedWithParticipants, item.Visibility);
        Assert.Equal(MemoryRetentionKind.Session, item.Retention.Kind);
        Assert.Equal(["release", "policy"], item.CoreTags);
        Assert.Equal(item.Id, conclusion.MemoryItemId);
    }
}
