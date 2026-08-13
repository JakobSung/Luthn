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
        Assert.Contains("LocalDeterministic — local default", index, StringComparison.Ordinal);
        Assert.Contains("LocalHttp — same-device endpoint", index, StringComparison.Ordinal);
        Assert.Contains("host.docker.internal", index, StringComparison.Ordinal);
        Assert.Contains("Access requests", index, StringComparison.Ordinal);
        Assert.Contains("Request review", index, StringComparison.Ordinal);
        Assert.Contains("Protected content and credentials are never loaded", index, StringComparison.Ordinal);
        Assert.Contains("Audit center", index, StringComparison.Ordinal);
        Assert.Contains("Sensitive access", index, StringComparison.Ordinal);
        Assert.Contains("Classification failures", index, StringComparison.Ordinal);
        Assert.Contains("Configuration changes", index, StringComparison.Ordinal);
        Assert.Contains("They never provide protected content", index, StringComparison.Ordinal);
        Assert.Contains("Agent connections", index, StringComparison.Ordinal);
        Assert.Contains("Read-only agent connection status", index, StringComparison.Ordinal);
        Assert.Contains("<th scope=\"col\">Owner</th>", index, StringComparison.Ordinal);
        Assert.Contains("External publication", index, StringComparison.Ordinal);
        Assert.DoesNotContain("Connect agent", index, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Disconnect agent", index, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("text/html", indexResponse.Content.Headers.ContentType?.MediaType);
        Assert.Equal(HttpStatusCode.OK, cssResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, jsResponse.StatusCode);
        Assert.Contains("/api/agent-connections", script, StringComparison.Ordinal);
        Assert.Contains("connection?.ownerUserId", script, StringComparison.Ordinal);
        Assert.Contains("/api/external-publication/status", script, StringComparison.Ordinal);
        Assert.Contains("settings.provider !== \"LocalHttp\"", script, StringComparison.Ordinal);
        Assert.Contains("settings.statusDetail", script, StringComparison.Ordinal);
        Assert.DoesNotContain("name=\"apiKey\"", index, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("form.get(\"apiKey\")", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Luthn__Classification__Credential", index, StringComparison.Ordinal);
        Assert.DoesNotContain("OpenRouter", index, StringComparison.Ordinal);
        Assert.DoesNotContain("ExternalHttp", index, StringComparison.Ordinal);
        Assert.Contains("/operator-detail", script, StringComparison.Ordinal);
        Assert.Contains("sanitizeAccessDetail", script, StringComparison.Ordinal);
        Assert.Contains("viewSelectedAccessAudit", script, StringComparison.Ordinal);
        Assert.Contains("applyAuditPreset", script, StringComparison.Ordinal);
        Assert.Contains("clearAccessDetail(\"Refreshing access requests...\")", script, StringComparison.Ordinal);
        Assert.Contains("requests.some((request) => request.id === previousSelectedId)", script, StringComparison.Ordinal);
        Assert.Contains("X-Luthn-CSRF", script, StringComparison.Ordinal);
        Assert.DoesNotContain("sessionStorage", script, StringComparison.Ordinal);
        Assert.DoesNotContain("request.requestedBy", script, StringComparison.Ordinal);
        Assert.DoesNotContain("request.workspaceId", script, StringComparison.Ordinal);
        Assert.DoesNotContain("detail?.requestedBy", script, StringComparison.Ordinal);
        Assert.DoesNotContain("detail?.workspaceId", script, StringComparison.Ordinal);
        Assert.DoesNotContain("detail?.sessionId", script, StringComparison.Ordinal);
        Assert.DoesNotContain("event.workspaceId", script, StringComparison.Ordinal);
        Assert.DoesNotContain("event.actorUserId", script, StringComparison.Ordinal);
        Assert.DoesNotContain("/observations", script, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductionLocalHttpClientDisablesAutomaticRedirects()
    {
        var handlerFactory = _factory.Services.GetRequiredService<IHttpMessageHandlerFactory>();
        HttpMessageHandler handler = handlerFactory.CreateHandler(ConfiguredContentClassifier.HttpClientName);
        while (handler is DelegatingHandler delegatingHandler)
        {
            handler = delegatingHandler.InnerHandler!;
        }

        var primaryHandler = Assert.IsType<HttpClientHandler>(handler);
        Assert.False(primaryHandler.AllowAutoRedirect);
        Assert.False(primaryHandler.UseProxy);
    }

    [Fact]
    public async Task DefaultLocalDeterministicProviderIsReady()
    {
        using var response = await _client.GetAsync("/api/operator/classification-provider");
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("LocalDeterministic", body.RootElement.GetProperty("provider").GetString());
        Assert.Equal("local-deterministic-ready", body.RootElement.GetProperty("status").GetString());
        Assert.Equal("local-only-deterministic", body.RootElement.GetProperty("providerBoundary").GetString());
        Assert.True(body.RootElement.GetProperty("localSensitiveDataGuardActive").GetBoolean());
        Assert.Equal(
            DeterministicSensitiveDataDetector.Version,
            body.RootElement.GetProperty("localSensitiveDataGuardVersion").GetString());
        Assert.Contains(
            "LocalDeterministic classification is ready",
            body.RootElement.GetProperty("statusDetail").GetString(),
            StringComparison.Ordinal);
        Assert.False(body.RootElement.TryGetProperty("apiKey", out _));
        Assert.False(body.RootElement.TryGetProperty("hasApiKey", out _));
        Assert.False(body.RootElement.TryGetProperty("mockAllowed", out _));

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
    public async Task RuntimeGuardOverridesProviderPublicFalseNegativeBeforePolicyRouting()
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
                    ["Luthn:Classification:Provider"] = "Unconfigured"
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
        Assert.False(configurationBody.RootElement.TryGetProperty("mockAllowed", out _));
        Assert.Equal("unconfigured", configurationBody.RootElement.GetProperty("status").GetString());
        Assert.Equal(
            ClassificationProviderOptions.ProviderRequiredMessage,
            configurationBody.RootElement.GetProperty("statusDetail").GetString());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, readinessResponse.StatusCode);
        Assert.Equal("classification-provider", readinessBody.RootElement.GetProperty("dependency").GetString());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, previewResponse.StatusCode);
        Assert.Contains(ClassificationProviderOptions.ProviderRequiredMessage, previewBody, StringComparison.Ordinal);
        Assert.DoesNotContain(submittedContent, previewBody, StringComparison.Ordinal);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LuthnDbContext>();
        Assert.Empty(await db.ClassificationResults.ToListAsync());
        Assert.Empty(await db.SourceEvents.ToListAsync());
        var audits = await db.AuditEvents.ToListAsync();
        Assert.Equal(2, audits.Count);
        var invokedAudit = Assert.Single(audits, audit => audit.Action == "classification.provider.invoked");
        Assert.Equal("started", invokedAudit.Outcome);
        Assert.Equal("provider-unconfigured", invokedAudit.RedactionState);
        var failedAudit = Assert.Single(audits, audit => audit.Action == "classification.provider.failed");
        Assert.Equal("failed", failedAudit.Outcome);
        Assert.Equal("provider-unconfigured", failedAudit.RedactionState);
    }

    [Theory]
    [InlineData("Mock")]
    [InlineData("ExternalHttp")]
    [InlineData("OpenAi")]
    [InlineData("OpenRouter")]
    [InlineData("Anthropic")]
    [InlineData("GoogleAi")]
    public async Task LegacyRuntimeProviderIsUnconfiguredAndClassificationFailsWithoutPersistingContent(
        string legacyProvider)
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
                    ["Luthn:Classification:Provider"] = legacyProvider,
                    ["Luthn:Classification:Credential"] = "legacy-runtime-secret",
                    ["Luthn:Classification:ExternalHttp:Endpoint"] = "https://provider.example/classify"
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
        Assert.Equal("Unconfigured", configurationBody.RootElement.GetProperty("provider").GetString());
        Assert.Equal("", configurationBody.RootElement.GetProperty("model").GetString());
        Assert.Equal("", configurationBody.RootElement.GetProperty("endpoint").GetString());
        Assert.Equal("", configurationBody.RootElement.GetProperty("authHeaderName").GetString());
        Assert.Equal("unconfigured", configurationBody.RootElement.GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, readinessResponse.StatusCode);
        Assert.Equal("classification-provider", readinessBody.RootElement.GetProperty("dependency").GetString());
        Assert.Contains(ClassificationProviderOptions.ProviderRequiredMessage, readinessBody.RootElement.ToString(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, previewResponse.StatusCode);
        Assert.Contains(ClassificationProviderOptions.ProviderRequiredMessage, previewBody, StringComparison.Ordinal);
        Assert.DoesNotContain(submittedContent, previewBody, StringComparison.Ordinal);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LuthnDbContext>();
        Assert.Empty(await db.ClassificationResults.ToListAsync());
        Assert.Empty(await db.SourceEvents.ToListAsync());
        var audits = await db.AuditEvents.ToListAsync();
        Assert.Equal(2, audits.Count);
        var invokedAudit = Assert.Single(audits, audit => audit.Action == "classification.provider.invoked");
        Assert.Equal("started", invokedAudit.Outcome);
        Assert.Equal("provider-unconfigured", invokedAudit.RedactionState);
        var failedAudit = Assert.Single(audits, audit => audit.Action == "classification.provider.failed");
        Assert.Equal("failed", failedAudit.Outcome);
        Assert.Equal("provider-unconfigured", failedAudit.RedactionState);
    }

    [Fact]
    public async Task OperatorProviderConfigurationRejectsLegacyMock()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "luthn-operator-tests",
            Guid.NewGuid().ToString("N"));
        var store = new OperatorClassificationSettingsStore(
            Options.Create(new OperatorConfigOptions { Directory = directory }),
            new ConfigurationBuilder().Build());

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveAsync(
            new SaveClassificationProviderConfigurationRequest(
                "Mock",
                null)).AsTask());

        Assert.Contains("Unsupported classification provider 'Mock'", error.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(directory, "classification-provider.json")));
    }

    [Fact]
    public async Task OperatorProviderEndpointRejectsLegacyMock()
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
                    ["Luthn:Classification:Provider"] = "Unconfigured"
                }));
        });
        using var client = factory.CreateClient();

        using var response = await client.PutAsJsonAsync("/api/operator/classification-provider", new
        {
            provider = "Mock",
            endpoint = ""
        });
        var responseBody = await response.Content.ReadAsStringAsync();

        Assert.True(
            response.StatusCode == HttpStatusCode.BadRequest,
            $"Expected BadRequest but received {(int)response.StatusCode} with body: {responseBody}");
        using var body = JsonDocument.Parse(responseBody);
        Assert.Equal(
            "Unsupported classification provider 'Mock'. Choose LocalDeterministic or LocalHttp.",
            body.RootElement.GetProperty("detail").GetString());
        Assert.False(File.Exists(Path.Combine(directory, "classification-provider.json")));
    }

    [Theory]
    [InlineData("Mock")]
    [InlineData("ExternalHttp")]
    [InlineData("OpenAi")]
    [InlineData("OpenRouter")]
    [InlineData("Anthropic")]
    [InlineData("GoogleAi")]
    public async Task PersistedLegacyProviderIsMigratedWithoutDecryptingCredential(string legacyProvider)
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
                provider = legacyProvider,
                model = "legacy-model",
                endpoint = "https://provider.example/classify",
                authHeaderName = "Authorization",
                protectedApiKey = "not-a-valid-protected-key",
                payloadClass = "local-classification-input",
                redactionState = "local-only"
            }));
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Luthn:Classification:Provider"] = "Unconfigured"
            })
            .Build();
        var store = new OperatorClassificationSettingsStore(
            Options.Create(new OperatorConfigOptions { Directory = directory }),
            configuration);
        var migrated = await store.ReadAsync();
        var persisted = await File.ReadAllTextAsync(Path.Combine(directory, "classification-provider.json"));
        var replacement = await store.SaveAsync(new SaveClassificationProviderConfigurationRequest(
            "LocalHttp",
            "http://127.0.0.1:5099/classify"));

        Assert.Equal(OperatorClassificationProviderKind.Unconfigured, migrated.Provider);
        Assert.Equal("", migrated.Endpoint);
        Assert.Equal("", migrated.Model);
        Assert.Equal("", migrated.AuthHeaderName);
        Assert.False(migrated.HasApiKey);
        Assert.Contains("\"provider\": \"Unconfigured\"", persisted, StringComparison.Ordinal);
        Assert.DoesNotContain("not-a-valid-protected-key", persisted, StringComparison.Ordinal);
        Assert.Equal(OperatorClassificationProviderKind.LocalHttp, replacement.Provider);
        Assert.Equal(OperatorClassificationProviderKind.LocalHttp, (await store.ReadAsync()).Provider);
    }

    [Fact]
    public async Task PersistedRemoteLocalHttpIsMigratedToClearedUnconfigured()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "luthn-operator-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "classification-provider.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
        {
            provider = "LocalHttp",
            model = "legacy-model",
            endpoint = "http://192.168.1.20/classify",
            authHeaderName = "Authorization",
            protectedApiKey = "not-a-valid-protected-key"
        }));
        var store = new OperatorClassificationSettingsStore(
            Options.Create(new OperatorConfigOptions { Directory = directory }),
            new ConfigurationBuilder().Build());

        var settings = await store.ReadAsync();
        var persisted = await File.ReadAllTextAsync(path);

        Assert.Equal(OperatorClassificationProviderKind.Unconfigured, settings.Provider);
        Assert.Equal("", settings.Endpoint);
        Assert.Equal("", settings.Model);
        Assert.Equal("", settings.AuthHeaderName);
        Assert.False(settings.HasApiKey);
        Assert.Contains("\"provider\": \"Unconfigured\"", persisted, StringComparison.Ordinal);
        Assert.DoesNotContain("192.168.1.20", persisted, StringComparison.Ordinal);
        Assert.DoesNotContain("legacy-model", persisted, StringComparison.Ordinal);
        Assert.DoesNotContain("Authorization", persisted, StringComparison.Ordinal);
        Assert.DoesNotContain("not-a-valid-protected-key", persisted, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LocalHttpConfigurationReportsSameDeviceGuardedBoundary()
    {
        var response = await OperatorConfigurationEndpoints.ReadClassificationProvider(
            new StaticSettingsStore(new OperatorClassificationProviderSettings
            {
                Provider = OperatorClassificationProviderKind.LocalHttp,
                Endpoint = "http://127.0.0.1:5099/classify",
                PayloadClass = "classification-input",
                RedactionState = "same-device-local-http"
            }),
            CancellationToken.None);

        Assert.NotNull(response.Value);
        Assert.Equal("LocalHttp", response.Value.Provider);
        Assert.Equal("local-http-ready", response.Value.Status);
        Assert.Equal("same-device-local-http", response.Value.ProviderBoundary);
        Assert.Contains("same-device", response.Value.StatusDetail, StringComparison.OrdinalIgnoreCase);
        Assert.True(response.Value.LocalSensitiveDataGuardActive);
        Assert.Equal(DeterministicSensitiveDataDetector.Version, response.Value.LocalSensitiveDataGuardVersion);
    }

    [Theory]
    [InlineData("http://localhost:11434/classify")]
    [InlineData("https://127.0.0.1:11434/classify")]
    [InlineData("http://127.12.34.56:11434/classify")]
    [InlineData("http://[::1]:11434/classify")]
    [InlineData("http://host.docker.internal:11434/classify")]
    public async Task LocalHttpConfigurationAcceptsOnlyDesignedSameDeviceEndpoints(string endpoint)
    {
        var store = CreateSettingsStore();

        var saved = await store.SaveAsync(new SaveClassificationProviderConfigurationRequest(
            "LocalHttp",
            endpoint));

        Assert.Equal(OperatorClassificationProviderKind.LocalHttp, saved.Provider);
        Assert.Equal(new Uri(endpoint).AbsoluteUri, saved.Endpoint);
        Assert.Equal("", saved.Model);
        Assert.Equal("", saved.AuthHeaderName);
        Assert.False(saved.HasApiKey);
    }

    [Theory]
    [InlineData("http://classifier.local/classify")]
    [InlineData("http://192.168.1.20/classify")]
    [InlineData("http://10.0.0.20/classify")]
    [InlineData("http://172.16.0.20/classify")]
    [InlineData("https://8.8.8.8/classify")]
    [InlineData("http://user@localhost:11434/classify")]
    [InlineData("ftp://localhost/classify")]
    [InlineData("/classify")]
    public async Task LocalHttpConfigurationRejectsRemoteUserInfoAndNonHttpEndpoints(string endpoint)
    {
        var store = CreateSettingsStore();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveAsync(
            new SaveClassificationProviderConfigurationRequest("LocalHttp", endpoint)).AsTask());

        Assert.Equal(LocalHttpEndpointValidator.ValidationMessage, error.Message);
    }

    [Fact]
    public async Task RemoteRuntimeLocalHttpBecomesClearedUnconfiguredWithoutCredentialUse()
    {
        var store = CreateSettingsStore(new Dictionary<string, string?>
        {
            ["Luthn:Classification:Provider"] = "LocalHttp",
            ["Luthn:Classification:LocalHttp:Endpoint"] = "http://192.168.1.20/classify",
            ["Luthn:Classification:Credential"] = "legacy-runtime-secret",
            ["Luthn:Classification:Model"] = "legacy-model",
            ["Luthn:Classification:AuthHeaderName"] = "X-Legacy"
        });

        var settings = await store.ReadAsync();

        Assert.Equal(OperatorClassificationProviderKind.Unconfigured, settings.Provider);
        Assert.Equal("", settings.Endpoint);
        Assert.Equal("", settings.Model);
        Assert.Equal("", settings.AuthHeaderName);
        Assert.False(settings.HasApiKey);
    }

    [Fact]
    public async Task StoredLocalHttpClearsLegacyModelCredentialAndAuthFields()
    {
        var directory = Path.Combine(Path.GetTempPath(), "luthn-operator-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "classification-provider.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
        {
            provider = "LocalHttp",
            model = "legacy-model",
            endpoint = "http://127.0.0.1:11434/classify",
            authHeaderName = "X-Legacy",
            protectedApiKey = "opaque-secret-that-must-not-be-used",
            payloadClass = "classification-input",
            redactionState = "legacy"
        }));
        var store = new OperatorClassificationSettingsStore(
            Options.Create(new OperatorConfigOptions { Directory = directory }),
            new ConfigurationBuilder().Build());

        var settings = await store.ReadAsync();
        var migratedJson = await File.ReadAllTextAsync(path);

        Assert.Equal(OperatorClassificationProviderKind.LocalHttp, settings.Provider);
        Assert.Equal("", settings.Model);
        Assert.Equal("", settings.AuthHeaderName);
        Assert.False(settings.HasApiKey);
        Assert.DoesNotContain("legacy-model", migratedJson, StringComparison.Ordinal);
        Assert.DoesNotContain("X-Legacy", migratedJson, StringComparison.Ordinal);
        Assert.DoesNotContain("opaque-secret", migratedJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OperatorProviderTestUsesConfiguredClassifier()
    {
        using var saveResponse = await _client.PutAsJsonAsync("/api/operator/classification-provider", new
        {
            provider = "LocalDeterministic",
            endpoint = ""
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
        Assert.Equal("LocalDeterministic", body.RootElement.GetProperty("configuration").GetProperty("provider").GetString());
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
            new LocalContextualContentClassifier(),
            new PolicyEngine());

        var response = await service.PreviewAsync(new ClassificationPreviewRequest(
            "source-1",
            "Customer contract and payment details.",
            "note"));

        Assert.Equal(SensitivityLevel.Confidential, response.Classification.Sensitivity);
        Assert.Equal(StorageDecisionKind.SensitiveDbOnly, response.StorageDecision.Kind);
    }

    [Fact]
    public async Task PreviewRoutesMonetaryContentToSensitiveStoreOnlyWithLocalGuard()
    {
        var service = new ClassificationPreviewService(
            new HybridContentClassifier(
                new LocalContextualContentClassifier(),
                new DeterministicSensitiveDataDetector()),
            new PolicyEngine());

        var response = await service.PreviewAsync(new ClassificationPreviewRequest(
            "source-money",
            "홍길동 사원의 견적금액은 1,000원입니다.",
            "note"));

        Assert.Equal(SensitivityLevel.Confidential, response.Classification.Sensitivity);
        Assert.Contains("finance", response.Classification.Categories);
        Assert.True(response.Classification.ContainsSensitiveMaterial);
        Assert.Equal(StorageDecisionKind.SensitiveDbOnly, response.StorageDecision.Kind);
    }

    [Fact]
    public async Task PreviewKeepsDateVersionAndOrdinaryQuantityPublic()
    {
        var response = await new ClassificationPreviewService(
            new LocalContextualContentClassifier(),
            new PolicyEngine())
            .PreviewAsync(new ClassificationPreviewRequest(
                "source-benign-numbers",
                "2026-08-04에 v1.2.3을 배포했고 3개 항목을 처리했다.",
                "note"));

        Assert.Equal(SensitivityLevel.Public, response.Classification.Sensitivity);
        Assert.Empty(response.Classification.Categories);
        Assert.False(response.Classification.ContainsSensitiveMaterial);
        Assert.Equal(StorageDecisionKind.WikiCandidate, response.StorageDecision.Kind);
    }

    [Fact]
    public void LocalDeterministicProviderUsesLocalContextualClassifier()
    {
        var options = new ClassificationProviderOptions
        {
            Provider = "LocalDeterministic"
        };

        Assert.Equal(OperatorClassificationProviderKind.LocalDeterministic, options.ResolveProvider());
        var boundary = new LocalContextualContentClassifier().Boundary;
        Assert.Equal("LocalDeterministic", boundary.ProviderName);
        Assert.Equal("local-only", boundary.RedactionState);
    }

    [Fact]
    public void ClassificationProviderDefaultsToLocalDeterministic()
    {
        var options = new ClassificationProviderOptions();

        Assert.Equal(OperatorClassificationProviderKind.LocalDeterministic, options.ResolveProvider());
    }

    [Theory]
    [InlineData("Unconfigured")]
    [InlineData("Mock")]
    [InlineData("ExternalHttp")]
    [InlineData("OpenAi")]
    public async Task ServiceCollectionFailsClosedForUnconfiguredAndLegacyProviders(string providerName)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Luthn:Classification:Provider"] = providerName
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

        Assert.Equal(ClassificationProviderOptions.ProviderRequiredMessage, error.Message);
    }

    [Fact]
    public void RemoteLocalHttpOptionsResolveToUnconfigured()
    {
        var options = new ClassificationProviderOptions
        {
            Provider = "LocalHttp",
            LocalHttp = new LocalHttpClassificationProviderOptions
            {
                Endpoint = "https://192.168.1.20/classify"
            }
        };

        Assert.Equal(OperatorClassificationProviderKind.Unconfigured, options.ResolveProvider());
    }

    [Fact]
    public async Task LocalHttpClassifierSendsBoundaryMetadataAndMapsProviderResponse()
    {
        using var handler = new CapturingHandler();
        var classifier = CreateLocalHttpClassifier(handler);

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
        Assert.Equal("same-device-local-http", handler.RequestJson.RootElement.GetProperty("redactionState").GetString());
    }

    [Fact]
    public async Task LocalHttpClassifierDropsUnsafeProviderCategories()
    {
        using var handler = new CapturingHandler
        {
            Categories = ["contract", "Customer contract raw phrase never persisted.", "payment"]
        };
        var classifier = CreateLocalHttpClassifier(handler);

        var result = await classifier.ClassifyAsync(
            new("source-1"),
            "Customer contract summary.",
            "note");

        Assert.Equal(["contract", "payment"], result.Categories.OrderBy(category => category).ToArray());
    }

    [Fact]
    public async Task LocalHttpClassifierNormalizesContradictoryProviderFieldsConservatively()
    {
        using var handler = new CapturingHandler
        {
            Sensitivity = "Public",
            Categories = ["Private Key"],
            ContainsSensitiveMaterial = false
        };
        var classifier = CreateLocalHttpClassifier(handler);

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
    public async Task LocalHttpClassifierRejectsUndefinedNumericSensitivity()
    {
        using var handler = new CapturingHandler
        {
            Sensitivity = "999"
        };
        var classifier = CreateLocalHttpClassifier(handler);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => classifier.ClassifyAsync(
            new("source-1"),
            "Customer contract summary.",
            "note").AsTask());

        Assert.Equal("LocalHttp classification provider returned unsupported sensitivity '999'.", error.Message);
    }

    [Fact]
    public async Task LocalHttpResultCannotWeakenHybridLocalGuard()
    {
        using var handler = new CapturingHandler
        {
            Sensitivity = "Public",
            Categories = [],
            ContainsSensitiveMaterial = false
        };
        var classifier = new HybridContentClassifier(
            CreateLocalHttpClassifier(handler),
            new DeterministicSensitiveDataDetector());

        var result = await classifier.ClassifyAsync(
            new("source-guarded"),
            "견적금액은 1,000원입니다.",
            "note");

        Assert.Equal(SensitivityLevel.Confidential, result.Sensitivity);
        Assert.Contains("finance", result.Categories);
        Assert.True(result.ContainsSensitiveMaterial);
        Assert.Equal(StorageDecisionKind.SensitiveDbOnly, new PolicyEngine().Decide(result).Kind);
    }

    [Theory]
    [InlineData(HttpStatusCode.MovedPermanently)]
    [InlineData(HttpStatusCode.Redirect)]
    [InlineData(HttpStatusCode.TemporaryRedirect)]
    [InlineData(HttpStatusCode.PermanentRedirect)]
    public async Task LocalHttpRedirectResponsesFailClosed(HttpStatusCode statusCode)
    {
        using var handler = new RedirectHandler(statusCode);
        var classifier = CreateLocalHttpClassifier(handler);

        var error = await Assert.ThrowsAsync<ClassificationProviderException>(() => classifier.ClassifyAsync(
            new("source-redirect"),
            "Content must stay on the validated endpoint.",
            "note").AsTask());

        Assert.Equal(1, handler.Attempts);
        Assert.Contains($"HTTP {(int)statusCode}", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConfiguredClassifierRetriesTransientProviderFailure()
    {
        using var handler = new TransientFailureHandler();
        var classifier = new ConfiguredContentClassifier(
            new StaticSettingsStore(new OperatorClassificationProviderSettings
            {
                Provider = OperatorClassificationProviderKind.LocalHttp,
                Endpoint = "http://127.0.0.1:11434/classify",
                PayloadClass = "classification-input",
                RedactionState = "same-device-local-http"
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
                Provider = OperatorClassificationProviderKind.LocalHttp,
                Endpoint = "http://127.0.0.1:11434/classify",
                PayloadClass = "classification-input",
                RedactionState = "same-device-local-http"
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
            "LocalHttp",
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
            "LocalHttp",
            metrics,
            CancellationToken.None);

        var firstRecorded = await metrics.FirstProviderRequest;
        Assert.Equal("retry", firstRecorded.Outcome);
        Assert.False(request.IsCompleted);

        using var response = await request;
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["retry", "succeeded"], metrics.ProviderRequests.Select(item => item.Outcome));
    }

    private static OperatorClassificationSettingsStore CreateSettingsStore(
        IReadOnlyDictionary<string, string?>? values = null)
    {
        var configurationBuilder = new ConfigurationBuilder();
        if (values is not null)
        {
            configurationBuilder.AddInMemoryCollection(values);
        }

        return new OperatorClassificationSettingsStore(
            Options.Create(new OperatorConfigOptions
            {
                Directory = Path.Combine(
                    Path.GetTempPath(),
                    "luthn-operator-tests",
                    Guid.NewGuid().ToString("N"))
            }),
            configurationBuilder.Build());
    }

    private static ConfiguredContentClassifier CreateLocalHttpClassifier(HttpMessageHandler handler) =>
        new(
            new StaticSettingsStore(new OperatorClassificationProviderSettings
            {
                Provider = OperatorClassificationProviderKind.LocalHttp,
                Endpoint = "http://127.0.0.1:11434/classify",
                PayloadClass = "classification-input",
                RedactionState = "same-device-local-http"
            }),
            new StaticHttpClientFactory(new HttpClient(handler)),
            Options.Create(new ClassificationProviderRuntimeOptions
            {
                MaxAttempts = 1
            }),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ConfiguredContentClassifier>.Instance);

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
            Assert.Equal("http://127.0.0.1:11434/classify", request.RequestUri?.ToString());

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

    private sealed class RedirectHandler(HttpStatusCode statusCode) : HttpMessageHandler, IDisposable
    {
        public int Attempts { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Attempts++;
            return Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Headers =
                {
                    Location = new Uri("https://provider.example/classify")
                }
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
