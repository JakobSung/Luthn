using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Luthn.Core.Classification;
using Luthn.Core.Memory;
using Luthn.Core.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Luthn.Host.Api.Tests;

public sealed class ExternalPublicationEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string ReadBearer = "external-publication-read-local";
    private const string WriteBearer = "external-publication-write-local";
    private readonly WebApplicationFactory<Program> _factory;

    public ExternalPublicationEndpointTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task StatusReportsDisabledTransportWithoutConnectionAttempt()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/external-publication/status");
        var responseJson = await response.Content.ReadAsStringAsync();
        using var body = JsonDocument.Parse(responseJson);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Disabled", body.RootElement.GetProperty("connectionState").GetString());
        Assert.Equal("Idle", body.RootElement.GetProperty("outboxState").GetString());
        Assert.Equal(0, body.RootElement.GetProperty("pendingCount").GetInt32());
        Assert.DoesNotContain("safeEnvelope", responseJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("originInstanceId", responseJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", responseJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ApproveAndRevokeExposeLifecycleWhileKeepingOutboxLocal()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        await SeedMemoryAsync(factory, safeForAgents: true);

        using var approved = await client.PostAsync(
            "/api/external-publication/memory-items/memory-1/approve",
            content: null);
        using var approvedBody = JsonDocument.Parse(await approved.Content.ReadAsStringAsync());
        using var status = await client.GetAsync("/api/external-publication/status");
        using var statusBody = JsonDocument.Parse(await status.Content.ReadAsStringAsync());
        using var revoked = await client.PostAsync(
            "/api/external-publication/memory-items/memory-1/revoke",
            content: null);
        using var revokedBody = JsonDocument.Parse(await revoked.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        Assert.Equal("ApprovedForExternal", approvedBody.RootElement.GetProperty("publicationState").GetString());
        Assert.Equal("Pending", statusBody.RootElement.GetProperty("outboxState").GetString());
        Assert.Equal("Disabled", statusBody.RootElement.GetProperty("connectionState").GetString());
        Assert.Equal(HttpStatusCode.OK, revoked.StatusCode);
        Assert.Equal("Revoked", revokedBody.RootElement.GetProperty("publicationState").GetString());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LuthnDbContext>();
        Assert.Equal(2, await db.SafeProjectionSyncOutbox.CountAsync());
        Assert.All(await db.SafeProjectionSyncOutbox.ToArrayAsync(), record =>
            Assert.Equal(SafeProjectionSyncOutboxState.Pending, record.State));
    }

    [Fact]
    public async Task UnsafeMemoryCannotBeApproved()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        await SeedMemoryAsync(factory, safeForAgents: false);

        using var response = await client.PostAsync(
            "/api/external-publication/memory-items/memory-1/approve",
            content: null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LuthnDbContext>();
        Assert.Empty(await db.SafeProjectionSyncOutbox.ToArrayAsync());
    }

    [Fact]
    public async Task ReadAndWriteScopesAreIndependent()
    {
        using var factory = CreateAuthFactory();
        using var readClient = factory.CreateClient();
        using var writeClient = factory.CreateClient();
        readClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ReadBearer);
        writeClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", WriteBearer);
        await SeedMemoryAsync(factory, safeForAgents: true);

        using var acceptedRead = await readClient.GetAsync("/api/external-publication/status");
        using var forbiddenWrite = await readClient.PostAsync(
            "/api/external-publication/memory-items/memory-1/approve",
            content: null);
        using var acceptedWrite = await writeClient.PostAsync(
            "/api/external-publication/memory-items/memory-1/approve",
            content: null);
        using var forbiddenRead = await writeClient.GetAsync("/api/external-publication/status");

        Assert.Equal(HttpStatusCode.OK, acceptedRead.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, forbiddenWrite.StatusCode);
        Assert.Equal(HttpStatusCode.OK, acceptedWrite.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, forbiddenRead.StatusCode);
    }

    private WebApplicationFactory<Program> CreateFactory() =>
        _factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Luthn:TestingDatabaseName", Guid.NewGuid().ToString("N"));
        });

    private WebApplicationFactory<Program> CreateAuthFactory() =>
        _factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Luthn:TestingDatabaseName", Guid.NewGuid().ToString("N"));
            builder.UseSetting("Luthn:Auth:RequireServiceToken", "true");
            ConfigureToken(builder, 0, "publication-reader", ReadBearer, "external-publication.read");
            ConfigureToken(builder, 1, "publication-writer", WriteBearer, "external-publication.write");
        });

    private static async Task SeedMemoryAsync(
        WebApplicationFactory<Program> factory,
        bool safeForAgents)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LuthnDbContext>();
        var now = DateTimeOffset.Parse("2026-07-13T01:00:00Z");
        db.SharedMemoryItems.Add(new SharedMemoryItemRecord
        {
            Id = "memory-1",
            Title = "Safe runbook",
            SafeSummary = "Public-safe deployment steps.",
            Sensitivity = safeForAgents ? SensitivityLevel.Public : SensitivityLevel.Confidential,
            CoreTags = ["runbook"],
            Visibility = safeForAgents ? MemoryVisibility.SharedAcrossAgents : MemoryVisibility.PrivateToOwner,
            RetentionKind = MemoryRetentionKind.Durable,
            AllowsAgentContext = safeForAgents,
            CreatedAt = now,
            UpdatedAt = now,
            CreatedBy = "local-tools"
        });
        await db.SaveChangesAsync();
    }

    private static void ConfigureToken(
        IWebHostBuilder builder,
        int index,
        string name,
        string bearer,
        string scope)
    {
        builder.UseSetting($"Luthn:Auth:Tokens:{index}:Name", name);
        builder.UseSetting($"Luthn:Auth:Tokens:{index}:Sha256Digest", Sha256Digest(bearer));
        builder.UseSetting($"Luthn:Auth:Tokens:{index}:Scopes:0", scope);
    }

    private static string Sha256Digest(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return $"sha256:{Convert.ToHexString(hash).ToLowerInvariant()}";
    }
}
