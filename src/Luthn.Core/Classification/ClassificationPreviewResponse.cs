using Luthn.Core.Classification;

namespace Luthn.Core.Classification;

public sealed record ClassificationPreviewResponse(
    string SourceId,
    ClassificationPreviewClassification Classification,
    StorageDecision StorageDecision);

public sealed record ClassificationPreviewClassification(
    SensitivityLevel Sensitivity,
    double Confidence,
    IReadOnlySet<string> Categories,
    bool ContainsSensitiveMaterial);
