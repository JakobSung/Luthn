namespace Luthn.Host.Api;

public sealed record LuthnMemoryOptions
{
    public const int DefaultAutomaticTurnRetentionDays = 30;
    public const int MinimumAutomaticTurnRetentionDays = 1;
    public const int MaximumAutomaticTurnRetentionDays = 365;
    public const string AutomaticTurnRetentionValidationMessage =
        "Luthn:Memory:AutomaticTurnRetentionDays must be between 1 and 365 days.";

    public int AutomaticTurnRetentionDays { get; init; } = DefaultAutomaticTurnRetentionDays;

    public bool HasValidAutomaticTurnRetention =>
        AutomaticTurnRetentionDays is >= MinimumAutomaticTurnRetentionDays
            and <= MaximumAutomaticTurnRetentionDays;

    public DateTimeOffset GetAutomaticTurnExpiration(DateTimeOffset receivedAt) =>
        receivedAt.AddDays(AutomaticTurnRetentionDays);
}
