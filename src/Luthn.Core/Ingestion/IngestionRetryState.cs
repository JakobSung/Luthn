namespace Luthn.Core.Ingestion;

public sealed class IngestionRetryState
{
    public IngestionRetryState(
        int attemptCount,
        int maxAttempts,
        DateTimeOffset? nextAttemptAt = null,
        string? lastErrorClass = null)
    {
        if (attemptCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(attemptCount), "Retry attempt count cannot be negative.");
        }

        if (maxAttempts < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxAttempts), "Retry max attempts must be at least one.");
        }

        if (attemptCount > maxAttempts)
        {
            throw new ArgumentException("Retry attempt count cannot exceed max attempts.", nameof(attemptCount));
        }

        AttemptCount = attemptCount;
        MaxAttempts = maxAttempts;
        NextAttemptAt = nextAttemptAt;
        LastErrorClass = string.IsNullOrWhiteSpace(lastErrorClass) ? null : lastErrorClass.Trim();
    }

    public int AttemptCount { get; }

    public int MaxAttempts { get; }

    public DateTimeOffset? NextAttemptAt { get; }

    public string? LastErrorClass { get; }

    public bool CanRetry => AttemptCount < MaxAttempts;

    public bool IsExhausted => !CanRetry;

    public bool IsReady(DateTimeOffset now) =>
        CanRetry && (NextAttemptAt is null || NextAttemptAt <= now);

    public IngestionRetryState RecordFailure(string errorClass, DateTimeOffset? nextAttemptAt = null)
    {
        if (string.IsNullOrWhiteSpace(errorClass))
        {
            throw new ArgumentException("Retry error class is required.", nameof(errorClass));
        }

        if (IsExhausted)
        {
            throw new InvalidOperationException("Cannot record another retry failure after retry attempts are exhausted.");
        }

        var nextAttemptCount = AttemptCount + 1;
        return new IngestionRetryState(
            nextAttemptCount,
            MaxAttempts,
            nextAttemptCount < MaxAttempts ? nextAttemptAt : null,
            errorClass);
    }
}
