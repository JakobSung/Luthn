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
    private readonly IOperatorClassificationSettingsStore _settingsStore;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ClassificationProviderRuntimeOptions _runtimeOptions;
    private readonly ILogger<ConfiguredContentClassifier> _logger;
    private readonly IOperationalMetrics _metrics;

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

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

    public ClassificationProviderBoundary Boundary => BoundaryFor(_settingsStore.Current);

    public async ValueTask<ClassificationResult> ClassifyAsync(
        PublicRecordId sourceId,
        string content,
        string? sourceType,
        CancellationToken cancellationToken = default)
    {
        var settings = _settingsStore.Current;
        return settings.Provider switch
        {
            OperatorClassificationProviderKind.Mock => await new MockContentClassifier()
                .ClassifyAsync(sourceId, content, sourceType, cancellationToken),
            OperatorClassificationProviderKind.ExternalHttp => await ClassifyExternalHttpAsync(
                settings,
                sourceId,
                content,
                sourceType,
                cancellationToken),
            OperatorClassificationProviderKind.OpenAi => await ClassifyOpenAiCompatibleAsync(
                settings,
                sourceId,
                content,
                sourceType,
                cancellationToken),
            OperatorClassificationProviderKind.OpenRouter => await ClassifyOpenAiCompatibleAsync(
                settings,
                sourceId,
                content,
                sourceType,
                cancellationToken),
            OperatorClassificationProviderKind.Anthropic => await ClassifyAnthropicAsync(
                settings,
                sourceId,
                content,
                sourceType,
                cancellationToken),
            OperatorClassificationProviderKind.GoogleAi => await ClassifyGoogleAiAsync(
                settings,
                sourceId,
                content,
                sourceType,
                cancellationToken),
            _ => throw new InvalidOperationException($"Unsupported classification provider '{settings.Provider}'.")
        };
    }

    private async ValueTask<ClassificationResult> ClassifyExternalHttpAsync(
        OperatorClassificationProviderSettings settings,
        PublicRecordId sourceId,
        string content,
        string? sourceType,
        CancellationToken cancellationToken)
    {
        using var response = await SendProviderRequestAsync(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Post, settings.Endpoint)
            {
                Content = JsonContent.Create(new
                {
                    sourceId = sourceId.Value,
                    sourceType,
                    content,
                    payloadClass = settings.PayloadClass,
                    redactionState = settings.RedactionState
                }, options: SerializerOptions)
            };
            AddAuthorizationHeader(request, settings);
            return request;
        }, settings.Provider, cancellationToken);

        var providerResponse = await response.Content.ReadFromJsonAsync<ClassifierJsonResponse>(
            SerializerOptions,
            cancellationToken);
        return ToClassificationResult(sourceId, providerResponse);
    }

    private async ValueTask<ClassificationResult> ClassifyOpenAiCompatibleAsync(
        OperatorClassificationProviderSettings settings,
        PublicRecordId sourceId,
        string content,
        string? sourceType,
        CancellationToken cancellationToken)
    {
        using var response = await SendProviderRequestAsync(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Post, settings.Endpoint)
            {
                Content = JsonContent.Create(new
                {
                    model = settings.Model,
                    messages = new object[]
                    {
                        new
                        {
                            role = "system",
                            content = ClassificationSystemPrompt
                        },
                        new
                        {
                            role = "user",
                            content = BuildClassificationUserPrompt(sourceId, content, sourceType)
                        }
                    },
                    temperature = 0,
                    response_format = new
                    {
                        type = "json_schema",
                        json_schema = new
                        {
                            name = "luthn_classification",
                            strict = true,
                            schema = ClassificationJsonSchema
                        }
                    }
                }, options: SerializerOptions)
            };
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {settings.ApiKey}");

            if (settings.Provider == OperatorClassificationProviderKind.OpenRouter)
            {
                request.Headers.TryAddWithoutValidation("X-Title", "Luthn");
            }
            return request;
        }, settings.Provider, cancellationToken);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var contentText = document.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString();
        return ToClassificationResult(sourceId, ParseClassifierJson(contentText));
    }

    private async ValueTask<ClassificationResult> ClassifyAnthropicAsync(
        OperatorClassificationProviderSettings settings,
        PublicRecordId sourceId,
        string content,
        string? sourceType,
        CancellationToken cancellationToken)
    {
        using var response = await SendProviderRequestAsync(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Post, settings.Endpoint)
            {
                Content = JsonContent.Create(new
                {
                    model = settings.Model,
                    max_tokens = 400,
                    system = ClassificationSystemPrompt,
                    messages = new object[]
                    {
                        new
                        {
                            role = "user",
                            content = BuildClassificationUserPrompt(sourceId, content, sourceType)
                        }
                    },
                    tools = new object[]
                    {
                        new
                        {
                            name = "classify_luthn_content",
                            description = "Return the Luthn content classification result.",
                            input_schema = ClassificationJsonSchema
                        }
                    },
                    tool_choice = new
                    {
                        type = "tool",
                        name = "classify_luthn_content"
                    }
                }, options: SerializerOptions)
            };
            request.Headers.TryAddWithoutValidation("x-api-key", settings.ApiKey);
            request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
            return request;
        }, settings.Provider, cancellationToken);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        foreach (var block in document.RootElement.GetProperty("content").EnumerateArray())
        {
            if (block.TryGetProperty("type", out var type) &&
                string.Equals(type.GetString(), "tool_use", StringComparison.OrdinalIgnoreCase) &&
                block.TryGetProperty("input", out var input))
            {
                return ToClassificationResult(sourceId, input.Deserialize<ClassifierJsonResponse>(SerializerOptions));
            }
        }

        throw new InvalidOperationException("Anthropic provider did not return the classification tool result.");
    }

    private async ValueTask<ClassificationResult> ClassifyGoogleAiAsync(
        OperatorClassificationProviderSettings settings,
        PublicRecordId sourceId,
        string content,
        string? sourceType,
        CancellationToken cancellationToken)
    {
        var endpointBase = string.IsNullOrWhiteSpace(settings.Endpoint)
            ? "https://generativelanguage.googleapis.com/v1beta/models"
            : settings.Endpoint.TrimEnd('/');
        var endpoint = $"{endpointBase}/{Uri.EscapeDataString(settings.Model)}:generateContent";
        using var response = await SendProviderRequestAsync(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = JsonContent.Create(new
                {
                    systemInstruction = new
                    {
                        parts = new object[] { new { text = ClassificationSystemPrompt } }
                    },
                    contents = new object[]
                    {
                        new
                        {
                            role = "user",
                            parts = new object[]
                            {
                                new { text = BuildClassificationUserPrompt(sourceId, content, sourceType) }
                            }
                        }
                    },
                    generationConfig = new
                    {
                        temperature = 0,
                        responseMimeType = "application/json",
                        responseSchema = ClassificationJsonSchema
                    }
                }, options: SerializerOptions)
            };
            request.Headers.TryAddWithoutValidation(
                string.IsNullOrWhiteSpace(settings.AuthHeaderName)
                    ? "x-goog-api-key"
                    : settings.AuthHeaderName.Trim(),
                settings.ApiKey);
            return request;
        }, settings.Provider, cancellationToken);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var text = document.RootElement
            .GetProperty("candidates")[0]
            .GetProperty("content")
            .GetProperty("parts")[0]
            .GetProperty("text")
            .GetString();
        return ToClassificationResult(sourceId, ParseClassifierJson(text));
    }

    private static ClassificationProviderBoundary BoundaryFor(OperatorClassificationProviderSettings settings) =>
        settings.Provider == OperatorClassificationProviderKind.Mock
            ? new ClassificationProviderBoundary("mock", "local-classification-input", "local-only")
            : new ClassificationProviderBoundary(
                settings.Provider.ToString(),
                settings.PayloadClass,
                settings.RedactionState);

    private static void AddAuthorizationHeader(
        HttpRequestMessage request,
        OperatorClassificationProviderSettings settings)
    {
        if (!settings.HasApiKey)
        {
            return;
        }

        var headerName = string.IsNullOrWhiteSpace(settings.AuthHeaderName)
            ? "Authorization"
            : settings.AuthHeaderName.Trim();
        var headerValue = string.Equals(headerName, "Authorization", StringComparison.OrdinalIgnoreCase)
            ? $"Bearer {settings.ApiKey}"
            : settings.ApiKey;
        request.Headers.TryAddWithoutValidation(headerName, headerValue);
    }

    private Task<HttpResponseMessage> SendProviderRequestAsync(
        Func<HttpRequestMessage> createRequest,
        OperatorClassificationProviderKind provider,
        CancellationToken cancellationToken) =>
        ClassificationProviderHttp.SendAsync(
            _httpClientFactory,
            nameof(ConfiguredContentClassifier),
            createRequest,
            _runtimeOptions,
            _logger,
            provider.ToString(),
            _metrics,
            cancellationToken);

    private static ClassifierJsonResponse? ParseClassifierJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new InvalidOperationException("Classification provider returned an empty JSON result.");
        }

        return JsonSerializer.Deserialize<ClassifierJsonResponse>(json, SerializerOptions);
    }

    private static ClassificationResult ToClassificationResult(
        PublicRecordId sourceId,
        ClassifierJsonResponse? response)
    {
        if (response is null)
        {
            throw new InvalidOperationException("Classification provider returned an empty response.");
        }

        if (!Enum.TryParse<SensitivityLevel>(
            response.Sensitivity,
            ignoreCase: true,
            out var sensitivity)
            || !Enum.IsDefined(sensitivity))
        {
            throw new InvalidOperationException(
                $"Classification provider returned unsupported sensitivity '{response.Sensitivity}'.");
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

    private static string BuildClassificationUserPrompt(
        PublicRecordId sourceId,
        string content,
        string? sourceType) =>
        $"""
        Classify this source for Luthn.

        sourceId: {sourceId.Value}
        sourceType: {sourceType ?? ""}

        content:
        {content}
        """;

    private static readonly string ClassificationSystemPrompt =
        $"You classify source content for Luthn using category taxonomy version {ClassificationTaxonomy.Version}. " +
        "Return only the requested structured classification. Use Public, Internal, Confidential, or Restricted sensitivity. " +
        "Restricted categories are credential, private key, access key, and customer original. " +
        "All other allowed categories are Confidential. Mark containsSensitiveMaterial true for Confidential or Restricted material.";

    private static readonly object ClassificationJsonSchema = new
    {
        type = "object",
        additionalProperties = false,
        required = new[]
        {
            "sensitivity",
            "confidence",
            "categories",
            "containsSensitiveMaterial"
        },
        properties = new
        {
            sensitivity = new
            {
                type = "string",
                @enum = new[] { "Public", "Internal", "Confidential", "Restricted" }
            },
            confidence = new
            {
                type = "number",
                minimum = 0,
                maximum = 1
            },
            categories = new
            {
                type = "array",
                items = new
                {
                    type = "string",
                    @enum = ClassificationTaxonomy.CategoryNames
                }
            },
            containsSensitiveMaterial = new
            {
                type = "boolean"
            }
        }
    };
    private sealed record ClassifierJsonResponse(
        string Sensitivity,
        double Confidence,
        IReadOnlyList<string>? Categories,
        bool ContainsSensitiveMaterial);
}
