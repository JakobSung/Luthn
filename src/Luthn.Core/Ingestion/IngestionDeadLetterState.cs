namespace Luthn.Core.Ingestion;

public sealed class IngestionDeadLetterState
{
    public IngestionDeadLetterState(
        IngestionDeadLetterReason reason,
        DateTimeOffset deadLetteredAt,
        string errorClass,
        string? diagnosticCode = null)
    {
        Reason = reason;
        DeadLetteredAt = deadLetteredAt;
        ErrorClass = RequiredToken(errorClass, nameof(errorClass));
        DiagnosticCode = string.IsNullOrWhiteSpace(diagnosticCode)
            ? null
            : RequiredToken(diagnosticCode, nameof(diagnosticCode));
    }

    public IngestionDeadLetterReason Reason { get; }

    public DateTimeOffset DeadLetteredAt { get; }

    public string ErrorClass { get; }

    public string? DiagnosticCode { get; }

    private static string RequiredToken(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Ingestion dead-letter metadata value is required.", parameterName);
        }

        var trimmed = value.Trim();
        if (trimmed.Any(char.IsWhiteSpace))
        {
            throw new ArgumentException("Ingestion dead-letter metadata cannot contain whitespace.", parameterName);
        }

        return trimmed;
    }
}
