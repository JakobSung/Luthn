using System.Net;
using System.Text;
using System.Text.Json;
using Luthn.Sdk.Console;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
            environment,
            TimeProvider.System);
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
    public async Task ExpiredCloudAuthorityRevokesSessionWithoutRestoringLocalAccess()
    {
        var provider = new MutableCloudLoginProvider();
        using var factory = CreateFactory(provider);
        using var client = CreateHttpsClient(factory);
        await EnrollAsync(client);

        using var login = await client.PostAsync("/api/operator/cloud-login", null);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        provider.IsActive = false;
        using var session = await client.GetAsync("/api/operator/session");
        using var body = await JsonDocument.ParseAsync(await session.Content.ReadAsStreamAsync());
        using var localAttempt = await client.PostAsync("/api/operator/session/local/connect", null);

        Assert.Equal(HttpStatusCode.OK, session.StatusCode);
        Assert.Equal("CloudLoginRequired", body.RootElement.GetProperty("mode").GetString());
        Assert.Equal("LoginRequired", body.RootElement.GetProperty("state").GetString());
        Assert.Equal("cloud-account-expired", body.RootElement.GetProperty("reason").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, localAttempt.StatusCode);
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

        using var review = await client.GetAsync("/api/access-requests");
        using var decisionRequest = new HttpRequestMessage(HttpMethod.Post, "/api/access-requests/missing/approve")
        {
            Content = new StringContent("{\"reason\":\"member must not decide\"}", Encoding.UTF8, "application/json")
        };
        decisionRequest.Headers.Add(ConsoleAccessOptions.AntiforgeryHeaderName, csrf);
        using var decision = await client.SendAsync(decisionRequest);
        using var forbidden = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.Equal(HttpStatusCode.OK, review.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, decision.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
    }

    [Fact]
    public void PlainHttpCloudBridgeIsLimitedToDirectLocalOnlyLoopback()
    {
        var context = new DefaultHttpContext();
        context.Connection.LocalIpAddress = IPAddress.Loopback;
        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        var environment = new TestHostEnvironment { EnvironmentName = Environments.Production };

        Assert.True(ConsoleRequestSecurity.IsTrustedLocalRequest(
            context,
            new ConsoleAccessOptions { LocalOnly = true },
            new LuthnHostOperationalOptions { EnableForwardedHeaders = false },
            environment));
        Assert.False(ConsoleRequestSecurity.IsTrustedLocalRequest(
            context,
            new ConsoleAccessOptions { LocalOnly = true },
            new LuthnHostOperationalOptions { EnableForwardedHeaders = true },
            environment));

        context.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.10");
        Assert.False(ConsoleRequestSecurity.IsTrustedLocalRequest(
            context,
            new ConsoleAccessOptions { LocalOnly = true },
            new LuthnHostOperationalOptions(),
            environment));

        context.Connection.LocalIpAddress = IPAddress.Parse("172.20.0.2");
        context.Connection.RemoteIpAddress = IPAddress.Parse("172.20.0.1");
        context.Request.Host = new HostString("127.0.0.1", 8080);
        Assert.True(ConsoleRequestSecurity.IsTrustedLocalRequest(
            context,
            new ConsoleAccessOptions { LocalOnly = true, TrustedLocalBridge = true },
            new LuthnHostOperationalOptions(),
            environment));

        context.Request.Host = new HostString("console.example.test");
        Assert.False(ConsoleRequestSecurity.IsTrustedLocalRequest(
            context,
            new ConsoleAccessOptions { LocalOnly = true, TrustedLocalBridge = true },
            new LuthnHostOperationalOptions(),
            environment));
    }

    private static async Task EnrollAsync(HttpClient client)
    {
        using var local = await ConsoleSessionSecurityTests.CreateArmedLocalSessionAsync(client);
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
            ConsoleSessionSecurityTests.ConfigureOperatorCredential(builder);
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

    private static WebApplicationFactory<Program> CreateFactory(MutableCloudLoginProvider provider) =>
        CreateFactory("Fake").WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IConsoleCloudLoginProvider>();
                services.AddSingleton<IConsoleCloudLoginProvider>(provider);
            }));

    private sealed class MutableCloudLoginProvider : IConsoleCloudLoginProvider
    {
        private static readonly IReadOnlyList<ConsoleCapability> Capabilities =
        [
            ConsoleCapability.AccessReview,
            ConsoleCapability.AccessDecision,
            ConsoleCapability.AuditRead,
            ConsoleCapability.ConfigurationWrite
        ];

        public bool IsActive { get; set; } = true;
        public ConsoleCloudLoginProvider Kind => ConsoleCloudLoginProvider.Fake;
        public bool Available => true;

        public ValueTask<AuthenticatedConsoleAuthority> AuthenticateAsync(CancellationToken cancellationToken)
        {
            if (!IsActive)
            {
                return ValueTask.FromException<AuthenticatedConsoleAuthority>(
                    new InvalidOperationException("Cloud account authentication has expired."));
            }

            return ValueTask.FromResult(CreateAuthority());
        }

        public ValueTask<AuthenticatedConsoleAuthority?> ValidateAsync(
            string subjectKey,
            CancellationToken cancellationToken) =>
            IsActive && string.Equals(subjectKey, "test-org:test-user", StringComparison.Ordinal)
                ? ValueTask.FromResult<AuthenticatedConsoleAuthority?>(CreateAuthority())
                : ValueTask.FromResult<AuthenticatedConsoleAuthority?>(null);

        private static AuthenticatedConsoleAuthority CreateAuthority() =>
            new(
                "test-org:test-user",
                "test-user",
                "test-org",
                "test-workspace",
                true,
                ConsoleMembershipState.Active,
                ConsoleEntitlementState.Active,
                Capabilities,
                new HashSet<string>(
                [
                    ServiceScopes.AccessReview,
                    ServiceScopes.AccessDecide,
                    ServiceScopes.AuditRead,
                    ServiceScopes.ConfigWrite
                ],
                StringComparer.OrdinalIgnoreCase));
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "Luthn.Host.Api.Tests";
        public string ContentRootPath { get; set; } = Directory.GetCurrentDirectory();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
