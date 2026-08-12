namespace Luthn.Host.Api;

public sealed record ClassificationProviderOptions
{
    public const string LocalDeterministicProvider = "LocalDeterministic";
    public const string LocalHttpProvider = "LocalHttp";
    public const string UnconfiguredProvider = "Unconfigured";
    public const string ProviderRequiredMessage =
        "No supported local classification provider is configured. Choose LocalDeterministic or configure a same-device LocalHttp endpoint.";

    public string Provider { get; init; } = LocalDeterministicProvider;
    public LocalHttpClassificationProviderOptions LocalHttp { get; init; } = new();

    public OperatorClassificationProviderKind ResolveProvider()
    {
        if (string.Equals(Provider?.Trim(), LocalDeterministicProvider, StringComparison.OrdinalIgnoreCase))
        {
            return OperatorClassificationProviderKind.LocalDeterministic;
        }

        if (string.Equals(Provider?.Trim(), LocalHttpProvider, StringComparison.OrdinalIgnoreCase))
        {
            return LocalHttpEndpointValidator.TryValidate(LocalHttp.Endpoint, out _)
                ? OperatorClassificationProviderKind.LocalHttp
                : OperatorClassificationProviderKind.Unconfigured;
        }

        return OperatorClassificationProviderKind.Unconfigured;
    }
}

public sealed record LocalHttpClassificationProviderOptions
{
    public string? Endpoint { get; init; }
    public string PayloadClass { get; init; } = "classification-input";
    public string RedactionState { get; init; } = "same-device-local-http";
}
