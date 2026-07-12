using Luthn.Core.Common;

namespace Luthn.Core.Classification;

public sealed record ClassificationResult(
    PublicRecordId SourceId,
    SensitivityLevel Sensitivity,
    double Confidence,
    IReadOnlySet<string> Categories,
    bool ContainsSensitiveMaterial);
