using System.Diagnostics;
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

        var duration = timeProvider.GetElapsedTime(_startedAt);
        try
        {
            metrics.RecordSearchRequest(
                surface,
                outcome,
                cacheStatus,
                duration,
                resultCount);
        }
        catch
        {
            // Local aggregation is best-effort and must never alter retrieval behavior.
        }

        try
        {
            LuthnTelemetryEvents.RecordRetrievalCompleted(
                RetrievalId,
                surface,
                outcome,
                cacheStatus,
                duration,
                resultCount);
        }
        catch
        {
            // Activity projection is best-effort and must never alter retrieval behavior.
        }
    }
}

/// <summary>
/// Vendor-neutral telemetry projection. OpenTelemetry can subscribe to the
/// ActivitySource without making the local OSS runtime depend on an exporter.
/// Only bounded fields and the opaque retrieval correlation are emitted.
/// </summary>
internal static class LuthnTelemetryEvents
{
    private static readonly ActivitySource Source = new("Luthn.Host.Api");

    public static void RecordRetrievalCompleted(
        string retrievalId,
        string surface,
        string outcome,
        string cacheStatus,
        TimeSpan duration,
        int resultCount)
    {
        if (!SearchTelemetry.IsValidRetrievalId(retrievalId))
        {
            return;
        }

        Publish(
            "retrieval.completed",
            retrievalId,
            new KeyValuePair<string, object?>[]
            {
                new("luthn.surface", SearchTelemetry.BoundSurface(surface)),
                new("luthn.outcome", SearchTelemetry.BoundOutcome(outcome)),
                new("luthn.cache_status", SearchTelemetry.BoundCacheStatus(cacheStatus)),
                new("luthn.duration_ms", Math.Clamp((long)Math.Ceiling(duration.TotalMilliseconds), 0, SearchTelemetry.MaximumDurationMilliseconds)),
                new("luthn.result_count", Math.Clamp(resultCount, 0, SearchTelemetry.MaximumResultCount))
            });
    }

    public static void RecordRetrievalObserved(
        string retrievalId,
        string surface,
        string outcome,
        string cacheStatus,
        TimeSpan duration,
        int resultCount)
    {
        if (!SearchTelemetry.IsValidRetrievalId(retrievalId))
        {
            return;
        }

        Publish(
            "retrieval.observed",
            retrievalId,
            new KeyValuePair<string, object?>[]
            {
                new("luthn.surface", SearchTelemetry.BoundSurface(surface)),
                new("luthn.outcome", SearchTelemetry.BoundOutcome(outcome)),
                new("luthn.cache_status", SearchTelemetry.BoundCacheStatus(cacheStatus)),
                new("luthn.duration_ms", Math.Clamp((long)Math.Ceiling(duration.TotalMilliseconds), 0, SearchTelemetry.MaximumDurationMilliseconds)),
                new("luthn.result_count", Math.Clamp(resultCount, 0, SearchTelemetry.MaximumResultCount))
            });
    }

    public static void RecordFeedback(string retrievalId, string judgment)
    {
        if (!SearchTelemetry.IsValidRetrievalId(retrievalId))
        {
            return;
        }

        Publish(
            "retrieval.feedback",
            retrievalId,
            [new("luthn.judgment", SearchTelemetry.BoundJudgment(judgment))]);
    }

    private static void Publish(
        string eventName,
        string retrievalId,
        IReadOnlyList<KeyValuePair<string, object?>> tags)
    {
        try
        {
            using var activity = Source.StartActivity(
                $"luthn.search.{eventName}",
                ActivityKind.Internal);
            if (activity is null)
            {
                return;
            }

            activity.SetTag("luthn.event.name", eventName);
            activity.SetTag("luthn.retrieval.id", retrievalId);
            foreach (var tag in tags)
            {
                activity.SetTag(tag.Key, tag.Value);
            }
        }
        catch
        {
            // Telemetry must never alter retrieval or feedback behavior.
        }
    }
}
