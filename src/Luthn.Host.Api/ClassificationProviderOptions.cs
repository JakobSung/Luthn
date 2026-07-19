namespace Luthn.Host.Api;

public sealed record ClassificationProviderOptions
{
    public const string UnconfiguredProvider = "unconfigured";
    public const string MockDisabledMessage =
        "The mock classification provider is disabled. Set Luthn:Classification:AllowMock=true only for explicit development or test use.";
    public const string ProviderRequiredMessage =
        "No classification provider is configured. Configure a provider in the operator console before submitting content for classification.";

    public string Provider { get; init; } = UnconfiguredProvider;
    public bool AllowMock { get; init; }
    public ExternalHttpClassificationProviderOptions ExternalHttp { get; init; } = new();

    public string ResolveProvider()
    {
        var provider = string.IsNullOrWhiteSpace(Provider) ? UnconfiguredProvider : Provider.Trim();
        var hasExternalProvider = !string.IsNullOrWhiteSpace(ExternalHttp.Endpoint);

        if (string.Equals(provider, "mock", StringComparison.OrdinalIgnoreCase) && hasExternalProvider)
        {
            throw new InvalidOperationException(
                "The mock classification provider is test and experiment only. Use 'external-http' when Luthn:Classification:ExternalHttp:Endpoint is configured.");
        }

        return provider;
    }

    public void EnsureMockAllowed()
    {
        if (!AllowMock)
        {
            throw new InvalidOperationException(MockDisabledMessage);
        }
    }
}

public sealed record ExternalHttpClassificationProviderOptions
{
    public string? Endpoint { get; init; }
    public string? CredentialEnvironmentVariable { get; init; }
    public string AuthHeaderName { get; init; } = "Authorization";
    public string PayloadClass { get; init; } = "classification-input";
    public string RedactionState { get; init; } = "external-provider-opt-in";
}
