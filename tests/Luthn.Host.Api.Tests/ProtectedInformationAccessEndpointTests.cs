using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
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

public sealed class ProtectedInformationAccessEndpointTests
{
    private const string AliceBearer = "protected-result-alice";
    private const string OtherAgentBearer = "protected-result-other-agent";
    private const string OperatorBearer = "protected-result-operator";

    [Fact]
    public async Task ApprovedResultIsNoStoreAndVisibleOnlyToRequestingPrincipal()
    {
        using var factory = CreateFactory();
        using var alice = Client(factory, AliceBearer);
        using var otherAgent = Client(factory, OtherAgentBearer);
        using var operatorClient = Client(factory, OperatorBearer);
        await SeedProtectedQuoteAsync(factory);

        using var resolveResponse = await alice.PostAsJsonAsync("/api/access-requests/resolve", new
        {
            memoryItemId = "memory-protected-quote",
            reason = "견적 금액 확인"
        });
        Assert.Equal(HttpStatusCode.OK, resolveResponse.StatusCode);
        Assert.Contains("no-store", resolveResponse.Headers.CacheControl!.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no-cache", resolveResponse.Headers.Pragma.ToString(), StringComparison.OrdinalIgnoreCase);
        using var resolve = await JsonDocument.ParseAsync(await resolveResponse.Content.ReadAsStreamAsync());
        var requestId = resolve.RootElement.GetProperty("requestId").GetString()!;
        var accessHandle = resolve.RootElement.GetProperty("accessHandle").GetString()!;
        Assert.Matches("^[0-9a-f]{64}$", accessHandle);

        using var detailResponse = await operatorClient.GetAsync(
            $"/api/access-requests/{requestId}/operator-detail");
        var detail = await detailResponse.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, detailResponse.StatusCode);
        Assert.Contains("\"accessMode\":\"ProtectedMemory\"", detail, StringComparison.Ordinal);
        Assert.DoesNotContain("10억", detail, StringComparison.Ordinal);
        Assert.DoesNotContain(accessHandle, detail, StringComparison.Ordinal);

        using var approval = await operatorClient.PostAsJsonAsync(
            $"/api/access-requests/{requestId}/approve",
            new { reason = "요청자에게 60분 동안 1회 공개" });
        Assert.Equal(HttpStatusCode.OK, approval.StatusCode);

        using var legacyResult = await alice.GetAsync($"/api/access-requests/{requestId}/result");
        using var legacyBody = await JsonDocument.ParseAsync(await legacyResult.Content.ReadAsStreamAsync());
        Assert.Equal("ProtectedMemory", legacyBody.RootElement.GetProperty("accessMode").GetString());
        Assert.Equal(
            "approved-protected-output-authorized",
            legacyBody.RootElement.GetProperty("outputPolicy").GetString());
        Assert.False(legacyBody.RootElement.GetProperty("redactedOutputAvailable").GetBoolean());
        Assert.Equal(1, legacyBody.RootElement.GetProperty("remainingReads").GetInt32());

        using var otherRead = await otherAgent.PostAsJsonAsync(
            "/api/access-requests/protected-result",
            new { accessHandle });
        using var otherBody = await JsonDocument.ParseAsync(await otherRead.Content.ReadAsStreamAsync());
        Assert.Equal(HttpStatusCode.OK, otherRead.StatusCode);
        Assert.Equal("protected-result-not-found", otherBody.RootElement.GetProperty("status").GetString());
        Assert.False(otherBody.RootElement.GetProperty("contentAvailable").GetBoolean());
        Assert.False(otherBody.RootElement.TryGetProperty("content", out var otherContent) &&
            otherContent.ValueKind == JsonValueKind.String);

        using var ownerRead = await alice.PostAsJsonAsync(
            "/api/access-requests/protected-result",
            new { accessHandle });
        using var ownerBody = await JsonDocument.ParseAsync(await ownerRead.Content.ReadAsStreamAsync());
        Assert.Equal(HttpStatusCode.OK, ownerRead.StatusCode);
        Assert.Contains("no-store", ownerRead.Headers.CacheControl!.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no-cache", ownerRead.Headers.Pragma.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.True(ownerBody.RootElement.GetProperty("contentAvailable").GetBoolean());
        Assert.Equal("퍼시스 견적", ownerBody.RootElement.GetProperty("title").GetString());
        Assert.Equal(
            "퍼시스 가구회사에 견적 10억을 제시했어.",
            ownerBody.RootElement.GetProperty("content").GetString());
        Assert.Equal(0, ownerBody.RootElement.GetProperty("remainingReads").GetInt32());
        Assert.Equal(1, ownerBody.RootElement.GetProperty("maxReads").GetInt32());

        using var exhaustedRead = await alice.PostAsJsonAsync(
            "/api/access-requests/protected-result",
            new { accessHandle });
        using var exhausted = await JsonDocument.ParseAsync(await exhaustedRead.Content.ReadAsStreamAsync());
        Assert.Equal("grant-consumed", exhausted.RootElement.GetProperty("status").GetString());
        Assert.False(exhausted.RootElement.GetProperty("contentAvailable").GetBoolean());
    }

    private static WebApplicationFactory<Program> CreateFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Luthn:TestingDatabaseName", Guid.NewGuid().ToString("N"));
            builder.UseSetting("Luthn:Auth:RequireServiceToken", "true");
            builder.UseSetting("Luthn:Identity:Mode", "MultiUser");
            ConfigureToken(builder, 0, "alice-agent", AliceBearer, "alice", false,
                "access.request");
            ConfigureToken(builder, 1, "other-agent", OtherAgentBearer, "alice", false,
                "access.request");
            ConfigureToken(builder, 2, "operator", OperatorBearer, "operator", true,
                "access.review", "access.decide");
        });

    private static async Task SeedProtectedQuoteAsync(WebApplicationFactory<Program> factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<LuthnDbContext>();
        var protector = scope.ServiceProvider.GetRequiredService<ISensitiveMemoryPayloadProtector>();
        var now = DateTimeOffset.UtcNow;
        db.SourceEvents.Add(new SourceEventRecord
        {
            Id = "source-protected-quote",
            WorkspaceId = "personal:alice",
            OwnerUserId = "alice",
            SourceSystem = "codex",
            SourceType = "turn-summary",
            ReceivedAt = now,
            ContentDigest = "sha256:" + new string('d', 64),
            ContainsSensitiveMaterial = true
        });
        db.SharedMemoryItems.Add(new SharedMemoryItemRecord
        {
            Id = "memory-protected-quote",
            Title = "보호된 견적",
            SafeSummary = "승인 뒤 확인할 수 있는 견적 정보가 있다.",
            CoreTags = ["quote"],
            Sensitivity = SensitivityLevel.Public,
            Visibility = MemoryVisibility.SharedAcrossAgents,
            RetentionKind = MemoryRetentionKind.Durable,
            AllowsAgentContext = true,
            CreatedAt = now,
            UpdatedAt = now,
            CreatedBy = "alice-agent",
            WorkspaceId = "personal:alice",
            OwnerUserId = "alice"
        });
        var payload = new SensitiveMemoryPayload(
            SensitiveMemoryPayload.CurrentContractVersion,
            "퍼시스 견적",
            "퍼시스 가구회사에 견적 10억을 제시했어.",
            ["quote"],
            null,
            null,
            [],
            null);
        db.SensitiveMemoryPayloads.Add(new SensitiveMemoryPayloadRecord
        {
            MemoryItemId = "memory-protected-quote",
            ContractVersion = payload.ContractVersion,
            ProtectionScheme = protector.ProtectionScheme,
            ProtectedPayload = protector.Protect("memory-protected-quote", payload),
            CreatedAt = now,
            UpdatedAt = now
        });
        db.SensitiveRecordReferences.Add(new SensitiveRecordReferenceRecord
        {
            Id = "reference-protected-quote",
            SourceEventId = "source-protected-quote",
            MemoryItemId = "memory-protected-quote",
            SourceSystem = "codex",
            SourceType = "turn-summary",
            ReceivedAt = now,
            ContainsSensitiveMaterial = true,
            ReferenceLabel = "Protected information",
            RedactedSummary = "승인 뒤 확인할 수 있는 견적 정보가 있다.",
            WorkspaceId = "personal:alice",
            OwnerUserId = "alice"
        });
        await db.SaveChangesAsync();
    }

    private static HttpClient Client(WebApplicationFactory<Program> factory, string bearer)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return client;
    }

    private static void ConfigureToken(
        IWebHostBuilder builder,
        int index,
        string name,
        string bearer,
        string userId,
        bool isOperator,
        params string[] scopes)
    {
        builder.UseSetting($"Luthn:Auth:Tokens:{index}:Name", name);
        builder.UseSetting($"Luthn:Auth:Tokens:{index}:Sha256Digest", Sha256(bearer));
        builder.UseSetting($"Luthn:Auth:Tokens:{index}:UserId", userId);
        builder.UseSetting($"Luthn:Auth:Tokens:{index}:WorkspaceId", "personal:alice");
        builder.UseSetting($"Luthn:Auth:Tokens:{index}:IsOperator", isOperator.ToString());
        for (var scopeIndex = 0; scopeIndex < scopes.Length; scopeIndex++)
        {
            builder.UseSetting($"Luthn:Auth:Tokens:{index}:Scopes:{scopeIndex}", scopes[scopeIndex]);
        }
    }

    private static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
