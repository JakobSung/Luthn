using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Luthn.Core.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Luthn.Host.Api.Tests;

public sealed class SensitiveAccessPolicyEndpointTests
{
    private const string ConfigureBearer = "configure-sensitive-access-local";
    private const string DecideBearer = "decide-sensitive-access-local";
    private const string ReviewBearer = "review-sensitive-access-local";

    [Fact]
    public async Task ConfigureScopedOperatorCanReadAndRevisePolicyWithoutExtendingExistingGrant()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        client.SetBearer(ConfigureBearer);

        using var initialResponse = await client.GetAsync("/api/access-requests/policy");
        using var initialBody = await JsonDocument.ParseAsync(
            await initialResponse.Content.ReadAsStreamAsync());
        var initialRevision = initialBody.RootElement.GetProperty("revision").GetInt32();
        var grantExpiry = DateTimeOffset.UtcNow.AddMinutes(5);
        await AddExistingGrantAsync(factory, initialRevision, grantExpiry);

        using var updateResponse = await client.PutAsJsonAsync(
            "/api/access-requests/policy",
            new
            {
                requestTimeoutSeconds = 120,
                grantDurationSeconds = 180,
                maximumSuccessfulReads = 3
            });
        using var updateBody = await JsonDocument.ParseAsync(
            await updateResponse.Content.ReadAsStreamAsync());

        Assert.Equal(HttpStatusCode.OK, initialResponse.StatusCode);
        Assert.True(initialResponse.Headers.CacheControl?.NoStore == true);
        Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);
        Assert.True(updateResponse.Headers.CacheControl?.NoStore == true);
        Assert.Equal(initialRevision + 1, updateBody.RootElement.GetProperty("revision").GetInt32());
        Assert.Equal(120, updateBody.RootElement.GetProperty("requestTimeoutSeconds").GetInt32());
        Assert.Equal(180, updateBody.RootElement.GetProperty("grantDurationSeconds").GetInt32());
        Assert.Equal(3, updateBody.RootElement.GetProperty("maximumSuccessfulReads").GetInt32());
        Assert.False(updateBody.RootElement.TryGetProperty("workspaceId", out _));
        Assert.False(updateBody.RootElement.TryGetProperty("createdBy", out _));

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LuthnDbContext>();
        var grant = await db.SensitiveAccessGrants.SingleAsync();
        Assert.Equal(initialRevision, grant.PolicyRevision);
        Assert.Equal(grantExpiry, grant.ExpiresAt);
        Assert.Equal(1, grant.MaximumSuccessfulReads);
    }

    [Fact]
    public async Task PolicyUpdateRejectsInvalidValuesWithoutCreatingRevision()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        client.SetBearer(ConfigureBearer);

        using var initialResponse = await client.GetAsync("/api/access-requests/policy");
        using var invalidResponse = await client.PutAsJsonAsync(
            "/api/access-requests/policy",
            new
            {
                requestTimeoutSeconds = 59,
                grantDurationSeconds = 600,
                maximumSuccessfulReads = 1
            });
        using var currentResponse = await client.GetAsync("/api/access-requests/policy");
        using var currentBody = await JsonDocument.ParseAsync(
            await currentResponse.Content.ReadAsStreamAsync());

        Assert.Equal(HttpStatusCode.OK, initialResponse.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, invalidResponse.StatusCode);
        Assert.Equal(1, currentBody.RootElement.GetProperty("revision").GetInt32());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LuthnDbContext>();
        Assert.Equal(1, await db.SensitiveAccessPolicyRevisions.CountAsync());
    }

    [Theory]
    [InlineData(DecideBearer)]
    [InlineData(ReviewBearer)]
    public async Task ReviewAndDecideScopesCannotConfigurePolicy(string bearer)
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        client.SetBearer(bearer);

        using var readResponse = await client.GetAsync("/api/access-requests/policy");
        using var updateResponse = await client.PutAsJsonAsync(
            "/api/access-requests/policy",
            new
            {
                requestTimeoutSeconds = 120,
                grantDurationSeconds = 180,
                maximumSuccessfulReads = 3
            });

        Assert.Equal(HttpStatusCode.Forbidden, readResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, updateResponse.StatusCode);
    }

    [Fact]
    public async Task ConfigureScopeDoesNotGrantReviewOrDecisionAuthority()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        client.SetBearer(ConfigureBearer);

        using var listResponse = await client.GetAsync("/api/access-requests?status=Pending");
        using var decisionResponse = await client.PostAsJsonAsync(
            "/api/access-requests/missing/deny",
            new { reason = "must remain forbidden" });

        Assert.Equal(HttpStatusCode.Forbidden, listResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, decisionResponse.StatusCode);
    }

    private static WebApplicationFactory<Program> CreateFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Luthn:TestingDatabaseName", Guid.NewGuid().ToString("N"));
            builder.UseSetting("Luthn:Auth:RequireServiceToken", "true");
            ConfigureToken(builder, 0, "access-configurer", ConfigureBearer, ServiceScopes.AccessConfigure);
            ConfigureToken(builder, 1, "access-decider", DecideBearer, ServiceScopes.AccessDecide);
            ConfigureToken(builder, 2, "access-reviewer", ReviewBearer, ServiceScopes.AccessReview);
        });

    private static void ConfigureToken(
        IWebHostBuilder builder,
        int index,
        string name,
        string bearer,
        string scope)
    {
        var prefix = $"Luthn:Auth:Tokens:{index}";
        builder.UseSetting($"{prefix}:Name", name);
        builder.UseSetting($"{prefix}:Sha256Digest", Sha256Digest(bearer));
        builder.UseSetting($"{prefix}:Scopes:0", scope);
        builder.UseSetting($"{prefix}:IsOperator", "true");
        builder.UseSetting($"{prefix}:ActorKind", nameof(LuthnActorKind.User));
    }

    private static async Task AddExistingGrantAsync(
        WebApplicationFactory<Program> factory,
        int policyRevision,
        DateTimeOffset expiresAt)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LuthnDbContext>();
        var requestId = $"existing-grant-{Guid.NewGuid():N}";
        db.SensitiveAccessRequests.Add(new SensitiveAccessRequestRecord
        {
            Id = requestId,
            SensitiveRecordReferenceId = "existing-reference",
            RequestedBy = "agent",
            SessionId = "existing-session",
            RequestReason = "existing bounded request",
            Status = SensitiveAccessRequestStatus.Approved,
            CreatedAt = expiresAt.AddMinutes(-2),
            ExpiresAt = expiresAt.AddMinutes(8),
            UpdatedAt = expiresAt.AddMinutes(-1),
            DecidedBy = "operator",
            DecidedAt = expiresAt.AddMinutes(-1),
            WorkspaceId = "default",
            OwnerUserId = LuthnIdentityOptions.DefaultSingleOwnerUserId,
            PolicyRevision = policyRevision,
            RequestTimeoutSeconds = SensitiveAccessPolicyLimits.DefaultRequestTimeoutSeconds
        });
        db.SensitiveAccessGrants.Add(new SensitiveAccessGrantRecord
        {
            SensitiveAccessRequestId = requestId,
            WorkspaceId = "default",
            OwnerUserId = LuthnIdentityOptions.DefaultSingleOwnerUserId,
            PolicyRevision = policyRevision,
            GrantDurationSeconds = SensitiveAccessPolicyLimits.DefaultGrantDurationSeconds,
            StartsAt = expiresAt.AddMinutes(-1),
            ExpiresAt = expiresAt,
            MaximumSuccessfulReads = 1
        });
        await db.SaveChangesAsync();
    }

    private static string Sha256Digest(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
