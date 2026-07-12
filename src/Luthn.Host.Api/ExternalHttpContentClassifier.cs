using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Luthn.Core.Classification;
using Luthn.Core.Common;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Luthn.Host.Api;

public sealed class ExternalHttpContentClassifier : IContentClassifier
{
    public const string HttpClientName = "LuthnClassificationProvider";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptions<ClassificationProviderOptions> _options;
    private readonly ClassificationProviderRuntimeOptions _runtimeOptions;
    private readonly ILogger<ExternalHttpContentClassifier> _logger;

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly HashSet<string> SafeCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        "access key",
        "contract",
        "credential",
        "customer",
        "customer original",
        "email",
        "invoice",
        "payment",
        "private key",
        "tax"
    };

    public ExternalHttpContentClassifier(
        IHttpClientFactory httpClientFactory,
        IOptions<ClassificationProviderOptions> options)
        : this(
            httpClientFactory,
            options,
            Options.Create(new ClassificationProviderRuntimeOptions()),
            NullLogger<ExternalHttpContentClassifier>.Instance)
    {
    }

    public ExternalHttpContentClassifier(
        IHttpClientFactory httpClientFactory,
        IOptions<ClassificationProviderOptions> options,
        IOptions<ClassificationProviderRuntimeOptions> runtimeOptions,
        ILogger<ExternalHttpContentClassifier> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options;
        _runtimeOptions = runtimeOptions.Value;
        _logger = logger;
    }

    public ClassificationProviderBoundary Boundary
    {
        get
        {
            var external = _options.Value.ExternalHttp;
            return new ClassificationProviderBoundary(
                "external-http",
                Normalize(external.PayloadClass, "classification-input"),
                Normalize(external.RedactionState, "external-provider-opt-in"));
        }
    }

    public async ValueTask<ClassificationResult> ClassifyAsync(
        PublicRecordId sourceId,
        string content,
        string? sourceType,
        CancellationToken cancellationToken = default)
    {
        var external = _options.Value.ExternalHttp;
        var endpoint = ReadEndpoint(external);
        var requestBody = new ExternalClassifierRequest(
            sourceId.Value,
            sourceType,
            content,
            Boundary.PayloadClass,
            Boundary.RedactionState);

        using var response = await ClassificationProviderHttp.SendAsync(
            _httpClientFactory,
            HttpClientName,
            () =>
            {
                var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
                {
                    Content = JsonContent.Create(requestBody, options: SerializerOptions)
                };
                AddCredentialHeader(request, external);
                return request;
            },
            _runtimeOptions,
            _logger,
            "external-http",
            cancellationToken);

        var providerResponse = await response.Content.ReadFromJsonAsync<ExternalClassifierResponse>(
            SerializerOptions,
            cancellationToken);

        if (providerResponse is null)
        {
            throw new InvalidOperationException("Classification provider returned an empty response.");
        }

        if (!Enum.TryParse<SensitivityLevel>(
            providerResponse.Sensitivity,
            ignoreCase: true,
            out var sensitivity)
            || !Enum.IsDefined(sensitivity))
        {
            throw new InvalidOperationException(
                $"Classification provider returned unsupported sensitivity '{providerResponse.Sensitivity}'.");
        }

        var categories = (providerResponse.Categories ?? [])
            .Where(category => !string.IsNullOrWhiteSpace(category))
            .Select(category => category.Trim())
            .Where(category => SafeCategories.Contains(category))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return new ClassificationResult(
            sourceId,
            sensitivity,
            Math.Clamp(providerResponse.Confidence, 0, 1),
            categories,
            providerResponse.ContainsSensitiveMaterial);
    }

    private static Uri ReadEndpoint(ExternalHttpClassificationProviderOptions external)
    {
        if (!Uri.TryCreate(external.Endpoint, UriKind.Absolute, out var endpoint))
        {
            throw new InvalidOperationException(
                "Luthn:Classification:ExternalHttp:Endpoint must be an absolute URL when external-http is selected.");
        }

        return endpoint;
    }

    private static void AddCredentialHeader(
        HttpRequestMessage request,
        ExternalHttpClassificationProviderOptions external)
    {
        if (string.IsNullOrWhiteSpace(external.CredentialEnvironmentVariable))
        {
            return;
        }

        var credential = Environment.GetEnvironmentVariable(external.CredentialEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(credential))
        {
            throw new InvalidOperationException(
                $"Configured classification provider credential environment variable '{external.CredentialEnvironmentVariable}' is not set.");
        }

        var headerName = Normalize(external.AuthHeaderName, "Authorization");
        if (!request.Headers.TryAddWithoutValidation(headerName, credential))
        {
            throw new InvalidOperationException(
                $"Configured classification provider auth header '{headerName}' could not be added.");
        }
    }

    private static string Normalize(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private sealed record ExternalClassifierRequest(
        string SourceId,
        string? SourceType,
        string Content,
        string PayloadClass,
        string RedactionState);

    private sealed record ExternalClassifierResponse(
        string Sensitivity,
        double Confidence,
        IReadOnlyList<string>? Categories,
        bool ContainsSensitiveMaterial);
}
