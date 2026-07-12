using Luthn.Core.Classification;
using Luthn.Core.Memory;

namespace Luthn.Core.Persistence;

public sealed class SourceEventRecord
{
    public string Id { get; set; } = "";
    public string SourceSystem { get; set; } = "";
    public string SourceType { get; set; } = "";
    public DateTimeOffset ReceivedAt { get; set; }
    public string ContentDigest { get; set; } = "";
    public bool ContainsSensitiveMaterial { get; set; }
}

public sealed class ClassificationResultRecord
{
    public string Id { get; set; } = "";
    public string SourceEventId { get; set; } = "";
    public SensitivityLevel Sensitivity { get; set; }
    public double Confidence { get; set; }
    public List<string> Categories { get; set; } = [];
    public bool ContainsSensitiveMaterial { get; set; }
    public StorageDecisionKind StorageDecision { get; set; }
    public SourceEventRecord? SourceEvent { get; set; }
}

public sealed class WikiProposalRecord
{
    public string Id { get; set; } = "";
    public string SourceEventId { get; set; } = "";
    public string Title { get; set; } = "";
    public string SafeSummary { get; set; } = "";
    public SensitivityLevel Sensitivity { get; set; }
    public List<string> CoreTags { get; set; } = [];
    public bool AllowsAgentContext { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public SourceEventRecord? SourceEvent { get; set; }
}

public sealed class SensitiveRecordReferenceRecord
{
    public string Id { get; set; } = "";
    public string SourceEventId { get; set; } = "";
    public string SourceSystem { get; set; } = "";
    public string SourceType { get; set; } = "";
    public DateTimeOffset ReceivedAt { get; set; }
    public bool ContainsSensitiveMaterial { get; set; }
    public string ReferenceLabel { get; set; } = "";
    public string RedactedSummary { get; set; } = "";
    public SourceEventRecord? SourceEvent { get; set; }
}

public enum SensitiveAccessRequestStatus
{
    Pending,
    Approved,
    Denied
}

public enum SensitiveAccessDecisionKind
{
    Approved,
    Denied
}

public sealed class SensitiveAccessRequestRecord
{
    public string Id { get; set; } = "";
    public string SensitiveRecordReferenceId { get; set; } = "";
    public string RequestedBy { get; set; } = "";
    public string RequestReason { get; set; } = "";
    public SensitiveAccessRequestStatus Status { get; set; } = SensitiveAccessRequestStatus.Pending;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string? DecidedBy { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    public SensitiveRecordReferenceRecord? SensitiveRecordReference { get; set; }
}

public sealed class SensitiveAccessDecisionRecord
{
    public string Id { get; set; } = "";
    public string SensitiveAccessRequestId { get; set; } = "";
    public SensitiveAccessDecisionKind Decision { get; set; }
    public string DecidedBy { get; set; } = "";
    public string DecisionReason { get; set; } = "";
    public DateTimeOffset DecidedAt { get; set; }
    public string PayloadClass { get; set; } = "";
    public string RedactionState { get; set; } = "";
    public SensitiveAccessRequestRecord? SensitiveAccessRequest { get; set; }
}

public sealed class SharedMemoryItemRecord
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string SafeSummary { get; set; } = "";
    public SensitivityLevel Sensitivity { get; set; }
    public List<string> CoreTags { get; set; } = [];
    public MemoryVisibility Visibility { get; set; }
    public MemoryRetentionKind RetentionKind { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public string? SourceSessionId { get; set; }
    public bool AllowsAgentContext { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string CreatedBy { get; set; } = "";
}

public enum AgentConnectionVerificationState
{
    Unknown,
    Verified,
    Failed
}

public enum AgentConnectionActivityState
{
    Unknown,
    Succeeded,
    Failed
}

public sealed class AgentConnectionChannelRecord
{
    public string Id { get; set; } = "";
    public string AgentId { get; set; } = "";
    public string AgentName { get; set; } = "";
    public string IntegrationKind { get; set; } = "";
    public string ConnectorVersion { get; set; } = "";
    public string Channel { get; set; } = "";
    public string ConfigurationOwner { get; set; } = "luthn";
    public bool IsConfigured { get; set; }
    public AgentConnectionVerificationState VerificationState { get; set; }
    public AgentConnectionActivityState ActivityState { get; set; }
    public DateTimeOffset? LastVerifiedAt { get; set; }
    public DateTimeOffset? LastActivityAt { get; set; }
    public DateTimeOffset? LastSuccessfulActivityAt { get; set; }
    public string? FailureCode { get; set; }
    public DateTimeOffset FirstObservedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public static class AuditEventPayloadVersions
{
    public const int Current = 1;
}

public sealed class AuditEventRecord
{
    public string Id { get; set; } = "";
    public DateTimeOffset OccurredAt { get; set; }
    public string Actor { get; set; } = "";
    public string Action { get; set; } = "";
    public string SubjectId { get; set; } = "";
    public int PayloadVersion { get; set; } = AuditEventPayloadVersions.Current;
    public string PayloadClass { get; set; } = "";
    public string RedactionState { get; set; } = "";
}
