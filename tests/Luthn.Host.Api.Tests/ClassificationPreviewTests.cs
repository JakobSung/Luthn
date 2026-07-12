using Luthn.Core.Classification;
using Luthn.Core.Persistence;
using Luthn.Core.Policy;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Luthn.Host.Api.Tests;

public sealed class ClassificationPreviewTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public ClassificationPreviewTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Testing");
                builder.UseSetting("Luthn:TestingDatabaseName", Guid.NewGuid().ToString("N"));
                builder.UseSetting(
                    "Luthn:OperatorConfig:Directory",
                    Path.Combine(Path.GetTempPath(), "luthn-operator-tests", Guid.NewGuid().ToString("N")));
            });
        _client = _factory.CreateClient();
    }

    [Fact]
    public async Task GetHealthzReturnsOkStatus()
    {
        using var response = await _client.GetAsync("/healthz");
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ok", body.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task OperatorConsoleStaticFilesAreServed()
    {
        using var indexResponse = await _client.GetAsync("/");
        var index = await indexResponse.Content.ReadAsStringAsync();
        using var cssResponse = await _client.GetAsync("/assets/operator.css");
        using var jsResponse = await _client.GetAsync("/assets/operator.js");
        var script = await jsResponse.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, indexResponse.StatusCode);
        Assert.Contains("Luthn Operator Console", index, StringComparison.Ordinal);
        Assert.Contains("Classification provider", index, StringComparison.Ordinal);
        Assert.Contains("Access requests", index, StringComparison.Ordinal);
        Assert.Contains("Agent connections", index, StringComparison.Ordinal);
        Assert.Contains("Read-only agent connection status", index, StringComparison.Ordinal);
        Assert.DoesNotContain("Connect agent", index, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Disconnect agent", index, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("text/html", indexResponse.Content.Headers.ContentType?.MediaType);
        Assert.Equal(HttpStatusCode.OK, cssResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, jsResponse.StatusCode);
        Assert.Contains("/api/agent-connections", script, StringComparison.Ordinal);
        Assert.DoesNotContain("/observations", script, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OperatorProviderConfigurationDefaultsToMockWithoutExposingApiKey()
    {
        using var response = await _client.GetAsync("/api/operator/classification-provider");
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Mock", body.RootElement.GetProperty("provider").GetString());
        Assert.False(body.RootElement.GetProperty("hasApiKey").GetBoolean());
        Assert.False(body.RootElement.TryGetProperty("apiKey", out _));
    }

    [Fact]
    public async Task OperatorProviderConfigurationStoresKeyServerSideOnly()
    {
        using var response = await _client.PutAsJsonAsync("/api/operator/classification-provider", new
        {
            provider = "OpenRouter",
            model = "openai/gpt-4.1-mini",
            endpoint = "https://openrouter.ai/api/v1/chat/completions",
            authHeaderName = "Authorization",
            apiKey = "sk-or-test-secret",
            clearApiKey = false
        });
        var responseJson = await response.Content.ReadAsStringAsync();
        using var body = JsonDocument.Parse(responseJson);
        using var getResponse = await _client.GetAsync("/api/operator/classification-provider");
        var getJson = await getResponse.Content.ReadAsStringAsync();
        using var getBody = JsonDocument.Parse(getJson);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("OpenRouter", body.RootElement.GetProperty("provider").GetString());
        Assert.True(body.RootElement.GetProperty("hasApiKey").GetBoolean());
        Assert.DoesNotContain("sk-or-test-secret", responseJson, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        Assert.True(getBody.RootElement.GetProperty("hasApiKey").GetBoolean());
        Assert.DoesNotContain("sk-or-test-secret", getJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OperatorProviderConfigurationClearsSavedKeyWhenSwitchingToMock()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "luthn-operator-tests",
            Guid.NewGuid().ToString("N"));
        var store = new OperatorClassificationSettingsStore(
            Options.Create(new OperatorConfigOptions { Directory = directory }),
            Microsoft.AspNetCore.DataProtection.DataProtectionProvider.Create(
                new DirectoryInfo(Path.Combine(directory, "keys"))),
            new ConfigurationBuilder().Build());

        var external = await store.SaveAsync(new SaveClassificationProviderConfigurationRequest(
            "OpenRouter",
            "openai/gpt-4.1-mini",
            "https://openrouter.ai/api/v1/chat/completions",
            "Authorization",
            "sk-or-test-secret",
            ClearApiKey: false));
        var mock = await store.SaveAsync(new SaveClassificationProviderConfigurationRequest(
            "Mock",
            null,
            null,
            null,
            null,
            ClearApiKey: false));

        Assert.True(external.HasApiKey);
        Assert.Equal(OperatorClassificationProviderKind.Mock, mock.Provider);
        Assert.False(mock.HasApiKey);
        Assert.False((await store.ReadAsync()).HasApiKey);
    }

    [Fact]
    public async Task OperatorProviderConfigurationCanReplaceUndecryptableApiKey()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "luthn-operator-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(
            Path.Combine(directory, "classification-provider.json"),
            JsonSerializer.Serialize(new
            {
                provider = "OpenAi",
                model = "gpt-4.1-mini",
                endpoint = "https://api.openai.com/v1/chat/completions",
                authHeaderName = "Authorization",
                protectedApiKey = "not-a-valid-protected-key",
                payloadClass = "classification-input",
                redactionState = "operator-configured-provider"
            }));
        var store = new OperatorClassificationSettingsStore(
            Options.Create(new OperatorConfigOptions { Directory = directory }),
            Microsoft.AspNetCore.DataProtection.DataProtectionProvider.Create(
                new DirectoryInfo(Path.Combine(directory, "keys"))),
            new ConfigurationBuilder().Build());

        var saved = await store.SaveAsync(new SaveClassificationProviderConfigurationRequest(
            "OpenAi",
            "gpt-4.1-mini",
            "https://api.openai.com/v1/chat/completions",
            "Authorization",
            "sk-new-secret",
            ClearApiKey: false));
        var read = await store.ReadAsync();

        Assert.Equal("sk-new-secret", saved.ApiKey);
        Assert.Equal("sk-new-secret", read.ApiKey);
    }

    [Fact]
    public async Task OperatorProviderConfigurationRejectsUnexpectedDirectProviderHost()
    {
        var store = new OperatorClassificationSettingsStore(
            Options.Create(new OperatorConfigOptions
            {
                Directory = Path.Combine(
                    Path.GetTempPath(),
                    "luthn-operator-tests",
                    Guid.NewGuid().ToString("N"))
            }),
            Microsoft.AspNetCore.DataProtection.DataProtectionProvider.Create(
                new DirectoryInfo(Path.Combine(Path.GetTempPath(), "luthn-operator-test-keys", Guid.NewGuid().ToString("N")))),
            new ConfigurationBuilder().Build());

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveAsync(
            new SaveClassificationProviderConfigurationRequest(
                "OpenAi",
                "gpt-4.1-mini",
                "https://provider.example/v1/chat/completions",
                "Authorization",
                "sk-test-secret",
                ClearApiKey: false)).AsTask());

        Assert.Equal("OpenAi provider endpoint host must be api.openai.com.", error.Message);
    }

    [Fact]
    public async Task OperatorProviderConfigurationRejectsPlainHttpExternalProviderWithApiKey()
    {
        var store = new OperatorClassificationSettingsStore(
            Options.Create(new OperatorConfigOptions
            {
                Directory = Path.Combine(
                    Path.GetTempPath(),
                    "luthn-operator-tests",
                    Guid.NewGuid().ToString("N"))
            }),
            Microsoft.AspNetCore.DataProtection.DataProtectionProvider.Create(
                new DirectoryInfo(Path.Combine(Path.GetTempPath(), "luthn-operator-test-keys", Guid.NewGuid().ToString("N")))),
            new ConfigurationBuilder().Build());

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveAsync(
            new SaveClassificationProviderConfigurationRequest(
                "ExternalHttp",
                "",
                "http://provider.example/classify",
                "Authorization",
                "external-provider-secret",
                ClearApiKey: false)).AsTask());

        Assert.Equal("External HTTP provider endpoint must be HTTPS when an API key is configured.", error.Message);
    }

    [Fact]
    public async Task OperatorProviderTestUsesConfiguredClassifier()
    {
        using var saveResponse = await _client.PutAsJsonAsync("/api/operator/classification-provider", new
        {
            provider = "Mock",
            model = "",
            endpoint = "",
            authHeaderName = "Authorization",
            apiKey = "",
            clearApiKey = true
        });
        using var testResponse = await _client.PostAsJsonAsync("/api/operator/classification-provider/test", new
        {
            content = "Customer contract includes payment terms.",
            sourceType = "note"
        });
        using var body = await JsonDocument.ParseAsync(await testResponse.Content.ReadAsStreamAsync());

        Assert.Equal(HttpStatusCode.OK, saveResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, testResponse.StatusCode);
        Assert.Equal("Confidential", body.RootElement.GetProperty("classification").GetProperty("sensitivity").GetString());
        Assert.Equal("Mock", body.RootElement.GetProperty("configuration").GetProperty("provider").GetString());
    }

    [Fact]
    public async Task OperatorProviderTestRejectsOversizedContent()
    {
        using var response = await _client.PostAsJsonAsync("/api/operator/classification-provider/test", new
        {
            content = new string('c', 20_001),
            sourceType = "note"
        });
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("content must be 20000 characters or fewer.", body.RootElement.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task GetReadyzReturnsReadyStatusWhenDatabaseCanConnect()
    {
        using var response = await _client.GetAsync("/readyz");
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ready", body.RootElement.GetProperty("status").GetString());
        Assert.Equal("database", body.RootElement.GetProperty("dependency").GetString());
        Assert.Contains(
            body.RootElement.GetProperty("checks").EnumerateArray(),
            check => check.GetProperty("name").GetString() == "classification-provider" &&
                check.GetProperty("status").GetString() == "ready");
    }

    [Fact]
    public async Task GetHealthzStaysLiveWhenProductionDatabaseIsUnavailable()
    {
        using var factory = _factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.UseSetting(
                "ConnectionStrings:LuthnDb",
                "Host=127.0.0.1;Port=1;Database=luthn;Username=luthn;Timeout=1;Command Timeout=1");
            builder.UseSetting("Luthn:Database:EnableRetries", "false");
        });
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/healthz");
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        using var readyResponse = await client.GetAsync("/readyz");
        using var readyBody = await JsonDocument.ParseAsync(await readyResponse.Content.ReadAsStreamAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ok", body.RootElement.GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, readyResponse.StatusCode);
        Assert.Equal("not_ready", readyBody.RootElement.GetProperty("status").GetString());
        Assert.Equal("database", readyBody.RootElement.GetProperty("dependency").GetString());
    }

    [Fact]
    public async Task PostPreviewReturnsClassificationPreview()
    {
        using var response = await _client.PostAsJsonAsync("/api/classification/preview", new
        {
            sourceId = "source-1",
            content = "Customer contract and payment details.",
            sourceType = "note"
        });
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("source-1", body.RootElement.GetProperty("sourceId").GetString());
        Assert.Equal("Confidential", body.RootElement.GetProperty("classification").GetProperty("sensitivity").GetString());
        Assert.Equal("SensitiveDbOnly", body.RootElement.GetProperty("storageDecision").GetProperty("kind").GetString());
    }

    [Fact]
    public async Task PostPreviewPersistsProviderInvocationAudit()
    {
        using var response = await _client.PostAsJsonAsync("/api/classification/preview", new
        {
            sourceId = "source-preview-audit",
            content = "Customer contract and payment details.",
            sourceType = "note"
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LuthnDbContext>();
        var audit = await db.AuditEvents.SingleAsync(record => record.Action == "classification.provider.invoked");
        Assert.Equal("source-preview-audit", audit.SubjectId);
        Assert.Equal("local-classification-input", audit.PayloadClass);
        Assert.Equal("local-only", audit.RedactionState);
    }

    [Fact]
    public async Task PostPreviewWithoutSourceIdReturnsBadRequest()
    {
        using var response = await _client.PostAsJsonAsync("/api/classification/preview", new
        {
            content = "Customer contract and payment details."
        });
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("sourceId is required.", body.RootElement.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task PostPreviewWithoutContentReturnsBadRequest()
    {
        using var response = await _client.PostAsJsonAsync("/api/classification/preview", new
        {
            sourceId = "source-1"
        });
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("content is required.", body.RootElement.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task PostPreviewWithOversizedContentReturnsBadRequestBeforeAudit()
    {
        using var response = await _client.PostAsJsonAsync("/api/classification/preview", new
        {
            sourceId = "source-1",
            content = new string('c', 20_001)
        });
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("content must be 20000 characters or fewer.", body.RootElement.GetProperty("detail").GetString());

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LuthnDbContext>();
        Assert.Empty(await db.AuditEvents.ToArrayAsync());
    }

    [Fact]
    public async Task PostPreviewWithInvalidSourceIdReturnsBadRequestBeforeAudit()
    {
        using var response = await _client.PostAsJsonAsync("/api/classification/preview", new
        {
            sourceId = "source 1",
            content = "Customer contract and payment details."
        });
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("cannot contain whitespace", body.RootElement.GetProperty("detail").GetString());

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LuthnDbContext>();
        Assert.Empty(await db.AuditEvents.ToArrayAsync());
    }

    [Fact]
    public async Task PostPreviewResponseUsesStableJsonContract()
    {
        using var response = await _client.PostAsJsonAsync("/api/classification/preview", new
        {
            sourceId = "source-1",
            content = "Customer contract and payment details."
        });
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        var root = body.RootElement;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(root.TryGetProperty("sourceId", out var sourceId));
        Assert.Equal(JsonValueKind.String, sourceId.ValueKind);
        Assert.True(root.TryGetProperty("classification", out var classification));
        Assert.True(root.TryGetProperty("storageDecision", out var storageDecision));
        Assert.True(classification.TryGetProperty("sensitivity", out var sensitivity));
        Assert.Equal(JsonValueKind.String, sensitivity.ValueKind);
        Assert.True(classification.TryGetProperty("confidence", out var confidence));
        Assert.Equal(JsonValueKind.Number, confidence.ValueKind);
        Assert.True(classification.TryGetProperty("categories", out var categories));
        Assert.Equal(JsonValueKind.Array, categories.ValueKind);
        Assert.True(classification.TryGetProperty("containsSensitiveMaterial", out var containsSensitiveMaterial));
        Assert.True(containsSensitiveMaterial.ValueKind is JsonValueKind.True or JsonValueKind.False);
        Assert.True(storageDecision.TryGetProperty("kind", out var kind));
        Assert.Equal(JsonValueKind.String, kind.ValueKind);
        Assert.True(storageDecision.TryGetProperty("reasons", out var reasons));
        Assert.Equal(JsonValueKind.Array, reasons.ValueKind);
        Assert.True(storageDecision.TryGetProperty("allowsWikiProjection", out var allowsWikiProjection));
        Assert.True(allowsWikiProjection.ValueKind is JsonValueKind.True or JsonValueKind.False);
        Assert.True(storageDecision.TryGetProperty("allowsAgentContext", out var allowsAgentContext));
        Assert.True(allowsAgentContext.ValueKind is JsonValueKind.True or JsonValueKind.False);
        Assert.True(storageDecision.TryGetProperty("requiresHumanReview", out var requiresHumanReview));
        Assert.True(requiresHumanReview.ValueKind is JsonValueKind.True or JsonValueKind.False);
    }

    [Fact]
    public async Task PreviewRoutesSensitiveContentToSensitiveStoreOnly()
    {
        var service = new ClassificationPreviewService(
            new MockContentClassifier(),
            new PolicyEngine());

        var response = await service.PreviewAsync(new ClassificationPreviewRequest(
            "source-1",
            "Customer contract and payment details.",
            "note"));

        Assert.Equal(SensitivityLevel.Confidential, response.Classification.Sensitivity);
        Assert.Equal(StorageDecisionKind.SensitiveDbOnly, response.StorageDecision.Kind);
    }

    [Fact]
    public void MockProviderIsAllowedOnlyWhenNoExternalProviderIsConfigured()
    {
        var options = new ClassificationProviderOptions
        {
            Provider = "mock",
            ExternalHttp = new ExternalHttpClassificationProviderOptions()
        };

        Assert.Equal("mock", options.ResolveProvider());
        Assert.Equal(
            "MockContentClassifier is test and experiment only; production classification requires an external provider.",
            MockContentClassifier.UsageNotice);
    }

    [Fact]
    public void MockProviderIsRejectedWhenExternalProviderIsConfigured()
    {
        var options = new ClassificationProviderOptions
        {
            Provider = "mock",
            ExternalHttp = new ExternalHttpClassificationProviderOptions
            {
                Endpoint = "https://classifier.local/classify"
            }
        };

        var error = Assert.Throws<InvalidOperationException>(() => options.ResolveProvider());

        Assert.Equal(
            "The mock classification provider is test and experiment only. Use 'external-http' when Luthn:Classification:ExternalHttp:Endpoint is configured.",
            error.Message);
    }

    [Fact]
    public async Task ExternalHttpClassifierSendsBoundaryMetadataAndMapsProviderResponse()
    {
        using var handler = new CapturingHandler();
        var classifier = new ExternalHttpContentClassifier(
            new StaticHttpClientFactory(new HttpClient(handler)),
            Options.Create(new ClassificationProviderOptions
            {
                Provider = "external-http",
                ExternalHttp = new ExternalHttpClassificationProviderOptions
                {
                    Endpoint = "https://classifier.local/classify",
                    PayloadClass = "classification-input",
                    RedactionState = "external-provider-opt-in"
                }
            }));

        var result = await classifier.ClassifyAsync(
            new("source-1"),
            "Customer contract summary.",
            "note");

        Assert.Equal(SensitivityLevel.Confidential, result.Sensitivity);
        Assert.Equal(0.92, result.Confidence);
        Assert.Contains("contract", result.Categories);
        Assert.True(result.ContainsSensitiveMaterial);

        Assert.NotNull(handler.RequestJson);
        Assert.Equal("source-1", handler.RequestJson.RootElement.GetProperty("sourceId").GetString());
        Assert.Equal("note", handler.RequestJson.RootElement.GetProperty("sourceType").GetString());
        Assert.Equal("classification-input", handler.RequestJson.RootElement.GetProperty("payloadClass").GetString());
        Assert.Equal("external-provider-opt-in", handler.RequestJson.RootElement.GetProperty("redactionState").GetString());
    }

    [Fact]
    public async Task ExternalHttpClassifierDropsUnsafeProviderCategories()
    {
        using var handler = new CapturingHandler
        {
            Categories = ["contract", "Customer contract raw phrase never persisted.", "payment"]
        };
        var classifier = new ExternalHttpContentClassifier(
            new StaticHttpClientFactory(new HttpClient(handler)),
            Options.Create(new ClassificationProviderOptions
            {
                Provider = "external-http",
                ExternalHttp = new ExternalHttpClassificationProviderOptions
                {
                    Endpoint = "https://classifier.local/classify"
                }
            }));

        var result = await classifier.ClassifyAsync(
            new("source-1"),
            "Customer contract summary.",
            "note");

        Assert.Equal(["contract", "payment"], result.Categories.OrderBy(category => category).ToArray());
    }

    [Fact]
    public async Task ExternalHttpClassifierRejectsUndefinedNumericSensitivity()
    {
        using var handler = new CapturingHandler
        {
            Sensitivity = "999"
        };
        var classifier = new ExternalHttpContentClassifier(
            new StaticHttpClientFactory(new HttpClient(handler)),
            Options.Create(new ClassificationProviderOptions
            {
                Provider = "external-http",
                ExternalHttp = new ExternalHttpClassificationProviderOptions
                {
                    Endpoint = "https://classifier.local/classify"
                }
            }));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => classifier.ClassifyAsync(
            new("source-1"),
            "Customer contract summary.",
            "note").AsTask());

        Assert.Equal("Classification provider returned unsupported sensitivity '999'.", error.Message);
    }

    [Fact]
    public async Task GoogleAiClassifierSendsApiKeyHeaderWithoutQuerySecret()
    {
        using var handler = new GoogleAiCapturingHandler();
        var classifier = new ConfiguredContentClassifier(
            new StaticSettingsStore(new OperatorClassificationProviderSettings
            {
                Provider = OperatorClassificationProviderKind.GoogleAi,
                Model = "gemini-test",
                Endpoint = "https://generativelanguage.googleapis.com/v1beta/models",
                AuthHeaderName = "x-goog-api-key",
                ApiKey = "google-test-secret",
                PayloadClass = "classification-input",
                RedactionState = "operator-configured-provider"
            }),
            new StaticHttpClientFactory(new HttpClient(handler)));

        var result = await classifier.ClassifyAsync(
            new("source-1"),
            "Customer contract summary.",
            "note");

        Assert.Equal(SensitivityLevel.Confidential, result.Sensitivity);
        Assert.Equal("https://generativelanguage.googleapis.com/v1beta/models/gemini-test:generateContent",
            handler.RequestUri?.ToString());
        Assert.DoesNotContain("google-test-secret", handler.RequestUri?.ToString(), StringComparison.Ordinal);
        Assert.Equal("google-test-secret", handler.ApiKeyHeader);
    }

    [Fact]
    public async Task ConfiguredClassifierRetriesTransientProviderFailure()
    {
        using var handler = new TransientFailureHandler();
        var classifier = new ConfiguredContentClassifier(
            new StaticSettingsStore(new OperatorClassificationProviderSettings
            {
                Provider = OperatorClassificationProviderKind.ExternalHttp,
                Endpoint = "https://classifier.local/classify",
                PayloadClass = "classification-input",
                RedactionState = "operator-configured-provider"
            }),
            new StaticHttpClientFactory(new HttpClient(handler)),
            Options.Create(new ClassificationProviderRuntimeOptions
            {
                TimeoutSeconds = 5,
                MaxAttempts = 2,
                RetryDelayMilliseconds = 0
            }),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ConfiguredContentClassifier>.Instance);

        var result = await classifier.ClassifyAsync(
            new("source-1"),
            "Customer contract summary.",
            "note");

        Assert.Equal(2, handler.Attempts);
        Assert.Equal(SensitivityLevel.Confidential, result.Sensitivity);
    }

    [Fact]
    public async Task ConfiguredClassifierAppliesProviderTimeoutWhileReadingBody()
    {
        using var handler = new StalledBodyHandler();
        var classifier = new ConfiguredContentClassifier(
            new StaticSettingsStore(new OperatorClassificationProviderSettings
            {
                Provider = OperatorClassificationProviderKind.ExternalHttp,
                Endpoint = "https://classifier.local/classify",
                PayloadClass = "classification-input",
                RedactionState = "operator-configured-provider"
            }),
            new StaticHttpClientFactory(new HttpClient(handler)),
            Options.Create(new ClassificationProviderRuntimeOptions
            {
                TimeoutSeconds = 1,
                MaxAttempts = 1,
                RetryDelayMilliseconds = 0
            }),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ConfiguredContentClassifier>.Instance);

        var error = await Assert.ThrowsAsync<ClassificationProviderException>(() => classifier.ClassifyAsync(
            new("source-1"),
            "Customer contract summary.",
            "note").AsTask());

        Assert.Equal(1, handler.Attempts);
        Assert.Contains("timed out", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class StaticSettingsStore(
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

    private sealed class StaticHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class CapturingHandler : HttpMessageHandler, IDisposable
    {
        public JsonDocument RequestJson { get; private set; } = null!;
        public string Sensitivity { get; init; } = "Confidential";
        public IReadOnlyList<string> Categories { get; init; } = ["contract"];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://classifier.local/classify", request.RequestUri?.ToString());

            RequestJson = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new
                {
                    sensitivity = Sensitivity,
                    confidence = 0.92,
                    categories = Categories,
                    containsSensitiveMaterial = true
                })
            };
        }
    }

    private sealed class GoogleAiCapturingHandler : HttpMessageHandler, IDisposable
    {
        public Uri? RequestUri { get; private set; }
        public string? ApiKeyHeader { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            RequestUri = request.RequestUri;
            ApiKeyHeader = request.Headers.GetValues("x-goog-api-key").Single();

            var providerResult = JsonSerializer.Serialize(new
            {
                sensitivity = "Confidential",
                confidence = 0.92,
                categories = new[] { "contract" },
                containsSensitiveMaterial = true
            });

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new
                {
                    candidates = new[]
                    {
                        new
                        {
                            content = new
                            {
                                parts = new[]
                                {
                                    new { text = providerResult }
                                }
                            }
                        }
                    }
                })
            });
        }
    }

    private sealed class TransientFailureHandler : HttpMessageHandler, IDisposable
    {
        public int Attempts { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Attempts++;
            if (Attempts == 1)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new
                {
                    sensitivity = "Confidential",
                    confidence = 0.92,
                    categories = new[] { "contract" },
                    containsSensitiveMaterial = true
                })
            });
        }
    }

    private sealed class StalledBodyHandler : HttpMessageHandler, IDisposable
    {
        public int Attempts { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Attempts++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StalledContent()
            });
        }
    }

    private sealed class StalledContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            Task.Delay(TimeSpan.FromSeconds(10));

        protected override async Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context,
            CancellationToken cancellationToken)
        {
            await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = -1;
            return false;
        }
    }
}
