using Luthn.Core.Common;

namespace Luthn.Core.Classification;

/// <summary>
/// Preserves provider failure behavior and applies a local sensitive-data floor
/// only after the configured provider has returned a valid classification.
/// </summary>
public sealed class HybridContentClassifier(
    IContentClassifier providerClassifier,
    DeterministicSensitiveDataDetector detector) : IContentClassifier
{
    public ClassificationProviderBoundary Boundary => providerClassifier.Boundary;

    public async ValueTask<ClassificationResult> ClassifyAsync(
        PublicRecordId sourceId,
        string content,
        string? sourceType,
        CancellationToken cancellationToken = default)
    {
        var providerResult = await providerClassifier.ClassifyAsync(
            sourceId,
            content,
            sourceType,
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        return ConservativeClassificationMerger.Merge(
            providerResult,
            detector.Detect(sourceId, content));
    }
}
