using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Luthn.Host.Api.Tests;

public sealed class HostManagedExtensionTests
{
    private const string Bearer = ConsoleSessionSecurityTests.OperatorBearer;
    private const string ProvisioningToken = "bootstrap-token-BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";

    [Fact]
    public async Task SignedOfferRequiresLocalApprovalAndAuthenticatedActivationBeforeSuccess()
    {
        using var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var factory = CreateFactory(signingKey.ExportSubjectPublicKeyInfoPem());
        using var browser = factory.CreateClient();
        using var session = await ConsoleSessionSecurityTests.CreateArmedLocalSessionAsync(browser);
        var csrf = Assert.Single(session.Headers.GetValues(ConsoleAccessOptions.AntiforgeryHeaderName));
        var manifest = CreateManifest(DateTimeOffset.UtcNow.AddMinutes(5));
        using var create = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/operator/managed-extensions/actions")
        {
            Content = JsonContent.Create(new
            {
                agentKind = "codex",
                manifest,
                signature = Sign(signingKey, manifest.CanonicalPayload()),
                provisioningToken = ProvisioningToken,
            }),
        };
        create.Headers.Add(ConsoleAccessOptions.AntiforgeryHeaderName, csrf);

        using var created = await browser.SendAsync(create);
        using var helper = factory.CreateClient();
        helper.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Bearer);
        using var claimed = await helper.PostAsync(
            "/api/host/managed-extensions/actions/claim",
            null);
        using var claimBody = await JsonDocument.ParseAsync(await claimed.Content.ReadAsStreamAsync());
        var actionId = claimBody.RootElement.GetProperty("action").GetProperty("id").GetString();
        using var completed = await helper.PostAsJsonAsync(
            $"/api/host/managed-extensions/actions/{actionId}/complete",
            new { outcome = "succeeded", verificationCode = "ABCD-EFGH" });
        using var prepared = await browser.GetAsync(
            $"/api/operator/managed-extensions/actions/{actionId}");
        var preparedText = await prepared.Content.ReadAsStringAsync();
        using var finalize = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/operator/managed-extensions/actions/{actionId}/finalize")
        {
            Content = JsonContent.Create(new { outcome = "activated" }),
        };
        finalize.Headers.Add(ConsoleAccessOptions.AntiforgeryHeaderName, csrf);
        using var finalized = await browser.SendAsync(finalize);
        var finalizedText = await finalized.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        Assert.Equal(HttpStatusCode.OK, claimed.StatusCode);
        Assert.Equal(ProvisioningToken, claimBody.RootElement.GetProperty("action")
            .GetProperty("provisioningToken").GetString());
        Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
        Assert.Equal(HttpStatusCode.OK, prepared.StatusCode);
        Assert.Contains("\"state\":\"prepared\"", preparedText, StringComparison.Ordinal);
        Assert.Contains("ABCD-EFGH", preparedText, StringComparison.Ordinal);
        Assert.DoesNotContain(ProvisioningToken, preparedText, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, finalized.StatusCode);
        Assert.Contains("\"state\":\"succeeded\"", finalizedText, StringComparison.Ordinal);
        Assert.DoesNotContain("ABCD-EFGH", finalizedText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailedActivationSchedulesHelperCleanupBeforeTerminalFailure()
    {
        using var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var factory = CreateFactory(signingKey.ExportSubjectPublicKeyInfoPem());
        using var browser = factory.CreateClient();
        using var session = await ConsoleSessionSecurityTests.CreateArmedLocalSessionAsync(browser);
        var csrf = Assert.Single(session.Headers.GetValues(ConsoleAccessOptions.AntiforgeryHeaderName));
        var manifest = CreateManifest(DateTimeOffset.UtcNow.AddMinutes(5));
        using var create = new HttpRequestMessage(HttpMethod.Post, "/api/operator/managed-extensions/actions")
        {
            Content = JsonContent.Create(new
            {
                agentKind = "codex",
                manifest,
                signature = Sign(signingKey, manifest.CanonicalPayload()),
                provisioningToken = ProvisioningToken,
            }),
        };
        create.Headers.Add(ConsoleAccessOptions.AntiforgeryHeaderName, csrf);
        using var created = await browser.SendAsync(create);
        var actionId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();
        using var helper = factory.CreateClient();
        helper.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Bearer);
        await helper.PostAsync("/api/host/managed-extensions/actions/claim", null);
        await helper.PostAsJsonAsync(
            $"/api/host/managed-extensions/actions/{actionId}/complete",
            new { outcome = "succeeded", verificationCode = "ABCD-EFGH" });
        using var finalize = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/operator/managed-extensions/actions/{actionId}/finalize")
        {
            Content = JsonContent.Create(new { outcome = "failed" }),
        };
        finalize.Headers.Add(ConsoleAccessOptions.AntiforgeryHeaderName, csrf);
        using var finalized = await browser.SendAsync(finalize);
        using var cleanupClaim = await helper.PostAsync("/api/host/managed-extensions/actions/claim", null);
        var cleanup = await cleanupClaim.Content.ReadFromJsonAsync<JsonElement>();
        using var cleanupCompleted = await helper.PostAsJsonAsync(
            $"/api/host/managed-extensions/actions/{actionId}/complete",
            new { outcome = "succeeded" });
        var terminal = await cleanupCompleted.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, finalized.StatusCode);
        Assert.Equal("remove", cleanup.GetProperty("action").GetProperty("operation").GetString());
        Assert.Equal("cleanup-claimed", cleanup.GetProperty("action").GetProperty("state").GetString());
        Assert.Equal(HttpStatusCode.OK, cleanupCompleted.StatusCode);
        Assert.Equal("failed", terminal.GetProperty("state").GetString());
        Assert.Equal("extension.activation_failed", terminal.GetProperty("failureCode").GetString());
    }

    [Fact]
    public async Task RejectsTamperedExpiredAndUnsignedOffers()
    {
        using var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var factory = CreateFactory(signingKey.ExportSubjectPublicKeyInfoPem());
        using var browser = factory.CreateClient();
        using var session = await ConsoleSessionSecurityTests.CreateArmedLocalSessionAsync(browser);
        var csrf = Assert.Single(session.Headers.GetValues(ConsoleAccessOptions.AntiforgeryHeaderName));
        var signedManifest = CreateManifest(DateTimeOffset.UtcNow.AddMinutes(5));
        var signature = Sign(signingKey, signedManifest.CanonicalPayload());

        foreach (var candidate in new[]
        {
            (Manifest: signedManifest with { PackageUri = "https://evil.example/artifact" }, Signature: signature),
            (Manifest: CreateManifest(DateTimeOffset.UtcNow.AddMinutes(-1)), Signature: signature),
            (Manifest: signedManifest, Signature: "not-a-signature"),
        })
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                "/api/operator/managed-extensions/actions")
            {
                Content = JsonContent.Create(new
                {
                    agentKind = "codex",
                    manifest = candidate.Manifest,
                    signature = candidate.Signature,
                    provisioningToken = ProvisioningToken,
                }),
            };
            request.Headers.Add(ConsoleAccessOptions.AntiforgeryHeaderName, csrf);
            using var response = await browser.SendAsync(request);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
    }

    private static HostManagedExtensionManifest CreateManifest(DateTimeOffset expiresAt) => new(
        1,
        "managed-addon",
        "trusted-publisher",
        "Managed Extension",
        "https://service.example/packages/0123456789abcdef0123456789abcdef",
        $"sha256:{new string('a', 64)}",
        "0.1.0",
        $"mcr.microsoft.com/dotnet/aspnet:10.0@sha256:{new string('b', 64)}",
        "https://app.example/",
        expiresAt);

    private static string Sign(ECDsa key, string payload) => Convert.ToBase64String(key.SignData(
            Encoding.UTF8.GetBytes(payload),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static WebApplicationFactory<Program> CreateFactory(string publicKey) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Luthn:TestingDatabaseName", Guid.NewGuid().ToString("N"));
            builder.UseSetting(
                "Luthn:OperatorConfig:Directory",
                Path.Combine(Path.GetTempPath(), "luthn-managed-addon-tests", Guid.NewGuid().ToString("N")));
            builder.UseSetting("Luthn:Identity:Mode", "SingleOwner");
            builder.UseSetting("Luthn:Console:LocalOnly", "true");
            builder.UseSetting("Luthn:Auth:RequireServiceToken", "true");
            builder.UseSetting("Luthn:ManagedExtensions:TrustedSigningPublicKeyPem", publicKey);
            var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Bearer)))
                .ToLowerInvariant();
            builder.UseSetting("Luthn:Auth:Tokens:0:Name", "test-host-helper");
            builder.UseSetting("Luthn:Auth:Tokens:0:Sha256Digest", $"sha256:{digest}");
            builder.UseSetting("Luthn:Auth:Tokens:0:WorkspaceId", "default");
            builder.UseSetting("Luthn:Auth:Tokens:0:ActorKind", "Service");
            builder.UseSetting("Luthn:Auth:Tokens:0:IsOperator", "true");
            builder.UseSetting("Luthn:Auth:Tokens:0:Scopes:0", ServiceScopes.ConfigWrite);
            builder.UseSetting("Luthn:Auth:Tokens:0:Scopes:1", ServiceScopes.AgentConnectionWrite);
        });
}
