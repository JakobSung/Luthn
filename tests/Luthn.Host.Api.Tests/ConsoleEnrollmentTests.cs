using System.Net;
using System.Text.Json;
using Luthn.Core.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

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

    private static WebApplicationFactory<Program> CreateFactory(
        string adapter,
        string? directory = null) =>
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
        });
}
