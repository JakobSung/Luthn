namespace Luthn.Core.Ingestion;

public sealed class IngestionConsent
{
    public IngestionConsent(IngestionConsentKind kind, string grantedBy, DateTimeOffset grantedAt)
    {
        Kind = kind;
        GrantedBy = RequiredText(grantedBy, nameof(grantedBy));
        GrantedAt = grantedAt;
    }

    public IngestionConsentKind Kind { get; }

    public string GrantedBy { get; }

    public DateTimeOffset GrantedAt { get; }

    private static string RequiredText(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Ingestion consent actor is required.", parameterName);
        }

        return value.Trim();
    }
}
