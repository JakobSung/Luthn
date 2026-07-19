using System.Text.RegularExpressions;

namespace Luthn.Host.Api;

public static partial class SearchTelemetry
{
    public const long MaximumDurationMilliseconds = 60_000;
    public const int MaximumResultCount = 50;
    public const int MaximumRetrievalIdLength = 64;

    public static string CreateRetrievalId() => $"retrieval-{Guid.NewGuid():N}";

    public static bool IsValidRetrievalId(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= MaximumRetrievalIdLength &&
        RetrievalIdPattern().IsMatch(value);

    public static string BoundSurface(string value) => value switch
    {
        "context_pack" or "agent_search" or "memory_query" or "mcp_context_pack" => value,
        _ => "other"
    };

    public static string BoundOutcome(string value) => value switch
    {
        "succeeded" or "zero_result" or "timeout" or "canceled" or "error" => value,
        _ => "other"
    };

    public static string BoundCacheStatus(string value) => value switch
    {
        "not_applicable" or "hit" or "miss" or "bypass" or "expired" => value,
        _ => "other"
    };

    public static string BoundJudgment(string value) => value switch
    {
        "helpful" or "unhelpful" => value,
        _ => "other"
    };

    [GeneratedRegex("^retrieval-[0-9a-f]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex RetrievalIdPattern();
}

internal sealed class SearchTelemetryScope(
    IOperationalMetrics metrics,
    TimeProvider timeProvider,
    string surface,
    string cacheStatus = "not_applicable")
{
    private readonly long _startedAt = timeProvider.GetTimestamp();
    private int _recorded;

    public string RetrievalId { get; } = SearchTelemetry.CreateRetrievalId();

    public void Complete(int resultCount)
    {
        var outcome = resultCount == 0 ? "zero_result" : "succeeded";
        Record(outcome, resultCount);
    }

    public void Timeout() => Record("timeout", 0);
    public void Canceled() => Record("canceled", 0);
    public void Error() => Record("error", 0);

    private void Record(string outcome, int resultCount)
    {
        if (Interlocked.Exchange(ref _recorded, 1) != 0)
        {
            return;
        }

        metrics.RecordSearchRequest(
            surface,
            outcome,
            cacheStatus,
            timeProvider.GetElapsedTime(_startedAt),
            resultCount);
    }
}
