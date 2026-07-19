using Luthn.Core.Classification;
using Luthn.Core.Common;
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
        Assert.Contains("Unconfigured — choose a provider", index, StringComparison.Ordinal);
        Assert.Contains("Mock — development/test only", index, StringComparison.Ordinal);
        Assert.Contains("Self-hosted / external HTTP", index, StringComparison.Ordinal);
        Assert.Contains("Access requests", index, StringComparison.Ordinal);
        Assert.Contains("Agent connections", index, StringComparison.Ordinal);
        Assert.Contains("Read-only agent connection status", index, StringComparison.Ordinal);
        Assert.Contains("External publication", index, StringComparison.Ordinal);
        Assert.DoesNotContain("Connect agent", index, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Disconnect agent", index, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("text/html", indexResponse.Content.Headers.ContentType?.MediaType);
        Assert.Equal(HttpStatusCode.OK, cssResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, jsResponse.StatusCode);
        Assert.Contains("/api/agent-connections", script, StringComparison.Ordinal);
        Assert.Contains("/api/external-publication/status", script, StringComparison.Ordinal);
        Assert.Contains("mockOption.disabled = !settings.mockAllowed", script, StringComparison.Ordinal);
        Assert.Contains("settings.statusDetail", script, StringComparison.Ordinal);
        Assert.DoesNotContain("/observations", script, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TestingProviderConfigurationExplicitlyAllowsMockWithoutExposingApiKey()
    {
        using var response = await _client.GetAsync("/api/operator/classification-provider");
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Mock", body.RootElement.GetProperty("provider").GetString());
        Assert.False(body.RootElement.GetProperty("hasApiKey").GetBoolean());
        Assert.True(body.RootElement.GetProperty("mockAllowed").GetBoolean());
        Assert.Equal("mock-non-production", body.RootElement.GetProperty("status").GetString());
        Assert.Equal("local-non-production", body.RootElement.GetProperty("providerBoundary").GetString());
        Assert.True(body.RootElement.GetProperty("localSensitiveDataGuardActive").GetBoolean());
        Assert.Equal(
            DeterministicSensitiveDataDetector.Version,
            body.RootElement.GetProperty("localSensitiveDataGuardVersion").GetString());
        Assert.Contains(
            "development or test",
            body.RootElement.GetProperty("statusDetail").GetString(),
            StringComparison.Ordinal);
        Assert.False(body.RootElement.TryGetProperty("apiKey", out _));

        using var scope = _factory.Services.CreateScope();
        Assert.IsType<HybridContentClassifier>(scope.ServiceProvider.GetRequiredService<IContentClassifier>());
    }

    [Fact]
    public async Task ReadinessReportsLocalGuardWithoutSensitiveEvidence()
    {
        using var response = await _client.GetAsync("/readyz");
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var guard = body.RootElement.GetProperty("checks")
            .EnumerateArray()
            .Single(check => check.GetProperty("name").GetString() == "classification-guard");
        Assert.Equal("ready", guard.GetProperty("status").GetString());
        Assert.Equal(
            $"Local secret/PII guard version {DeterministicSensitiveDataDetector.Version} is active.",
            guard.GetProperty("detail").GetString());
        Assert.False(body.RootElement.ToString().Contains("matched", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RuntimeGuardOverridesMockPublicFalseNegativeBeforePolicyRouting()
    {
        const string submittedValue = "010-1234-5678";
        using var response = await _client.PostAsJsonAsync("/api/classification/preview", new
        {
            sourceId = "guarded-preview-source",
            content = $"연락처 {submittedValue}",
            sourceType = "note"
        });
        var responseJson = await response.Content.ReadAsStringAsync();
        using var body = JsonDocument.Parse(responseJson);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            "Confidential",
            body.RootElement.GetProperty("classification").GetProperty("sensitivity").GetString());
        Assert.Contains(
            body.RootElement.GetProperty("classification").GetProperty("categories").EnumerateArray(),
            category => category.GetString() == "personal identifier");
        Assert.True(body.RootElement.GetProperty("classification").GetProperty("containsSensitiveMaterial").GetBoolean());
        Assert.Equal(
            "SensitiveDbOnly",
            body.RootElement.GetProperty("storageDecision").GetProperty("kind").GetString());
        Assert.False(body.RootElement.GetProperty("storageDecision").GetProperty("allowsAgentContext").GetBoolean());
        Assert.DoesNotContain(submittedValue, responseJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnconfiguredProviderIsNotReadyAndClassificationFailsWithoutEchoingContent()
    {
        const string submittedContent = "raw-content-must-not-be-echoed";
        using var factory = _factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Luthn:TestingDatabaseName", Guid.NewGuid().ToString("N"));
            builder.UseSetting(
                "Luthn:OperatorConfig:Directory",
                Path.Combine(Path.GetTempPath(), "luthn-operator-tests", Guid.NewGuid().ToString("N")));
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Luthn:Classification:Provider"] = "unconfigured",
                    ["Luthn:Classification:AllowMock"] = "false"
                }));
        });
        using var client = factory.CreateClient();

        using var configurationResponse = await client.GetAsync("/api/operator/classification-provider");
        using var configurationBody = await JsonDocument.ParseAsync(
            await configurationResponse.Content.ReadAsStreamAsync());
        using var readinessResponse = await client.GetAsync("/readyz");
        using var readinessBody = await JsonDocument.ParseAsync(await readinessResponse.Content.ReadAsStreamAsync());
        using var previewResponse = await client.PostAsJsonAsync("/api/classification/preview", new
        {
            sourceId = "unconfigured-source",
            content = submittedContent,
            sourceType = "note"
        });
        var previewBody = await previewResponse.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, configurationResponse.StatusCode);
        Assert.Equal("Unconfigured", configurationBody.RootElement.GetProperty("provider").GetString());
        Assert.False(configurationBody.RootElement.GetProperty("mockAllowed").GetBoolean());
        Assert.Equal("unconfigured", configurationBody.RootElement.GetProperty("status").GetString());
        Assert.Equal(
            ClassificationProviderOptions.ProviderRequiredMessage,
            configurationBody.RootElement.GetProperty("statusDetail").GetString());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, readinessResponse.StatusCode);
        Assert.Equal("classification-provider", readinessBody.RootElement.GetProperty("dependency").GetString());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, previewResponse.StatusCode);
        Assert.Contains("No classification provider is configured", previewBody, StringComparison.Ordinal);
        Assert.DoesNotContain(submittedContent, previewBody, StringComparison.Ordinal);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LuthnDbContext>();
        Assert.Empty(await db.ClassificationResults.ToListAsync());
        Assert.Empty(await db.SourceEvents.ToListAsync());
        var audit = Assert.Single(await db.AuditEvents.ToListAsync());
        Assert.Equal("classification.provider.invoked", audit.Action);
        Assert.Equal("provider-unconfigured", audit.RedactionState);
    }

    [Fact]
    public async Task DisallowedMockIsNotReadyAndClassificationFailsWithoutPersistingContent()
    {
        const string submittedContent = "disabled-mock-content-must-not-be-echoed";
        using var factory = _factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Luthn:TestingDatabaseName", Guid.NewGuid().ToString("N"));
            builder.UseSetting(
                "Luthn:OperatorConfig:Directory",
                Path.Combine(Path.GetTempPath(), "luthn-operator-tests", Guid.NewGuid().ToString("N")));
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Luthn:Classification:Provider"] = "mock",
                    ["Luthn:Classification:AllowMock"] = "false"
                }));
        });
        using var client = factory.CreateClient();

        using var configurationResponse = await client.GetAsync("/api/operator/classification-provider");
        using var configurationBody = await JsonDocument.ParseAsync(
            await configurationResponse.Content.ReadAsStreamAsync());
        using var readinessResponse = await client.GetAsync("/readyz");
        using var readinessBody = await JsonDocument.ParseAsync(await readinessResponse.Content.ReadAsStreamAsync());
        using var previewResponse = await client.PostAsJsonAsync("/api/classification/preview", new
        {
            sourceId = "disabled-mock-source",
            content = submittedContent,
            sourceType = "note"
        });
        var previewBody = await previewResponse.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, configurationResponse.StatusCode);
        Assert.Equal("Mock", configurationBody.RootElement.GetProperty("provider").GetString());
        Assert.Equal("mock-disabled", configurationBody.RootElement.GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, readinessResponse.StatusCode);
        Assert.Equal("classification-provider", readinessBody.RootElement.GetProperty("dependency").GetString());
        Assert.Contains(ClassificationProviderOptions.MockDisabledMessage, readinessBody.RootElement.ToString(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, previewResponse.StatusCode);
        Assert.Contains(ClassificationProviderOptions.MockDisabledMessage, previewBody, StringComparison.Ordinal);
        Assert.DoesNotContain(submittedContent, previewBody, StringComparison.Ordinal);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LuthnDbContext>();
        Assert.Empty(await db.ClassificationResults.ToListAsync());
        Assert.Empty(await db.SourceEvents.ToListAsync());
        var audit = Assert.Single(await db.AuditEvents.ToListAsync());
        Assert.Equal("classification.provider.invoked", audit.Action);
        Assert.Equal("mock-disabled", audit.RedactionState);
    }

    [Fact]
    public async Task OperatorProviderConfigurationRejectsMockWithoutExplicitOptIn()
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

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveAsync(
            new SaveClassificationProviderConfigurationRequest(
                "Mock",
                null,
                null,
                null,
                null,
                ClearApiKey: true)).AsTask());

        Assert.Equal(ClassificationProviderOptions.MockDisabledMessage, error.Message);
        Assert.False(File.Exists(Path.Combine(directory, "classification-provider.json")));
    }

    [Fact]
    public async Task OperatorProviderEndpointRejectsMockWithoutExplicitOptIn()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "luthn-operator-tests",
            Guid.NewGuid().ToString("N"));
        using var factory = _factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Luthn:TestingDatabaseName", Guid.NewGuid().ToString("N"));
            builder.UseSetting("Luthn:OperatorConfig:Directory", directory);
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Luthn:Classification:Provider"] = "unconfigured",
                    ["Luthn:Classification:AllowMock"] = "false"
                }));
        });
        using var client = factory.CreateClient();

        using var response = await client.PutAsJsonAsync("/api/operator/classification-provider", new
        {
            provider = "Mock",
            model = "",
            endpoint = "",
            authHeaderName = "Authorization",
            apiKey = "",
            clearApiKey = true
        });
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            ClassificationProviderOptions.MockDisabledMessage,
            body.RootElement.GetProperty("detail").GetString());
        Assert.False(File.Exists(Path.Combine(directory, "classification-provider.json")));
    }

    [Fact]
    public async Task PersistedMockIsBlockedAfterUpgradeAndCanBeReconfigured()
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
                provider = "Mock",
                model = "",
                endpoint = "",
                authHeaderName = "Authorization",
                protectedApiKey = "",
                payloadClass = "local-classification-input",
                redactionState = "local-only"
            }));
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Luthn:Classification:Provider"] = "unconfigured",
                ["Luthn:Classification:AllowMock"] = "false"
            })
            .Build();
        var store = new OperatorClassificationSettingsStore(
            Options.Create(new OperatorConfigOptions { Directory = directory }),
            Microsoft.AspNetCore.DataProtection.DataProtectionProvider.Create(
                new DirectoryInfo(Path.Combine(directory, "keys"))),
            configuration);
        var classifier = new ConfiguredContentClassifier(
            store,
            new StaticHttpClientFactory(new HttpClient()),
            Options.Create(new ClassificationProviderRuntimeOptions()),
            Options.Create(new ClassificationProviderOptions()),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ConfiguredContentClassifier>.Instance);

        Assert.Equal(OperatorClassificationProviderKind.Mock, store.Current.Provider);
        var error = await Assert.ThrowsAsync<ClassificationProviderException>(() => classifier.ClassifyAsync(
            new("upgrade-mock-source"),
            "content that must not be classified by mock",
            "note").AsTask());
        var replacement = await store.SaveAsync(new SaveClassificationProviderConfigurationRequest(
            "ExternalHttp",
            "",
            "http://127.0.0.1:5099/classify",
            "Authorization",
            null,
            ClearApiKey: true));

        Assert.Equal(ClassificationProviderOptions.MockDisabledMessage, error.Message);
        Assert.Equal(OperatorClassificationProviderKind.ExternalHttp, replacement.Provider);
        Assert.Equal(OperatorClassificationProviderKind.ExternalHttp, (await store.ReadAsync()).Provider);
    }

    [Fact]
    public async Task PersistedMockConfigurationReportsDisabledOperatorStatus()
    {
        var response = await OperatorConfigurationEndpoints.ReadClassificationProvider(
            new StaticSettingsStore(new OperatorClassificationProviderSettings
            {
                Provider = OperatorClassificationProviderKind.Mock,
                PayloadClass = "local-classification-input",
                RedactionState = "local-only"
            }),
            Options.Create(new ClassificationProviderOptions { AllowMock = false }),
            CancellationToken.None);

        Assert.NotNull(response.Value);
        Assert.Equal("Mock", response.Value.Provider);
        Assert.False(response.Value.MockAllowed);
        Assert.Equal("mock-disabled", response.Value.Status);
        Assert.Equal(ClassificationProviderOptions.MockDisabledMessage, response.Value.StatusDetail);
        Assert.True(response.Value.LocalSensitiveDataGuardActive);
        Assert.Equal(DeterministicSensitiveDataDetector.Version, response.Value.LocalSensitiveDataGuardVersion);
    }

    [Fact]
    public async Task ExternalHttpConfigurationReportsSelfHostedCapableGuardedBoundary()
    {
        var response = await OperatorConfigurationEndpoints.ReadClassificationProvider(
            new StaticSettingsStore(new OperatorClassificationProviderSettings
            {
                Provider = OperatorClassificationProviderKind.ExternalHttp,
                Endpoint = "http://127.0.0.1:5099/classify",
                PayloadClass = "classification-input",
                RedactionState = "operator-configured-provider"
            }),
            Options.Create(new ClassificationProviderOptions()),
            CancellationToken.None);

        Assert.NotNull(response.Value);
        Assert.Equal("self-hosted-capable-external-http", response.Value.ProviderBoundary);
        Assert.True(response.Value.LocalSensitiveDataGuardActive);
        Assert.Contains("self-hosted", response.Value.StatusDetail, StringComparison.OrdinalIgnoreCase);
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
        Assert.Equal("configured", body.RootElement.GetProperty("status").GetString());
        Assert.DoesNotContain("sk-or-test-secret", responseJson, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        Assert.True(getBody.RootElement.GetProperty("hasApiKey").GetBoolean());
        Assert.Equal("configured", getBody.RootElement.GetProperty("status").GetString());
        Assert.DoesNotContain("sk-or-test-secret", getJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OperatorProviderConfigurationClearsSavedKeyWhenExplicitlySwitchingToMock()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "luthn-operator-tests",
            Guid.NewGuid().ToString("N"));
        var store = new OperatorClassificationSettingsStore(
            Options.Create(new OperatorConfigOptions { Directory = directory }),
            Microsoft.AspNetCore.DataProtection.DataProtectionProvider.Create(
                new DirectoryInfo(Path.Combine(directory, "keys"))),
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Luthn:Classification:Provider"] = "mock",
                    ["Luthn:Classification:AllowMock"] = "true"
                })
                .Build());

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
    public void MockProviderRequiresExplicitOptInAndNoExternalProvider()
    {
        var options = new ClassificationProviderOptions
        {
            Provider = "mock",
            AllowMock = true,
            ExternalHttp = new ExternalHttpClassificationProviderOptions()
        };

        Assert.Equal("mock", options.ResolveProvider());
        options.EnsureMockAllowed();
        Assert.Equal(
            "MockContentClassifier is test and experiment only; production classification requires an external provider.",
            MockContentClassifier.UsageNotice);
    }

    [Fact]
    public void ClassificationProviderDefaultsToUnconfiguredAndRejectsImplicitMock()
    {
        var options = new ClassificationProviderOptions();

        Assert.Equal(ClassificationProviderOptions.UnconfiguredProvider, options.ResolveProvider());
        var error = Assert.Throws<InvalidOperationException>(() => options.EnsureMockAllowed());
        Assert.Equal(ClassificationProviderOptions.MockDisabledMessage, error.Message);
    }

    [Theory]
    [InlineData("unconfigured", ClassificationProviderOptions.ProviderRequiredMessage)]
    [InlineData("mock", ClassificationProviderOptions.MockDisabledMessage)]
    public async Task ServiceCollectionKeepsRuntimeAvailableWhileProviderIsBlocked(
        string providerName,
        string expectedMessage)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Luthn:Classification:Provider"] = providerName,
                ["Luthn:Classification:AllowMock"] = "false"
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLuthnClassification(configuration);
        using var serviceProvider = services.BuildServiceProvider();
        var classifier = serviceProvider.GetRequiredService<IContentClassifier>();

        var error = await Assert.ThrowsAsync<ClassificationProviderException>(() => classifier.ClassifyAsync(
            new PublicRecordId("blocked-provider"),
            "content must not be classified",
            "note").AsTask());

        Assert.Equal(expectedMessage, error.Message);
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
    public async Task ExternalHttpClassifierNormalizesContradictoryProviderFieldsConservatively()
    {
        using var handler = new CapturingHandler
        {
            Sensitivity = "Public",
            Categories = ["Private Key"],
            ContainsSensitiveMaterial = false
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
            new("source-contradictory"),
            "Provider response intentionally contradicts itself.",
            "note");

        Assert.Equal(SensitivityLevel.Restricted, result.Sensitivity);
        Assert.True(result.ContainsSensitiveMaterial);
        Assert.Equal(["private key"], result.Categories);
        Assert.Equal(StorageDecisionKind.SensitiveDbOnly, new PolicyEngine().Decide(result).Kind);
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
            Options.Create(new ClassificationProviderOptions { Provider = "external-http" }),
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
            Options.Create(new ClassificationProviderOptions { Provider = "external-http" }),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ConfiguredContentClassifier>.Instance);

        var error = await Assert.ThrowsAsync<ClassificationProviderException>(() => classifier.ClassifyAsync(
            new("source-1"),
            "Customer contract summary.",
            "note").AsTask());

        Assert.Equal(1, handler.Attempts);
        Assert.Contains("timed out", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProviderAttemptRecordsCallerCancellation()
    {
        using var handler = new CancelableHandler();
        var metrics = new RecordingOperationalMetrics();
        using var cancellationSource = new CancellationTokenSource();
        var request = ClassificationProviderHttp.SendAsync(
            new StaticHttpClientFactory(new HttpClient(handler)),
            "test",
            () => new HttpRequestMessage(HttpMethod.Get, "https://classifier.local/classify"),
            new ClassificationProviderRuntimeOptions { MaxAttempts = 1 },
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
            "ExternalHttp",
            metrics,
            cancellationSource.Token);

        await handler.Started;
        cancellationSource.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        var recorded = Assert.Single(metrics.ProviderRequests);
        Assert.Equal("canceled", recorded.Outcome);
    }

    [Fact]
    public async Task ProviderAttemptRecordsRetryBeforeNonzeroBackoff()
    {
        using var handler = new TransientFailureHandler();
        var metrics = new RecordingOperationalMetrics();
        var request = ClassificationProviderHttp.SendAsync(
            new StaticHttpClientFactory(new HttpClient(handler)),
            "test",
            () => new HttpRequestMessage(HttpMethod.Get, "https://classifier.local/classify"),
            new ClassificationProviderRuntimeOptions
            {
                MaxAttempts = 2,
                RetryDelayMilliseconds = 250
            },
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
            "ExternalHttp",
            metrics,
            CancellationToken.None);

        var firstRecorded = await metrics.FirstProviderRequest;
        Assert.Equal("retry", firstRecorded.Outcome);
        Assert.False(request.IsCompleted);

        using var response = await request;
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["retry", "succeeded"], metrics.ProviderRequests.Select(item => item.Outcome));
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
        public bool ContainsSensitiveMaterial { get; init; } = true;

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
                    containsSensitiveMaterial = ContainsSensitiveMaterial
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

    private sealed class CancelableHandler : HttpMessageHandler, IDisposable
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Started => _started.Task;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            _started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Cancellation was not observed.");
        }
    }

    private sealed class RecordingOperationalMetrics : IOperationalMetrics
    {
        private readonly TaskCompletionSource<ProviderRequest> _firstProviderRequest =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<ProviderRequest> ProviderRequests { get; } = [];
        public Task<ProviderRequest> FirstProviderRequest => _firstProviderRequest.Task;

        public void RecordClassificationProviderRequest(string provider, string outcome, TimeSpan duration)
        {
            var request = new ProviderRequest(outcome, duration);
            ProviderRequests.Add(request);
            _firstProviderRequest.TrySetResult(request);
        }

        public void RecordSensitiveAccessRequest() { }
        public void RecordSensitiveAccessDecision(string outcome) { }
        public void RecordSafeSearchCandidates(string source, int count) { }
        public void RecordSearchRequest(string surface, string outcome, string cacheStatus, TimeSpan duration, int resultCount) { }
        public void RecordSearchFeedback(string judgment) { }
        public OperationalMetricsSnapshot Snapshot() => OperationalMetricsSnapshot.Empty;
    }

    private sealed record ProviderRequest(string Outcome, TimeSpan Duration);

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
