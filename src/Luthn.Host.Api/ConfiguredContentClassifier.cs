using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Luthn.Core.Classification;
using Luthn.Core.Common;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Luthn.Host.Api;

public sealed class ConfiguredContentClassifier : IContentClassifier
{
    public const string HttpClientName = "LuthnLocalClassificationProvider";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly IOperatorClassificationSettingsStore _settingsStore;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ClassificationProviderRuntimeOptions _runtimeOptions;
    private readonly ILogger<ConfiguredContentClassifier> _logger;
    private readonly IOperationalMetrics _metrics;
    private readonly LocalContextualContentClassifier _localClassifier = new();

    public ConfiguredContentClassifier(
        IOperatorClassificationSettingsStore settingsStore,
        IHttpClientFactory httpClientFactory)
        : this(
            settingsStore,
            httpClientFactory,
            Options.Create(new ClassificationProviderRuntimeOptions()),
            NullLogger<ConfiguredContentClassifier>.Instance,
            NullOperationalMetrics.Instance)
    {
    }

    public ConfiguredContentClassifier(
        IOperatorClassificationSettingsStore settingsStore,
        IHttpClientFactory httpClientFactory,
        IOptions<ClassificationProviderRuntimeOptions> runtimeOptions,
        ILogger<ConfiguredContentClassifier> logger,
        IOperationalMetrics? metrics = null)
    {
        _settingsStore = settingsStore;
        _httpClientFactory = httpClientFactory;
        _runtimeOptions = runtimeOptions.Value;
        _logger = logger;
        _metrics = metrics ?? NullOperationalMetrics.Instance;
    }

    public ClassificationProviderBoundary Boundary => _settingsStore.Current.Provider switch
    {
        OperatorClassificationProviderKind.LocalDeterministic => _localClassifier.Boundary,
        OperatorClassificationProviderKind.LocalHttp => new ClassificationProviderBoundary(
            ClassificationProviderOptions.LocalHttpProvider,
            _settingsStore.Current.PayloadClass,
            _settingsStore.Current.RedactionState),
        _ => new ClassificationProviderBoundary(
            ClassificationProviderOptions.UnconfiguredProvider,
            "classification-input",
            "provider-unconfigured")
    };

    public async ValueTask<ClassificationResult> ClassifyAsync(
        PublicRecordId sourceId,
        string content,
        string? sourceType,
        CancellationToken cancellationToken = default)
    {
        var settings = _settingsStore.Current;
        return settings.Provider switch
        {
            OperatorClassificationProviderKind.LocalDeterministic => await _localClassifier.ClassifyAsync(
                sourceId,
                content,
                sourceType,
                cancellationToken),
            OperatorClassificationProviderKind.LocalHttp => await ClassifyLocalHttpAsync(
                settings,
                sourceId,
                content,
                sourceType,
                cancellationToken),
            _ => throw new ClassificationProviderException(ClassificationProviderOptions.ProviderRequiredMessage)
        };
    }

    private async ValueTask<ClassificationResult> ClassifyLocalHttpAsync(
        OperatorClassificationProviderSettings settings,
        PublicRecordId sourceId,
        string content,
        string? sourceType,
        CancellationToken cancellationToken)
    {
        if (!LocalHttpEndpointValidator.TryValidate(settings.Endpoint, out var endpoint, out var error))
        {
            throw new ClassificationProviderException(error);
        }

        using var response = await ClassificationProviderHttp.SendAsync(
            _httpClientFactory,
            HttpClientName,
            () => new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = JsonContent.Create(new LocalClassifierRequest(
                    sourceId.Value,
                    sourceType,
                    content,
                    settings.PayloadClass,
                    settings.RedactionState), options: SerializerOptions)
            },
            _runtimeOptions,
            _logger,
            ClassificationProviderOptions.LocalHttpProvider,
            _metrics,
            cancellationToken);

        var providerResponse = await response.Content.ReadFromJsonAsync<LocalClassifierResponse>(
            SerializerOptions,
            cancellationToken);
        return ToClassificationResult(sourceId, providerResponse);
    }

    private static ClassificationResult ToClassificationResult(
        PublicRecordId sourceId,
        LocalClassifierResponse? response)
    {
        if (response is null)
        {
            throw new InvalidOperationException("LocalHttp classification provider returned an empty response.");
        }

        if (!Enum.TryParse<SensitivityLevel>(response.Sensitivity, true, out var sensitivity) ||
            !Enum.IsDefined(sensitivity))
        {
            throw new InvalidOperationException(
                $"LocalHttp classification provider returned unsupported sensitivity '{response.Sensitivity}'.");
        }

        var categories = (response.Categories ?? [])
            .Where(category => !string.IsNullOrWhiteSpace(category))
            .Select(category => category.Trim())
            .Where(ClassificationTaxonomy.IsKnownCategory)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return ClassificationResultNormalizer.Normalize(new ClassificationResult(
            sourceId,
            sensitivity,
            Math.Clamp(response.Confidence, 0, 1),
            categories,
            response.ContainsSensitiveMaterial));
    }

    private sealed record LocalClassifierRequest(
        string SourceId,
        string? SourceType,
        string Content,
        string PayloadClass,
        string RedactionState);

    private sealed record LocalClassifierResponse(
        string Sensitivity,
        double Confidence,
        IReadOnlyList<string>? Categories,
        bool ContainsSensitiveMaterial);
}
