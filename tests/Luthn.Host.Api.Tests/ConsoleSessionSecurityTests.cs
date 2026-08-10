using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Luthn.Host.Api.Tests;

public sealed class ConsoleSessionSecurityTests
{
    [Fact]
    public async Task EligiblePersonalInstallIssuesBoundedHostOnlyHttpOnlySession()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var initial = await client.GetAsync("/api/operator/session");
        using var initialBody = await JsonDocument.ParseAsync(await initial.Content.ReadAsStreamAsync());
        using var created = await client.PostAsync("/api/operator/session/local", null);
        using var createdBody = await JsonDocument.ParseAsync(await created.Content.ReadAsStreamAsync());

        Assert.Equal("Anonymous", initialBody.RootElement.GetProperty("state").GetString());
        Assert.Equal("create-local-session", initialBody.RootElement.GetProperty("nextAction").GetString());
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

        using var response = await client.PostAsync("/api/operator/session/local", null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False(response.Headers.TryGetValues("Set-Cookie", out var values) &&
            values.Any(value => value.StartsWith("LuthnConsoleSid=", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task CookieAuthenticatedMutationRequiresAntiforgeryProof()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        using var created = await client.PostAsync("/api/operator/session/local", null);
        var csrf = Assert.Single(created.Headers.GetValues(ConsoleAccessOptions.AntiforgeryHeaderName));

        using var rejected = await client.PutAsJsonAsync("/api/operator/classification-provider", new
        {
            provider = "Mock",
            clearApiKey = true
        });
        using var allowedRequest = new HttpRequestMessage(HttpMethod.Put, "/api/operator/classification-provider")
        {
            Content = JsonContent.Create(new { provider = "Mock", clearApiKey = true })
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
        using var created = await client.PostAsync("/api/operator/session/local", null);
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
        Assert.DoesNotContain("sessionStorage", script, StringComparison.Ordinal);
        Assert.DoesNotContain("localStorage", script, StringComparison.Ordinal);
        Assert.Contains("/api/operator/session/local", script, StringComparison.Ordinal);
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
            builder.ConfigureTestServices(services =>
                services.RemoveAll<IConsoleInstallationState>());
            builder.ConfigureTestServices(services =>
                services.AddSingleton<IConsoleInstallationState, UnenrolledConsoleInstallationState>());
        });
}
