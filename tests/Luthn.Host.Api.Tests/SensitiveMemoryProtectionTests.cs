using System.Security.Cryptography;
using System.Net;
using System.Net.Http.Json;
using Luthn.Core.Classification;
using Luthn.Core.Memory;
using Luthn.Core.Persistence;
using Luthn.Core.Policy;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Luthn.Host.Api.Tests;

public sealed class SensitiveMemoryProtectionTests
{
    [Fact]
    public void ProtectedPayloadRoundTripsAndRejectsTamperOrDifferentMemoryPurpose()
    {
        var protector = CreateProtector();
        var payload = Payload("secret-title", "secret-summary");

        var protectedPayload = protector.Protect("memory-a", payload);
        var roundTrip = protector.Unprotect("memory-a", protectedPayload);
        var tampered = protectedPayload[..^1] +
            (protectedPayload[^1] == 'A' ? "B" : "A");

        Assert.Equal(payload.ContractVersion, roundTrip.ContractVersion);
        Assert.Equal(payload.Title, roundTrip.Title);
        Assert.Equal(payload.SafeSummary, roundTrip.SafeSummary);
        Assert.Equal(payload.CoreTags, roundTrip.CoreTags);
        Assert.Equal(payload.ProjectKey, roundTrip.ProjectKey);
        Assert.Equal(payload.TaskKey, roundTrip.TaskKey);
        Assert.Equal(payload.TopicTags, roundTrip.TopicTags);
        Assert.Equal(payload.SourceSessionId, roundTrip.SourceSessionId);
        Assert.DoesNotContain("secret-title", protectedPayload, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-summary", protectedPayload, StringComparison.Ordinal);
        Assert.Throws<CryptographicException>(() => protector.Unprotect("memory-b", protectedPayload));
        Assert.Throws<CryptographicException>(() => protector.Unprotect("memory-a", tampered));
    }

    [Fact]
    public async Task LegacyPrivateRowsMigrateToInertProjectionAndEncryptedPayloadIdempotently()
    {
        var options = CreateOptions();
        await using var db = new LuthnDbContext(options);
        db.SharedMemoryItems.Add(LegacyRecord("memory-legacy", "legacy-secret-title", "legacy-secret-summary"));
        await db.SaveChangesAsync();
        var protector = CreateProtector();
        var state = new SensitiveMemoryProtectionState();
        var migrator = CreateMigrator(db, protector, state);

        await migrator.MigrateAndVerifyAsync();
        await migrator.MigrateAndVerifyAsync();

        Assert.True(state.IsReady);
        Assert.Equal(0, state.MigratedRecords);
        var memory = await db.SharedMemoryItems.AsNoTracking().SingleAsync();
        var encrypted = await db.SensitiveMemoryPayloads.AsNoTracking().SingleAsync();
        Assert.True(SensitiveMemoryPersistence.IsInertProjection(memory));
        Assert.DoesNotContain("legacy-secret", memory.SearchTerms, StringComparison.Ordinal);
        Assert.DoesNotContain("legacy-secret", memory.SearchTagKeys, StringComparison.Ordinal);
        Assert.DoesNotContain("legacy-secret", encrypted.ProtectedPayload, StringComparison.Ordinal);
        var plaintext = protector.Unprotect(memory.Id, encrypted.ProtectedPayload);
        Assert.Equal("legacy-secret-title", plaintext.Title);
        Assert.Equal("legacy-secret-summary", plaintext.SafeSummary);
        Assert.Equal(["legacy-secret-tag"], plaintext.CoreTags);
        Assert.Equal("legacy-secret-project", plaintext.ProjectKey);
        Assert.Equal("legacy-secret-task", plaintext.TaskKey);
        Assert.Equal(["legacy-secret-topic"], plaintext.TopicTags);
        Assert.Equal("legacy-secret-session", plaintext.SourceSessionId);
        var audit = await db.AuditEvents.AsNoTracking()
            .SingleAsync(record => record.Action == "memory.protection.migrated");
        Assert.Equal("sensitive-memory-payloads", audit.SubjectId);
        Assert.Equal("metadata-only", audit.PayloadClass);
        Assert.Equal("encrypted-payload-only", audit.RedactionState);
    }

    [Fact]
    public async Task LegacyMigrationFailureDoesNotOverwritePlaintextOrPersistPartialCiphertext()
    {
        var options = CreateOptions();
        await using (var seed = new LuthnDbContext(options))
        {
            seed.SharedMemoryItems.AddRange(
                LegacyRecord("memory-a", "first-secret", "first-summary"),
                LegacyRecord("memory-b", "second-secret", "second-summary"));
            await seed.SaveChangesAsync();
        }

        await using (var failing = new LuthnDbContext(options))
        {
            var protector = new ThrowOnSecondProtectProtector(CreateProtector());
            var migrator = CreateMigrator(
                failing,
                protector,
                new SensitiveMemoryProtectionState());

            var error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => migrator.MigrateAndVerifyAsync());

            Assert.Equal("Sensitive memory protection startup verification failed.", error.Message);
        }

        await using var verify = new LuthnDbContext(options);
        var records = await verify.SharedMemoryItems.AsNoTracking().OrderBy(record => record.Id).ToArrayAsync();
        Assert.Equal(["first-secret", "second-secret"], records.Select(record => record.Title));
        Assert.Empty(await verify.SensitiveMemoryPayloads.AsNoTracking().ToArrayAsync());
        Assert.Empty(await verify.AuditEvents.AsNoTracking().ToArrayAsync());
    }

    [Fact]
    public async Task ProtectionFailureKeepsLivenessObservableAndBlocksProductTraffic()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Luthn:TestingDatabaseName", Guid.NewGuid().ToString("N"));
            builder.ConfigureServices(services =>
                services.AddSingleton<ISensitiveMemoryPayloadProtector, FailingValidationProtector>());
        });
        using var client = factory.CreateClient();

        using var health = await client.GetAsync("/healthz");
        using var readiness = await client.GetAsync("/readyz");
        using var blocked = await client.PostAsJsonAsync("/api/memory/items", new
        {
            title = "must-not-be-logged",
            safeSummary = "must-not-be-stored",
            coreTags = new[] { "security" }
        });
        var blockedBody = await blocked.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, readiness.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, blocked.StatusCode);
        Assert.DoesNotContain("must-not", blockedBody, StringComparison.Ordinal);
        Assert.Contains("Sensitive memory protection is not ready", blockedBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvalidExistingCiphertextFailsClosedWithoutChangingStoredData()
    {
        var options = CreateOptions();
        await using (var seed = new LuthnDbContext(options))
        {
            var record = LegacyRecord(
                "memory-corrupt",
                SensitiveMemoryPersistence.ProtectedTitle,
                SensitiveMemoryPersistence.ProtectedSummary);
            record.CoreTags = [];
            record.ProjectKey = null;
            record.TaskKey = null;
            record.TopicTags = [];
            record.SourceSessionId = null;
            seed.SharedMemoryItems.Add(record);
            seed.SensitiveMemoryPayloads.Add(new SensitiveMemoryPayloadRecord
            {
                MemoryItemId = record.Id,
                ContractVersion = SensitiveMemoryPayload.CurrentContractVersion,
                ProtectionScheme = DataProtectionSensitiveMemoryPayloadProtector.Scheme,
                ProtectedPayload = "invalid-ciphertext",
                CreatedAt = record.CreatedAt,
                UpdatedAt = record.UpdatedAt
            });
            await seed.SaveChangesAsync();
        }

        await using (var db = new LuthnDbContext(options))
        {
            var state = new SensitiveMemoryProtectionState();
            var migrator = CreateMigrator(db, CreateProtector(), state);

            await Assert.ThrowsAsync<InvalidOperationException>(() => migrator.MigrateAndVerifyAsync());
            Assert.False(state.IsReady);
        }

        await using var verify = new LuthnDbContext(options);
        Assert.True(SensitiveMemoryPersistence.IsInertProjection(
            await verify.SharedMemoryItems.AsNoTracking().SingleAsync()));
        Assert.Equal(
            "invalid-ciphertext",
            (await verify.SensitiveMemoryPayloads.AsNoTracking().SingleAsync()).ProtectedPayload);
        Assert.Empty(await verify.AuditEvents.AsNoTracking().ToArrayAsync());
    }

    [Fact]
    public async Task SensitiveWriteProtectionFailurePersistsNoPlaintextOrAudit()
    {
        var options = CreateOptions();
        await using var db = new LuthnDbContext(options);
        var request = new CreateMemoryItemRequest
        {
            Title = "Sensitive memory",
            SafeSummary = "Credential rotation detail must stay private.",
            CoreTags = ["security"],
            Visibility = MemoryVisibility.SharedAcrossAgents
        };

        await Assert.ThrowsAsync<CryptographicException>(() => MemoryEndpoints.CreateMemoryItem(
            request,
            new MockContentClassifier(),
            new PolicyEngine(),
            new FailingValidationProtector(),
            db,
            new DefaultHttpContext(),
            CancellationToken.None));

        Assert.Empty(await db.SharedMemoryItems.AsNoTracking().ToArrayAsync());
        Assert.Empty(await db.SensitiveMemoryPayloads.AsNoTracking().ToArrayAsync());
        Assert.Empty(await db.AuditEvents.AsNoTracking().ToArrayAsync());
    }

    private static DataProtectionSensitiveMemoryPayloadProtector CreateProtector() =>
        new(new EphemeralDataProtectionProvider());

    private static SensitiveMemoryPayload Payload(string title, string summary) => new(
        SensitiveMemoryPayload.CurrentContractVersion,
        title,
        summary,
        ["legacy-secret-tag"],
        "legacy-secret-project",
        "legacy-secret-task",
        ["legacy-secret-topic"],
        "legacy-secret-session");

    private static SharedMemoryItemRecord LegacyRecord(string id, string title, string summary) => new()
    {
        Id = id,
        Title = title,
        SafeSummary = summary,
        Sensitivity = SensitivityLevel.Restricted,
        CoreTags = ["legacy-secret-tag"],
        ProjectKey = "legacy-secret-project",
        TaskKey = "legacy-secret-task",
        TopicTags = ["legacy-secret-topic"],
        Visibility = MemoryVisibility.PrivateToOwner,
        RetentionKind = MemoryRetentionKind.Durable,
        SourceSessionId = "legacy-secret-session",
        AllowsAgentContext = false,
        CreatedAt = DateTimeOffset.Parse("2026-07-01T00:00:00Z"),
        UpdatedAt = DateTimeOffset.Parse("2026-07-01T00:00:00Z"),
        CreatedBy = "agent-service"
    };

    private static DbContextOptions<LuthnDbContext> CreateOptions() =>
        new DbContextOptionsBuilder<LuthnDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

    private static SensitiveMemoryPayloadMigrator CreateMigrator(
        LuthnDbContext db,
        ISensitiveMemoryPayloadProtector protector,
        SensitiveMemoryProtectionState state) =>
        new(
            db,
            protector,
            state,
            TimeProvider.System,
            NullLogger<SensitiveMemoryPayloadMigrator>.Instance);

    private sealed class ThrowOnSecondProtectProtector(
        ISensitiveMemoryPayloadProtector inner) : ISensitiveMemoryPayloadProtector
    {
        private int _protectCount;
        public string ProtectionScheme => inner.ProtectionScheme;
        public string Protect(string memoryItemId, SensitiveMemoryPayload payload)
        {
            if (Interlocked.Increment(ref _protectCount) == 2)
            {
                throw new CryptographicException("simulated key failure");
            }

            return inner.Protect(memoryItemId, payload);
        }

        public SensitiveMemoryPayload Unprotect(string memoryItemId, string protectedPayload) =>
            inner.Unprotect(memoryItemId, protectedPayload);
        public void ValidateKeyRing() => inner.ValidateKeyRing();
    }

    private sealed class FailingValidationProtector : ISensitiveMemoryPayloadProtector
    {
        public string ProtectionScheme => DataProtectionSensitiveMemoryPayloadProtector.Scheme;
        public string Protect(string memoryItemId, SensitiveMemoryPayload payload) =>
            throw new CryptographicException("simulated missing key ring");
        public SensitiveMemoryPayload Unprotect(string memoryItemId, string protectedPayload) =>
            throw new CryptographicException("simulated missing key ring");
        public void ValidateKeyRing() => throw new CryptographicException("simulated missing key ring");
    }
}
