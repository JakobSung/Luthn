namespace Luthn.Core.Ingestion;

public sealed class IngestionOrderingState
{
    public IngestionOrderingState(
        string partitionKey,
        long sequenceNumber,
        DateTimeOffset enqueuedAt,
        bool requiresOrderedProcessing = true)
    {
        if (sequenceNumber < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sequenceNumber), "Ingestion sequence number cannot be negative.");
        }

        PartitionKey = RequiredToken(partitionKey, nameof(partitionKey));
        SequenceNumber = sequenceNumber;
        EnqueuedAt = enqueuedAt;
        RequiresOrderedProcessing = requiresOrderedProcessing;
    }

    public string PartitionKey { get; }

    public long SequenceNumber { get; }

    public DateTimeOffset EnqueuedAt { get; }

    public bool RequiresOrderedProcessing { get; }

    public bool IsAfter(IngestionOrderingState previous)
    {
        ArgumentNullException.ThrowIfNull(previous);

        return string.Equals(PartitionKey, previous.PartitionKey, StringComparison.Ordinal)
            && SequenceNumber > previous.SequenceNumber;
    }

    private static string RequiredToken(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Ingestion ordering partition key is required.", parameterName);
        }

        var trimmed = value.Trim();
        if (trimmed.Any(char.IsWhiteSpace))
        {
            throw new ArgumentException("Ingestion ordering partition key cannot contain whitespace.", parameterName);
        }

        return trimmed;
    }
}
