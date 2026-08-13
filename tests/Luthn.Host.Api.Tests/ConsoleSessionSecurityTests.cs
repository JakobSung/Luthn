using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Luthn.Host.Api.Tests;

public sealed class ConsoleSessionSecurityTests
{
    internal const string OperatorBearer = "test-local-operator";

    [Fact]
    public async Task EligiblePersonalInstallIssuesBoundedHostOnlyHttpOnlySession()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var initial = await client.GetAsync("/api/operator/session");
        using var initialBody = await JsonDocument.ParseAsync(await initial.Content.ReadAsStreamAsync());
        using var arm = await ArmLocalConsoleAsync(client);
        using var armedStatus = await client.GetAsync("/api/operator/session");
        using var armedBody = await JsonDocument.ParseAsync(await armedStatus.Content.ReadAsStreamAsync());
        using var created = await client.PostAsync("/api/operator/session/local", null);
        using var createdBody = await JsonDocument.ParseAsync(await created.Content.ReadAsStreamAsync());

        Assert.Equal("Anonymous", initialBody.RootElement.GetProperty("state").GetString());
        Assert.Equal("arm-local-session", initialBody.RootElement.GetProperty("nextAction").GetString());
        var candidateCookie = Assert.Single(initial.Headers.GetValues("Set-Cookie"), value =>
            value.StartsWith("LuthnConsoleCandidate=", StringComparison.Ordinal));
        Assert.Contains("httponly", candidateCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", candidateCookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("LuthnConsoleCandidate", initialBody.RootElement.GetRawText(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NoContent, arm.StatusCode);
        Assert.Empty(await arm.Content.ReadAsByteArrayAsync());
        Assert.Equal("create-local-session", armedBody.RootElement.GetProperty("nextAction").GetString());
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        Assert.Equal("LocalAuto", createdBody.RootElement.GetProperty("mode").GetString());
        Assert.Equal("Active", createdBody.RootElement.GetProperty("state").GetString());
        var sessionCookie = Assert.Single(created.Headers.GetValues("Set-Cookie"), value =>
            value.StartsWith("LuthnConsoleSid=", StringComparison.Ordinal));
        Assert.Contains("httponly", sessionCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", sessionCookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("domain=", sessionCookie, StringComparison.OrdinalIgnoreCase);
        Assert.True(created.Headers.Contains(ConsoleAccessOptions.AntiforgeryHeaderName));
        Assert.DoesNotContain("sessionId", createdBody.RootElement.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EligiblePersonalInstallCanConnectLocalFromConsoleAfterOperatorAuthorization()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var candidate = await client.GetAsync("/api/operator/session");
        using var rejected = await client.PostAsync("/api/operator/session/local/connect", null);
        using var arm = await ArmLocalConsoleAsync(client);
        using var connected = await client.PostAsync("/api/operator/session/local/connect", null);
        using var body = await JsonDocument.ParseAsync(await connected.Content.ReadAsStreamAsync());

        Assert.Equal(HttpStatusCode.OK, candidate.StatusCode);
        Assert.Equal("arm-local-session", (await JsonDocument.ParseAsync(await candidate.Content.ReadAsStreamAsync()))
            .RootElement.GetProperty("nextAction").GetString());
        Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, arm.StatusCode);
        Assert.Equal(HttpStatusCode.OK, connected.StatusCode);
        Assert.Equal("LocalAuto", body.RootElement.GetProperty("mode").GetString());
        Assert.Equal("Active", body.RootElement.GetProperty("state").GetString());
        Assert.True(connected.Headers.Contains(ConsoleAccessOptions.AntiforgeryHeaderName));
    }

    [Fact]
    public async Task LocalSessionRequiresAndConsumesOneOperatorAuthorization()
    {
        using var factory = CreateFactory();
        using var firstClient = factory.CreateClient();
        using var direct = await firstClient.PostAsync("/api/operator/session/local", null);
        using var arm = await ArmLocalConsoleAsync(firstClient);
        using var secondClient = factory.CreateClient();
        using var otherProcess = await secondClient.PostAsync("/api/operator/session/local", null);
        using var created = await firstClient.PostAsync("/api/operator/session/local", null);
        using var replay = await firstClient.PostAsync("/api/operator/session/local", null);

        Assert.Equal(HttpStatusCode.Forbidden, direct.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, arm.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, otherProcess.StatusCode);
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, replay.StatusCode);
    }

    [Fact]
    public async Task MultipleBrowserCandidatesFailClosed()
    {
        using var factory = CreateFactory();
        using var firstClient = factory.CreateClient();
        using var secondClient = factory.CreateClient();
        using var firstCandidate = await firstClient.GetAsync("/api/operator/session");
        using var secondCandidate = await secondClient.GetAsync("/api/operator/session");
        using var arm = await ArmLocalConsoleWithoutCandidateAsync(firstClient);

        Assert.Equal(HttpStatusCode.Conflict, arm.StatusCode);
    }

    [Theory]
    [InlineData("MultiUser", true, false)]
    [InlineData("SingleOwner", false, false)]
    [InlineData("SingleOwner", true, true)]
    public async Task LocalAutoFailsClosedOutsideExplicitPersonalMode(
        string identityMode,
        bool localOnly,
        bool forwardedHeaders)
    {
        using var factory = CreateFactory(identityMode, localOnly, forwardedHeaders);
        using var client = factory.CreateClient();

        using var arm = await ArmLocalConsoleAsync(client);
        using var response = await client.PostAsync("/api/operator/session/local", null);
        using var browserConnect = await client.PostAsync("/api/operator/session/local/connect", null);

        Assert.Equal(HttpStatusCode.Forbidden, arm.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, browserConnect.StatusCode);
        Assert.False(response.Headers.TryGetValues("Set-Cookie", out var values) &&
            values.Any(value => value.StartsWith("LuthnConsoleSid=", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task CookieAuthenticatedMutationRequiresAntiforgeryProof()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        using var created = await CreateArmedLocalSessionAsync(client);
        var csrf = Assert.Single(created.Headers.GetValues(ConsoleAccessOptions.AntiforgeryHeaderName));

        using var rejected = await client.PutAsJsonAsync("/api/operator/classification-provider", new
        {
            provider = "LocalDeterministic"
        });
        using var allowedRequest = new HttpRequestMessage(HttpMethod.Put, "/api/operator/classification-provider")
        {
            Content = JsonContent.Create(new { provider = "LocalDeterministic" })
        };
        allowedRequest.Headers.Add(ConsoleAccessOptions.AntiforgeryHeaderName, csrf);
        using var allowed = await client.SendAsync(allowedRequest);

        Assert.Equal(HttpStatusCode.Forbidden, rejected.StatusCode);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
    }

    [Fact]
    public async Task LogoutRevokesServerSession()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        using var created = await CreateArmedLocalSessionAsync(client);
        var csrf = Assert.Single(created.Headers.GetValues(ConsoleAccessOptions.AntiforgeryHeaderName));
        using var logoutRequest = new HttpRequestMessage(HttpMethod.Post, "/api/operator/session/logout");
        logoutRequest.Headers.Add(ConsoleAccessOptions.AntiforgeryHeaderName, csrf);

        using var logout = await client.SendAsync(logoutRequest);
        using var status = await client.GetAsync("/api/operator/session");
        using var body = await JsonDocument.ParseAsync(await status.Content.ReadAsStreamAsync());

        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        Assert.Equal("Anonymous", body.RootElement.GetProperty("state").GetString());
    }

    [Fact]
    public async Task ConsoleStaticAssetsContainNoRawCredentialStorageOrInputs()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var index = await client.GetStringAsync("/");
        var script = await client.GetStringAsync("/assets/operator.js");

        Assert.DoesNotContain("serviceToken", index, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("decisionToken", index, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("name=\"apiKey\"", index, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("form.get(\"apiKey\")", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Luthn__Classification__Credential", index, StringComparison.Ordinal);
        Assert.DoesNotContain("sessionStorage", script, StringComparison.Ordinal);
        Assert.DoesNotContain("localStorage", script, StringComparison.Ordinal);
        Assert.Contains("/api/operator/session/local", script, StringComparison.Ordinal);
        Assert.Contains("/api/operator/session/local/connect", script, StringComparison.Ordinal);
        Assert.DoesNotContain("luthn-console-bootstrap", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("X-Luthn-CSRF", script, StringComparison.Ordinal);
    }

    internal static WebApplicationFactory<Program> CreateFactory(
        string identityMode = "SingleOwner",
        bool localOnly = true,
        bool forwardedHeaders = false) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Luthn:TestingDatabaseName", Guid.NewGuid().ToString("N"));
            builder.UseSetting(
                "Luthn:OperatorConfig:Directory",
                Path.Combine(Path.GetTempPath(), "luthn-console-session-tests", Guid.NewGuid().ToString("N")));
            builder.UseSetting("Luthn:Identity:Mode", identityMode);
            builder.UseSetting("Luthn:Console:LocalOnly", localOnly.ToString());
            builder.UseSetting("Luthn:Host:EnableForwardedHeaders", forwardedHeaders.ToString());
            builder.UseSetting("Luthn:Auth:RequireServiceToken", "true");
            ConfigureOperatorCredential(builder);
            builder.ConfigureTestServices(services =>
                services.RemoveAll<IConsoleInstallationState>());
            builder.ConfigureTestServices(services =>
                services.AddSingleton<IConsoleInstallationState, UnenrolledConsoleInstallationState>());
        });

    internal static void ConfigureOperatorCredential(IWebHostBuilder builder)
    {
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(OperatorBearer)))
            .ToLowerInvariant();
        builder.UseSetting("Luthn:Auth:Tokens:0:Name", "test-local-operator");
        builder.UseSetting("Luthn:Auth:Tokens:0:Sha256Digest", $"sha256:{digest}");
        builder.UseSetting("Luthn:Auth:Tokens:0:WorkspaceId", "default");
        builder.UseSetting("Luthn:Auth:Tokens:0:ActorKind", "Service");
        builder.UseSetting("Luthn:Auth:Tokens:0:IsOperator", "true");
        builder.UseSetting("Luthn:Auth:Tokens:0:Scopes:0", ServiceScopes.ConfigWrite);
    }

    internal static async Task<HttpResponseMessage> ArmLocalConsoleAsync(HttpClient client)
    {
        using var candidate = await client.GetAsync("/api/operator/session");
        Assert.Equal(HttpStatusCode.OK, candidate.StatusCode);
        return await ArmLocalConsoleWithoutCandidateAsync(client);
    }

    private static async Task<HttpResponseMessage> ArmLocalConsoleWithoutCandidateAsync(HttpClient client)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/operator/session/local/arm");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", OperatorBearer);
        return await client.SendAsync(request);
    }

    internal static async Task<HttpResponseMessage> CreateArmedLocalSessionAsync(HttpClient client)
    {
        using var arm = await ArmLocalConsoleAsync(client);
        Assert.Equal(HttpStatusCode.NoContent, arm.StatusCode);
        return await client.PostAsync("/api/operator/session/local", null);
    }
}
