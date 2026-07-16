using System.Diagnostics.Metrics;

namespace Luthn.Host.Api;

internal static class LuthnHostMetrics
{
    private static readonly Meter Meter = new("Luthn.Host.Api");

    public static readonly Counter<long> ClassificationProviderAttempts =
        Meter.CreateCounter<long>(
            "luthn.classification_provider.attempts",
            description: "Classification provider HTTP attempts.");

    public static readonly Counter<long> ClassificationProviderFailures =
        Meter.CreateCounter<long>(
            "luthn.classification_provider.failures",
            description: "Classification provider HTTP failures after bounded attempts.");

    public static readonly Counter<long> ClassificationProviderRetries =
        Meter.CreateCounter<long>(
            "luthn.classification_provider.retries",
            description: "Classification provider transient retries.");

    public static readonly Histogram<long> SafeSearchCandidateCount =
        Meter.CreateHistogram<long>(
            "luthn.safe_search.candidates",
            description: "Number of public agent-safe candidates selected by database filters for deterministic final ranking.");
}
