namespace Luthn.Host.Api;

public sealed record ClassificationProviderOptions
{
    public string Provider { get; init; } = "mock";
    public ExternalHttpClassificationProviderOptions ExternalHttp { get; init; } = new();

    public string ResolveProvider()
    {
        var provider = string.IsNullOrWhiteSpace(Provider) ? "mock" : Provider.Trim();
        var hasExternalProvider = !string.IsNullOrWhiteSpace(ExternalHttp.Endpoint);

        if (string.Equals(provider, "mock", StringComparison.OrdinalIgnoreCase) && hasExternalProvider)
        {
            throw new InvalidOperationException(
                "The mock classification provider is test and experiment only. Use 'external-http' when Luthn:Classification:ExternalHttp:Endpoint is configured.");
        }

        return provider;
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
