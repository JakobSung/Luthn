using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Luthn.Core.Persistence;
using Luthn.Sdk.Console;
using Luthn.Sdk.Sync;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Luthn.Host.Api.Tests;

public sealed class ConsoleEnrollmentTests
{
    [Fact]
    public async Task DisabledAdapterKeepsLocalSessionAndPerformsNoActivation()
    {
        using var factory = CreateFactory("Disabled");
        using var client = factory.CreateClient();
        var csrf = await CreateLocalSessionAsync(client);

        using var start = await SendMutationAsync(client, "/api/operator/enrollment/start", csrf);
        using var session = await client.GetAsync("/api/operator/session");
        using var sessionBody = await JsonDocument.ParseAsync(await session.Content.ReadAsStreamAsync());

        Assert.Equal(HttpStatusCode.Conflict, start.StatusCode);
        Assert.Equal("Active", sessionBody.RootElement.GetProperty("state").GetString());
        Assert.Equal("LocalAuto", sessionBody.RootElement.GetProperty("mode").GetString());
    }

    [Fact]
    public async Task FakeEnrollmentActivatesOnlyAfterVerificationAndRevokesLocalSession()
    {
        var directory = NewDirectory();
        using var factory = CreateFactory("Fake", directory);
        using var client = factory.CreateClient();
        var csrf = await CreateLocalSessionAsync(client);

        using var start = await SendMutationAsync(client, "/api/operator/enrollment/start", csrf);
        using var startBody = await JsonDocument.ParseAsync(await start.Content.ReadAsStreamAsync());
        using var beforeVerify = await client.GetAsync("/api/operator/session");
        using var beforeBody = await JsonDocument.ParseAsync(await beforeVerify.Content.ReadAsStreamAsync());
        csrf = Assert.Single(beforeVerify.Headers.GetValues(ConsoleAccessOptions.AntiforgeryHeaderName));
        using var verify = await SendMutationAsync(client, "/api/operator/enrollment/verify", csrf);
        using var verifyBody = await JsonDocument.ParseAsync(await verify.Content.ReadAsStreamAsync());
        using var afterVerify = await client.GetAsync("/api/operator/session");
        using var afterBody = await JsonDocument.ParseAsync(await afterVerify.Content.ReadAsStreamAsync());

        Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        Assert.Equal("Pending", startBody.RootElement.GetProperty("state").GetString());
        Assert.Equal("Active", beforeBody.RootElement.GetProperty("state").GetString());
        Assert.Equal(HttpStatusCode.OK, verify.StatusCode);
        Assert.Equal("Approved", verifyBody.RootElement.GetProperty("state").GetString());
        Assert.Equal("LoginRequired", afterBody.RootElement.GetProperty("state").GetString());
        Assert.Equal("CloudLoginRequired", afterBody.RootElement.GetProperty("mode").GetString());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LuthnDbContext>();
        var audits = db.AuditEvents
            .Where(item => item.Action.StartsWith("console.enrollment."))
            .OrderBy(item => item.OccurredAt)
            .ToArray();
        Assert.Equal(2, audits.Length);
        Assert.All(audits, item =>
        {
            Assert.Equal("metadata-only", item.PayloadClass);
            Assert.Equal("no-content", item.RedactionState);
        });
        var auditJson = JsonSerializer.Serialize(audits);
        Assert.DoesNotContain("fingerprint", auditJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("credential", auditJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ApprovedEnrollmentSurvivesHostRestart()
    {
        var directory = NewDirectory();
        using (var firstFactory = CreateFactory("Fake", directory))
        using (var firstClient = firstFactory.CreateClient())
        {
            var csrf = await CreateLocalSessionAsync(firstClient);
            using var start = await SendMutationAsync(firstClient, "/api/operator/enrollment/start", csrf);
            using var session = await firstClient.GetAsync("/api/operator/session");
            csrf = Assert.Single(session.Headers.GetValues(ConsoleAccessOptions.AntiforgeryHeaderName));
            using var verify = await SendMutationAsync(firstClient, "/api/operator/enrollment/verify", csrf);
            Assert.Equal(HttpStatusCode.OK, verify.StatusCode);
        }

        using var restartedFactory = CreateFactory("Fake", directory);
        using var restartedClient = restartedFactory.CreateClient();
        using var arm = await ConsoleSessionSecurityTests.ArmLocalConsoleAsync(restartedClient);
        using var localAttempt = await restartedClient.PostAsync("/api/operator/session/local", null);
        using var browserLocalAttempt = await restartedClient.PostAsync("/api/operator/session/local/connect", null);
        using var status = await restartedClient.GetAsync("/api/operator/session");
        using var body = await JsonDocument.ParseAsync(await status.Content.ReadAsStreamAsync());

        Assert.Equal(HttpStatusCode.Forbidden, localAttempt.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, browserLocalAttempt.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, arm.StatusCode);
        Assert.Equal("CloudLoginRequired", body.RootElement.GetProperty("mode").GetString());
        Assert.Equal("LoginRequired", body.RootElement.GetProperty("state").GetString());
    }

    [Fact]
    public async Task CloudEnrollmentReturnsDisplayCodeWithoutPersistingRawEnrollmentId()
    {
        var directory = NewDirectory();
        var now = DateTimeOffset.UtcNow;
        var handler = new QueueHandler(
            _ =>
            {
                var response = JsonResponse(
                    HttpStatusCode.Created,
                    $$"""
                    {
                      "enrollmentId":"enrollment_very_secret_reference",
                      "verificationUri":"https://cloud.example/hub/activate",
                      "userCode":"ABCD-EFGH",
                      "expiresAt":"{{now.AddMinutes(10):O}}",
                      "pollIntervalSeconds":5
                    }
                    """);
                response.Headers.TryAddWithoutValidation("DPoP-Nonce", "nonce_1");
                return response;
            },
            _ => new HttpResponseMessage(HttpStatusCode.NoContent));
        using var protocolHttpClient = new HttpClient(handler);
        var protocolClient = new CloudHubProtocolClient(
            protocolHttpClient,
            new FixedTimeProvider(now));
        using var factory = CreateFactory("Cloud", directory, protocolClient);
        using var client = factory.CreateClient();
        var csrf = await CreateLocalSessionAsync(client);

        using var start = await SendMutationAsync(client, "/api/operator/enrollment/start", csrf);
        using var body = await JsonDocument.ParseAsync(await start.Content.ReadAsStreamAsync());

        Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        Assert.Equal("Cloud", body.RootElement.GetProperty("adapter").GetString());
        Assert.Equal("ABCD-EFGH", body.RootElement.GetProperty("userCode").GetString());
        Assert.Equal(
            "https://cloud.example/hub/activate",
            body.RootElement.GetProperty("verificationUri").GetString());
        var lifecycleFile = await File.ReadAllTextAsync(Path.Combine(directory, "console-lifecycle.json"));
        var cloudStateFile = await File.ReadAllTextAsync(Path.Combine(directory, "cloud-hub-state.json"));
        Assert.DoesNotContain("enrollment_very_secret_reference", lifecycleFile, StringComparison.Ordinal);
        Assert.DoesNotContain("enrollment_very_secret_reference", cloudStateFile, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CloudEnrollmentVerificationRecoversAfterSessionWasStoredBeforeLocalActivation()
    {
        var directory = NewDirectory();
        var now = new DateTimeOffset(2026, 8, 20, 8, 0, 0, TimeSpan.Zero);
        var options = Options.Create(new CloudHubConnectionOptions
        {
            Enabled = true,
            BaseUrl = "https://cloud.example",
            StateDirectory = directory,
        });
        var store = new DataProtectionCloudHubStateStore(
            options,
            new DataProtectionCloudHubStateProtector(new EphemeralDataProtectionProvider()));
        var state = store.Read();
        const string enrollmentId = "enrollment_recovery_1";
        await store.UpdateAsync(
            (current, _) => Task.FromResult(
                new CloudHubStateUpdate<bool>(
                    current with
                    {
                        PendingEnrollment = null,
                        Session = new CloudHubSession(
                            "installation_1",
                            "access_token_1",
                            now.AddMinutes(5),
                            "refresh_token_1",
                            now.AddDays(30),
                            current.Key.KeyId,
                            [CloudHubProtocolClient.ProjectionWriteScope]),
                        ApprovedEnrollment = new CloudHubApprovedEnrollment(
                            enrollmentId,
                            now.AddMinutes(10),
                            now),
                    },
                    true)),
            CancellationToken.None);
        using var protocolHttpClient = new HttpClient(new QueueHandler());
        var adapter = new CloudInstallationEnrollmentAdapter(
            store,
            new CloudHubProtocolClient(protocolHttpClient, new FixedTimeProvider(now)),
            options,
            new FixedTimeProvider(now));
        var snapshot = new ConsoleLifecycleSnapshot(
            InstallationEnrollmentState.Pending,
            "installation-fingerprint",
            now.AddMinutes(10),
            null,
            PendingReference(enrollmentId),
            [CloudHubProtocolClient.SafeProjectionCapability],
            ConsoleOrganizationState.Active,
            null,
            [],
            new Uri("https://cloud.example/hub/activate"),
            "ABCD-EFGH");

        var grant = await adapter.VerifyAsync(snapshot, CancellationToken.None);

        Assert.Equal(snapshot.PendingReference, grant.PendingReference);
        Assert.Equal(now, grant.ApprovedAt);
        Assert.Equal(now.AddMinutes(10), grant.ExpiresAt);
    }

    private static async Task<string> CreateLocalSessionAsync(HttpClient client)
    {
        using var response = await ConsoleSessionSecurityTests.CreateArmedLocalSessionAsync(client);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return Assert.Single(response.Headers.GetValues(ConsoleAccessOptions.AntiforgeryHeaderName));
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

    private static string NewDirectory() =>
        Path.Combine(Path.GetTempPath(), "luthn-console-enrollment-tests", Guid.NewGuid().ToString("N"));

    private static string PendingReference(string enrollmentId) =>
        $"enrollment_{Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(enrollmentId)))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_')}";

    private static WebApplicationFactory<Program> CreateFactory(
        string adapter,
        string? directory = null,
        CloudHubProtocolClient? protocolClient = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Luthn:TestingDatabaseName", Guid.NewGuid().ToString("N"));
            builder.UseSetting("Luthn:Auth:RequireServiceToken", "true");
            ConsoleSessionSecurityTests.ConfigureOperatorCredential(builder);
            builder.UseSetting("Luthn:Identity:Mode", "SingleOwner");
            builder.UseSetting("Luthn:Console:LocalOnly", "true");
            builder.UseSetting("Luthn:Console:Enrollment:Adapter", adapter);
            builder.UseSetting("Luthn:OperatorConfig:Directory", directory ?? NewDirectory());
            if (protocolClient is not null)
            {
                builder.UseSetting("Luthn:Cloud:Enabled", "true");
                builder.UseSetting("Luthn:Cloud:BaseUrl", "https://cloud.example");
                builder.UseSetting("Luthn:Cloud:StateDirectory", directory ?? NewDirectory());
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<CloudHubProtocolClient>();
                    services.AddSingleton(protocolClient);
                });
            }
        });

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, string json) => new(status)
    {
        Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
    };

    private sealed class QueueHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] responses)
        : HttpMessageHandler
    {
        private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responses = new(responses);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(_responses.Dequeue()(request));
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
