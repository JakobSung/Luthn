using System.Text.RegularExpressions;
using Luthn.Core.Classification;
using Luthn.Core.Memory;
using Luthn.Core.Persistence;
using Luthn.Core.Search;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;

namespace Luthn.Host.Api.Tests;

public sealed class PostgresIntegrationSmokeTests
{
    [Fact]
    public async Task DisposablePostgresDatabasePrunesExpiredAutomaticTurnCapsulesWhenEnabled()
    {
        var connectionString = Environment.GetEnvironmentVariable("LUTHN_POSTGRES_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString) ||
            !string.Equals(
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
        var now = DateTimeOffset.Parse("2026-07-23T12:00:00Z");

        await using (var seed = new LuthnDbContext(options))
        {
            await seed.Database.EnsureDeletedAsync();
            await seed.Database.MigrateAsync();
            AddCleanupCapsule(seed, "cleanup-oldest", now.AddDays(-2), sensitive: true);
            AddCleanupCapsule(seed, "cleanup-next", now.AddDays(-1));
            AddCleanupCapsule(
                seed,
                "cleanup-durable",
                expiresAt: null,
                retentionKind: MemoryRetentionKind.Durable);
            await seed.SaveChangesAsync();
        }

        await using var firstDb = new LuthnDbContext(options);
        await using var secondDb = new LuthnDbContext(options);
        var results = await Task.WhenAll(
            new AutomaticTurnRetentionCleanupProcessor(firstDb).ProcessBatchAsync(now, 1),
            new AutomaticTurnRetentionCleanupProcessor(secondDb).ProcessBatchAsync(now, 1));
        Assert.Equal(1, results.Sum(result => result.DeletedCount));

        await using (var remainingDb = new LuthnDbContext(options))
        {
            var remaining = await new AutomaticTurnRetentionCleanupProcessor(remainingDb)
                .ProcessBatchAsync(now, 100);
            Assert.Equal(1, remaining.DeletedCount);
        }

        await using var verify = new LuthnDbContext(options);
        Assert.False(await verify.SharedMemoryItems.AnyAsync(record => record.Id == "memory-cleanup-oldest"));
        Assert.False(await verify.SharedMemoryItems.AnyAsync(record => record.Id == "memory-cleanup-next"));
        Assert.False(await verify.SensitiveMemoryPayloads.AnyAsync(record =>
            record.MemoryItemId == "memory-cleanup-oldest"));
        Assert.False(await verify.CollectionProvenance.AnyAsync(record =>
            record.SourceEventId == "cleanup-oldest" ||
            record.SourceEventId == "cleanup-next"));
        Assert.False(await verify.ClassificationResults.AnyAsync(record =>
            record.SourceEventId == "cleanup-oldest" ||
            record.SourceEventId == "cleanup-next"));
        Assert.False(await verify.SourceEvents.AnyAsync(record =>
            record.Id == "cleanup-oldest" ||
            record.Id == "cleanup-next"));
        Assert.True(await verify.SharedMemoryItems.AnyAsync(record =>
            record.Id == "memory-cleanup-durable"));
        Assert.Equal(
            2,
            await verify.AuditEvents.CountAsync(record =>
                record.Action == "turn_summary.retention.pruned"));
        Assert.Equal(
            2,
            await verify.AuditEvents.CountAsync(record =>
                record.Action == "turn_summary.intake.classified" &&
                (record.SubjectId == "cleanup-oldest" || record.SubjectId == "cleanup-next")));

        var cleanupIndexCount = await verify.Database.SqlQueryRaw<int>(
                """
                SELECT COUNT(*)::int AS "Value"
                FROM pg_indexes
                WHERE tablename = 'shared_memory_items'
                  AND indexname = 'IX_shared_memory_items_cleanup_candidates'
                  AND indexdef LIKE '%("RetentionKind", "ExternalPublicationState", "ExpiresAt", "CreatedAt", "Id")%'
                """)
            .SingleAsync();
        Assert.Equal(1, cleanupIndexCount);
    }

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
        var migrations = db.Database.GetMigrations().ToArray();
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

            INSERT INTO source_events
                ("Id", "SourceSystem", "SourceType", "ReceivedAt", "ContentDigest", "ContainsSensitiveMaterial")
            VALUES
                ('source-legacy-recall', 'test', 'postgres-smoke', CURRENT_TIMESTAMP, 'sha256:legacy-recall', FALSE);

            INSERT INTO wiki_proposals
                ("Id", "SourceEventId", "Title", "SafeSummary", "Sensitivity", "CoreTags", "AllowsAgentContext", "CreatedAt")
            VALUES
                ('wiki-legacy-recall', 'source-legacy-recall', 'Legacy recall', 'Legacy safe projection.', 'Public', '["legacy"]'::jsonb, TRUE, CURRENT_TIMESTAMP);

            INSERT INTO shared_memory_items
                ("Id", "Title", "SafeSummary", "Sensitivity", "CoreTags", "Visibility", "RetentionKind", "AllowsAgentContext", "CreatedAt", "UpdatedAt", "CreatedBy")
            VALUES
                ('memory-legacy-recall', 'Legacy memory', 'Legacy safe memory.', 'Public', '["legacy"]'::jsonb, 'SharedAcrossAgents', 'Durable', TRUE, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP, 'postgres-smoke');

            INSERT INTO shared_memory_items
                ("Id", "Title", "SafeSummary", "Sensitivity", "CoreTags", "Visibility", "RetentionKind", "AllowsAgentContext", "CreatedAt", "UpdatedAt", "CreatedBy")
            VALUES
                ('memory-legacy-sensitive', 'Legacy database secret', 'Legacy plaintext sensitive summary.', 'Restricted', '["database-secret"]'::jsonb, 'PrivateToOwner', 'Durable', FALSE, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP, 'postgres-smoke');

            INSERT INTO agent_connection_channels
                ("Id", "AgentId", "AgentName", "IntegrationKind", "ConnectorVersion", "Channel", "ConfigurationOwner", "IsConfigured", "VerificationState", "ActivityState", "FirstObservedAt", "UpdatedAt")
            VALUES
                ('codex:mcp', 'codex', 'Codex', 'host-hook-mcp', 'legacy', 'mcp', 'luthn', TRUE, 'Verified', 'Unknown', CURRENT_TIMESTAMP, CURRENT_TIMESTAMP);
            """);
        await migrator.MigrateAsync();

        var payloadProtector = new DataProtectionSensitiveMemoryPayloadProtector(
            new EphemeralDataProtectionProvider());
        var protectionState = new SensitiveMemoryProtectionState();
        var payloadMigrator = new SensitiveMemoryPayloadMigrator(
            db,
            payloadProtector,
            protectionState,
            TimeProvider.System,
            NullLogger<SensitiveMemoryPayloadMigrator>.Instance);
        await payloadMigrator.MigrateAndVerifyAsync();

        var migratedRequest = await db.SensitiveAccessRequests
            .SingleAsync(record => record.Id == "access-legacy-approved");
        Assert.Equal("Reviewed legacy output.", migratedRequest.RedactedSummary);
        Assert.Equal("local-owner", migratedRequest.OwnerUserId);
        Assert.Equal(
            "local-owner",
            (await db.SensitiveRecordReferences.SingleAsync(record => record.Id == "sensitive-ref-legacy-approved")).OwnerUserId);
        var migratedWiki = await db.WikiProposals.SingleAsync(record => record.Id == "wiki-legacy-recall");
        Assert.Equal("local-owner", migratedWiki.OwnerUserId);
        Assert.Null(migratedWiki.ProjectKey);
        Assert.Null(migratedWiki.TaskKey);
        Assert.Empty(migratedWiki.TopicTags);
        var migratedMemory = await db.SharedMemoryItems.SingleAsync(record => record.Id == "memory-legacy-recall");
        Assert.Equal("local-owner", migratedMemory.OwnerUserId);
        Assert.Null(migratedMemory.ProjectKey);
        Assert.Null(migratedMemory.TaskKey);
        Assert.Empty(migratedMemory.TopicTags);
        var migratedAgentConnection = await db.AgentConnectionChannels
            .SingleAsync(record => record.Id == "codex:mcp");
        Assert.Equal("local-owner", migratedAgentConnection.OwnerUserId);
        var migratedSensitiveMemory = await db.SharedMemoryItems
            .AsNoTracking()
            .SingleAsync(record => record.Id == "memory-legacy-sensitive");
        var migratedSensitivePayload = await db.SensitiveMemoryPayloads
            .AsNoTracking()
            .SingleAsync(record => record.MemoryItemId == "memory-legacy-sensitive");
        Assert.True(SensitiveMemoryPersistence.IsInertProjection(migratedSensitiveMemory));
        Assert.DoesNotContain("Legacy", migratedSensitivePayload.ProtectedPayload, StringComparison.Ordinal);
        Assert.Equal(
            "Legacy plaintext sensitive summary.",
            payloadProtector.Unprotect(
                migratedSensitiveMemory.Id,
                migratedSensitivePayload.ProtectedPayload).SafeSummary);
        Assert.True(protectionState.IsReady);
        Assert.Equal(1, protectionState.MigratedRecords);
        var legacyProvenance = await db.CollectionProvenance.AsNoTracking().ToArrayAsync();
        Assert.Equal(4, legacyProvenance.Length);
        Assert.All(legacyProvenance, record =>
        {
            Assert.Equal(1, record.ContractVersion);
            Assert.Equal(CollectionProvenance.LegacyUnknownTrust, record.AuthenticatedActor);
            Assert.Equal("local-owner", record.AuthenticatedUserId);
            Assert.Equal(CollectionProvenance.LegacyUnknownTrust, record.ActorTrust);
            Assert.Equal(CollectionProvenance.LegacyUnknownTrust, record.ClaimsTrust);
            Assert.Null(record.ClaimedUserId);
            Assert.Null(record.AgentId);
            Assert.Null(record.ApplicationId);
            Assert.Null(record.PluginId);
            Assert.Null(record.ConnectorId);
            Assert.Null(record.ConnectorVersion);
            Assert.Null(record.CollectedAt);
        });
        Assert.Equal(1, legacyProvenance.Count(record => record.SourceEventId == "source-legacy-recall"));
        Assert.Equal(1, legacyProvenance.Count(record => record.MemoryItemId == "memory-legacy-recall"));
        await db.SharedMemoryItems
            .Where(record => record.Id == "memory-legacy-sensitive")
            .ExecuteDeleteAsync();
        Assert.False(await db.SensitiveMemoryPayloads
            .AnyAsync(record => record.MemoryItemId == "memory-legacy-sensitive"));
        Assert.False(await db.CollectionProvenance
            .AnyAsync(record => record.MemoryItemId == "memory-legacy-sensitive"));
        var migratedResult = await SensitiveAccessEndpoints.ReadRequestResult(
            "access-legacy-approved",
            db,
            new DefaultHttpContext(),
            CancellationToken.None);
        var migratedResultOk = Assert.IsType<Ok<SensitiveAccessResultResponse>>(migratedResult.Result);
        Assert.True(migratedResultOk.Value!.RedactedOutputAvailable);
        Assert.Equal("Reviewed legacy output.", migratedResultOk.Value.RedactedOutput);

        var pending = await db.Database.GetPendingMigrationsAsync();
        Assert.Empty(pending);
        var retainedOwnerDefaults = await db.Database.SqlQueryRaw<int>(
                """
                SELECT COUNT(*)::int AS "Value"
                FROM pg_attrdef defaults
                JOIN pg_attribute attributes
                  ON attributes.attrelid = defaults.adrelid
                 AND attributes.attnum = defaults.adnum
                JOIN pg_class tables ON tables.oid = defaults.adrelid
                WHERE (tables.relname, attributes.attname) IN (
                    ('source_events', 'OwnerUserId'),
                    ('wiki_proposals', 'OwnerUserId'),
                    ('shared_memory_items', 'OwnerUserId'),
                    ('sensitive_record_references', 'OwnerUserId'),
                    ('sensitive_access_requests', 'OwnerUserId'),
                    ('safe_projection_sync_outbox', 'OwnerUserId'),
                    ('collection_provenance', 'AuthenticatedUserId'),
                    ('agent_connection_channels', 'OwnerUserId'))
                """)
            .SingleAsync();
        Assert.Equal(0, retainedOwnerDefaults);

        db.AgentConnectionChannels.AddRange(
            new AgentConnectionChannelRecord
            {
                Id = "agent-connection-owner-alice",
                OwnerUserId = "alice",
                AgentId = "shared-agent",
                AgentName = "Shared agent",
                IntegrationKind = "postgres-smoke",
                ConnectorVersion = "1",
                Channel = "mcp",
                ConfigurationOwner = "luthn",
                IsConfigured = true,
                VerificationState = AgentConnectionVerificationState.Verified,
                FirstObservedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            },
            new AgentConnectionChannelRecord
            {
                Id = "agent-connection-owner-bob",
                OwnerUserId = "bob",
                AgentId = "shared-agent",
                AgentName = "Shared agent",
                IntegrationKind = "postgres-smoke",
                ConnectorVersion = "1",
                Channel = "mcp",
                ConfigurationOwner = "luthn",
                IsConfigured = true,
                VerificationState = AgentConnectionVerificationState.Verified,
                FirstObservedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            });
        await db.SaveChangesAsync();
        Assert.Equal(
            2,
            await db.AgentConnectionChannels.CountAsync(record => record.AgentId == "shared-agent"));

        var now = DateTimeOffset.UtcNow;
        db.SourceEvents.Add(new SourceEventRecord
        {
            Id = "source-old-db-match",
            SourceSystem = "test",
            SourceType = "postgres-smoke",
            ReceivedAt = now.AddDays(-2),
            ContentDigest = "sha256:old-db-match"
        });
        db.CollectionProvenance.Add(new CollectionProvenanceRecord
        {
            Id = "provenance-old-db-match",
            SourceEventId = "source-old-db-match",
            AuthenticatedActor = "postgres-smoke",
            ActorTrust = CollectionProvenance.ServiceTokenActorTrust,
            ClaimsTrust = CollectionProvenance.NoClaimsTrust,
            ReceivedAt = now.AddDays(-2)
        });
        db.SourceEvents.AddRange(Enumerable.Range(0, 1001).Select(index => new SourceEventRecord
        {
            Id = $"source-newer-db-nonmatch-{index}",
            SourceSystem = "test",
            SourceType = "postgres-smoke",
            ReceivedAt = now.AddMinutes(index),
            ContentDigest = $"sha256:newer-db-nonmatch-{index}"
        }));
        db.CollectionProvenance.AddRange(Enumerable.Range(0, 1001).Select(index =>
            new CollectionProvenanceRecord
            {
                Id = $"provenance-newer-{index}",
                SourceEventId = $"source-newer-db-nonmatch-{index}",
                AuthenticatedActor = "postgres-smoke",
                ActorTrust = CollectionProvenance.ServiceTokenActorTrust,
                ClaimsTrust = CollectionProvenance.NoClaimsTrust,
                ReceivedAt = now.AddMinutes(index)
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
            Id = "source-other-owner-match",
            OwnerUserId = "other-owner",
            SourceSystem = "test",
            SourceType = "postgres-smoke",
            ReceivedAt = now.AddDays(1),
            ContentDigest = "sha256:other-owner-match"
        });
        db.CollectionProvenance.Add(new CollectionProvenanceRecord
        {
            Id = "provenance-other-owner-match",
            SourceEventId = "source-other-owner-match",
            AuthenticatedActor = "postgres-smoke",
            AuthenticatedUserId = "other-owner",
            ActorTrust = CollectionProvenance.ServiceTokenActorTrust,
            ClaimsTrust = CollectionProvenance.NoClaimsTrust,
            ReceivedAt = now.AddDays(1)
        });
        db.WikiProposals.Add(new WikiProposalRecord
        {
            Id = "wiki-other-owner-match",
            OwnerUserId = "other-owner",
            SourceEventId = "source-other-owner-match",
            Title = "Needle recovery runbook",
            SafeSummary = "A newer cross-owner result must remain invisible.",
            Sensitivity = SensitivityLevel.Public,
            CoreTags = ["needle"],
            AllowsAgentContext = true,
            CreatedAt = now.AddDays(1)
        });
        db.SourceEvents.Add(new SourceEventRecord
        {
            Id = "source-sensitive-retry-smoke",
            SourceSystem = "test",
            SourceType = "postgres-smoke",
            ReceivedAt = now,
            ContentDigest = "sha256:sensitive-retry-smoke",
            ContainsSensitiveMaterial = true
        });
        db.CollectionProvenance.Add(new CollectionProvenanceRecord
        {
            Id = "provenance-sensitive-retry-smoke",
            SourceEventId = "source-sensitive-retry-smoke",
            AuthenticatedActor = "postgres-smoke",
            ActorTrust = CollectionProvenance.ServiceTokenActorTrust,
            ClaimsTrust = CollectionProvenance.NoClaimsTrust,
            ReceivedAt = now
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
            new DefaultHttpContext(),
            CancellationToken.None);
        var expired = Assert.IsType<Ok<SensitiveAccessRequestResponse>>(expiry.Result);
        Assert.Equal("Expired", expired.Value!.Status);
        Assert.Equal(1, await operationDb.AuditEvents.CountAsync(record =>
            record.SubjectId == "access-retry-expiry-smoke" &&
            record.Action == "sensitive_access.expired"));

        await Assert.ThrowsAnyAsync<Exception>(() => operationDb.Database.ExecuteSqlRawAsync(
            "UPDATE collection_provenance SET \"AgentId\" = 'tampered' WHERE \"Id\" = 'provenance-old-db-match'"));
        await Assert.ThrowsAnyAsync<Exception>(() => operationDb.Database.ExecuteSqlRawAsync(
            "UPDATE shared_memory_items SET \"OwnerUserId\" = '' WHERE \"Id\" = 'memory-legacy-recall'"));
        await using (var missingProvenanceDb = new LuthnDbContext(options))
        {
            missingProvenanceDb.SourceEvents.Add(new SourceEventRecord
            {
                Id = "source-missing-provenance",
                SourceSystem = "test",
                SourceType = "postgres-smoke",
                ReceivedAt = now,
                ContentDigest = "sha256:missing-provenance"
            });
            await Assert.ThrowsAsync<DbUpdateException>(() => missingProvenanceDb.SaveChangesAsync());
        }

        var selector = new DbBackedRetrievalCandidateSelector(db, TimeProvider.System);
        var candidates = await selector.SelectAgentContextAsync(
            new SafeSearchRequest("needle", ["needle"], 10),
            "local-owner",
            CancellationToken.None);
        var candidate = Assert.Single(candidates);
        Assert.Equal("wiki-old-db-match", candidate.Id);

        var readiness = await ClassificationEndpoints.CheckReadiness(
            db,
            new FakeHostEnvironment("Development"),
            Options.Create(new LuthnAuthOptions()),
            Options.Create(new LuthnIdentityOptions()),
            Options.Create(new LuthnHostOperationalOptions()),
            Options.Create(new ClassificationProviderOptions
            {
                Provider = "mock",
                AllowMock = true
            }),
            new StaticSettingsStore(new OperatorClassificationProviderSettings
            {
                Provider = OperatorClassificationProviderKind.Mock
            }),
            protectionState,
            CancellationToken.None);
        var ready = Assert.IsType<Ok<ReadinessResponse>>(readiness);
        var response = Assert.IsType<ReadinessResponse>(ready.Value);

        Assert.Equal("ready", response.Status);
        Assert.Equal("database", response.Dependency);
    }

    private static void AddCleanupCapsule(
        LuthnDbContext db,
        string id,
        DateTimeOffset? expiresAt,
        MemoryRetentionKind retentionKind = MemoryRetentionKind.Ephemeral,
        bool sensitive = false)
    {
        var memoryItemId = $"memory-{id}";
        var createdAt = DateTimeOffset.Parse("2026-07-20T12:00:00Z");
        db.SourceEvents.Add(new SourceEventRecord
        {
            Id = id,
            SourceSystem = "codex",
            SourceType = "turn-summary",
            ReceivedAt = createdAt,
            ContentDigest = $"sha256:{id}"
        });
        db.ClassificationResults.Add(new ClassificationResultRecord
        {
            Id = $"classification-{id}",
            SourceEventId = id,
            Sensitivity = sensitive ? SensitivityLevel.Restricted : SensitivityLevel.Public,
            Confidence = 1,
            ContainsSensitiveMaterial = sensitive,
            StorageDecision = sensitive
                ? StorageDecisionKind.SensitiveDbOnly
                : StorageDecisionKind.WikiCandidate
        });
        db.SharedMemoryItems.Add(new SharedMemoryItemRecord
        {
            Id = memoryItemId,
            Title = sensitive ? "[protected]" : $"Memory {id}",
            SafeSummary = sensitive ? "[protected]" : $"Summary {id}",
            Sensitivity = sensitive ? SensitivityLevel.Restricted : SensitivityLevel.Public,
            CoreTags = ["retention"],
            Visibility = sensitive
                ? MemoryVisibility.PrivateToOwner
                : MemoryVisibility.SharedAcrossAgents,
            RetentionKind = retentionKind,
            ExpiresAt = expiresAt,
            AllowsAgentContext = !sensitive,
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
            CreatedBy = "postgres-smoke"
        });
        db.CollectionProvenance.Add(new CollectionProvenanceRecord
        {
            Id = $"provenance-{id}",
            SourceEventId = id,
            MemoryItemId = memoryItemId,
            AuthenticatedActor = "postgres-smoke",
            ActorTrust = CollectionProvenance.ServiceTokenActorTrust,
            ClaimsTrust = CollectionProvenance.NoClaimsTrust,
            ReceivedAt = createdAt
        });
        db.AuditEvents.Add(new AuditEventRecord
        {
            Id = $"audit-{id}",
            OccurredAt = createdAt,
            Actor = "postgres-smoke",
            Action = "turn_summary.intake.classified",
            SubjectId = id,
            PayloadClass = "metadata-only",
            RedactionState = sensitive ? "encrypted-payload-only" : "safe-projection-only"
        });
        if (sensitive)
        {
            db.SensitiveMemoryPayloads.Add(new SensitiveMemoryPayloadRecord
            {
                MemoryItemId = memoryItemId,
                ProtectionScheme = "postgres-smoke",
                ProtectedPayload = "ciphertext",
                CreatedAt = createdAt,
                UpdatedAt = createdAt
            });
        }
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
