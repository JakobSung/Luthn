using Luthn.Core.Common;

namespace Luthn.Core.Vault;

public sealed record VaultRecordReference(
    PublicRecordId Id,
    string SourceSystem,
    string SourceType,
    DateTimeOffset ReceivedAt,
    bool ContainsSensitiveMaterial);
