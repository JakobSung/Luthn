using System.Text.RegularExpressions;
using Luthn.Core.Classification;
using Luthn.Core.Persistence;
using Luthn.Core.Search;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Options;

namespace Luthn.Host.Api.Tests;

public sealed class PostgresIntegrationSmokeTests
{
    [Fact]
    public async Task DisposablePostgresDatabaseRunsMigrationsRetryingTransactionsAndApiReadinessWhenEnabled()
    {
        var connectionString = Environment.GetEnvironmentVariable("LUTHN_POSTGRES_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        if (!string.Equals(
            Environment.GetEnvironmentVariable("LUTHN_POSTGRES_TEST_ALLOW_RESET"),
            "true",
            StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!IsDisposableTestDatabase(connectionString))
        {
            throw new InvalidOperationException(
                "LUTHN_POSTGRES_TEST_CONNECTION must target a disposable database whose name starts with luthn_test.");
        }

        var optionsBuilder = new DbContextOptionsBuilder<LuthnDbContext>();
        optionsBuilder.UseLuthnPostgres(new LuthnDatabaseOptions(connectionString, EnableRetries: true));
        var options = optionsBuilder.Options;

        await using var db = new LuthnDbContext(options);
        await db.Database.EnsureDeletedAsync();

        const string migrationUnderTest = "20260718110000_AddSensitiveAccessRequestExpiryAndSession";
        var migrations = (await db.Database.GetMigrationsAsync()).ToArray();
        var migrationIndex = Array.IndexOf(migrations, migrationUnderTest);
        Assert.True(migrationIndex > 0);

        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(migrations[migrationIndex - 1]);
        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO source_events
                ("Id", "SourceSystem", "SourceType", "ReceivedAt", "ContentDigest", "ContainsSensitiveMaterial")
            VALUES
                ('source-legacy-approved', 'test', 'postgres-smoke', CURRENT_TIMESTAMP, 'sha256:legacy-approved', TRUE);

            INSERT INTO sensitive_record_references
                ("Id", "SourceEventId", "SourceSystem", "SourceType", "ReceivedAt", "ContainsSensitiveMaterial", "ReferenceLabel", "RedactedSummary")
            VALUES
                ('sensitive-ref-legacy-approved', 'source-legacy-approved', 'test', 'postgres-smoke', CURRENT_TIMESTAMP, TRUE, 'sensitive-record:source-legacy-approved', 'Reviewed legacy output.');

            INSERT INTO sensitive_access_requests
                ("Id", "SensitiveRecordReferenceId", "RequestedBy", "RequestReason", "Status", "CreatedAt", "UpdatedAt")
            VALUES
                ('access-legacy-approved', 'sensitive-ref-legacy-approved', 'postgres-smoke', 'Verify approved result survives migration.', 'Approved', CURRENT_TIMESTAMP, CURRENT_TIMESTAMP);
            """);
        await migrator.MigrateAsync();

        var migratedRequest = await db.SensitiveAccessRequests
            .SingleAsync(record => record.Id == "access-legacy-approved");
        Assert.Equal("Reviewed legacy output.", migratedRequest.RedactedSummary);

        var pending = await db.Database.GetPendingMigrationsAsync();
        Assert.Empty(pending);

        var now = DateTimeOffset.UtcNow;
        db.SourceEvents.Add(new SourceEventRecord
        {
            Id = "source-old-db-match",
            SourceSystem = "test",
            SourceType = "postgres-smoke",
            ReceivedAt = now.AddDays(-2),
            ContentDigest = "sha256:old-db-match"
        });
        db.SourceEvents.AddRange(Enumerable.Range(0, 1001).Select(index => new SourceEventRecord
        {
            Id = $"source-newer-db-nonmatch-{index}",
            SourceSystem = "test",
            SourceType = "postgres-smoke",
            ReceivedAt = now.AddMinutes(index),
            ContentDigest = $"sha256:newer-db-nonmatch-{index}"
        }));
        db.WikiProposals.Add(new WikiProposalRecord
        {
            Id = "wiki-old-db-match",
            SourceEventId = "source-old-db-match",
            Title = "Needle recovery runbook",
            SafeSummary = "Public-safe database recovery steps.",
            Sensitivity = SensitivityLevel.Public,
            CoreTags = ["needle"],
            AllowsAgentContext = true,
            CreatedAt = now.AddDays(-2)
        });
        db.WikiProposals.AddRange(Enumerable.Range(0, 1001).Select(index => new WikiProposalRecord
        {
            Id = $"wiki-newer-db-nonmatch-{index}",
            SourceEventId = $"source-newer-db-nonmatch-{index}",
            Title = $"General database release {index}",
            SafeSummary = "Public-safe unmatched projection.",
            Sensitivity = SensitivityLevel.Public,
            CoreTags = ["release"],
            AllowsAgentContext = true,
            CreatedAt = now.AddMinutes(index)
        }));
        db.SourceEvents.Add(new SourceEventRecord
        {
            Id = "source-sensitive-retry-smoke",
            SourceSystem = "test",
            SourceType = "postgres-smoke",
            ReceivedAt = now,
            ContentDigest = "sha256:sensitive-retry-smoke",
            ContainsSensitiveMaterial = true
        });
        db.SensitiveRecordReferences.Add(new SensitiveRecordReferenceRecord
        {
            Id = "sensitive-ref-retry-smoke",
            SourceEventId = "source-sensitive-retry-smoke",
            SourceSystem = "test",
            SourceType = "postgres-smoke",
            ReceivedAt = now,
            ContainsSensitiveMaterial = true,
            ReferenceLabel = "sensitive-record:source-sensitive-retry-smoke",
            RedactedSummary = ""
        });
        db.SensitiveAccessRequests.AddRange(
            new SensitiveAccessRequestRecord
            {
                Id = "access-retry-decision-smoke",
                SensitiveRecordReferenceId = "sensitive-ref-retry-smoke",
                RequestedBy = "postgres-smoke",
                SessionId = "session-retry-decision-smoke",
                RequestReason = "Verify retry-strategy decision transaction.",
                Status = SensitiveAccessRequestStatus.Pending,
                CreatedAt = now,
                ExpiresAt = now.AddMinutes(10),
                UpdatedAt = now
            },
            new SensitiveAccessRequestRecord
            {
                Id = "access-retry-expiry-smoke",
                SensitiveRecordReferenceId = "sensitive-ref-retry-smoke",
                RequestedBy = "postgres-smoke",
                SessionId = "session-retry-expiry-smoke",
                RequestReason = "Verify retry-strategy expiry transaction.",
                Status = SensitiveAccessRequestStatus.Pending,
                CreatedAt = now.AddMinutes(-10),
                ExpiresAt = now.AddMinutes(-1),
                UpdatedAt = now.AddMinutes(-10)
            });
        await db.SaveChangesAsync();

        await using var operationDb = new LuthnDbContext(options);

        var decision = await SensitiveAccessEndpoints.DenyRequest(
            "access-retry-decision-smoke",
            new SensitiveAccessDecisionRequest { Reason = "PostgreSQL retry transaction smoke test." },
            operationDb,
            new DefaultHttpContext(),
            NullOperationalMetrics.Instance,
            CancellationToken.None);
        var denied = Assert.IsType<Ok<SensitiveAccessRequestResponse>>(decision.Result);
        Assert.Equal("Denied", denied.Value!.Status);

        var expiry = await SensitiveAccessEndpoints.ReadRequest(
            "access-retry-expiry-smoke",
            operationDb,
            CancellationToken.None);
        var expired = Assert.IsType<Ok<SensitiveAccessRequestResponse>>(expiry.Result);
        Assert.Equal("Expired", expired.Value!.Status);
        Assert.Equal(1, await operationDb.AuditEvents.CountAsync(record =>
            record.SubjectId == "access-retry-expiry-smoke" &&
            record.Action == "sensitive_access.expired"));

        var selector = new DbBackedRetrievalCandidateSelector(db, TimeProvider.System);
        var candidates = await selector.SelectAgentContextAsync(
            new SafeSearchRequest("needle", ["needle"], 10),
            CancellationToken.None);
        var candidate = Assert.Single(candidates);
        Assert.Equal("wiki-old-db-match", candidate.Id);

        var readiness = await ClassificationEndpoints.CheckReadiness(
            db,
            new FakeHostEnvironment("Development"),
            Options.Create(new LuthnAuthOptions()),
            Options.Create(new LuthnHostOperationalOptions()),
            new StaticSettingsStore(new OperatorClassificationProviderSettings()),
            CancellationToken.None);
        var ready = Assert.IsType<Ok<ReadinessResponse>>(readiness);
        var response = Assert.IsType<ReadinessResponse>(ready.Value);

        Assert.Equal("ready", response.Status);
        Assert.Equal("database", response.Dependency);
    }

    private static bool IsDisposableTestDatabase(string connectionString) =>
        Regex.IsMatch(
            connectionString,
            @"(^|;)\s*(Database|Db)\s*=\s*luthn_test[\w-]*\s*(;|$)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private sealed class StaticSettingsStore(
        OperatorClassificationProviderSettings settings) : IOperatorClassificationSettingsStore
    {
        public OperatorClassificationProviderSettings Current => settings;

        public ValueTask<OperatorClassificationProviderSettings> ReadAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(settings);

        public ValueTask<OperatorClassificationProviderSettings> SaveAsync(
            SaveClassificationProviderConfigurationRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
