using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Luthn.Host.Api.Tests;

public sealed class OperatorConsoleProfileTests
{
    [Fact]
    public async Task ProfileDerivesLocalModeAndZeroOutboundBoundaryFromServerConfiguration()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            "/api/operator/console-profile?consoleMode=MultiUser&workspaceId=caller-selected");
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        using var postResponse = await client.PostAsync("/api/operator/console-profile", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, postResponse.StatusCode);
        Assert.Equal("Local", body.RootElement.GetProperty("consoleMode").GetString());
        Assert.Equal("disabled", body.RootElement.GetProperty("outboundTransport").GetString());
        Assert.Equal("oss-console", body.RootElement.GetProperty("sensitiveAuthority").GetString());
        Assert.Equal("authenticated-request", body.RootElement.GetProperty("tenancySource").GetString());
        Assert.True(body.RootElement.GetProperty("serverDerived").GetBoolean());
        Assert.DoesNotContain("workspaceId", body.RootElement.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("organizationId", body.RootElement.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("installationId", body.RootElement.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProfileDerivesMultiUserModeFromIdentityConfiguration()
    {
        using var factory = CreateFactory("MultiUser");
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/operator/console-profile");
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("MultiUser", body.RootElement.GetProperty("consoleMode").GetString());
        Assert.Equal("disabled", body.RootElement.GetProperty("outboundTransport").GetString());
    }

    internal static WebApplicationFactory<Program> CreateFactory(string? identityMode = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Luthn:TestingDatabaseName", Guid.NewGuid().ToString("N"));
            builder.UseSetting(
                "Luthn:OperatorConfig:Directory",
                Path.Combine(Path.GetTempPath(), "luthn-console-tests", Guid.NewGuid().ToString("N")));
            if (identityMode is not null)
            {
                builder.UseSetting("Luthn:Identity:Mode", identityMode);
            }
        });
}

public sealed class OperatorConsoleContractTests
{
    [Fact]
    public async Task ConsoleKeepsSensitiveAccessPublicationAndAuditSurfacesSeparate()
    {
        using var factory = OperatorConsoleProfileTests.CreateFactory();
        using var client = factory.CreateClient();

        var index = await client.GetStringAsync("/");
        var script = await client.GetStringAsync("/assets/operator.js");

        Assert.Contains("id=\"externalPublicationTitle\"", index, StringComparison.Ordinal);
        Assert.Contains("id=\"connectLocal\"", index, StringComparison.Ordinal);
        Assert.Contains("id=\"mcpProfilesTitle\"", index, StringComparison.Ordinal);
        Assert.Contains("id=\"remoteProfileOffer\"", index, StringComparison.Ordinal);
        Assert.Contains("data-i18n=\"access.title\"", index, StringComparison.Ordinal);
        Assert.Contains("data-i18n=\"audit.title\"", index, StringComparison.Ordinal);
        Assert.Contains("name=\"category\"", index, StringComparison.Ordinal);
        Assert.Contains("id=\"nextAuditPage\"", index, StringComparison.Ordinal);
        Assert.Contains("id=\"exportAudit\"", index, StringComparison.Ordinal);
        Assert.Contains("id=\"auditDetailFields\"", index, StringComparison.Ordinal);
        Assert.Contains("data-audit-preset=\"hub\"", index, StringComparison.Ordinal);
        Assert.Contains("/api/operator/console-profile", script, StringComparison.Ordinal);
        Assert.Contains("/api/operator/mcp-profiles", script, StringComparison.Ordinal);
        Assert.Contains("luthn.remote-profile.offer", script, StringComparison.Ordinal);
        Assert.Contains("/api/access-requests/", script, StringComparison.Ordinal);
        Assert.Contains("/api/external-publication/", script, StringComparison.Ordinal);
        Assert.Contains("/api/audit-events/export", script, StringComparison.Ordinal);
        Assert.Contains("readAuditExportFilename", script, StringComparison.Ordinal);
        Assert.Contains("credentials: \"same-origin\"", script, StringComparison.Ordinal);
        Assert.Contains("viewSelectedAuditCorrelation", script, StringComparison.Ordinal);
        Assert.Contains("result?.nextCursor", script, StringComparison.Ordinal);
        Assert.Contains("event.retentionClass", script, StringComparison.Ordinal);
        Assert.DoesNotContain("innerHTML", script, StringComparison.Ordinal);
        Assert.DoesNotContain("event.workspaceId", script, StringComparison.Ordinal);
        Assert.Contains("event.actorUserId", script, StringComparison.Ordinal);
        Assert.DoesNotContain("/api/operator/metrics", script, StringComparison.Ordinal);
        Assert.Contains("renderSessionGuidance()", script, StringComparison.Ordinal);
        Assert.Contains("console-nav-7", index, StringComparison.Ordinal);
    }
}

public sealed class OperatorConsoleLocalizationTests
{
    [Fact]
    public async Task LocalizationUsesAllowlistedLanguagePreferenceAndTextOnlyRendering()
    {
        using var factory = OperatorConsoleProfileTests.CreateFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/assets/operator-i18n.js");
        var script = await response.Content.ReadAsStringAsync();
        var index = await client.GetStringAsync("/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("new Set([\"en\", \"ko\"])", script, StringComparison.Ordinal);
        Assert.Contains("luthn.consoleLanguage", script, StringComparison.Ordinal);
        Assert.Contains("en: {", script, StringComparison.Ordinal);
        Assert.Contains("ko: {", script, StringComparison.Ordinal);
        Assert.Contains("node.textContent = translate", script, StringComparison.Ordinal);
        Assert.Contains("data-i18n=\"mode.label\"", index, StringComparison.Ordinal);
        Assert.Contains("id=\"consoleLanguage\"", index, StringComparison.Ordinal);
        Assert.DoesNotContain("innerHTML", script, StringComparison.Ordinal);
        Assert.DoesNotContain("workspaceId", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("organizationId", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("localPath", script, StringComparison.OrdinalIgnoreCase);
    }
}
