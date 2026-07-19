using Luthn.Core.Classification;
using Luthn.Core.Common;

namespace Luthn.Host.Api;

internal static class ClassificationProviderServiceCollectionExtensions
{
    public static IServiceCollection AddLuthnClassification(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var section = configuration.GetSection("Luthn:Classification");
        var options = section.Get<ClassificationProviderOptions>() ?? new ClassificationProviderOptions();

        services.Configure<ClassificationProviderOptions>(section);
        services.Configure<ClassificationProviderRuntimeOptions>(
            configuration.GetSection("Luthn:Classification:Runtime"));

        var provider = options.ResolveProvider();

        if (string.Equals(
            provider,
            ClassificationProviderOptions.UnconfiguredProvider,
            StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<IContentClassifier>(
                new UnavailableContentClassifier(
                    ClassificationProviderOptions.ProviderRequiredMessage,
                    new ClassificationProviderBoundary(
                        "Unconfigured",
                        "classification-input",
                        "provider-unconfigured")));
            return services;
        }

        if (string.Equals(provider, "mock", StringComparison.OrdinalIgnoreCase))
        {
            if (!options.AllowMock)
            {
                services.AddSingleton<IContentClassifier>(
                    new UnavailableContentClassifier(
                        ClassificationProviderOptions.MockDisabledMessage,
                        new ClassificationProviderBoundary(
                            "Mock",
                            "local-classification-input",
                            "mock-disabled")));
                return services;
            }

            services.AddSingleton<IContentClassifier, MockContentClassifier>();
            return services;
        }

        if (string.Equals(provider, "external-http", StringComparison.OrdinalIgnoreCase))
        {
            services.AddHttpClient(ExternalHttpContentClassifier.HttpClientName, client =>
            {
                client.Timeout = Timeout.InfiniteTimeSpan;
            });
            services.AddSingleton<IContentClassifier, ExternalHttpContentClassifier>();
            return services;
        }

        throw new InvalidOperationException(
            $"Unsupported classification provider '{provider}'. Supported values are 'unconfigured', 'mock', and 'external-http'.");
    }

    private sealed class UnavailableContentClassifier(
        string message,
        ClassificationProviderBoundary boundary) : IContentClassifier
    {
        public ClassificationProviderBoundary Boundary => boundary;

        public ValueTask<ClassificationResult> ClassifyAsync(
            PublicRecordId sourceId,
            string content,
            string? sourceType,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<ClassificationResult>(new ClassificationProviderException(message));
    }
}
