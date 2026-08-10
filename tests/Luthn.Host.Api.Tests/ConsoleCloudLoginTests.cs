using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Luthn.Host.Api.Tests;

public sealed class ConsoleCloudLoginTests
{
    [Fact]
    public async Task FakeSecurityProvidersAreDisabledInProduction()
    {
        var environment = new TestHostEnvironment { EnvironmentName = Environments.Production };
        var login = new FakeConsoleCloudLoginProvider(
            Options.Create(new ConsoleCloudLoginOptions()),
            environment);
        var enrollment = new FakeInstallationEnrollmentAdapter(
            TimeProvider.System,
            Options.Create(new ConsoleEnrollmentOptions()),
            environment);
        var recovery = new FakeConsoleOfflineRecoveryVerifier(
            Options.Create(new ConsoleRecoveryOptions { FakeProofVerified = true }),
            environment);

        Assert.False(login.Available);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await login.AuthenticateAsync(CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await enrollment.BeginAsync("fingerprint", CancellationToken.None));
        Assert.False(await recovery.VerifyAsync(CancellationToken.None));
    }

    [Fact]
    public async Task DisabledProviderCannotCreateCloudSessionAfterEnrollment()
    {
        using var factory = CreateFactory("Disabled");
        using var client = CreateHttpsClient(factory);
        await EnrollAsync(client);

        using var login = await client.PostAsync("/api/operator/cloud-login", null);
        using var status = await client.GetAsync("/api/operator/cloud-login");
        using var body = await JsonDocument.ParseAsync(await status.Content.ReadAsStreamAsync());

        Assert.Equal(HttpStatusCode.Forbidden, login.StatusCode);
        Assert.False(body.RootElement.GetProperty("available").GetBoolean());
        Assert.Equal("Disabled", body.RootElement.GetProperty("provider").GetString());
        Assert.False(login.Headers.TryGetValues("Set-Cookie", out var values) &&
            values.Any(value => value.StartsWith("LuthnConsoleSid=", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task FakeProviderIgnoresCallerAuthorityAndIssuesSecureServerDerivedSession()
    {
        using var factory = CreateFactory("Fake");
        using var client = CreateHttpsClient(factory);
        await EnrollAsync(client);
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/operator/cloud-login?organizationId=attacker&workspaceId=attacker")
        {
            Content = new StringContent(
                "{\"organizationId\":\"attacker\",\"workspaceId\":\"attacker\"}",
                Encoding.UTF8,
                "application/json")
        };

        using var login = await client.SendAsync(request);
        using var loginBody = await JsonDocument.ParseAsync(await login.Content.ReadAsStreamAsync());
        using var status = await client.GetAsync("/api/operator/cloud-login");
        using var statusBody = await JsonDocument.ParseAsync(await status.Content.ReadAsStreamAsync());
        using var session = await client.GetAsync("/api/operator/session");
        using var sessionBody = await JsonDocument.ParseAsync(await session.Content.ReadAsStreamAsync());

        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.Equal("CloudAuthenticated", loginBody.RootElement.GetProperty("mode").GetString());
        var cookie = Assert.Single(login.Headers.GetValues("Set-Cookie"), value =>
            value.StartsWith("LuthnConsoleSid=", StringComparison.Ordinal));
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.True(statusBody.RootElement.GetProperty("serverDerived").GetBoolean());
        Assert.Equal("Active", statusBody.RootElement.GetProperty("sessionState").GetString());
        Assert.Equal("CloudAuthenticated", sessionBody.RootElement.GetProperty("mode").GetString());
        var combinedJson = statusBody.RootElement.GetRawText() + sessionBody.RootElement.GetRawText();
        Assert.DoesNotContain("attacker", combinedJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("organizationId", combinedJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("workspaceId", combinedJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ServerConfiguredMemberCannotEscalateToConfigurationWrite()
    {
        using var factory = CreateFactory("Fake", owner: false);
        using var client = CreateHttpsClient(factory);
        await EnrollAsync(client);
        using var login = await client.PostAsync("/api/operator/cloud-login", null);
        var csrf = Assert.Single(login.Headers.GetValues(ConsoleAccessOptions.AntiforgeryHeaderName));
        using var request = new HttpRequestMessage(HttpMethod.Put, "/api/operator/classification-provider")
        {
            Content = new StringContent("{\"provider\":\"Mock\",\"clearApiKey\":true}", Encoding.UTF8, "application/json")
        };
        request.Headers.Add(ConsoleAccessOptions.AntiforgeryHeaderName, csrf);

        using var forbidden = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
    }

    private static async Task EnrollAsync(HttpClient client)
    {
        using var local = await client.PostAsync("/api/operator/session/local", null);
        var csrf = Assert.Single(local.Headers.GetValues(ConsoleAccessOptions.AntiforgeryHeaderName));
        using var start = await SendMutationAsync(client, "/api/operator/enrollment/start", csrf);
        using var session = await client.GetAsync("/api/operator/session");
        csrf = Assert.Single(session.Headers.GetValues(ConsoleAccessOptions.AntiforgeryHeaderName));
        using var verify = await SendMutationAsync(client, "/api/operator/enrollment/verify", csrf);
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        Assert.Equal(HttpStatusCode.OK, verify.StatusCode);
    }

    private static async Task<HttpResponseMessage> SendMutationAsync(
        HttpClient client,
        string path,
        string csrf)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.Add(ConsoleAccessOptions.AntiforgeryHeaderName, csrf);
        return await client.SendAsync(request);
    }

    private static HttpClient CreateHttpsClient(WebApplicationFactory<Program> factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = true
        });

    private static WebApplicationFactory<Program> CreateFactory(
        string loginProvider,
        bool owner = true) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Luthn:TestingDatabaseName", Guid.NewGuid().ToString("N"));
            builder.UseSetting("Luthn:Auth:RequireServiceToken", "true");
            builder.UseSetting("Luthn:Identity:Mode", "SingleOwner");
            builder.UseSetting("Luthn:Console:LocalOnly", "true");
            builder.UseSetting("Luthn:Console:Enrollment:Adapter", "Fake");
            builder.UseSetting("Luthn:Console:CloudLogin:Provider", loginProvider);
            builder.UseSetting("Luthn:Console:CloudLogin:Owner", owner.ToString());
            builder.UseSetting("Luthn:Console:CloudLogin:UserId", "server-user");
            builder.UseSetting("Luthn:Console:CloudLogin:OrganizationId", "server-org");
            builder.UseSetting("Luthn:Console:CloudLogin:WorkspaceId", "server-workspace");
            builder.UseSetting(
                "Luthn:OperatorConfig:Directory",
                Path.Combine(Path.GetTempPath(), "luthn-console-login-tests", Guid.NewGuid().ToString("N")));
        });

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "Luthn.Host.Api.Tests";
        public string ContentRootPath { get; set; } = Directory.GetCurrentDirectory();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
