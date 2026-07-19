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
        services.AddSingleton<DeterministicSensitiveDataDetector>();

        var provider = options.ResolveProvider();

        if (string.Equals(
            provider,
            ClassificationProviderOptions.UnconfiguredProvider,
            StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<IContentClassifier>(provider =>
                new HybridContentClassifier(
                    new UnavailableContentClassifier(
                        ClassificationProviderOptions.ProviderRequiredMessage,
                        new ClassificationProviderBoundary(
                            "Unconfigured",
                            "classification-input",
                            "provider-unconfigured")),
                    provider.GetRequiredService<DeterministicSensitiveDataDetector>()));
            return services;
        }

        if (string.Equals(provider, "mock", StringComparison.OrdinalIgnoreCase))
        {
            if (!options.AllowMock)
            {
                services.AddSingleton<IContentClassifier>(provider =>
                    new HybridContentClassifier(
                        new UnavailableContentClassifier(
                            ClassificationProviderOptions.MockDisabledMessage,
                            new ClassificationProviderBoundary(
                                "Mock",
                                "local-classification-input",
                                "mock-disabled")),
                        provider.GetRequiredService<DeterministicSensitiveDataDetector>()));
                return services;
            }

            services.AddSingleton<MockContentClassifier>();
            services.AddSingleton<IContentClassifier>(provider =>
                new HybridContentClassifier(
                    provider.GetRequiredService<MockContentClassifier>(),
                    provider.GetRequiredService<DeterministicSensitiveDataDetector>()));
            return services;
        }

        if (string.Equals(provider, "external-http", StringComparison.OrdinalIgnoreCase))
        {
            services.AddHttpClient(ExternalHttpContentClassifier.HttpClientName, client =>
            {
                client.Timeout = Timeout.InfiniteTimeSpan;
            });
            services.AddSingleton<ExternalHttpContentClassifier>();
            services.AddSingleton<IContentClassifier>(provider =>
                new HybridContentClassifier(
                    provider.GetRequiredService<ExternalHttpContentClassifier>(),
                    provider.GetRequiredService<DeterministicSensitiveDataDetector>()));
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
