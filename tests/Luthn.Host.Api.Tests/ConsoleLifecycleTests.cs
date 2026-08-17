using System.Net;
using System.Text;
using System.Text.Json;
using Luthn.Core.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Luthn.Host.Api.Tests;

public sealed class ConsoleLifecycleTests
{
    [Fact]
    public async Task ConcurrentConsoleProfileRequestsInitializeLifecycleOnce()
    {
        using var factory = CreateFactory();
        using var client = CreateHttpsClient(factory);

        var responses = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => client.GetAsync("/api/operator/console-profile")));

        Assert.All(responses, response =>
        {
            using (response)
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            }
        });
    }

    [Fact]
    public async Task MembershipRemovalRevokesOnlySubjectAndNeverFallsBackToLocal()
    {
        using var factory = CreateFactory();
        using var client = CreateHttpsClient(factory);
        var csrf = await EnrollAndLoginAsync(client);

        using var removed = await SendMutationAsync(
            client,
            "/api/operator/lifecycle/fake-membership-removed",
            csrf);
        using var removedBody = await JsonDocument.ParseAsync(await removed.Content.ReadAsStreamAsync());
        using var session = await client.GetAsync("/api/operator/session");
        using var sessionBody = await JsonDocument.ParseAsync(await session.Content.ReadAsStreamAsync());
        using var relogin = await client.PostAsync("/api/operator/cloud-login", null);
        using var arm = await ConsoleSessionSecurityTests.ArmLocalConsoleAsync(client);
        using var local = await client.PostAsync("/api/operator/session/local", null);

        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        Assert.Equal("Active", removedBody.RootElement.GetProperty("organizationState").GetString());
        Assert.True(removedBody.RootElement.GetProperty("connectionAuthorityActive").GetBoolean());
        Assert.Contains("switch-account", removedBody.RootElement.GetProperty("allowedActions").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal("CloudLoginRequired", sessionBody.RootElement.GetProperty("mode").GetString());
        Assert.Equal("LoginRequired", sessionBody.RootElement.GetProperty("state").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, relogin.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, arm.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, local.StatusCode);
    }

    [Fact]
    public async Task OrganizationRestrictionRemovesMutationAuthorityAndCanReconnect()
    {
        using var factory = CreateFactory();
        using var client = CreateHttpsClient(factory);
        var csrf = await EnrollAndLoginAsync(client);

        using var restricted = await SendMutationAsync(
            client,
            "/api/operator/lifecycle/fake-organization-restricted",
            csrf);
        using var restrictedBody = await JsonDocument.ParseAsync(await restricted.Content.ReadAsStreamAsync());
        using var session = await client.GetAsync("/api/operator/session");
        using var sessionBody = await JsonDocument.ParseAsync(await session.Content.ReadAsStreamAsync());
        csrf = Assert.Single(session.Headers.GetValues(ConsoleAccessOptions.AntiforgeryHeaderName));
        using var configRequest = new HttpRequestMessage(HttpMethod.Put, "/api/operator/classification-provider")
        {
            Content = new StringContent("{\"provider\":\"LocalDeterministic\"}", Encoding.UTF8, "application/json")
        };
        configRequest.Headers.Add(ConsoleAccessOptions.AntiforgeryHeaderName, csrf);
        using var configWrite = await client.SendAsync(configRequest);
        using var local = await client.PostAsync("/api/operator/session/local", null);
        using var reconnect = await SendMutationAsync(client, "/api/operator/lifecycle/reconnect", csrf);
        using var reconnectBody = await JsonDocument.ParseAsync(await reconnect.Content.ReadAsStreamAsync());

        Assert.Equal(HttpStatusCode.OK, restricted.StatusCode);
        Assert.Equal("RestrictedOffboarding", restrictedBody.RootElement.GetProperty("organizationState").GetString());
        Assert.False(restrictedBody.RootElement.GetProperty("connectionAuthorityActive").GetBoolean());
        Assert.Equal("Restricted", sessionBody.RootElement.GetProperty("state").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, configWrite.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, local.StatusCode);
        Assert.Equal(HttpStatusCode.OK, reconnect.StatusCode);
        Assert.Equal("Active", reconnectBody.RootElement.GetProperty("organizationState").GetString());
        Assert.True(reconnectBody.RootElement.GetProperty("connectionAuthorityActive").GetBoolean());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LuthnDbContext>();
        var audits = db.AuditEvents
            .Where(item => item.Action == "console.organization.restricted" ||
                item.Action == "console.organization.reconnected")
            .ToArray();
        Assert.Equal(2, audits.Length);
        Assert.All(audits, item =>
        {
            Assert.Equal("console:cloud-user", item.Actor);
            Assert.Equal("user", item.ActorKind);
            Assert.Equal("lifecycle-owner", item.ActorUserId);
        });
    }

    [Fact]
    public async Task OwnerReclaimRevokesAuthorityBeforeExplicitlyRestoringLocalEligibility()
    {
        using var factory = CreateFactory();
        using var client = CreateHttpsClient(factory);
        var csrf = await EnrollAndLoginAsync(client);
        using var restricted = await SendMutationAsync(
            client,
            "/api/operator/lifecycle/fake-organization-restricted",
            csrf);
        using var session = await client.GetAsync("/api/operator/session");
        csrf = Assert.Single(session.Headers.GetValues(ConsoleAccessOptions.AntiforgeryHeaderName));

        using var reclaim = await SendMutationAsync(
            client,
            "/api/operator/lifecycle/reclaim",
            csrf,
            "{\"method\":\"CloudOwnerReauthentication\"}");
        using var reclaimBody = await JsonDocument.ParseAsync(await reclaim.Content.ReadAsStreamAsync());
        using var local = await ConsoleSessionSecurityTests.CreateArmedLocalSessionAsync(client);
        using var localBody = await JsonDocument.ParseAsync(await local.Content.ReadAsStreamAsync());

        Assert.Equal(HttpStatusCode.OK, reclaim.StatusCode);
        Assert.Equal("Detached", reclaimBody.RootElement.GetProperty("organizationState").GetString());
        Assert.False(reclaimBody.RootElement.GetProperty("connectionAuthorityActive").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, local.StatusCode);
        Assert.Equal("LocalAuto", localBody.RootElement.GetProperty("mode").GetString());
    }

    [Fact]
    public async Task OfflineReclaimIsFailClosedUnlessVerifierIsExplicitlyEnabled()
    {
        using var disabledFactory = CreateFactory();
        using var disabledClient = CreateHttpsClient(disabledFactory);
        var csrf = await EnrollAndLoginAsync(disabledClient);
        using var removed = await SendMutationAsync(
            disabledClient,
            "/api/operator/lifecycle/fake-membership-removed",
            csrf);
        using var rejected = await SendMutationAsync(
            disabledClient,
            "/api/operator/lifecycle/reclaim",
            null,
            "{\"method\":\"OfflineRecovery\"}");
        using var rejectedArm = await ConsoleSessionSecurityTests.ArmLocalConsoleAsync(disabledClient);
        using var rejectedLocal = await disabledClient.PostAsync("/api/operator/session/local", null);

        using var enabledFactory = CreateFactory(recoveryVerifier: "Fake", fakeProofVerified: true);
        using var enabledClient = CreateHttpsClient(enabledFactory);
        csrf = await EnrollAndLoginAsync(enabledClient);
        using var enabledRemoved = await SendMutationAsync(
            enabledClient,
            "/api/operator/lifecycle/fake-membership-removed",
            csrf);
        using var reclaimed = await SendMutationAsync(
            enabledClient,
            "/api/operator/lifecycle/reclaim",
            null,
            "{\"method\":\"OfflineRecovery\"}");
        using var reclaimedBody = await JsonDocument.ParseAsync(await reclaimed.Content.ReadAsStreamAsync());
        using var allowedLocal = await ConsoleSessionSecurityTests.CreateArmedLocalSessionAsync(enabledClient);

        Assert.Equal(HttpStatusCode.Forbidden, rejected.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, rejectedArm.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, rejectedLocal.StatusCode);
        Assert.Equal(HttpStatusCode.OK, reclaimed.StatusCode);
        Assert.Equal("Detached", reclaimedBody.RootElement.GetProperty("organizationState").GetString());
        Assert.Equal(HttpStatusCode.OK, allowedLocal.StatusCode);

        using var scope = enabledFactory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LuthnDbContext>();
        var audit = Assert.Single(db.AuditEvents.Where(item =>
            item.Action == "console.local_reclaim.completed"));
        Assert.Equal("console:offline-recovery", audit.Actor);
        Assert.Equal("system", audit.ActorKind);
        Assert.Null(audit.ActorUserId);
    }

    private static async Task<string> EnrollAndLoginAsync(HttpClient client)
    {
        using var local = await ConsoleSessionSecurityTests.CreateArmedLocalSessionAsync(client);
        var csrf = Assert.Single(local.Headers.GetValues(ConsoleAccessOptions.AntiforgeryHeaderName));
        using var start = await SendMutationAsync(client, "/api/operator/enrollment/start", csrf);
        using var localStatus = await client.GetAsync("/api/operator/session");
        csrf = Assert.Single(localStatus.Headers.GetValues(ConsoleAccessOptions.AntiforgeryHeaderName));
        using var verify = await SendMutationAsync(client, "/api/operator/enrollment/verify", csrf);
        using var login = await client.PostAsync("/api/operator/cloud-login", null);
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        Assert.Equal(HttpStatusCode.OK, verify.StatusCode);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return Assert.Single(login.Headers.GetValues(ConsoleAccessOptions.AntiforgeryHeaderName));
    }

    private static async Task<HttpResponseMessage> SendMutationAsync(
        HttpClient client,
        string path,
        string? csrf,
        string? json = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path);
        if (csrf is not null)
        {
            request.Headers.Add(ConsoleAccessOptions.AntiforgeryHeaderName, csrf);
        }
        if (json is not null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }
        return await client.SendAsync(request);
    }

    private static HttpClient CreateHttpsClient(WebApplicationFactory<Program> factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = true
        });

    private static WebApplicationFactory<Program> CreateFactory(
        string recoveryVerifier = "Disabled",
        bool fakeProofVerified = false) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Luthn:TestingDatabaseName", Guid.NewGuid().ToString("N"));
            builder.UseSetting("Luthn:Auth:RequireServiceToken", "true");
            ConsoleSessionSecurityTests.ConfigureOperatorCredential(builder);
            builder.UseSetting("Luthn:Identity:Mode", "SingleOwner");
            builder.UseSetting("Luthn:Console:LocalOnly", "true");
            builder.UseSetting("Luthn:Console:Enrollment:Adapter", "Fake");
            builder.UseSetting("Luthn:Console:CloudLogin:Provider", "Fake");
            builder.UseSetting("Luthn:Console:CloudLogin:Owner", "true");
            builder.UseSetting("Luthn:Console:CloudLogin:UserId", "lifecycle-owner");
            builder.UseSetting("Luthn:Console:CloudLogin:OrganizationId", "lifecycle-org");
            builder.UseSetting("Luthn:Console:CloudLogin:WorkspaceId", "lifecycle-workspace");
            builder.UseSetting("Luthn:Console:Recovery:Verifier", recoveryVerifier);
            builder.UseSetting("Luthn:Console:Recovery:FakeProofVerified", fakeProofVerified.ToString());
            builder.UseSetting(
                "Luthn:OperatorConfig:Directory",
                Path.Combine(Path.GetTempPath(), "luthn-console-lifecycle-tests", Guid.NewGuid().ToString("N")));
        });
}
