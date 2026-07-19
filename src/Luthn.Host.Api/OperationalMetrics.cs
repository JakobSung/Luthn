using System.Collections.Concurrent;

namespace Luthn.Host.Api;

public interface IOperationalMetrics
{
    void RecordClassificationProviderRequest(string provider, string outcome, TimeSpan duration);
    void RecordSensitiveAccessRequest();
    void RecordSensitiveAccessDecision(string outcome);
    void RecordSafeSearchCandidates(string source, int count);
    void RecordSearchRequest(string surface, string outcome, string cacheStatus, TimeSpan duration, int resultCount);
    void RecordSearchFeedback(string judgment);
    OperationalMetricsSnapshot Snapshot();
}

public sealed class OperationalMetrics : IOperationalMetrics
{
    private static readonly long[] SearchDurationBucketUpperBoundsMilliseconds =
        [10, 50, 100, 500, 1_000, 5_000, SearchTelemetry.MaximumDurationMilliseconds];

    private readonly ConcurrentDictionary<string, ProviderMetricAggregate> _providers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _accessDecisions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SearchMetricAggregate> _search = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SearchRequestMetricAggregate> _searchRequests = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _searchFeedback = new(StringComparer.Ordinal);
    private long _accessRequests;

    public void RecordClassificationProviderRequest(string provider, string outcome, TimeSpan duration)
    {
        var boundedProvider = BoundProvider(provider);
        var boundedOutcome = BoundProviderOutcome(outcome);
        var aggregate = _providers.GetOrAdd($"{boundedProvider}:{boundedOutcome}", _ => new ProviderMetricAggregate(boundedProvider, boundedOutcome));
        aggregate.Record(duration);
    }

    public void RecordSensitiveAccessRequest() => Interlocked.Increment(ref _accessRequests);
    public void RecordSensitiveAccessDecision(string outcome) => _accessDecisions.AddOrUpdate(BoundAccessOutcome(outcome), 1, static (_, current) => current + 1);

    public void RecordSafeSearchCandidates(string source, int count) =>
        _search.GetOrAdd(BoundSearchSource(source), static key => new SearchMetricAggregate(key)).Record(Math.Max(0, count));

    public void RecordSearchRequest(
        string surface,
        string outcome,
        string cacheStatus,
        TimeSpan duration,
        int resultCount)
    {
        var boundedSurface = SearchTelemetry.BoundSurface(surface);
        var boundedOutcome = SearchTelemetry.BoundOutcome(outcome);
        var boundedCacheStatus = SearchTelemetry.BoundCacheStatus(cacheStatus);
        _searchRequests
            .GetOrAdd(
                $"{boundedSurface}:{boundedOutcome}:{boundedCacheStatus}",
                _ => new SearchRequestMetricAggregate(boundedSurface, boundedOutcome, boundedCacheStatus))
            .Record(duration, resultCount);

        var tags = new[]
        {
            new KeyValuePair<string, object?>("surface", boundedSurface),
            new KeyValuePair<string, object?>("outcome", boundedOutcome),
            new KeyValuePair<string, object?>("cache_status", boundedCacheStatus)
        };
        LuthnHostMetrics.SearchRequests.Add(1, tags);
        LuthnHostMetrics.SearchDurationMilliseconds.Record(
            Math.Clamp(
                (long)Math.Ceiling(duration.TotalMilliseconds),
                0,
                SearchTelemetry.MaximumDurationMilliseconds),
            tags);
        LuthnHostMetrics.SearchResults.Add(
            Math.Clamp(resultCount, 0, SearchTelemetry.MaximumResultCount),
            tags);
    }

    public void RecordSearchFeedback(string judgment)
    {
        var boundedJudgment = SearchTelemetry.BoundJudgment(judgment);
        _searchFeedback.AddOrUpdate(
            boundedJudgment,
            1,
            static (_, current) => current + 1);
        LuthnHostMetrics.SearchFeedback.Add(
            1,
            new KeyValuePair<string, object?>("judgment", boundedJudgment));
    }

    public OperationalMetricsSnapshot Snapshot() => new(
        "metadata-only",
        new OperationalMetricExportBoundary("local-runtime", "no-external-publication", "aggregate-low-cardinality"),
        _providers.Values.OrderBy(metric => metric.Provider, StringComparer.Ordinal).ThenBy(metric => metric.Outcome, StringComparer.Ordinal).Select(metric => metric.ToSnapshot()).ToArray(),
        new SensitiveAccessMetricSnapshot(Interlocked.Read(ref _accessRequests), _accessDecisions.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => new OutcomeCountSnapshot(pair.Key, pair.Value)).ToArray()),
        _search.Values.OrderBy(metric => metric.Source, StringComparer.Ordinal).Select(metric => metric.ToSnapshot()).ToArray(),
        _searchRequests.Values
            .OrderBy(metric => metric.Surface, StringComparer.Ordinal)
            .ThenBy(metric => metric.Outcome, StringComparer.Ordinal)
            .ThenBy(metric => metric.CacheStatus, StringComparer.Ordinal)
            .Select(metric => metric.ToSnapshot())
            .ToArray(),
        _searchFeedback.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new SearchFeedbackMetricSnapshot(pair.Key, pair.Value))
            .ToArray());

    private static string BoundProvider(string provider) => provider switch { "ExternalHttp" or "OpenAi" or "OpenRouter" or "Anthropic" or "GoogleAi" => provider, _ => "other" };
    private static string BoundProviderOutcome(string outcome) => outcome switch { "succeeded" or "http_failure" or "timeout" or "http_exception" or "retry" or "canceled" => outcome, _ => "other" };
    private static string BoundAccessOutcome(string outcome) => outcome switch { "approved" or "denied" => outcome, _ => "other" };
    private static string BoundSearchSource(string source) => source switch { "wiki_proposals" or "shared_memory_items" => source, _ => "other" };

    private sealed class ProviderMetricAggregate(string provider, string outcome)
    {
        private long _count;
        private long _totalMilliseconds;
        public string Provider { get; } = provider;
        public string Outcome { get; } = outcome;
        public void Record(TimeSpan duration) { Interlocked.Increment(ref _count); Interlocked.Add(ref _totalMilliseconds, Math.Max(0, (long)Math.Ceiling(duration.TotalMilliseconds))); }
        public ProviderMetricSnapshot ToSnapshot() => new(Provider, Outcome, Interlocked.Read(ref _count), Interlocked.Read(ref _totalMilliseconds));
    }

    private sealed class SearchMetricAggregate(string source)
    {
        private long _observations;
        private long _totalCandidates;
        private long _maxCandidates;
        public string Source { get; } = source;
        public void Record(int count)
        {
            Interlocked.Increment(ref _observations);
            Interlocked.Add(ref _totalCandidates, count);
            long current;
            while ((current = Interlocked.Read(ref _maxCandidates)) < count && Interlocked.CompareExchange(ref _maxCandidates, count, current) != current) { }
        }
        public SearchMetricSnapshot ToSnapshot() => new(Source, Interlocked.Read(ref _observations), Interlocked.Read(ref _totalCandidates), Interlocked.Read(ref _maxCandidates));
    }

    private sealed class SearchRequestMetricAggregate(string surface, string outcome, string cacheStatus)
    {
        private long _count;
        private long _totalDurationMilliseconds;
        private long _maxDurationMilliseconds;
        private long _totalResults;
        private long _zeroResultCount;
        private readonly long[] _durationBucketCounts =
            new long[SearchDurationBucketUpperBoundsMilliseconds.Length];

        public string Surface { get; } = surface;
        public string Outcome { get; } = outcome;
        public string CacheStatus { get; } = cacheStatus;

        public void Record(TimeSpan duration, int resultCount)
        {
            var durationMilliseconds = Math.Clamp(
                (long)Math.Ceiling(duration.TotalMilliseconds),
                0,
                SearchTelemetry.MaximumDurationMilliseconds);
            var boundedResultCount = Math.Clamp(resultCount, 0, SearchTelemetry.MaximumResultCount);
            Interlocked.Increment(ref _count);
            Interlocked.Add(ref _totalDurationMilliseconds, durationMilliseconds);
            Interlocked.Add(ref _totalResults, boundedResultCount);
            for (var index = 0; index < SearchDurationBucketUpperBoundsMilliseconds.Length; index++)
            {
                if (durationMilliseconds <= SearchDurationBucketUpperBoundsMilliseconds[index])
                {
                    Interlocked.Increment(ref _durationBucketCounts[index]);
                }
            }

            if (Outcome == "zero_result")
            {
                Interlocked.Increment(ref _zeroResultCount);
            }

            long current;
            while ((current = Interlocked.Read(ref _maxDurationMilliseconds)) < durationMilliseconds &&
                Interlocked.CompareExchange(ref _maxDurationMilliseconds, durationMilliseconds, current) != current)
            {
            }
        }

        public SearchRequestMetricSnapshot ToSnapshot() => new(
            Surface,
            Outcome,
            CacheStatus,
            Interlocked.Read(ref _count),
            Interlocked.Read(ref _totalDurationMilliseconds),
            Interlocked.Read(ref _maxDurationMilliseconds),
            Interlocked.Read(ref _totalResults),
            Interlocked.Read(ref _zeroResultCount),
            SearchDurationBucketUpperBoundsMilliseconds
                .Select((upperBound, index) => new SearchDurationBucketSnapshot(
                    upperBound,
                    Interlocked.Read(ref _durationBucketCounts[index])))
                .ToArray());
    }
}

public sealed class NullOperationalMetrics : IOperationalMetrics
{
    public static readonly NullOperationalMetrics Instance = new();
    private NullOperationalMetrics() { }
    public void RecordClassificationProviderRequest(string provider, string outcome, TimeSpan duration) { }
    public void RecordSensitiveAccessRequest() { }
    public void RecordSensitiveAccessDecision(string outcome) { }
    public void RecordSafeSearchCandidates(string source, int count) { }
    public void RecordSearchRequest(string surface, string outcome, string cacheStatus, TimeSpan duration, int resultCount) { }
    public void RecordSearchFeedback(string judgment) { }
    public OperationalMetricsSnapshot Snapshot() => OperationalMetricsSnapshot.Empty;
}

public sealed record OperationalMetricsSnapshot(
    string PayloadClass,
    OperationalMetricExportBoundary ExportBoundary,
    IReadOnlyList<ProviderMetricSnapshot> ClassificationProvider,
    SensitiveAccessMetricSnapshot SensitiveAccess,
    IReadOnlyList<SearchMetricSnapshot> SafeSearch,
    IReadOnlyList<SearchRequestMetricSnapshot> SearchRequests,
    IReadOnlyList<SearchFeedbackMetricSnapshot> SearchFeedback)
{
    public static readonly OperationalMetricsSnapshot Empty = new("metadata-only", new OperationalMetricExportBoundary("local-runtime", "no-external-publication", "aggregate-low-cardinality"), [], new SensitiveAccessMetricSnapshot(0, []), [], [], []);
}

public sealed record OperationalMetricExportBoundary(string Scope, string Publication, string DetailLevel);
public sealed record ProviderMetricSnapshot(string Provider, string Outcome, long Count, long TotalDurationMilliseconds);
public sealed record SensitiveAccessMetricSnapshot(long Requests, IReadOnlyList<OutcomeCountSnapshot> Decisions);
public sealed record OutcomeCountSnapshot(string Outcome, long Count);
public sealed record SearchMetricSnapshot(string Source, long Observations, long TotalCandidates, long MaxCandidates);
public sealed record SearchRequestMetricSnapshot(
    string Surface,
    string Outcome,
    string CacheStatus,
    long Count,
    long TotalDurationMilliseconds,
    long MaxDurationMilliseconds,
    long TotalResults,
    long ZeroResultCount,
    IReadOnlyList<SearchDurationBucketSnapshot> DurationBuckets);
public sealed record SearchDurationBucketSnapshot(long UpperBoundMilliseconds, long Count);
public sealed record SearchFeedbackMetricSnapshot(string Judgment, long Count);
