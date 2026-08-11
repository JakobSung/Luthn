using Microsoft.EntityFrameworkCore;
using Luthn.Core.Common;
using Luthn.Core.Memory;
using Luthn.Core.Search;

namespace Luthn.Core.Persistence;

public sealed class LuthnDbContext(DbContextOptions<LuthnDbContext> options) : DbContext(options)
{
    private bool _allowAuditRetentionDelete;

    public DbSet<SourceEventRecord> SourceEvents => Set<SourceEventRecord>();
    public DbSet<ClassificationResultRecord> ClassificationResults => Set<ClassificationResultRecord>();
    public DbSet<WikiProposalRecord> WikiProposals => Set<WikiProposalRecord>();
    public DbSet<SensitiveRecordReferenceRecord> SensitiveRecordReferences => Set<SensitiveRecordReferenceRecord>();
    public DbSet<SensitiveAccessPolicyRevisionRecord> SensitiveAccessPolicyRevisions => Set<SensitiveAccessPolicyRevisionRecord>();
    public DbSet<SensitiveAccessRequestRecord> SensitiveAccessRequests => Set<SensitiveAccessRequestRecord>();
    public DbSet<SensitiveAccessDecisionRecord> SensitiveAccessDecisions => Set<SensitiveAccessDecisionRecord>();
    public DbSet<SensitiveAccessGrantRecord> SensitiveAccessGrants => Set<SensitiveAccessGrantRecord>();
    public DbSet<SharedMemoryItemRecord> SharedMemoryItems => Set<SharedMemoryItemRecord>();
    public DbSet<SensitiveMemoryPayloadRecord> SensitiveMemoryPayloads => Set<SensitiveMemoryPayloadRecord>();
    public DbSet<CollectionProvenanceRecord> CollectionProvenance => Set<CollectionProvenanceRecord>();
    public DbSet<LocalInstallationStateRecord> LocalInstallationStates => Set<LocalInstallationStateRecord>();
    public DbSet<SafeProjectionSyncOutboxRecord> SafeProjectionSyncOutbox => Set<SafeProjectionSyncOutboxRecord>();
    public DbSet<SafeProjectionSyncCheckpointRecord> SafeProjectionSyncCheckpoints => Set<SafeProjectionSyncCheckpointRecord>();
    public DbSet<AgentConnectionChannelRecord> AgentConnectionChannels => Set<AgentConnectionChannelRecord>();
    public DbSet<HubIngressQueueRecord> HubIngressQueue => Set<HubIngressQueueRecord>();
    public DbSet<AuditEventRecord> AuditEvents => Set<AuditEventRecord>();

    public async Task<int> DeleteAuditEventsForRetentionAsync(
        IReadOnlyCollection<string> auditEventIds,
        AuditEventRecord retentionAudit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(auditEventIds);
        ArgumentNullException.ThrowIfNull(retentionAudit);
        if (auditEventIds.Count is < 1 or > 1000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(auditEventIds),
                "Audit retention deletion requires between 1 and 1000 event ids.");
        }
        if (retentionAudit.ScopeKind != AuditEventScopeKind.Installation ||
            !string.Equals(retentionAudit.Action, "audit.retention.pruned", StringComparison.Ordinal) ||
            !string.Equals(retentionAudit.PayloadClass, "metadata-only", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Audit retention deletion requires an installation-scoped metadata-only retention audit.",
                nameof(retentionAudit));
        }

        var candidates = await AuditEvents
            .Where(record => auditEventIds.Contains(record.Id))
            .ToArrayAsync(cancellationToken);
        if (candidates.Length == 0)
        {
            return 0;
        }

        AuditEvents.RemoveRange(candidates);
        AuditEvents.Add(retentionAudit);
        _allowAuditRetentionDelete = true;
        try
        {
            await SaveChangesAsync(cancellationToken);
        }
        finally
        {
            _allowAuditRetentionDelete = false;
        }

        return candidates.Length;
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SourceEventRecord>(entity =>
        {
            entity.ToTable("source_events", table =>
            {
                table.HasCheckConstraint("CK_source_events_owner_user_id", "\"OwnerUserId\" <> ''");
                table.HasCheckConstraint("CK_source_events_workspace_id", "\"WorkspaceId\" <> ''");
            });
            entity.HasKey(record => record.Id);
            entity.Property(record => record.Id).HasMaxLength(128);
            entity.Property(record => record.SourceSystem).HasMaxLength(128).IsRequired();
            entity.Property(record => record.SourceType).HasMaxLength(128).IsRequired();
            entity.Property(record => record.ContentDigest).HasMaxLength(256).IsRequired();
            entity.Property(record => record.WorkspaceId).HasMaxLength(WorkspaceIds.MaxLength).IsRequired();
            entity.Property(record => record.OwnerUserId).HasMaxLength(128).IsRequired();
            entity.HasIndex(record => new { record.WorkspaceId, record.ReceivedAt });
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
            entity.ToTable("wiki_proposals", table =>
            {
                table.HasCheckConstraint("CK_wiki_proposals_owner_user_id", "\"OwnerUserId\" <> ''");
                table.HasCheckConstraint("CK_wiki_proposals_workspace_id", "\"WorkspaceId\" <> ''");
            });
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
            entity.Property(record => record.WorkspaceId).HasMaxLength(WorkspaceIds.MaxLength).IsRequired();
            entity.Property(record => record.OwnerUserId).HasMaxLength(128).IsRequired();
            entity.HasIndex(record => new
            {
                record.AllowsAgentContext,
                record.Sensitivity,
                record.CreatedAt
            });
            entity.HasIndex(record => new { record.WorkspaceId, record.ProjectKey, record.TaskKey, record.CreatedAt });
            entity.HasOne(record => record.SourceEvent)
                .WithMany()
                .HasForeignKey(record => record.SourceEventId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<SensitiveRecordReferenceRecord>(entity =>
        {
            entity.ToTable("sensitive_record_references", table =>
            {
                table.HasCheckConstraint("CK_sensitive_record_references_owner_user_id", "\"OwnerUserId\" <> ''");
                table.HasCheckConstraint("CK_sensitive_record_references_workspace_id", "\"WorkspaceId\" <> ''");
            });
            entity.HasKey(record => record.Id);
            entity.Property(record => record.Id).HasMaxLength(128);
            entity.Property(record => record.SourceEventId).HasMaxLength(128).IsRequired();
            entity.Property(record => record.SourceSystem).HasMaxLength(128).IsRequired();
            entity.Property(record => record.SourceType).HasMaxLength(128).IsRequired();
            entity.Property(record => record.ReferenceLabel).HasMaxLength(256).IsRequired();
            entity.Property(record => record.RedactedSummary).HasMaxLength(4000).IsRequired();
            entity.Property(record => record.WorkspaceId).HasMaxLength(WorkspaceIds.MaxLength).IsRequired();
            entity.Property(record => record.OwnerUserId).HasMaxLength(128).IsRequired();
            entity.HasIndex(record => new { record.WorkspaceId, record.ReceivedAt });
            entity.HasOne(record => record.SourceEvent)
                .WithMany()
                .HasForeignKey(record => record.SourceEventId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<SensitiveAccessPolicyRevisionRecord>(entity =>
        {
            entity.ToTable("sensitive_access_policy_revisions", table =>
            {
                table.HasCheckConstraint(
                    "CK_sensitive_access_policy_revisions_workspace_id",
                    "\"WorkspaceId\" <> ''");
                table.HasCheckConstraint(
                    "CK_sensitive_access_policy_revisions_revision",
                    "\"Revision\" > 0");
                table.HasCheckConstraint(
                    "CK_sensitive_access_policy_revisions_request_timeout",
                    "\"RequestTimeoutSeconds\" BETWEEN 60 AND 3600");
                table.HasCheckConstraint(
                    "CK_sensitive_access_policy_revisions_grant_duration",
                    "\"GrantDurationSeconds\" BETWEEN 60 AND 3600");
                table.HasCheckConstraint(
                    "CK_sensitive_access_policy_revisions_maximum_successful_reads",
                    "\"MaximumSuccessfulReads\" BETWEEN 1 AND 10");
            });
            entity.HasKey(record => new { record.WorkspaceId, record.Revision });
            entity.Property(record => record.WorkspaceId).HasMaxLength(WorkspaceIds.MaxLength);
            entity.Property(record => record.CreatedBy).HasMaxLength(128).IsRequired();
            entity.HasIndex(record => new { record.WorkspaceId, record.CreatedAt });
        });

        modelBuilder.Entity<SensitiveAccessRequestRecord>(entity =>
        {
            entity.ToTable("sensitive_access_requests", table =>
            {
                table.HasCheckConstraint("CK_sensitive_access_requests_owner_user_id", "\"OwnerUserId\" <> ''");
                table.HasCheckConstraint("CK_sensitive_access_requests_workspace_id", "\"WorkspaceId\" <> ''");
                table.HasCheckConstraint("CK_sensitive_access_requests_policy_revision", "\"PolicyRevision\" > 0");
                table.HasCheckConstraint(
                    "CK_sensitive_access_requests_request_timeout",
                    "\"RequestTimeoutSeconds\" BETWEEN 60 AND 3600");
            });
            entity.HasKey(record => record.Id);
            entity.Property(record => record.Id).HasMaxLength(128);
            entity.Property(record => record.SensitiveRecordReferenceId).HasMaxLength(128).IsRequired();
            entity.Property(record => record.RequestedBy).HasMaxLength(128).IsRequired();
            entity.Property(record => record.SessionId).HasMaxLength(128).IsRequired();
            entity.Property(record => record.RequestReason).HasMaxLength(1000).IsRequired();
            entity.Property(record => record.RedactedSummary).HasMaxLength(4000).IsRequired();
            entity.Property(record => record.Status).HasConversion<string>().HasMaxLength(64);
            entity.Property(record => record.DecidedBy).HasMaxLength(128);
            entity.Property(record => record.WorkspaceId).HasMaxLength(WorkspaceIds.MaxLength).IsRequired();
            entity.Property(record => record.OwnerUserId).HasMaxLength(128).IsRequired();
            entity.HasIndex(record => new { record.Status, record.ExpiresAt, record.UpdatedAt });
            entity.HasIndex(record => new { record.WorkspaceId, record.Status, record.UpdatedAt });
            entity.HasOne(record => record.SensitiveRecordReference)
                .WithMany()
                .HasForeignKey(record => record.SensitiveRecordReferenceId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(record => record.Policy)
                .WithMany()
                .HasForeignKey(record => new { record.WorkspaceId, record.PolicyRevision })
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

        modelBuilder.Entity<SensitiveAccessGrantRecord>(entity =>
        {
            entity.ToTable("sensitive_access_grants", table =>
            {
                table.HasCheckConstraint("CK_sensitive_access_grants_owner_user_id", "\"OwnerUserId\" <> ''");
                table.HasCheckConstraint("CK_sensitive_access_grants_workspace_id", "\"WorkspaceId\" <> ''");
                table.HasCheckConstraint("CK_sensitive_access_grants_policy_revision", "\"PolicyRevision\" > 0");
                table.HasCheckConstraint(
                    "CK_sensitive_access_grants_grant_duration",
                    "\"GrantDurationSeconds\" BETWEEN 60 AND 3600");
                table.HasCheckConstraint(
                    "CK_sensitive_access_grants_maximum_successful_reads",
                    "\"MaximumSuccessfulReads\" BETWEEN 1 AND 10");
                table.HasCheckConstraint(
                    "CK_sensitive_access_grants_successful_read_count",
                    "\"SuccessfulReadCount\" >= 0 AND \"SuccessfulReadCount\" <= \"MaximumSuccessfulReads\"");
                table.HasCheckConstraint(
                    "CK_sensitive_access_grants_time_window",
                    "\"StartsAt\" < \"ExpiresAt\"");
            });
            entity.HasKey(record => record.SensitiveAccessRequestId);
            entity.Property(record => record.SensitiveAccessRequestId).HasMaxLength(128);
            entity.Property(record => record.WorkspaceId).HasMaxLength(WorkspaceIds.MaxLength).IsRequired();
            entity.Property(record => record.OwnerUserId).HasMaxLength(128).IsRequired();
            entity.Property(record => record.SuccessfulReadCount).IsConcurrencyToken();
            entity.HasIndex(record => new { record.WorkspaceId, record.OwnerUserId, record.ExpiresAt });
            entity.HasOne(record => record.SensitiveAccessRequest)
                .WithOne(record => record.Grant)
                .HasForeignKey<SensitiveAccessGrantRecord>(record => record.SensitiveAccessRequestId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(record => record.Policy)
                .WithMany()
                .HasForeignKey(record => new { record.WorkspaceId, record.PolicyRevision })
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<SharedMemoryItemRecord>(entity =>
        {
            entity.ToTable("shared_memory_items", table =>
            {
                table.HasCheckConstraint("CK_shared_memory_items_owner_user_id", "\"OwnerUserId\" <> ''");
                table.HasCheckConstraint("CK_shared_memory_items_workspace_id", "\"WorkspaceId\" <> ''");
            });
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
            entity.Property(record => record.WorkspaceId).HasMaxLength(WorkspaceIds.MaxLength).IsRequired();
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
            entity.HasIndex(record => new
            {
                record.RetentionKind,
                record.ExternalPublicationState,
                record.ExpiresAt,
                record.CreatedAt,
                record.Id
            }).HasDatabaseName("IX_shared_memory_items_cleanup_candidates");
            entity.HasIndex(record => new { record.WorkspaceId, record.ProjectKey, record.TaskKey, record.UpdatedAt });
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
                table.HasCheckConstraint(
                    "CK_collection_provenance_workspace_id",
                    "\"WorkspaceId\" <> ''");
            });
            entity.HasKey(record => record.Id);
            entity.Property(record => record.Id).HasMaxLength(64);
            entity.Property(record => record.ContractVersion).HasDefaultValue(1);
            entity.Property(record => record.SourceEventId).HasMaxLength(128);
            entity.Property(record => record.MemoryItemId).HasMaxLength(128);
            entity.Property(record => record.AuthenticatedActor).HasMaxLength(128).IsRequired();
            entity.Property(record => record.ActorTrust).HasMaxLength(32).IsRequired();
            entity.Property(record => record.ClaimsTrust).HasMaxLength(32).IsRequired();
            entity.Property(record => record.WorkspaceId).HasMaxLength(WorkspaceIds.MaxLength).IsRequired();
            entity.Property(record => record.AuthenticatedUserId).HasMaxLength(128).IsRequired();
            entity.Property(record => record.ClaimedUserId).HasMaxLength(128);
            entity.Property(record => record.AgentId).HasMaxLength(128);
            entity.Property(record => record.ApplicationId).HasMaxLength(128);
            entity.Property(record => record.PluginId).HasMaxLength(128);
            entity.Property(record => record.ConnectorId).HasMaxLength(128);
            entity.Property(record => record.ConnectorVersion).HasMaxLength(64);
            entity.HasIndex(record => record.SourceEventId).IsUnique();
            entity.HasIndex(record => record.MemoryItemId).IsUnique();
            entity.HasIndex(record => new { record.WorkspaceId, record.ReceivedAt });
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
            entity.ToTable("safe_projection_sync_outbox", table =>
            {
                table.HasCheckConstraint("CK_safe_projection_sync_outbox_owner_user_id", "\"OwnerUserId\" <> ''");
                table.HasCheckConstraint("CK_safe_projection_sync_outbox_workspace_id", "\"WorkspaceId\" <> ''");
            });
            entity.HasKey(record => record.Id);
            entity.Property(record => record.Id).HasMaxLength(128);
            entity.Property(record => record.IdempotencyKey).HasMaxLength(512).IsRequired();
            entity.Property(record => record.OriginInstanceId).HasMaxLength(128).IsRequired();
            entity.Property(record => record.LocalRecordId).HasMaxLength(128).IsRequired();
            entity.Property(record => record.WorkspaceId).HasMaxLength(WorkspaceIds.MaxLength).IsRequired();
            entity.Property(record => record.OwnerUserId).HasMaxLength(128).IsRequired();
            entity.Property(record => record.Operation).HasConversion<string>().HasMaxLength(32);
            entity.Property(record => record.SafeEnvelopeJson).HasColumnType("jsonb").IsRequired();
            entity.Property(record => record.State).HasConversion<string>().HasMaxLength(32);
            entity.Property(record => record.LastErrorCode).HasMaxLength(128);
            entity.Property(record => record.RemoteCheckpoint).HasMaxLength(512);
            entity.HasIndex(record => new { record.WorkspaceId, record.IdempotencyKey }).IsUnique();
            entity.HasIndex(record => new
            {
                record.WorkspaceId,
                record.OriginInstanceId,
                record.LocalRecordId,
                record.Revision,
                record.Operation
            }).IsUnique();
            entity.HasIndex(record => new { record.State, record.NextAttemptAt, record.CreatedAt });
            entity.HasIndex(record => new { record.WorkspaceId, record.State, record.CreatedAt });
        });

        modelBuilder.Entity<SafeProjectionSyncCheckpointRecord>(entity =>
        {
            entity.ToTable("safe_projection_sync_checkpoints", table => table.HasCheckConstraint(
                "CK_safe_projection_sync_checkpoints_workspace_id",
                "\"WorkspaceId\" <> ''"));
            entity.HasKey(record => new { record.WorkspaceId, record.TransportName });
            entity.Property(record => record.WorkspaceId).HasMaxLength(WorkspaceIds.MaxLength);
            entity.Property(record => record.TransportName).HasMaxLength(128);
            entity.Property(record => record.Checkpoint).HasMaxLength(512).IsRequired();
        });

        modelBuilder.Entity<AgentConnectionChannelRecord>(entity =>
        {
            entity.ToTable("agent_connection_channels", table =>
            {
                table.HasCheckConstraint("CK_agent_connection_channels_owner_user_id", "\"OwnerUserId\" <> ''");
                table.HasCheckConstraint("CK_agent_connection_channels_workspace_id", "\"WorkspaceId\" <> ''");
            });
            entity.HasKey(record => record.Id);
            entity.Property(record => record.Id).HasMaxLength(160);
            entity.Property(record => record.WorkspaceId).HasMaxLength(WorkspaceIds.MaxLength).IsRequired();
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
            entity.HasIndex(record => new { record.WorkspaceId, record.AgentId, record.Channel }).IsUnique();
            entity.HasIndex(record => new { record.WorkspaceId, record.UpdatedAt });
        });

        modelBuilder.Entity<HubIngressQueueRecord>(entity =>
        {
            entity.ToTable("hub_ingress_queue", table =>
            {
                table.HasCheckConstraint("CK_hub_ingress_queue_workspace_id", "\"WorkspaceId\" <> ''");
                table.HasCheckConstraint("CK_hub_ingress_queue_organization_id", "\"OrganizationId\" <> ''");
                table.HasCheckConstraint("CK_hub_ingress_queue_member_user_id", "\"MemberUserId\" <> ''");
                table.HasCheckConstraint("CK_hub_ingress_queue_capsule_size", "\"CapsuleSizeBytes\" > 0");
            });
            entity.HasKey(record => record.Id);
            entity.Property(record => record.Id).HasMaxLength(128);
            entity.Property(record => record.ReceiptId).HasMaxLength(128).IsRequired();
            entity.Property(record => record.OrganizationId).HasMaxLength(128).IsRequired();
            entity.Property(record => record.WorkspaceId).HasMaxLength(WorkspaceIds.MaxLength).IsRequired();
            entity.Property(record => record.MemberUserId).HasMaxLength(128).IsRequired();
            entity.Property(record => record.AgentConnectionId).HasMaxLength(128).IsRequired();
            entity.Property(record => record.AgentId).HasMaxLength(128).IsRequired();
            entity.Property(record => record.SessionId).HasMaxLength(128).IsRequired();
            entity.Property(record => record.TurnId).HasMaxLength(128).IsRequired();
            entity.Property(record => record.IdempotencyKey).HasMaxLength(128).IsRequired();
            entity.Property(record => record.ContentDigest).HasMaxLength(71).IsRequired();
            entity.Property(record => record.ProtectionScheme).HasMaxLength(64).IsRequired();
            entity.Property(record => record.ProtectedCapsule).HasColumnType("text").IsRequired();
            entity.Property(record => record.State).HasConversion<string>().HasMaxLength(32);
            entity.Property(record => record.LastErrorCode).HasMaxLength(64);
            entity.Property(record => record.Sensitivity).HasMaxLength(32);
            entity.Property(record => record.StorageDecision).HasMaxLength(32);
            entity.HasIndex(record => record.ReceiptId).IsUnique();
            entity.HasIndex(record => new
            {
                record.WorkspaceId,
                record.AgentConnectionId,
                record.IdempotencyKey
            }).IsUnique();
            entity.HasIndex(record => new { record.State, record.NextAttemptAt, record.AcceptedAt });
            entity.HasIndex(record => new { record.OrganizationId, record.State, record.AcceptedAt });
            entity.HasIndex(record => new { record.WorkspaceId, record.State, record.AcceptedAt });
            entity.HasIndex(record => new { record.WorkspaceId, record.MemberUserId, record.AcceptedAt });
            entity.HasIndex(record => new { record.WorkspaceId, record.AgentId, record.AcceptedAt });
        });

        modelBuilder.Entity<AuditEventRecord>(entity =>
        {
            entity.ToTable("audit_events", table =>
            {
                table.HasCheckConstraint(
                    "CK_audit_events_scope_workspace",
                    "(\"ScopeKind\" = 'Workspace' AND \"WorkspaceId\" <> '') OR (\"ScopeKind\" = 'Installation' AND \"WorkspaceId\" = '')");
            });
            entity.HasKey(record => record.Id);
            entity.Property(record => record.Id).HasMaxLength(128);
            entity.Property(record => record.ScopeKind)
                .HasConversion<string>()
                .HasMaxLength(32)
                .HasDefaultValue(AuditEventScopeKind.Workspace);
            entity.Property(record => record.WorkspaceId)
                .HasMaxLength(WorkspaceIds.MaxLength)
                .HasDefaultValue(WorkspaceIds.Default)
                .IsRequired();
            entity.Property(record => record.Actor).HasMaxLength(128).IsRequired();
            entity.Property(record => record.ActorUserId).HasMaxLength(128);
            entity.Property(record => record.ActorKind)
                .HasMaxLength(32)
                .HasDefaultValue("system")
                .IsRequired();
            entity.Property(record => record.Action).HasMaxLength(128).IsRequired();
            entity.Property(record => record.SubjectId).HasMaxLength(128).IsRequired();
            entity.Property(record => record.SubjectType)
                .HasMaxLength(64)
                .HasDefaultValue("unknown")
                .IsRequired();
            entity.Property(record => record.Outcome)
                .HasMaxLength(32)
                .HasDefaultValue("unspecified")
                .IsRequired();
            entity.Property(record => record.CorrelationId).HasMaxLength(128);
            entity.Property(record => record.PayloadVersion).HasDefaultValue(AuditEventPayloadVersions.Current);
            entity.Property(record => record.PayloadClass).HasMaxLength(128).IsRequired();
            entity.Property(record => record.RedactionState).HasMaxLength(128).IsRequired();
            entity.HasIndex(record => new { record.SubjectId, record.OccurredAt });
            entity.HasIndex(record => new { record.ScopeKind, record.WorkspaceId, record.OccurredAt });
            entity.HasIndex(record => new { record.WorkspaceId, record.CorrelationId });
        });
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        ValidateWorkspaceScopes();
        ValidateSensitiveAccessPoliciesAndGrants();
        ValidateAuditScopes();
        RejectProvenanceUpdates();
        UpdateSearchIndexes();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        ValidateWorkspaceScopes();
        ValidateSensitiveAccessPoliciesAndGrants();
        ValidateAuditScopes();
        RejectProvenanceUpdates();
        UpdateSearchIndexes();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void ValidateWorkspaceScopes()
    {
        var invalidEntry = ChangeTracker.Entries<IWorkspaceScopedRecord>()
            .FirstOrDefault(entry =>
                entry.State is EntityState.Added or EntityState.Modified &&
                (string.IsNullOrWhiteSpace(entry.Entity.WorkspaceId) ||
                    entry.Entity.WorkspaceId.Length > WorkspaceIds.MaxLength));
        if (invalidEntry is not null)
        {
            throw new InvalidOperationException(
                $"{invalidEntry.Metadata.ClrType.Name} requires a valid WorkspaceId.");
        }
    }

    private void ValidateAuditScopes()
    {
        var invalidScope = ChangeTracker.Entries<AuditEventRecord>()
            .FirstOrDefault(entry =>
                (entry.State is EntityState.Added or EntityState.Modified) &&
                ((entry.Entity.ScopeKind == AuditEventScopeKind.Workspace &&
                    (string.IsNullOrWhiteSpace(entry.Entity.WorkspaceId) ||
                        entry.Entity.WorkspaceId.Length > WorkspaceIds.MaxLength)) ||
                 (entry.Entity.ScopeKind == AuditEventScopeKind.Installation &&
                    !string.IsNullOrWhiteSpace(entry.Entity.WorkspaceId))));
        if (invalidScope is not null)
        {
            throw new InvalidOperationException(
                "Audit event scope requires a valid workspace or installation scope.");
        }

        var mutatedEntry = ChangeTracker.Entries<AuditEventRecord>()
            .FirstOrDefault(entry =>
                entry.State == EntityState.Modified ||
                (entry.State == EntityState.Deleted && !_allowAuditRetentionDelete));
        if (mutatedEntry is not null)
        {
            throw new InvalidOperationException("Audit event records are immutable.");
        }
    }

    private void ValidateSensitiveAccessPoliciesAndGrants()
    {
        var invalidPolicy = ChangeTracker.Entries<SensitiveAccessPolicyRevisionRecord>()
            .FirstOrDefault(entry =>
                entry.State is EntityState.Added or EntityState.Modified &&
                (entry.Entity.Revision < 1 ||
                 !SensitiveAccessPolicyLimits.IsValidDuration(entry.Entity.RequestTimeoutSeconds) ||
                 !SensitiveAccessPolicyLimits.IsValidDuration(entry.Entity.GrantDurationSeconds) ||
                 !SensitiveAccessPolicyLimits.IsValidMaximumSuccessfulReads(entry.Entity.MaximumSuccessfulReads)));
        if (invalidPolicy is not null)
        {
            throw new InvalidOperationException(
                "Sensitive access policy revisions require 60..3600 second durations and 1..10 successful reads.");
        }

        var invalidRequestSnapshot = ChangeTracker.Entries<SensitiveAccessRequestRecord>()
            .FirstOrDefault(entry =>
                entry.State is EntityState.Added or EntityState.Modified &&
                (entry.Entity.PolicyRevision < 1 ||
                 !SensitiveAccessPolicyLimits.IsValidDuration(entry.Entity.RequestTimeoutSeconds)));
        if (invalidRequestSnapshot is not null)
        {
            throw new InvalidOperationException(
                "Sensitive access requests require a valid policy revision and request timeout snapshot.");
        }

        var invalidGrant = ChangeTracker.Entries<SensitiveAccessGrantRecord>()
            .FirstOrDefault(entry =>
                entry.State is EntityState.Added or EntityState.Modified &&
                (entry.Entity.PolicyRevision < 1 ||
                 string.IsNullOrWhiteSpace(entry.Entity.OwnerUserId) ||
                 !SensitiveAccessPolicyLimits.IsValidDuration(entry.Entity.GrantDurationSeconds) ||
                 !SensitiveAccessPolicyLimits.IsValidMaximumSuccessfulReads(entry.Entity.MaximumSuccessfulReads) ||
                 entry.Entity.SuccessfulReadCount < 0 ||
                 entry.Entity.SuccessfulReadCount > entry.Entity.MaximumSuccessfulReads ||
                 entry.Entity.StartsAt >= entry.Entity.ExpiresAt));
        if (invalidGrant is not null)
        {
            throw new InvalidOperationException(
                "Sensitive access grants require a valid policy snapshot, bounded reads, and an active time window.");
        }
    }

    private void RejectProvenanceUpdates()
    {
        var invalidEntry = ChangeTracker.Entries<CollectionProvenanceRecord>()
            .FirstOrDefault(entry =>
                entry.State == EntityState.Modified ||
                (entry.State == EntityState.Deleted && !IsPrincipalCascadeDelete(entry.Entity)));
        if (invalidEntry is not null)
        {
            throw new InvalidOperationException("Collection provenance records are immutable.");
        }
    }

    private bool IsPrincipalCascadeDelete(CollectionProvenanceRecord provenance) =>
        (!string.IsNullOrWhiteSpace(provenance.MemoryItemId) &&
            ChangeTracker.Entries<SharedMemoryItemRecord>().Any(entry =>
                entry.State == EntityState.Deleted &&
                entry.Entity.Id == provenance.MemoryItemId)) ||
        (!string.IsNullOrWhiteSpace(provenance.SourceEventId) &&
            ChangeTracker.Entries<SourceEventRecord>().Any(entry =>
                entry.State == EntityState.Deleted &&
                entry.Entity.Id == provenance.SourceEventId));

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
