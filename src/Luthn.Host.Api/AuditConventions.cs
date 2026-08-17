namespace Luthn.Host.Api;

/// <summary>
/// Shared, bounded vocabulary for audit investigations. Endpoint filters,
/// category routing, and operator controls must use these action families.
/// </summary>
public static class AuditActionFamilies
{
    public const string Audit = "audit.";
    public const string Authorization = "authorization.";
    public const string ClassificationProvider = "classification.provider.";
    public const string Console = "console.";
    public const string HubIngress = "hub.ingress.";
    public const string Memory = "memory.";
    public const string OperatorClassificationProvider = "operator.classification_provider.";
    public const string Processing = "processing.";
    public const string Retrieval = "retrieval.";
    public const string SensitiveAccess = "sensitive_access.";
    public const string SourceIntake = "source.intake.";
    public const string Transport = "transport.";
    public const string TurnSummary = "turn_summary.";

    public static readonly IReadOnlySet<string> AllowedPrefixes = new HashSet<string>(StringComparer.Ordinal)
    {
        Audit,
        Authorization,
        ClassificationProvider,
        Console,
        HubIngress,
        Memory,
        OperatorClassificationProvider,
        Processing,
        Retrieval,
        SensitiveAccess,
        SourceIntake,
        Transport,
        TurnSummary
    };
}

/// <summary>
/// Outcome constants used by new audit producers. Existing historical values
/// remain queryable so audit retention never rewrites immutable history.
/// </summary>
public static class AuditOutcomes
{
    public const string Started = "started";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Denied = "denied";
}

public static class AuditCorrelationIds
{
    public static string CreateOperationId() => $"corr-{Guid.NewGuid():N}";
}
