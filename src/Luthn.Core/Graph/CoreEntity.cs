using Luthn.Core.Common;
using Luthn.Core.Vault;

namespace Luthn.Core.Graph;

public sealed record CoreEntity(
    PublicRecordId Id,
    CoreEntityType Type,
    string DisplayName,
    IReadOnlyList<SourceReference>? SourceReferences = null)
{
    public IReadOnlyList<SourceReference> SourceReferences { get; init; } =
        SourceReferences ?? [];
}
