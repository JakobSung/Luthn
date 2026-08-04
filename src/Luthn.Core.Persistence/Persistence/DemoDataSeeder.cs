using Luthn.Core.Classification;
using Luthn.Core.Common;
using Microsoft.EntityFrameworkCore;

namespace Luthn.Core.Persistence;

public static class DemoDataSeeder
{
    public const string SourceEventId = "demo-source-runbook";
    public const string WikiProposalId = "wiki-demo-runbook";
    public const string AuditEventId = "audit-demo-seed";

    public static async Task<DemoSeedResult> SeedAsync(
        LuthnDbContext db,
        DateTimeOffset? observedAt = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);

        var created = false;
        var timestamp = observedAt ?? DateTimeOffset.UtcNow;

        if (!await db.SourceEvents.AnyAsync(record => record.Id == SourceEventId, cancellationToken))
        {
            db.SourceEvents.Add(new SourceEventRecord
            {
                Id = SourceEventId,
                SourceSystem = "demo",
                SourceType = "public-runbook",
                ReceivedAt = timestamp,
                ContentDigest = "sha256:demo-public-runbook",
                ContainsSensitiveMaterial = false,
                WorkspaceId = WorkspaceIds.Default
            });
            created = true;
        }

        if (!await db.WikiProposals.AnyAsync(record => record.Id == WikiProposalId, cancellationToken))
        {
            db.WikiProposals.Add(new WikiProposalRecord
            {
                Id = WikiProposalId,
                SourceEventId = SourceEventId,
                Title = "Demo agent runbook",
                SafeSummary = "Public-safe demo context for local agent quickstarts.",
                Sensitivity = SensitivityLevel.Public,
                CoreTags = ["runbook", "demo"],
                AllowsAgentContext = true,
                CreatedAt = timestamp,
                WorkspaceId = WorkspaceIds.Default
            });
            created = true;
        }

        if (!await db.CollectionProvenance.AnyAsync(
                record => record.SourceEventId == SourceEventId,
                cancellationToken))
        {
            db.CollectionProvenance.Add(new CollectionProvenanceRecord
            {
                Id = "provenance-demo-source-runbook",
                ContractVersion = 1,
                SourceEventId = SourceEventId,
                AuthenticatedActor = "luthn-tools",
                ActorTrust = "local-runtime",
                ClaimsTrust = "no-claims",
                WorkspaceId = WorkspaceIds.Default,
                ReceivedAt = timestamp
            });
            created = true;
        }

        if (!await db.AuditEvents.AnyAsync(record => record.Id == AuditEventId, cancellationToken))
        {
            db.AuditEvents.Add(new AuditEventRecord
            {
                Id = AuditEventId,
                OccurredAt = timestamp,
                ScopeKind = AuditEventScopeKind.Workspace,
                WorkspaceId = WorkspaceIds.Default,
                Actor = "luthn-tools",
                ActorKind = "system",
                Action = "demo.seed",
                SubjectId = WikiProposalId,
                SubjectType = "wiki_proposal",
                Outcome = "completed",
                PayloadClass = "public-safe-demo",
                RedactionState = "safe-projection-only"
            });
            created = true;
        }

        if (created)
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        return new DemoSeedResult(WikiProposalId, created);
    }
}

public sealed record DemoSeedResult(string WikiProposalId, bool Created);
