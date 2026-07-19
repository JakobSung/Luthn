using Microsoft.EntityFrameworkCore;
using Luthn.Core.Memory;
using Luthn.Core.Search;

namespace Luthn.Core.Persistence;

public sealed class LuthnDbContext(DbContextOptions<LuthnDbContext> options) : DbContext(options)
{
    public DbSet<SourceEventRecord> SourceEvents => Set<SourceEventRecord>();
    public DbSet<ClassificationResultRecord> ClassificationResults => Set<ClassificationResultRecord>();
    public DbSet<WikiProposalRecord> WikiProposals => Set<WikiProposalRecord>();
    public DbSet<SensitiveRecordReferenceRecord> SensitiveRecordReferences => Set<SensitiveRecordReferenceRecord>();
    public DbSet<SensitiveAccessRequestRecord> SensitiveAccessRequests => Set<SensitiveAccessRequestRecord>();
    public DbSet<SensitiveAccessDecisionRecord> SensitiveAccessDecisions => Set<SensitiveAccessDecisionRecord>();
    public DbSet<SharedMemoryItemRecord> SharedMemoryItems => Set<SharedMemoryItemRecord>();
    public DbSet<SensitiveMemoryPayloadRecord> SensitiveMemoryPayloads => Set<SensitiveMemoryPayloadRecord>();
    public DbSet<CollectionProvenanceRecord> CollectionProvenance => Set<CollectionProvenanceRecord>();
    public DbSet<LocalInstallationStateRecord> LocalInstallationStates => Set<LocalInstallationStateRecord>();
    public DbSet<SafeProjectionSyncOutboxRecord> SafeProjectionSyncOutbox => Set<SafeProjectionSyncOutboxRecord>();
    public DbSet<SafeProjectionSyncCheckpointRecord> SafeProjectionSyncCheckpoints => Set<SafeProjectionSyncCheckpointRecord>();
    public DbSet<AgentConnectionChannelRecord> AgentConnectionChannels => Set<AgentConnectionChannelRecord>();
    public DbSet<AuditEventRecord> AuditEvents => Set<AuditEventRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SourceEventRecord>(entity =>
        {
            entity.ToTable("source_events", table => table.HasCheckConstraint(
                "CK_source_events_owner_user_id",
                "\"OwnerUserId\" <> ''"));
            entity.HasKey(record => record.Id);
            entity.Property(record => record.Id).HasMaxLength(128);
            entity.Property(record => record.SourceSystem).HasMaxLength(128).IsRequired();
            entity.Property(record => record.SourceType).HasMaxLength(128).IsRequired();
            entity.Property(record => record.ContentDigest).HasMaxLength(256).IsRequired();
            entity.Property(record => record.OwnerUserId).HasMaxLength(128).IsRequired();
            entity.HasIndex(record => record.OwnerUserId);
        });

        modelBuilder.Entity<ClassificationResultRecord>(entity =>
        {
            entity.ToTable("classification_results");
            entity.HasKey(record => record.Id);
            entity.Property(record => record.Id).HasMaxLength(128);
            entity.Property(record => record.SourceEventId).HasMaxLength(128).IsRequired();
            entity.Property(record => record.Sensitivity).HasConversion<string>().HasMaxLength(64);
            entity.Property(record => record.StorageDecision).HasConversion<string>().HasMaxLength(64);
            entity.Property(record => record.Categories).HasColumnType("jsonb");
            entity.HasOne(record => record.SourceEvent)
                .WithMany()
                .HasForeignKey(record => record.SourceEventId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<WikiProposalRecord>(entity =>
        {
            entity.ToTable("wiki_proposals", table => table.HasCheckConstraint(
                "CK_wiki_proposals_owner_user_id",
                "\"OwnerUserId\" <> ''"));
            entity.HasKey(record => record.Id);
            entity.Property(record => record.Id).HasMaxLength(128);
            entity.Property(record => record.SourceEventId).HasMaxLength(128).IsRequired();
            entity.Property(record => record.Title).HasMaxLength(200).IsRequired();
            entity.Property(record => record.SafeSummary).HasMaxLength(4000).IsRequired();
            entity.Property(record => record.Sensitivity).HasConversion<string>().HasMaxLength(64);
            entity.Property(record => record.CoreTags).HasColumnType("jsonb");
            entity.Property(record => record.ProjectKey).HasMaxLength(128);
            entity.Property(record => record.TaskKey).HasMaxLength(128);
            entity.Property(record => record.TopicTags).HasColumnType("jsonb").HasDefaultValueSql("'[]'::jsonb");
            entity.Property(record => record.SearchTerms).HasColumnType("text").HasDefaultValue("||");
            entity.Property(record => record.SearchTagKeys).HasColumnType("text").HasDefaultValue("||");
            entity.Property(record => record.OwnerUserId).HasMaxLength(128).IsRequired();
            entity.HasIndex(record => new
            {
                record.AllowsAgentContext,
                record.Sensitivity,
                record.CreatedAt
            });
            entity.HasIndex(record => new { record.OwnerUserId, record.ProjectKey, record.TaskKey, record.CreatedAt });
            entity.HasOne(record => record.SourceEvent)
                .WithMany()
                .HasForeignKey(record => record.SourceEventId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<SensitiveRecordReferenceRecord>(entity =>
        {
            entity.ToTable("sensitive_record_references", table => table.HasCheckConstraint(
                "CK_sensitive_record_references_owner_user_id",
                "\"OwnerUserId\" <> ''"));
            entity.HasKey(record => record.Id);
            entity.Property(record => record.Id).HasMaxLength(128);
            entity.Property(record => record.SourceEventId).HasMaxLength(128).IsRequired();
            entity.Property(record => record.SourceSystem).HasMaxLength(128).IsRequired();
            entity.Property(record => record.SourceType).HasMaxLength(128).IsRequired();
            entity.Property(record => record.ReferenceLabel).HasMaxLength(256).IsRequired();
            entity.Property(record => record.RedactedSummary).HasMaxLength(4000).IsRequired();
            entity.Property(record => record.OwnerUserId).HasMaxLength(128).IsRequired();
            entity.HasIndex(record => record.OwnerUserId);
            entity.HasOne(record => record.SourceEvent)
                .WithMany()
                .HasForeignKey(record => record.SourceEventId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<SensitiveAccessRequestRecord>(entity =>
        {
            entity.ToTable("sensitive_access_requests", table => table.HasCheckConstraint(
                "CK_sensitive_access_requests_owner_user_id",
                "\"OwnerUserId\" <> ''"));
            entity.HasKey(record => record.Id);
            entity.Property(record => record.Id).HasMaxLength(128);
            entity.Property(record => record.SensitiveRecordReferenceId).HasMaxLength(128).IsRequired();
            entity.Property(record => record.RequestedBy).HasMaxLength(128).IsRequired();
            entity.Property(record => record.SessionId).HasMaxLength(128).IsRequired();
            entity.Property(record => record.RequestReason).HasMaxLength(1000).IsRequired();
            entity.Property(record => record.RedactedSummary).HasMaxLength(4000).IsRequired();
            entity.Property(record => record.Status).HasConversion<string>().HasMaxLength(64);
            entity.Property(record => record.DecidedBy).HasMaxLength(128);
            entity.Property(record => record.OwnerUserId).HasMaxLength(128).IsRequired();
            entity.HasIndex(record => new { record.Status, record.ExpiresAt, record.UpdatedAt });
            entity.HasIndex(record => new { record.OwnerUserId, record.Status, record.UpdatedAt });
            entity.HasOne(record => record.SensitiveRecordReference)
                .WithMany()
                .HasForeignKey(record => record.SensitiveRecordReferenceId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<SensitiveAccessDecisionRecord>(entity =>
        {
            entity.ToTable("sensitive_access_decisions");
            entity.HasKey(record => record.Id);
            entity.Property(record => record.Id).HasMaxLength(128);
            entity.Property(record => record.SensitiveAccessRequestId).HasMaxLength(128).IsRequired();
            entity.Property(record => record.Decision).HasConversion<string>().HasMaxLength(64);
            entity.Property(record => record.DecidedBy).HasMaxLength(128).IsRequired();
            entity.Property(record => record.DecisionReason).HasMaxLength(1000).IsRequired();
            entity.Property(record => record.PayloadClass).HasMaxLength(128).IsRequired();
            entity.Property(record => record.RedactionState).HasMaxLength(128).IsRequired();
            entity.HasOne(record => record.SensitiveAccessRequest)
                .WithMany()
                .HasForeignKey(record => record.SensitiveAccessRequestId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<SharedMemoryItemRecord>(entity =>
        {
            entity.ToTable("shared_memory_items", table => table.HasCheckConstraint(
                "CK_shared_memory_items_owner_user_id",
                "\"OwnerUserId\" <> ''"));
            entity.HasKey(record => record.Id);
            entity.Property(record => record.Id).HasMaxLength(128);
            entity.Property(record => record.Title).HasMaxLength(200).IsRequired();
            entity.Property(record => record.SafeSummary).HasMaxLength(4000).IsRequired();
            entity.Property(record => record.Sensitivity).HasConversion<string>().HasMaxLength(64);
            entity.Property(record => record.CoreTags).HasColumnType("jsonb");
            entity.Property(record => record.ProjectKey).HasMaxLength(128);
            entity.Property(record => record.TaskKey).HasMaxLength(128);
            entity.Property(record => record.TopicTags).HasColumnType("jsonb").HasDefaultValueSql("'[]'::jsonb");
            entity.Property(record => record.SearchTerms).HasColumnType("text").HasDefaultValue("||");
            entity.Property(record => record.SearchTagKeys).HasColumnType("text").HasDefaultValue("||");
            entity.Property(record => record.Visibility).HasConversion<string>().HasMaxLength(64);
            entity.Property(record => record.RetentionKind).HasConversion<string>().HasMaxLength(64);
            entity.Property(record => record.SourceSessionId).HasMaxLength(128);
            entity.Property(record => record.CreatedBy).HasMaxLength(128).IsRequired();
            entity.Property(record => record.OwnerUserId).HasMaxLength(128).IsRequired();
            entity.Property(record => record.Revision).HasDefaultValue(1L);
            entity.Property(record => record.Revision).IsConcurrencyToken();
            entity.Property(record => record.ExternalPublicationState)
                .HasConversion<string>()
                .HasMaxLength(64)
                .HasDefaultValue(ExternalPublicationState.LocalOnly);
            entity.Property(record => record.ExternalPublicationDecidedBy).HasMaxLength(128);
            entity.HasIndex(record => new
            {
                record.AllowsAgentContext,
                record.Sensitivity,
                record.Visibility,
                record.ExpiresAt
            });
            entity.HasIndex(record => new { record.OwnerUserId, record.ProjectKey, record.TaskKey, record.UpdatedAt });
        });

        modelBuilder.Entity<SensitiveMemoryPayloadRecord>(entity =>
        {
            entity.ToTable("sensitive_memory_payloads");
            entity.HasKey(record => record.MemoryItemId);
            entity.Property(record => record.MemoryItemId).HasMaxLength(128);
            entity.Property(record => record.ContractVersion).HasDefaultValue(1);
            entity.Property(record => record.ProtectionScheme).HasMaxLength(64).IsRequired();
            entity.Property(record => record.ProtectedPayload).HasColumnType("text").IsRequired();
            entity.HasOne<SharedMemoryItemRecord>()
                .WithOne()
                .HasForeignKey<SensitiveMemoryPayloadRecord>(record => record.MemoryItemId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CollectionProvenanceRecord>(entity =>
        {
            entity.ToTable("collection_provenance", table =>
            {
                table.HasCheckConstraint(
                    "CK_collection_provenance_subject",
                    "\"SourceEventId\" IS NOT NULL OR \"MemoryItemId\" IS NOT NULL");
                table.HasCheckConstraint(
                    "CK_collection_provenance_authenticated_user_id",
                    "\"AuthenticatedUserId\" <> ''");
            });
            entity.HasKey(record => record.Id);
            entity.Property(record => record.Id).HasMaxLength(64);
            entity.Property(record => record.ContractVersion).HasDefaultValue(1);
            entity.Property(record => record.SourceEventId).HasMaxLength(128);
            entity.Property(record => record.MemoryItemId).HasMaxLength(128);
            entity.Property(record => record.AuthenticatedActor).HasMaxLength(128).IsRequired();
            entity.Property(record => record.ActorTrust).HasMaxLength(32).IsRequired();
            entity.Property(record => record.ClaimsTrust).HasMaxLength(32).IsRequired();
            entity.Property(record => record.AuthenticatedUserId).HasMaxLength(128).IsRequired();
            entity.Property(record => record.ClaimedUserId).HasMaxLength(128);
            entity.Property(record => record.AgentId).HasMaxLength(128);
            entity.Property(record => record.ApplicationId).HasMaxLength(128);
            entity.Property(record => record.PluginId).HasMaxLength(128);
            entity.Property(record => record.ConnectorId).HasMaxLength(128);
            entity.Property(record => record.ConnectorVersion).HasMaxLength(64);
            entity.HasIndex(record => record.SourceEventId).IsUnique();
            entity.HasIndex(record => record.MemoryItemId).IsUnique();
            entity.HasOne<SourceEventRecord>()
                .WithOne()
                .HasForeignKey<CollectionProvenanceRecord>(record => record.SourceEventId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<SharedMemoryItemRecord>()
                .WithOne()
                .HasForeignKey<CollectionProvenanceRecord>(record => record.MemoryItemId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<LocalInstallationStateRecord>(entity =>
        {
            entity.ToTable("local_installation_state");
            entity.HasKey(record => record.Id);
            entity.Property(record => record.Id).HasMaxLength(32);
            entity.Property(record => record.OriginInstanceId).HasMaxLength(128).IsRequired();
            entity.HasIndex(record => record.OriginInstanceId).IsUnique();
        });

        modelBuilder.Entity<SafeProjectionSyncOutboxRecord>(entity =>
        {
            entity.ToTable("safe_projection_sync_outbox", table => table.HasCheckConstraint(
                "CK_safe_projection_sync_outbox_owner_user_id",
                "\"OwnerUserId\" <> ''"));
            entity.HasKey(record => record.Id);
            entity.Property(record => record.Id).HasMaxLength(128);
            entity.Property(record => record.IdempotencyKey).HasMaxLength(512).IsRequired();
            entity.Property(record => record.OriginInstanceId).HasMaxLength(128).IsRequired();
            entity.Property(record => record.LocalRecordId).HasMaxLength(128).IsRequired();
            entity.Property(record => record.OwnerUserId).HasMaxLength(128).IsRequired();
            entity.Property(record => record.Operation).HasConversion<string>().HasMaxLength(32);
            entity.Property(record => record.SafeEnvelopeJson).HasColumnType("jsonb").IsRequired();
            entity.Property(record => record.State).HasConversion<string>().HasMaxLength(32);
            entity.Property(record => record.LastErrorCode).HasMaxLength(128);
            entity.Property(record => record.RemoteCheckpoint).HasMaxLength(512);
            entity.HasIndex(record => record.IdempotencyKey).IsUnique();
            entity.HasIndex(record => new
            {
                record.OriginInstanceId,
                record.LocalRecordId,
                record.Revision,
                record.Operation
            }).IsUnique();
            entity.HasIndex(record => new { record.State, record.NextAttemptAt, record.CreatedAt });
            entity.HasIndex(record => new { record.OwnerUserId, record.State, record.CreatedAt });
        });

        modelBuilder.Entity<SafeProjectionSyncCheckpointRecord>(entity =>
        {
            entity.ToTable("safe_projection_sync_checkpoints");
            entity.HasKey(record => record.TransportName);
            entity.Property(record => record.TransportName).HasMaxLength(128);
            entity.Property(record => record.Checkpoint).HasMaxLength(512).IsRequired();
        });

        modelBuilder.Entity<AgentConnectionChannelRecord>(entity =>
        {
            entity.ToTable("agent_connection_channels");
            entity.HasKey(record => record.Id);
            entity.ToTable(table => table.HasCheckConstraint(
                "CK_agent_connection_channels_owner_user_id",
                "\"OwnerUserId\" <> ''"));
            entity.Property(record => record.Id).HasMaxLength(160);
            entity.Property(record => record.OwnerUserId).HasMaxLength(128).IsRequired();
            entity.Property(record => record.AgentId).HasMaxLength(64).IsRequired();
            entity.Property(record => record.AgentName).HasMaxLength(128).IsRequired();
            entity.Property(record => record.IntegrationKind).HasMaxLength(64).IsRequired();
            entity.Property(record => record.ConnectorVersion).HasMaxLength(64).IsRequired();
            entity.Property(record => record.Channel).HasMaxLength(64).IsRequired();
            entity.Property(record => record.ConfigurationOwner).HasMaxLength(64).IsRequired();
            entity.Property(record => record.VerificationState).HasConversion<string>().HasMaxLength(32);
            entity.Property(record => record.ActivityState).HasConversion<string>().HasMaxLength(32);
            entity.Property(record => record.FailureCode).HasMaxLength(64);
            entity.HasIndex(record => new { record.OwnerUserId, record.AgentId, record.Channel }).IsUnique();
            entity.HasIndex(record => record.UpdatedAt);
        });

        modelBuilder.Entity<AuditEventRecord>(entity =>
        {
            entity.ToTable("audit_events");
            entity.HasKey(record => record.Id);
            entity.Property(record => record.Id).HasMaxLength(128);
            entity.Property(record => record.Actor).HasMaxLength(128).IsRequired();
            entity.Property(record => record.Action).HasMaxLength(128).IsRequired();
            entity.Property(record => record.SubjectId).HasMaxLength(128).IsRequired();
            entity.Property(record => record.PayloadVersion).HasDefaultValue(AuditEventPayloadVersions.Current);
            entity.Property(record => record.PayloadClass).HasMaxLength(128).IsRequired();
            entity.Property(record => record.RedactionState).HasMaxLength(128).IsRequired();
            entity.HasIndex(record => new { record.SubjectId, record.OccurredAt });
        });
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        RejectProvenanceUpdates();
        UpdateSearchIndexes();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        RejectProvenanceUpdates();
        UpdateSearchIndexes();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void RejectProvenanceUpdates()
    {
        if (ChangeTracker.Entries<CollectionProvenanceRecord>()
            .Any(entry => entry.State is EntityState.Modified or EntityState.Deleted))
        {
            throw new InvalidOperationException("Collection provenance records are immutable.");
        }
    }

    private void UpdateSearchIndexes()
    {
        foreach (var entry in ChangeTracker.Entries<WikiProposalRecord>()
                     .Where(entry => entry.State is EntityState.Added or EntityState.Modified))
        {
            entry.Entity.SearchTerms = SafeSearchText.BuildTokenIndex(
                [entry.Entity.Title, entry.Entity.SafeSummary, entry.Entity.ProjectKey, entry.Entity.TaskKey],
                entry.Entity.CoreTags,
                entry.Entity.TopicTags);
            entry.Entity.SearchTagKeys = SafeSearchText.BuildTagKeyIndex(entry.Entity.CoreTags);
        }

        foreach (var entry in ChangeTracker.Entries<SharedMemoryItemRecord>()
                     .Where(entry => entry.State is EntityState.Added or EntityState.Modified))
        {
            entry.Entity.SearchTerms = SafeSearchText.BuildTokenIndex(
                [entry.Entity.Title, entry.Entity.SafeSummary, entry.Entity.ProjectKey, entry.Entity.TaskKey],
                entry.Entity.CoreTags,
                entry.Entity.TopicTags);
            entry.Entity.SearchTagKeys = SafeSearchText.BuildTagKeyIndex(entry.Entity.CoreTags);
        }
    }
}
