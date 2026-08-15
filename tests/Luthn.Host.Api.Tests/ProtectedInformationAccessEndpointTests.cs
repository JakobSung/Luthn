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
    private const string OtherWorkspaceBearer = "protected-result-other-workspace";
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

    [Fact]
    public async Task WaitIsRequesterBoundStatusOnlyAndDoesNotBlockApprovalOrConsumeRead()
    {
        using var factory = CreateFactory();
        using var alice = Client(factory, AliceBearer);
        using var otherAgent = Client(factory, OtherAgentBearer);
        using var otherWorkspace = Client(factory, OtherWorkspaceBearer);
        using var operatorClient = Client(factory, OperatorBearer);
        await SeedProtectedQuoteAsync(factory);
        var protectedRequest = await ResolveAsync(alice, "승인 대기 경계 확인");

        foreach (var isolatedClient in new[] { otherAgent, otherWorkspace })
        {
            using var isolatedResponse = await isolatedClient.PostAsJsonAsync(
                "/api/access-requests/protected-wait",
                new
                {
                    accessHandle = protectedRequest.AccessHandle,
                    maxWaitSeconds = 1,
                    pollIntervalMs = 100
                });
            using var isolatedBody = await JsonDocument.ParseAsync(
                await isolatedResponse.Content.ReadAsStreamAsync());
            Assert.Equal(HttpStatusCode.OK, isolatedResponse.StatusCode);
            Assert.Equal("not-found", isolatedBody.RootElement.GetProperty("status").GetString());
            Assert.Equal(2, isolatedBody.RootElement.EnumerateObject().Count());
        }

        var waitTask = alice.PostAsJsonAsync(
            "/api/access-requests/protected-wait",
            new
            {
                accessHandle = protectedRequest.AccessHandle,
                maxWaitSeconds = 5,
                pollIntervalMs = 100
            });
        await Task.Delay(150);
        using var approval = await operatorClient.PostAsJsonAsync(
            $"/api/access-requests/{protectedRequest.RequestId}/approve",
            new { reason = "대기 중 승인" });
        Assert.Equal(HttpStatusCode.OK, approval.StatusCode);

        using var waitResponse = await waitTask;
        using var waitBody = await JsonDocument.ParseAsync(
            await waitResponse.Content.ReadAsStreamAsync());
        Assert.Equal(HttpStatusCode.OK, waitResponse.StatusCode);
        Assert.Contains("no-store", waitResponse.Headers.CacheControl!.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no-cache", waitResponse.Headers.Pragma.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("approved", waitBody.RootElement.GetProperty("status").GetString());
        Assert.Equal(2, waitBody.RootElement.EnumerateObject().Count());
        var responseText = waitBody.RootElement.GetRawText();
        Assert.DoesNotContain("content", responseText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("accessHandle", responseText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("requestId", responseText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(protectedRequest.AccessHandle, responseText, StringComparison.Ordinal);
        Assert.DoesNotContain(protectedRequest.RequestId, responseText, StringComparison.Ordinal);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<LuthnDbContext>();
        Assert.Equal(0, (await db.SensitiveAccessGrants.SingleAsync()).SuccessfulReadCount);
        Assert.Empty(await db.AuditEvents.Where(record =>
            record.Action == "sensitive_access.protected_result_read").ToArrayAsync());
    }

    [Fact]
    public async Task WaitReturnsDeniedExpiredTimedOutAndCancelledWithoutReadingProtectedResult()
    {
        using var factory = CreateFactory();
        using var alice = Client(factory, AliceBearer);
        using var operatorClient = Client(factory, OperatorBearer);
        await SeedProtectedQuoteAsync(factory);

        var deniedRequest = await ResolveAsync(alice, "거절 상태 확인");
        using (var denial = await operatorClient.PostAsJsonAsync(
            $"/api/access-requests/{deniedRequest.RequestId}/deny",
            new { reason = "거절 테스트" }))
        {
            Assert.Equal(HttpStatusCode.OK, denial.StatusCode);
        }
        await AssertWaitStatusAsync(alice, deniedRequest.AccessHandle, "denied", 1);

        var expiredRequest = await ResolveAsync(alice, "만료 상태 확인");
        await using (var expireScope = factory.Services.CreateAsyncScope())
        {
            var db = expireScope.ServiceProvider.GetRequiredService<LuthnDbContext>();
            var request = await db.SensitiveAccessRequests.SingleAsync(record =>
                record.Id == expiredRequest.RequestId);
            request.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1);
            request.UpdatedAt = request.ExpiresAt;
            await db.SaveChangesAsync();
        }
        await AssertWaitStatusAsync(alice, expiredRequest.AccessHandle, "expired", 1);

        var timedOutRequest = await ResolveAsync(alice, "시간 초과 상태 확인");
        await AssertWaitStatusAsync(alice, timedOutRequest.AccessHandle, "timed-out", 1);

        var cancelledRequest = await ResolveAsync(alice, "취소 상태 확인");
        await using (var cancelScope = factory.Services.CreateAsyncScope())
        {
            var workflow = cancelScope.ServiceProvider.GetRequiredService<ISensitiveAccessWorkflow>();
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
            var cancelled = await workflow.WaitForProtectedInformationAccessAsync(
                cancelledRequest.AccessHandle,
                TimeSpan.FromSeconds(5),
                TimeSpan.FromMilliseconds(100),
                new LuthnRequestPrincipal(
                    "alice",
                    "personal:alice",
                    LuthnActorKind.Service,
                    "alice-agent",
                    IsOperator: false),
                cancellation.Token);
            Assert.Equal("cancelled", cancelled.Status);
            Assert.DoesNotContain("content", cancelled.Message, StringComparison.OrdinalIgnoreCase);
        }

        await using var verifyScope = factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<LuthnDbContext>();
        Assert.All(await verifyDb.SensitiveAccessGrants.ToArrayAsync(), grant =>
            Assert.Equal(0, grant.SuccessfulReadCount));
        Assert.Empty(await verifyDb.AuditEvents.Where(record =>
            record.Action == "sensitive_access.protected_result_read").ToArrayAsync());
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
            ConfigureToken(builder, 3, "other-workspace", OtherWorkspaceBearer, "bob", false,
                "access.request");
            builder.UseSetting("Luthn:Auth:Tokens:3:WorkspaceId", "personal:bob");
        });

    private static async Task<ProtectedRequest> ResolveAsync(HttpClient client, string reason)
    {
        using var response = await client.PostAsJsonAsync("/api/access-requests/resolve", new
        {
            memoryItemId = "memory-protected-quote",
            reason
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        return new ProtectedRequest(
            body.RootElement.GetProperty("requestId").GetString()!,
            body.RootElement.GetProperty("accessHandle").GetString()!);
    }

    private static async Task AssertWaitStatusAsync(
        HttpClient client,
        string accessHandle,
        string expectedStatus,
        int maxWaitSeconds)
    {
        using var response = await client.PostAsJsonAsync(
            "/api/access-requests/protected-wait",
            new { accessHandle, maxWaitSeconds, pollIntervalMs = 100 });
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(expectedStatus, body.RootElement.GetProperty("status").GetString());
        Assert.Equal(2, body.RootElement.EnumerateObject().Count());
    }

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

    private sealed record ProtectedRequest(string RequestId, string AccessHandle);
}
