namespace Luthn.Core.Classification;

public static class ClassificationResultNormalizer
{
    public static ClassificationResult Normalize(ClassificationResult classification)
    {
        var categories = classification.Categories
            .Where(category => !string.IsNullOrWhiteSpace(category))
            .Select(category => category.Trim())
            .Select(category => ClassificationTaxonomy.CanonicalNameFor(category) ?? category)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var sensitivity = classification.Sensitivity;
        foreach (var category in categories)
        {
            var categoryMinimum = ClassificationTaxonomy.MinimumSensitivityFor(category);
            if (categoryMinimum is not null && categoryMinimum.Value > sensitivity)
            {
                sensitivity = categoryMinimum.Value;
            }
        }

        if (classification.ContainsSensitiveMaterial && sensitivity < SensitivityLevel.Confidential)
        {
            sensitivity = SensitivityLevel.Confidential;
        }

        var containsSensitiveMaterial = classification.ContainsSensitiveMaterial ||
            sensitivity is SensitivityLevel.Confidential or SensitivityLevel.Restricted;

        return classification with
        {
            Sensitivity = sensitivity,
            Confidence = double.IsFinite(classification.Confidence)
                ? Math.Clamp(classification.Confidence, 0, 1)
                : 0,
            Categories = categories,
            ContainsSensitiveMaterial = containsSensitiveMaterial
        };
    }
}
