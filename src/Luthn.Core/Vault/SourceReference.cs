using Luthn.Core.Common;

namespace Luthn.Core.Vault;

public sealed record SourceReference(
    PublicRecordId Id,
    PublicRecordId VaultRecordId,
    ReferenceKind Kind,
    RedactionState RedactionState,
    string? Note = null);
