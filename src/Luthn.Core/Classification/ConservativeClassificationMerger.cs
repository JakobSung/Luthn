namespace Luthn.Core.Classification;

public static class ConservativeClassificationMerger
{
    public static ClassificationResult Merge(
        ClassificationResult providerClassification,
        ClassificationResult localClassification)
    {
        ArgumentNullException.ThrowIfNull(providerClassification);
        ArgumentNullException.ThrowIfNull(localClassification);

        if (providerClassification.SourceId != localClassification.SourceId)
        {
            throw new InvalidOperationException("Classification results must refer to the same source id.");
        }

        var provider = ClassificationResultNormalizer.Normalize(providerClassification);
        var local = ClassificationResultNormalizer.Normalize(localClassification);
        var categories = provider.Categories
            .Concat(local.Categories)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return ClassificationResultNormalizer.Normalize(new ClassificationResult(
            provider.SourceId,
            provider.Sensitivity >= local.Sensitivity ? provider.Sensitivity : local.Sensitivity,
            Math.Max(provider.Confidence, local.Confidence),
            categories,
            provider.ContainsSensitiveMaterial || local.ContainsSensitiveMaterial));
    }
}
