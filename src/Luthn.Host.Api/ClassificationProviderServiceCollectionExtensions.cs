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

        var providerKind = options.ResolveProvider();

        if (providerKind == OperatorClassificationProviderKind.Unconfigured)
        {
            services.AddSingleton<IContentClassifier>(provider =>
                new HybridContentClassifier(
                    new UnavailableContentClassifier(
                        ClassificationProviderOptions.ProviderRequiredMessage,
                        new ClassificationProviderBoundary(
                            ClassificationProviderOptions.UnconfiguredProvider,
                            "classification-input",
                            "provider-unconfigured")),
                    provider.GetRequiredService<DeterministicSensitiveDataDetector>()));
            return services;
        }

        if (providerKind == OperatorClassificationProviderKind.LocalDeterministic)
        {
            services.AddSingleton<LocalContextualContentClassifier>();
            services.AddSingleton<IContentClassifier>(provider =>
                new HybridContentClassifier(
                    provider.GetRequiredService<LocalContextualContentClassifier>(),
                    provider.GetRequiredService<DeterministicSensitiveDataDetector>()));
            return services;
        }

        if (providerKind == OperatorClassificationProviderKind.LocalHttp)
        {
            services.AddSingleton<IOperatorClassificationSettingsStore>(provider =>
                new FixedClassificationSettingsStore(new OperatorClassificationProviderSettings
                {
                    Provider = OperatorClassificationProviderKind.LocalHttp,
                    Endpoint = options.LocalHttp.Endpoint ?? "",
                    PayloadClass = options.LocalHttp.PayloadClass,
                    RedactionState = options.LocalHttp.RedactionState
                }));
            services.AddHttpClient(ConfiguredContentClassifier.HttpClientName, client =>
            {
                client.Timeout = Timeout.InfiniteTimeSpan;
            })
                .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
                {
                    AllowAutoRedirect = false
                });
            services.AddSingleton<ConfiguredContentClassifier>();
            services.AddSingleton<IContentClassifier>(provider =>
                new HybridContentClassifier(
                    provider.GetRequiredService<ConfiguredContentClassifier>(),
                    provider.GetRequiredService<DeterministicSensitiveDataDetector>()));
            return services;
        }

        return services;
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

    private sealed class FixedClassificationSettingsStore(
        OperatorClassificationProviderSettings settings) : IOperatorClassificationSettingsStore
    {
        public OperatorClassificationProviderSettings Current => settings;

        public ValueTask<OperatorClassificationProviderSettings> ReadAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(settings);

        public ValueTask<OperatorClassificationProviderSettings> SaveAsync(
            SaveClassificationProviderConfigurationRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
