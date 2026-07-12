namespace Luthn.Core.Ingestion;

public enum IngestionDeadLetterReason
{
    RetryExhausted,
    InvalidPayload,
    ConsentRevoked,
    PolicyRejected,
    OperatorQuarantined
}
