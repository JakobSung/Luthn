using Luthn.Core.Common;

namespace Luthn.Core.Memory;

public sealed record MemoryParticipant(
    PublicRecordId ActorId,
    MemoryParticipantRole Role);
