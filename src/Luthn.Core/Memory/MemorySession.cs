using Luthn.Core.Common;

namespace Luthn.Core.Memory;

public sealed class MemorySession
{
    public MemorySession(
        PublicRecordId id,
        PublicRecordId ownerActorId,
        DateTimeOffset startedAt,
        IReadOnlyList<MemoryParticipant> participants,
        string? title = null)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(ownerActorId);
        ArgumentNullException.ThrowIfNull(participants);

        if (participants.Count == 0)
        {
            throw new ArgumentException("Memory session requires at least one participant.", nameof(participants));
        }

        if (!participants.Any(participant =>
                participant.ActorId == ownerActorId && participant.Role == MemoryParticipantRole.Owner))
        {
            throw new ArgumentException("Memory session owner must be listed as an owner participant.", nameof(participants));
        }

        Id = id;
        OwnerActorId = ownerActorId;
        StartedAt = startedAt;
        Participants = participants;
        Title = string.IsNullOrWhiteSpace(title) ? null : title;
    }

    public PublicRecordId Id { get; }

    public PublicRecordId OwnerActorId { get; }

    public DateTimeOffset StartedAt { get; }

    public IReadOnlyList<MemoryParticipant> Participants { get; }

    public string? Title { get; }
}
