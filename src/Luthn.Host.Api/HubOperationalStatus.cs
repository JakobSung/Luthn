using System.Collections.Concurrent;
using Luthn.Core.Memory;
using Luthn.Core.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Luthn.Host.Api;

public interface IHubIngressAdmissionCoordinator
{
    ValueTask<IAsyncDisposable> EnterAsync(string organizationId, CancellationToken cancellationToken);
}

public sealed class HubIngressAdmissionCoordinator : IHubIngressAdmissionCoordinator
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);

    public async ValueTask<IAsyncDisposable> EnterAsync(
        string organizationId,
        CancellationToken cancellationToken)
    {
        var gate = _locks.GetOrAdd(organizationId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        return new AdmissionLease(gate);
    }

    private sealed class AdmissionLease(SemaphoreSlim gate) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            gate.Release();
            return ValueTask.CompletedTask;
        }
    }
}

public interface IHubOperationalMetrics
{
    void RecordAdmission(string outcome);
    void RecordProviderLatency(TimeSpan duration);
    void RecordWorker(string outcome, TimeSpan duration);
    HubOperationalMetricsSnapshot Snapshot();
}

public sealed class HubOperationalMetrics : IHubOperationalMetrics
{
    private readonly ConcurrentDictionary<string, long> _admissions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, HubWorkerMetricAggregate> _worker = new(StringComparer.Ordinal);
    private readonly HubDurationMetricAggregate _providerLatency = new();

    public void RecordAdmission(string outcome) =>
        _admissions.AddOrUpdate(BoundAdmission(outcome), 1, static (_, current) => current + 1);

    public void RecordProviderLatency(TimeSpan duration) => _providerLatency.Record(duration);

    public void RecordWorker(string outcome, TimeSpan duration) =>
        _worker.GetOrAdd(BoundWorker(outcome), static key => new HubWorkerMetricAggregate(key))
            .Record(duration);

    public HubOperationalMetricsSnapshot Snapshot() => new(
        _admissions.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new HubOutcomeCount(pair.Key, pair.Value))
            .ToArray(),
        _worker.Values.OrderBy(item => item.Outcome, StringComparer.Ordinal)
            .Select(item => item.Snapshot())
            .ToArray(),
        _providerLatency.Snapshot());

    private static string BoundAdmission(string outcome) => outcome switch
    {
        "accepted" or "duplicate" or "rejected" or "backpressured" => outcome,
        _ => "other"
    };

    private static string BoundWorker(string outcome) => outcome switch
    {
        "completed" or "retry" or "dead_letter" or "recovered" => outcome,
        _ => "other"
    };

    private sealed class HubWorkerMetricAggregate(string outcome)
    {
        private long _count;
        private long _totalMilliseconds;
        private long _maxMilliseconds;
        public string Outcome { get; } = outcome;

        public void Record(TimeSpan duration)
        {
            var milliseconds = Math.Clamp((long)Math.Ceiling(duration.TotalMilliseconds), 0, 3_600_000);
            Interlocked.Increment(ref _count);
            Interlocked.Add(ref _totalMilliseconds, milliseconds);
            long current;
            while ((current = Interlocked.Read(ref _maxMilliseconds)) < milliseconds &&
                Interlocked.CompareExchange(ref _maxMilliseconds, milliseconds, current) != current)
            {
            }
        }

        public HubWorkerMetric Snapshot() => new(
            Outcome,
            Interlocked.Read(ref _count),
            Interlocked.Read(ref _totalMilliseconds),
            Interlocked.Read(ref _maxMilliseconds));
    }

    private sealed class HubDurationMetricAggregate
    {
        private long _count;
        private long _totalMilliseconds;
        private long _maxMilliseconds;

        public void Record(TimeSpan duration)
        {
            var milliseconds = Math.Clamp((long)Math.Ceiling(duration.TotalMilliseconds), 0, 3_600_000);
            Interlocked.Increment(ref _count);
            Interlocked.Add(ref _totalMilliseconds, milliseconds);
            long current;
            while ((current = Interlocked.Read(ref _maxMilliseconds)) < milliseconds &&
                Interlocked.CompareExchange(ref _maxMilliseconds, milliseconds, current) != current)
            {
            }
        }

        public HubDurationMetric Snapshot() => new(
            Interlocked.Read(ref _count),
            Interlocked.Read(ref _totalMilliseconds),
            Interlocked.Read(ref _maxMilliseconds));
    }
}

public sealed class NullHubOperationalMetrics : IHubOperationalMetrics
{
    public static readonly NullHubOperationalMetrics Instance = new();
    private NullHubOperationalMetrics() { }
    public void RecordAdmission(string outcome) { }
    public void RecordProviderLatency(TimeSpan duration) { }
    public void RecordWorker(string outcome, TimeSpan duration) { }
    public HubOperationalMetricsSnapshot Snapshot() => new([], [], new(0, 0, 0));
}

public sealed record HubOperationalMetricsSnapshot(
    IReadOnlyList<HubOutcomeCount> Admissions,
    IReadOnlyList<HubWorkerMetric> Worker,
    HubDurationMetric ProviderLatency);

public sealed record HubOutcomeCount(string Outcome, long Count);
public sealed record HubWorkerMetric(
    string Outcome,
    long Count,
    long TotalDurationMilliseconds,
    long MaxDurationMilliseconds);

public sealed record HubDurationMetric(
    long Count,
    long TotalDurationMilliseconds,
    long MaxDurationMilliseconds);

public sealed record HubQueueStatus(
    int Pending,
    int Processing,
    int RetryScheduled,
    int DeadLetter,
    long ProtectedBytes,
    long OldestPendingAgeSeconds);

public sealed record HubOutboxStatus(
    int Pending,
    int Failed,
    int Acknowledged,
    long OldestPendingAgeSeconds,
    int CheckpointCount);

public sealed record HubOperationalStatusResponse(
    string PayloadClass,
    string RedactionState,
    string RelayState,
    HubOperationalMetricsSnapshot Metrics,
    HubQueueStatus IngressQueue,
    HubOutboxStatus SafeProjectionOutbox);

public sealed class HubOperationalStatusService(
    LuthnDbContext db,
    IHubOutboundRelayTransport relay,
    IHubOperationalMetrics metrics,
    TimeProvider timeProvider)
{
    public async Task<HubOperationalStatusResponse> ReadAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var ingress = await db.HubIngressQueue.AsNoTracking()
            .GroupBy(_ => 1)
            .Select(group => new
            {
                Pending = group.Count(record => record.State == HubIngressQueueState.Pending),
                Processing = group.Count(record => record.State == HubIngressQueueState.Processing),
                Retry = group.Count(record => record.State == HubIngressQueueState.Failed),
                DeadLetter = group.Count(record => record.State == HubIngressQueueState.DeadLetter),
                ProtectedBytes = group
                    .Where(record => record.State != HubIngressQueueState.Completed)
                    .Sum(record => (long)record.CapsuleSizeBytes),
                Oldest = group
                    .Where(record => record.State == HubIngressQueueState.Pending ||
                        record.State == HubIngressQueueState.Processing ||
                        record.State == HubIngressQueueState.Failed)
                    .Min(record => (DateTimeOffset?)record.AcceptedAt)
            })
            .SingleOrDefaultAsync(cancellationToken);
        var outbox = await db.SafeProjectionSyncOutbox.AsNoTracking()
            .GroupBy(_ => 1)
            .Select(group => new
            {
                Pending = group.Count(record => record.State == SafeProjectionSyncOutboxState.Pending ||
                    record.State == SafeProjectionSyncOutboxState.Processing),
                Failed = group.Count(record => record.State == SafeProjectionSyncOutboxState.Failed),
                Acknowledged = group.Count(record => record.State == SafeProjectionSyncOutboxState.Acknowledged),
                Oldest = group
                    .Where(record => record.State == SafeProjectionSyncOutboxState.Pending ||
                        record.State == SafeProjectionSyncOutboxState.Processing ||
                        record.State == SafeProjectionSyncOutboxState.Failed)
                    .Min(record => (DateTimeOffset?)record.CreatedAt)
            })
            .SingleOrDefaultAsync(cancellationToken);
        var checkpointCount = await db.SafeProjectionSyncCheckpoints.CountAsync(cancellationToken);

        return new HubOperationalStatusResponse(
            "metadata-only",
            "aggregate-content-free",
            relay.State.ToString().ToLowerInvariant(),
            metrics.Snapshot(),
            new HubQueueStatus(
                ingress?.Pending ?? 0,
                ingress?.Processing ?? 0,
                ingress?.Retry ?? 0,
                ingress?.DeadLetter ?? 0,
                ingress?.ProtectedBytes ?? 0,
                AgeSeconds(now, ingress?.Oldest)),
            new HubOutboxStatus(
                outbox?.Pending ?? 0,
                outbox?.Failed ?? 0,
                outbox?.Acknowledged ?? 0,
                AgeSeconds(now, outbox?.Oldest),
                checkpointCount));
    }

    private static long AgeSeconds(DateTimeOffset now, DateTimeOffset? occurredAt) =>
        occurredAt is null
            ? 0
            : Math.Max(0, (long)Math.Floor((now - occurredAt.Value).TotalSeconds));
}

public static class HubOperationalStatusEndpoints
{
    public static IEndpointRouteBuilder MapHubOperationalStatus(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/hub/status", Read)
            .RequireServiceScope(ServiceScopes.HubIngressOperate)
            .WithName("ReadHubOperationalStatus");
        return app;
    }

    private static async Task<IResult> Read(
        HubOperationalStatusService service,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var principal = ServiceTokenAuthorization.GetPrincipal(httpContext);
        if (!principal.IsOperator)
        {
            return TypedResults.Problem(
                title: "Forbidden.",
                detail: "Operator authorization is required.",
                statusCode: StatusCodes.Status403Forbidden);
        }
        return TypedResults.Ok(await service.ReadAsync(cancellationToken));
    }
}
