namespace Luthn.Host.Api;

public sealed record LuthnMemoryOptions
{
    public const int DefaultAutomaticTurnRetentionDays = 30;
    public const int MinimumAutomaticTurnRetentionDays = 1;
    public const int MaximumAutomaticTurnRetentionDays = 365;
    public const int DefaultAutomaticTurnCleanupIntervalMinutes = 60;
    public const int MinimumAutomaticTurnCleanupIntervalMinutes = 1;
    public const int MaximumAutomaticTurnCleanupIntervalMinutes = 1440;
    public const int DefaultAutomaticTurnCleanupBatchSize = 100;
    public const int MinimumAutomaticTurnCleanupBatchSize = 1;
    public const int MaximumAutomaticTurnCleanupBatchSize = 1000;
    public const string AutomaticTurnRetentionValidationMessage =
        "Luthn:Memory:AutomaticTurnRetentionDays must be between 1 and 365 days.";
    public const string AutomaticTurnCleanupIntervalValidationMessage =
        "Luthn:Memory:AutomaticTurnCleanupIntervalMinutes must be between 1 and 1440 minutes.";
    public const string AutomaticTurnCleanupBatchValidationMessage =
        "Luthn:Memory:AutomaticTurnCleanupBatchSize must be between 1 and 1000 records.";

    public int AutomaticTurnRetentionDays { get; init; } = DefaultAutomaticTurnRetentionDays;
    public bool AutomaticTurnCleanupEnabled { get; init; } = true;
    public int AutomaticTurnCleanupIntervalMinutes { get; init; } =
        DefaultAutomaticTurnCleanupIntervalMinutes;
    public int AutomaticTurnCleanupBatchSize { get; init; } =
        DefaultAutomaticTurnCleanupBatchSize;

    public bool HasValidAutomaticTurnRetention =>
        AutomaticTurnRetentionDays is >= MinimumAutomaticTurnRetentionDays
            and <= MaximumAutomaticTurnRetentionDays;

    public bool HasValidAutomaticTurnCleanupInterval =>
        AutomaticTurnCleanupIntervalMinutes is >= MinimumAutomaticTurnCleanupIntervalMinutes
            and <= MaximumAutomaticTurnCleanupIntervalMinutes;

    public bool HasValidAutomaticTurnCleanupBatch =>
        AutomaticTurnCleanupBatchSize is >= MinimumAutomaticTurnCleanupBatchSize
            and <= MaximumAutomaticTurnCleanupBatchSize;

    public TimeSpan AutomaticTurnCleanupInterval =>
        TimeSpan.FromMinutes(AutomaticTurnCleanupIntervalMinutes);

    public DateTimeOffset GetAutomaticTurnExpiration(DateTimeOffset receivedAt) =>
        receivedAt.AddDays(AutomaticTurnRetentionDays);
}
