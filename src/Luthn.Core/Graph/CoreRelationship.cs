using Luthn.Core.Common;

namespace Luthn.Core.Graph;

public sealed class CoreRelationship
{
    public CoreRelationship(
        PublicRecordId id,
        CoreRelationshipType type,
        PublicRecordId fromEntityId,
        PublicRecordId toEntityId)
    {
        if (fromEntityId == toEntityId)
        {
            throw new ArgumentException("Core relationships must connect two different entities.", nameof(toEntityId));
        }

        Id = id;
        Type = type;
        FromEntityId = fromEntityId;
        ToEntityId = toEntityId;
    }

    public PublicRecordId Id { get; }

    public CoreRelationshipType Type { get; }

    public PublicRecordId FromEntityId { get; }

    public PublicRecordId ToEntityId { get; }
}
