using System.Collections.Concurrent;

namespace Luthn.Host.Api;

public interface IOperationalMetrics
{
    void RecordClassificationProviderRequest(string provider, string outcome, TimeSpan duration);
    void RecordSensitiveAccessRequest();
    void RecordSensitiveAccessDecision(string outcome);
    void RecordSafeSearchCandidates(string source, int count);
    OperationalMetricsSnapshot Snapshot();
}

public sealed class OperationalMetrics : IOperationalMetrics
{
    private readonly ConcurrentDictionary<string, ProviderMetricAggregate> _providers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _accessDecisions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SearchMetricAggregate> _search = new(StringComparer.Ordinal);
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

    public OperationalMetricsSnapshot Snapshot() => new(
        "metadata-only",
        new OperationalMetricExportBoundary("local-runtime", "no-external-publication", "aggregate-low-cardinality"),
        _providers.Values.OrderBy(metric => metric.Provider, StringComparer.Ordinal).ThenBy(metric => metric.Outcome, StringComparer.Ordinal).Select(metric => metric.ToSnapshot()).ToArray(),
        new SensitiveAccessMetricSnapshot(Interlocked.Read(ref _accessRequests), _accessDecisions.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => new OutcomeCountSnapshot(pair.Key, pair.Value)).ToArray()),
        _search.Values.OrderBy(metric => metric.Source, StringComparer.Ordinal).Select(metric => metric.ToSnapshot()).ToArray());

    private static string BoundProvider(string provider) => provider switch { "ExternalHttp" or "OpenAi" or "OpenRouter" or "Anthropic" or "GoogleAi" => provider, _ => "other" };
    private static string BoundProviderOutcome(string outcome) => outcome switch { "succeeded" or "http_failure" or "timeout" or "http_exception" or "retry" => outcome, _ => "other" };
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
}

public sealed class NullOperationalMetrics : IOperationalMetrics
{
    public static readonly NullOperationalMetrics Instance = new();
    private NullOperationalMetrics() { }
    public void RecordClassificationProviderRequest(string provider, string outcome, TimeSpan duration) { }
    public void RecordSensitiveAccessRequest() { }
    public void RecordSensitiveAccessDecision(string outcome) { }
    public void RecordSafeSearchCandidates(string source, int count) { }
    public OperationalMetricsSnapshot Snapshot() => OperationalMetricsSnapshot.Empty;
}

public sealed record OperationalMetricsSnapshot(string PayloadClass, OperationalMetricExportBoundary ExportBoundary, IReadOnlyList<ProviderMetricSnapshot> ClassificationProvider, SensitiveAccessMetricSnapshot SensitiveAccess, IReadOnlyList<SearchMetricSnapshot> SafeSearch)
{
    public static readonly OperationalMetricsSnapshot Empty = new("metadata-only", new OperationalMetricExportBoundary("local-runtime", "no-external-publication", "aggregate-low-cardinality"), [], new SensitiveAccessMetricSnapshot(0, []), []);
}

public sealed record OperationalMetricExportBoundary(string Scope, string Publication, string DetailLevel);
public sealed record ProviderMetricSnapshot(string Provider, string Outcome, long Count, long TotalDurationMilliseconds);
public sealed record SensitiveAccessMetricSnapshot(long Requests, IReadOnlyList<OutcomeCountSnapshot> Decisions);
public sealed record OutcomeCountSnapshot(string Outcome, long Count);
public sealed record SearchMetricSnapshot(string Source, long Observations, long TotalCandidates, long MaxCandidates);
