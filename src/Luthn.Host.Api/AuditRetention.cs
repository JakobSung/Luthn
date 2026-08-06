using Luthn.Core.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Luthn.Host.Api;

public sealed class AuditRetentionOptions
{
    public const int MinimumRetentionDays = 1;
    public const int MaximumRetentionDays = 3650;
    public const int MinimumCleanupIntervalMinutes = 1;
    public const int MaximumCleanupIntervalMinutes = 1440;
    public const int MinimumCleanupBatchSize = 1;
    public const int MaximumCleanupBatchSize = 1000;

    public bool CleanupEnabled { get; set; }
    public int CleanupIntervalMinutes { get; set; } = 60;
    public int CleanupBatchSize { get; set; } = 100;
    public int AccessDays { get; set; } = 365;
    public int SecurityDays { get; set; } = 365;
    public int ConfigurationDays { get; set; } = 730;
    public int PublicationDays { get; set; } = 365;
    public int IngestionDays { get; set; } = 90;
    public int RetentionDays { get; set; } = 730;

    public bool HasValidCleanupInterval =>
        CleanupIntervalMinutes is >= MinimumCleanupIntervalMinutes and <= MaximumCleanupIntervalMinutes;

    public bool HasValidCleanupBatch =>
        CleanupBatchSize is >= MinimumCleanupBatchSize and <= MaximumCleanupBatchSize;

    public bool HasValidRetentionDays =>
        RetentionValues.All(days => days is >= MinimumRetentionDays and <= MaximumRetentionDays);

    public TimeSpan CleanupInterval => TimeSpan.FromMinutes(CleanupIntervalMinutes);

    public int DaysFor(string category) => category switch
    {
        AuditEventCategories.Access => AccessDays,
        AuditEventCategories.Security => SecurityDays,
        AuditEventCategories.Configuration => ConfigurationDays,
        AuditEventCategories.Publication => PublicationDays,
        AuditEventCategories.Ingestion => IngestionDays,
        AuditEventCategories.Retention => RetentionDays,
        _ => SecurityDays
    };

    private int[] RetentionValues =>
    [
        AccessDays,
        SecurityDays,
        ConfigurationDays,
        PublicationDays,
        IngestionDays,
        RetentionDays
    ];
}

public static class AuditEventCategories
{
    public const string Access = "Access";
    public const string Security = "Security";
    public const string Configuration = "Configuration";
    public const string Publication = "Publication";
    public const string Ingestion = "Ingestion";
    public const string Retention = "Retention";

    public static readonly IReadOnlyList<string> All =
    [
        Access,
        Security,
        Configuration,
        Publication,
        Ingestion,
        Retention
    ];

    public static string FromAction(string action)
    {
        if (action.Contains(".retention.", StringComparison.Ordinal) ||
            action.StartsWith("audit.retention.", StringComparison.Ordinal))
        {
            return Retention;
        }

        if (action.StartsWith("operator.classification_provider.", StringComparison.Ordinal))
        {
            return Configuration;
        }

        if (action.StartsWith("transport.", StringComparison.Ordinal) ||
            action.StartsWith("processing.", StringComparison.Ordinal) ||
            action.StartsWith("memory.external_publication.", StringComparison.Ordinal))
        {
            return Publication;
        }

        if (action.StartsWith("sensitive_access.", StringComparison.Ordinal) ||
            action.StartsWith("retrieval.", StringComparison.Ordinal))
        {
            return Access;
        }

        if (action.StartsWith("source.intake.", StringComparison.Ordinal) ||
            action.StartsWith("turn_summary.", StringComparison.Ordinal) ||
            action.StartsWith("memory.", StringComparison.Ordinal))
        {
            return Ingestion;
        }

        return Security;
    }

    public static bool TryNormalize(string? value, out string? category)
    {
        category = string.IsNullOrWhiteSpace(value)
            ? null
            : All.FirstOrDefault(candidate =>
                string.Equals(candidate, value.Trim(), StringComparison.OrdinalIgnoreCase));
        return string.IsNullOrWhiteSpace(value) || category is not null;
    }

    public static IQueryable<AuditEventRecord> Apply(
        IQueryable<AuditEventRecord> query,
        string category) => category switch
    {
        Retention => query.Where(record =>
            record.Action.Contains(".retention.") ||
            record.Action.StartsWith("audit.retention.")),
        Configuration => query.Where(record =>
            record.Action.StartsWith("operator.classification_provider.")),
        Publication => query.Where(record =>
            record.Action.StartsWith("transport.") ||
            record.Action.StartsWith("processing.") ||
            record.Action.StartsWith("memory.external_publication.")),
        Access => query.Where(record =>
            record.Action.StartsWith("sensitive_access.") ||
            record.Action.StartsWith("retrieval.")),
        Ingestion => query.Where(record =>
            !record.Action.Contains(".retention.") &&
            !record.Action.StartsWith("memory.external_publication.") &&
            (record.Action.StartsWith("source.intake.") ||
                record.Action.StartsWith("turn_summary.") ||
                record.Action.StartsWith("memory."))),
        _ => query.Where(record =>
            !record.Action.Contains(".retention.") &&
            !record.Action.StartsWith("audit.retention.") &&
            !record.Action.StartsWith("operator.classification_provider.") &&
            !record.Action.StartsWith("transport.") &&
            !record.Action.StartsWith("processing.") &&
            !record.Action.StartsWith("memory.external_publication.") &&
            !record.Action.StartsWith("sensitive_access.") &&
            !record.Action.StartsWith("retrieval.") &&
            !record.Action.StartsWith("source.intake.") &&
            !record.Action.StartsWith("turn_summary.") &&
            !record.Action.StartsWith("memory."))
    };
}

public sealed record AuditRetentionCleanupResult(int DeletedCount);

public interface IAuditRetentionCleanupProcessor
{
    Task<AuditRetentionCleanupResult> ProcessBatchAsync(
        DateTimeOffset now,
        int batchSize,
        CancellationToken cancellationToken = default);
}

public sealed class AuditRetentionCleanupProcessor(
    LuthnDbContext db,
    IOptions<AuditRetentionOptions> options) : IAuditRetentionCleanupProcessor
{
    public async Task<AuditRetentionCleanupResult> ProcessBatchAsync(
        DateTimeOffset now,
        int batchSize,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(
            batchSize,
            AuditRetentionOptions.MinimumCleanupBatchSize);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            batchSize,
            AuditRetentionOptions.MaximumCleanupBatchSize);

        var candidateIds = new List<string>(batchSize);
        foreach (var category in AuditEventCategories.All)
        {
            var remaining = batchSize - candidateIds.Count;
            if (remaining == 0)
            {
                break;
            }

            var cutoff = now.AddDays(-options.Value.DaysFor(category));
            var categoryIds = await AuditEventCategories
                .Apply(db.AuditEvents.AsNoTracking().Where(record => record.OccurredAt <= cutoff), category)
                .OrderBy(record => record.OccurredAt)
                .ThenBy(record => record.Id)
                .Select(record => record.Id)
                .Take(remaining)
                .ToArrayAsync(cancellationToken);
            candidateIds.AddRange(categoryIds);
        }

        if (candidateIds.Count == 0)
        {
            return new AuditRetentionCleanupResult(0);
        }

        var deletedCount = await db.DeleteAuditEventsForRetentionAsync(
            candidateIds,
            AuditEventFactory.ForInstallation(
            actor: "luthn-audit-retention-cleanup",
            action: "audit.retention.pruned",
            subjectId: "audit-events",
            payloadClass: "metadata-only",
            redactionState: "expired-audit-metadata-deleted",
            occurredAt: now,
            subjectType: "audit_event_collection",
            outcome: "pruned"),
            cancellationToken);

        return new AuditRetentionCleanupResult(deletedCount);
    }
}

internal sealed class AuditRetentionCleanupHostedService(
    IServiceScopeFactory scopeFactory,
    IOptions<AuditRetentionOptions> options,
    TimeProvider timeProvider,
    ILogger<AuditRetentionCleanupHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var cleanupOptions = options.Value;
        if (!cleanupOptions.CleanupEnabled)
        {
            logger.LogInformation("Audit retention cleanup is disabled.");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var processor = scope.ServiceProvider.GetRequiredService<IAuditRetentionCleanupProcessor>();
                var result = await processor.ProcessBatchAsync(
                    timeProvider.GetUtcNow(),
                    cleanupOptions.CleanupBatchSize,
                    stoppingToken);
                if (result.DeletedCount > 0)
                {
                    logger.LogInformation(
                        "Audit retention cleanup completed: deleted={DeletedCount}.",
                        result.DeletedCount);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception error)
            {
                logger.LogError(error, "Audit retention cleanup failed; the API remains available.");
            }

            try
            {
                await Task.Delay(cleanupOptions.CleanupInterval, timeProvider, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
