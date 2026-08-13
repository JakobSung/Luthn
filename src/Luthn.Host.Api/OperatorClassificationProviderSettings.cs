using System.Text.Json.Serialization;
using Luthn.Core.Classification;

namespace Luthn.Host.Api;

public enum OperatorClassificationProviderKind
{
    Unconfigured,
    LocalDeterministic,
    LocalHttp
}

public sealed record OperatorClassificationProviderSettings
{
    public OperatorClassificationProviderKind Provider { get; init; } = OperatorClassificationProviderKind.Unconfigured;
    public string Model { get; init; } = "";
    public string Endpoint { get; init; } = "";
    public string AuthHeaderName { get; init; } = "";
    [JsonIgnore]
    public string ApiKey { get; init; } = "";
    public string PayloadClass { get; init; } = "classification-input";
    public string RedactionState { get; init; } = "operator-configured-provider";

    [JsonIgnore]
    public bool HasApiKey => !string.IsNullOrWhiteSpace(ApiKey);
}

public sealed record ClassificationProviderConfigurationResponse(
    string Provider,
    string Model,
    string Endpoint,
    string AuthHeaderName,
    string PayloadClass,
    string RedactionState,
    string Status,
    string StatusDetail,
    string ProviderBoundary,
    bool LocalSensitiveDataGuardActive,
    string LocalSensitiveDataGuardVersion);

public sealed record SaveClassificationProviderConfigurationRequest(
    string Provider,
    string? Endpoint);

public sealed record TestClassificationProviderConfigurationRequest(
    string? Content,
    string? SourceType);

public sealed record TestClassificationProviderConfigurationResponse(
    ClassificationProviderConfigurationResponse Configuration,
    ClassificationPreviewClassification Classification,
    StorageDecision StorageDecision);
