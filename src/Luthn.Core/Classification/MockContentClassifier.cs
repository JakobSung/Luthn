using Luthn.Core.Classification;
using Luthn.Core.Common;

namespace Luthn.Core.Classification;

public sealed class MockContentClassifier : IContentClassifier
{
    public const string UsageNotice =
        "MockContentClassifier is test and experiment only; production classification requires an external provider.";

    public ClassificationProviderBoundary Boundary { get; } =
        new("mock", "local-classification-input", "local-only");

    private static readonly string[] RestrictedMarkers =
    [
        "credential",
        "private key",
        "access key",
        "customer original"
    ];

    private static readonly string[] ConfidentialMarkers =
    [
        "contract",
        "invoice",
        "payment",
        "tax",
        "customer",
        "email"
    ];

    public ValueTask<ClassificationResult> ClassifyAsync(
        PublicRecordId sourceId,
        string content,
        string? sourceType,
        CancellationToken cancellationToken = default)
    {
        var normalized = content.ToLowerInvariant();
        var categories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        AddMatches(categories, normalized, RestrictedMarkers);
        AddMatches(categories, normalized, ConfidentialMarkers);

        var sensitivity = categories.Overlaps(RestrictedMarkers)
            ? SensitivityLevel.Restricted
            : categories.Count > 0
                ? SensitivityLevel.Confidential
                : IsLikelyOperationalKnowledge(normalized, sourceType)
                    ? SensitivityLevel.Internal
                    : SensitivityLevel.Public;

        var confidence = string.IsNullOrWhiteSpace(content)
            ? 0
            : categories.Count > 0
                ? 0.9
                : 0.75;

        return ValueTask.FromResult(new ClassificationResult(
            sourceId,
            sensitivity,
            confidence,
            categories,
            sensitivity is SensitivityLevel.Confidential or SensitivityLevel.Restricted));
    }

    private static bool IsLikelyOperationalKnowledge(string content, string? sourceType) =>
        string.Equals(sourceType, "runbook", StringComparison.OrdinalIgnoreCase)
        || content.Contains("runbook", StringComparison.Ordinal)
        || content.Contains("implementation", StringComparison.Ordinal)
        || content.Contains("decision", StringComparison.Ordinal);

    private static void AddMatches(HashSet<string> categories, string content, IEnumerable<string> markers)
    {
        foreach (var marker in markers)
        {
            if (content.Contains(marker, StringComparison.Ordinal))
            {
                categories.Add(marker);
            }
        }
    }
}
