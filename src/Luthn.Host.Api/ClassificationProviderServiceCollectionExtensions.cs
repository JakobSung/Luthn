using Luthn.Core.Classification;

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

        if (string.Equals(provider, "mock", StringComparison.OrdinalIgnoreCase))
        {
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
            $"Unsupported classification provider '{provider}'. Supported values are 'mock' and 'external-http'.");
    }
}
