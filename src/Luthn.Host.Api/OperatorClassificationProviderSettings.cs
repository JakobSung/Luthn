using System.Text.Json.Serialization;
using Luthn.Core.Classification;

namespace Luthn.Host.Api;

public enum OperatorClassificationProviderKind
{
    Unconfigured,
    Mock,
    ExternalHttp,
    OpenAi,
    Anthropic,
    GoogleAi,
    OpenRouter
}

public sealed record OperatorClassificationProviderSettings
{
    public OperatorClassificationProviderKind Provider { get; init; } = OperatorClassificationProviderKind.Unconfigured;
    public string Model { get; init; } = "";
    public string Endpoint { get; init; } = "";
    public string AuthHeaderName { get; init; } = "Authorization";
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
    bool HasApiKey,
    bool MockAllowed,
    string Status,
    string StatusDetail);

public sealed record SaveClassificationProviderConfigurationRequest(
    string Provider,
    string? Model,
    string? Endpoint,
    string? AuthHeaderName,
    string? ApiKey,
    bool ClearApiKey);

public sealed record TestClassificationProviderConfigurationRequest(
    string? Content,
    string? SourceType);

public sealed record TestClassificationProviderConfigurationResponse(
    ClassificationProviderConfigurationResponse Configuration,
    ClassificationPreviewClassification Classification,
    StorageDecision StorageDecision);
