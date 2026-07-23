using Luthn.Core.Classification;
using Luthn.Core.Memory;
using Luthn.Core.Persistence;
using Luthn.Core.Search;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Luthn.Core.Persistence.Tests;

public sealed class PersistenceContractTests
{
    [Fact]
    public async Task DbContextPersistsMvpRecordsWithCoreTagsAndWithoutRawContentColumns()
    {
        await using var db = CreateDbContext();
        var receivedAt = DateTimeOffset.Parse("2026-01-01T00:00:00Z");

        db.SourceEvents.Add(new SourceEventRecord
        {
            Id = "source-1",
            SourceSystem = "local",
            SourceType = "note",
            ReceivedAt = receivedAt,
            ContentDigest = "sha256:example",
            ContainsSensitiveMaterial = true
        });
        db.ClassificationResults.Add(new ClassificationResultRecord
        {
            Id = "classification-1",
            SourceEventId = "source-1",
            Sensitivity = SensitivityLevel.Confidential,
            Confidence = 0.9,
            Categories = ["contract"],
            ContainsSensitiveMaterial = true,
            StorageDecision = StorageDecisionKind.SensitiveDbOnly
        });
        db.WikiProposals.Add(new WikiProposalRecord
        {
            Id = "wiki-1",
            SourceEventId = "source-1",
            Title = "Safe runbook",
            SafeSummary = "Public-safe release steps.",
            Sensitivity = SensitivityLevel.Public,
            CoreTags = ["runbook"],
            ProjectKey = "luthn",
            TaskKey = "persistence",
            TopicTags = ["recall"],
            AllowsAgentContext = true,
            CreatedAt = receivedAt
        });
        db.SensitiveRecordReferences.Add(new SensitiveRecordReferenceRecord
        {
            Id = "vault-ref-1",
            SourceEventId = "source-1",
            SourceSystem = "local",
            SourceType = "note",
            ReceivedAt = receivedAt,
            ContainsSensitiveMaterial = true,
            ReferenceLabel = "sensitive-record:source-1"
        });
        db.AuditEvents.Add(new AuditEventRecord
        {
            Id = "audit-1",
            OccurredAt = receivedAt,
            Actor = "local-tools",
            Action = "classification.preview",
            SubjectId = "source-1",
            PayloadClass = "metadata-only",
            RedactionState = "safe-projection-only"
        });
        db.SharedMemoryItems.Add(new SharedMemoryItemRecord
        {
            Id = "memory-1",
            Title = "Safe memory",
            SafeSummary = "Public-safe memory summary.",
            Sensitivity = SensitivityLevel.Public,
            CoreTags = ["runbook"],
            ProjectKey = "luthn",
            TaskKey = "persistence",
            TopicTags = ["recall"],
            Visibility = MemoryVisibility.SharedAcrossAgents,
            RetentionKind = MemoryRetentionKind.Durable,
            AllowsAgentContext = true,
            CreatedAt = receivedAt,
            UpdatedAt = receivedAt,
            CreatedBy = "local-tools"
        });
        db.AgentConnectionChannels.Add(new AgentConnectionChannelRecord
        {
            Id = "codex:mcp",
            OwnerUserId = "local-owner",
            AgentId = "codex",
            AgentName = "Codex",
            IntegrationKind = "host-hook-mcp",
            ConnectorVersion = "1",
            Channel = "mcp",
            ConfigurationOwner = "luthn",
            IsConfigured = true,
            VerificationState = AgentConnectionVerificationState.Verified,
            ActivityState = AgentConnectionActivityState.Succeeded,
            LastVerifiedAt = receivedAt,
            LastActivityAt = receivedAt,
            LastSuccessfulActivityAt = receivedAt,
            FirstObservedAt = receivedAt,
            UpdatedAt = receivedAt
        });

        await db.SaveChangesAsync();

        Assert.Equal(1, await db.SourceEvents.CountAsync());
        Assert.Equal(1, await db.ClassificationResults.CountAsync());
        var proposal = await db.WikiProposals.SingleAsync();
        Assert.Equal(["runbook"], proposal.CoreTags);
        Assert.Equal("luthn", proposal.ProjectKey);
        Assert.Equal("persistence", proposal.TaskKey);
        Assert.Equal(["recall"], proposal.TopicTags);
        Assert.Contains(SafeSearchText.ToIndexMarker("luthn"), proposal.SearchTerms, StringComparison.Ordinal);
        Assert.Contains(SafeSearchText.ToIndexMarker("recall"), proposal.SearchTerms, StringComparison.Ordinal);
        Assert.Contains(SafeSearchText.ToIndexMarker("release"), proposal.SearchTerms, StringComparison.Ordinal);
        Assert.Contains(
            SafeSearchText.ToIndexMarker(SafeSearchText.BuildTagKey("runbook")),
            proposal.SearchTagKeys,
            StringComparison.Ordinal);
        Assert.Equal(1, await db.SensitiveRecordReferences.CountAsync());
        var memory = await db.SharedMemoryItems.SingleAsync();
        Assert.Equal(["runbook"], memory.CoreTags);
        Assert.Equal("luthn", memory.ProjectKey);
        Assert.Equal("persistence", memory.TaskKey);
        Assert.Equal(["recall"], memory.TopicTags);
        Assert.Contains(SafeSearchText.ToIndexMarker("memory"), memory.SearchTerms, StringComparison.Ordinal);
        Assert.Contains(
            SafeSearchText.ToIndexMarker(SafeSearchText.BuildTagKey("runbook")),
            memory.SearchTagKeys,
            StringComparison.Ordinal);
        var connection = await db.AgentConnectionChannels.SingleAsync();
        Assert.Equal("mcp", connection.Channel);
        Assert.Equal("local-owner", connection.OwnerUserId);
        Assert.Equal(AuditEventPayloadVersions.Current, (await db.AuditEvents.SingleAsync()).PayloadVersion);
        Assert.DoesNotContain(
            db.Model.GetEntityTypes().SelectMany(entity => entity.GetProperties()).Select(property => property.Name),
            propertyName => propertyName.Contains("Raw", StringComparison.OrdinalIgnoreCase)
                || propertyName.Contains("Content", StringComparison.OrdinalIgnoreCase)
                    && propertyName is not nameof(SourceEventRecord.ContentDigest));
        Assert.DoesNotContain(
            db.Model.GetEntityTypes().SelectMany(entity => entity.GetProperties()).Select(property => property.Name),
            propertyName => propertyName.Contains("Token", StringComparison.OrdinalIgnoreCase)
                || propertyName.Contains("Secret", StringComparison.OrdinalIgnoreCase)
                || propertyName.Contains("Prompt", StringComparison.OrdinalIgnoreCase)
                || propertyName.Contains("Transcript", StringComparison.OrdinalIgnoreCase)
                || propertyName.Contains("Path", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task DemoSeederCreatesIdempotentPublicSafeContextData()
    {
        await using var db = CreateDbContext();

        await DemoDataSeeder.SeedAsync(db);
        await DemoDataSeeder.SeedAsync(db);

        var proposal = Assert.Single(await db.WikiProposals.ToListAsync());
        Assert.Equal("wiki-demo-runbook", proposal.Id);
        Assert.Equal(["runbook", "demo"], proposal.CoreTags);
        Assert.Equal(SensitivityLevel.Public, proposal.Sensitivity);
        Assert.True(proposal.AllowsAgentContext);
        Assert.DoesNotContain("Raw", proposal.SafeSummary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Vault", proposal.SafeSummary, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, await db.SourceEvents.CountAsync());
        Assert.Equal(1, await db.AuditEvents.CountAsync());
    }

    [Fact]
    public void PostgresMigrationsIncludeInitialCreateForCurrentPersistenceModel()
    {
        using var db = CreatePostgresMetadataDbContext();

        var migrations = db.Database.GetMigrations().ToArray();

        Assert.Contains(migrations, migration => migration.EndsWith("_InitialCreate", StringComparison.Ordinal));
    }

    [Fact]
    public void PostgresMigrationScriptCreatesCurrentSafeSchemaOnly()
    {
        using var db = CreatePostgresMetadataDbContext();
        var migrator = db.GetService<IMigrator>();

        var script = migrator.GenerateScript(options: MigrationsSqlGenerationOptions.Idempotent);

        Assert.Contains("CREATE TABLE", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("source_events", script, StringComparison.Ordinal);
        Assert.Contains("wiki_proposals", script, StringComparison.Ordinal);
        Assert.Contains("shared_memory_items", script, StringComparison.Ordinal);
        Assert.Contains("agent_connection_channels", script, StringComparison.Ordinal);
        Assert.Contains("local_installation_state", script, StringComparison.Ordinal);
        Assert.Contains("safe_projection_sync_outbox", script, StringComparison.Ordinal);
        Assert.Contains("safe_projection_sync_checkpoints", script, StringComparison.Ordinal);
        Assert.Contains("ExternalPublicationState", script, StringComparison.Ordinal);
        Assert.Contains("\"CoreTags\"", script, StringComparison.Ordinal);
        Assert.Contains("\"SearchTerms\"", script, StringComparison.Ordinal);
        Assert.Contains("\"SearchTagKeys\"", script, StringComparison.Ordinal);
        Assert.Contains("\"ProjectKey\"", script, StringComparison.Ordinal);
        Assert.Contains("\"TaskKey\"", script, StringComparison.Ordinal);
        Assert.Contains("\"TopicTags\"", script, StringComparison.Ordinal);
        Assert.Contains("DEFAULT ('[]'::jsonb)", script, StringComparison.Ordinal);
        Assert.Contains("IX_wiki_proposals_ProjectKey_TaskKey_CreatedAt", script, StringComparison.Ordinal);
        Assert.Contains("IX_shared_memory_items_ProjectKey_TaskKey_UpdatedAt", script, StringComparison.Ordinal);
        Assert.Contains(
            "IX_shared_memory_items_cleanup_candidates",
            script,
            StringComparison.Ordinal);
        Assert.Contains("regexp_split_to_table", script, StringComparison.Ordinal);
        Assert.Contains("jsonb_array_elements_text", script, StringComparison.Ordinal);
        Assert.Contains("IX_wiki_proposals_AllowsAgentContext_Sensitivity_CreatedAt", script, StringComparison.Ordinal);
        Assert.Contains("IX_sensitive_access_requests_Status_UpdatedAt", script, StringComparison.Ordinal);
        Assert.Contains("RedactedSummary", script, StringComparison.Ordinal);
        Assert.Contains("UPDATE sensitive_access_requests AS request", script, StringComparison.Ordinal);
        Assert.Contains("SET \"RedactedSummary\" = reference.\"RedactedSummary\"", script, StringComparison.Ordinal);
        Assert.Contains("request.\"Status\" = 'Approved'", script, StringComparison.Ordinal);
        Assert.Contains("IX_audit_events_SubjectId_OccurredAt", script, StringComparison.Ordinal);
        Assert.Contains("IX_agent_connection_channels_AgentId_Channel", script, StringComparison.Ordinal);
        Assert.Contains(
            "IX_agent_connection_channels_OwnerUserId_AgentId_Channel",
            script,
            StringComparison.Ordinal);
        Assert.Contains("CK_agent_connection_channels_owner_user_id", script, StringComparison.Ordinal);
        Assert.Contains(
            "ALTER TABLE agent_connection_channels ALTER COLUMN \"OwnerUserId\" DROP DEFAULT",
            script,
            StringComparison.Ordinal);
        Assert.DoesNotContain("raw_content", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("raw_source", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DEFAULT 'LocalOnly'", script, StringComparison.Ordinal);
        Assert.Contains("SET \"UpdatedAt\" = \"CreatedAt\"", script, StringComparison.Ordinal);
        Assert.DoesNotContain("INSERT INTO safe_projection_sync_outbox", script, StringComparison.OrdinalIgnoreCase);
    }

    private static LuthnDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<LuthnDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        return new LuthnDbContext(options);
    }

    private static LuthnDbContext CreatePostgresMetadataDbContext()
    {
        var options = new DbContextOptionsBuilder<LuthnDbContext>()
            .UseNpgsql("Host=localhost;Port=5432;Database=luthn;Username=luthn")
            .Options;

        return new LuthnDbContext(options);
    }
}
