using System.Security.Cryptography;
using System.Text;
using Luthn.Core.Classification;
using Luthn.Core.Common;
using Luthn.Core.Memory;
using Luthn.Core.Persistence;
using Luthn.Core.Policy;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Luthn.Host.Api;

public static class TurnSummaryEndpoints
{
    public static IEndpointRouteBuilder MapTurnSummaryIntake(this IEndpointRouteBuilder app)
    {
        var agent = app.MapGroup("/api/agent");

        agent.MapPost("/turn-summaries", IntakeTurnSummary)
            .RequireServiceScope(ServiceScopes.AgentSummaryWrite)
            .WithName("IntakeTurnSummary");

        return app;
    }

    public static async Task<Results<Created<TurnSummaryIntakeResponse>, Ok<TurnSummaryIntakeResponse>, BadRequest<ProblemDetails>, ProblemHttpResult>> IntakeTurnSummary(
        TurnSummaryIntakeRequest request,
        AgentSafeMemoryProjectionSelector projectionSelector,
        ISensitiveMemoryPayloadProtector payloadProtector,
        IOptions<LuthnMemoryOptions> memoryOptions,
        TimeProvider timeProvider,
        LuthnDbContext db,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var validationError = Validate(request);
        if (validationError is not null)
        {
            return TypedResults.BadRequest(validationError);
        }

        var metadataError = RecallMetadataValidation.TryNormalize(
            request.ProjectKey,
            request.TaskKey,
            request.TopicTags,
            "Invalid turn summary intake request.",
            out var recallMetadata);
        if (metadataError is not null)
        {
            return TypedResults.BadRequest(metadataError);
        }

        var normalizedTags = NormalizeTags(request.CoreTags!);
        var contentDigest = NormalizeDigest(request.ContentDigest) ?? ComputeSha256Digest(request.Summary);
        var actor = ServiceTokenAuthorization.GetActor(httpContext);
        var principal = ServiceTokenAuthorization.GetPrincipal(httpContext);
        var summaryId = CreateStableSummaryId(
            request,
            contentDigest,
            principal.WorkspaceId,
            principal.UserId);
        var sourceEventId = summaryId;
        var existingSource = await db.SourceEvents
            .AsNoTracking()
            .SingleOrDefaultAsync(
                record => record.Id == sourceEventId &&
                    record.WorkspaceId == principal.WorkspaceId &&
                    record.OwnerUserId == principal.UserId,
                cancellationToken);
        if (existingSource is not null)
        {
            var existing = await BuildExistingResponseAsync(
                db,
                sourceEventId,
                principal.WorkspaceId,
                principal.UserId,
                cancellationToken);
            return TypedResults.Ok(existing);
        }

        var receivedAt = timeProvider.GetUtcNow();
        var memoryItemId = $"memory-{sourceEventId}";
        var provenanceError = CollectionProvenance.TryCreate(
            sourceEventId,
            memoryItemId,
            request.Provenance,
            actor,
            principal.UserId,
            principal.WorkspaceId,
            ServiceTokenAuthorization.IsServiceTokenAuthenticated(httpContext),
            receivedAt,
            out var provenance,
            fallbackAgentId: request.SourceAgent,
            fallbackApplicationId: request.SourceAgent);
        if (provenanceError is not null)
        {
            return TypedResults.BadRequest(provenanceError);
        }
        var sourceId = new PublicRecordId(sourceEventId);
        var resolvedTitle = ResolveTitle(request);
        var providerAuditEventId = $"audit-{Guid.NewGuid():N}";
        db.AuditEvents.Add(AuditEventFactory.ForWorkspace(
            principal,
            actor,
            "turn_summary.classification_provider.invoked",
            sourceEventId,
            projectionSelector.Boundary.PayloadClass,
            projectionSelector.Boundary.RedactionState,
            receivedAt,
            subjectType: "source_event",
            outcome: "started",
            id: providerAuditEventId));
        await db.SaveChangesAsync(cancellationToken);

        AgentSafeMemoryProjectionSelection projection;
        try
        {
            projection = await projectionSelector.SelectAsync(
                new AgentSafeMemoryProjectionCandidate(
                    resolvedTitle,
                    request.Summary,
                    SensitivityLevel.Public,
                    MemoryVisibility.SharedAcrossAgents,
                    normalizedTags,
                    recallMetadata.ProjectKey,
                    recallMetadata.TaskKey,
                    recallMetadata.TopicTags,
                    request.SessionId),
                sourceId,
                "turn-summary",
                cancellationToken);
        }
        catch (ClassificationProviderException error)
        {
            db.AuditEvents.Add(AuditEventFactory.ForWorkspace(
                principal,
                actor,
                "turn_summary.classification_provider.failed",
                sourceEventId,
                "metadata-only",
                projectionSelector.Boundary.RedactionState,
                timeProvider.GetUtcNow(),
                subjectType: "source_event",
                outcome: "failed"));
            await db.SaveChangesAsync(cancellationToken);
            return ApiProblems.ClassificationProviderUnavailable(error);
        }

        var classification = projection.Classification;
        var decision = projection.Decision;
        var allowsAgentContext = decision.AllowsAgentContext &&
            classification.Sensitivity == SensitivityLevel.Public &&
            !classification.ContainsSensitiveMaterial;
        var classificationResultId = $"classification-{sourceEventId}";
        var auditEventId = $"audit-{Guid.NewGuid():N}";
        var visibility = allowsAgentContext
            ? MemoryVisibility.SharedAcrossAgents
            : MemoryVisibility.PrivateToOwner;
        var memory = new SharedMemoryItem(
            new PublicRecordId(memoryItemId),
            projection.Title,
            projection.SafeSummary,
            classification.Sensitivity,
            normalizedTags,
            visibility,
            MemoryRetentionPolicy.Ephemeral(
                memoryOptions.Value.GetAutomaticTurnExpiration(receivedAt)),
            projection.RetainsEncryptedOriginal
                ? null
                : new PublicRecordId(request.SessionId.Trim()));

        db.SourceEvents.Add(new SourceEventRecord
        {
            Id = sourceEventId,
            SourceSystem = request.SourceAgent.Trim(),
            SourceType = "turn-summary",
            ReceivedAt = receivedAt,
            ContentDigest = contentDigest,
            ContainsSensitiveMaterial = projection.OriginalClassification.ContainsSensitiveMaterial,
            WorkspaceId = principal.WorkspaceId,
            OwnerUserId = principal.UserId
        });
        db.CollectionProvenance.Add(provenance);
        db.ClassificationResults.Add(new ClassificationResultRecord
        {
            Id = classificationResultId,
            SourceEventId = sourceEventId,
            Sensitivity = classification.Sensitivity,
            Confidence = classification.Confidence,
            Categories = classification.Categories
                .OrderBy(category => category, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            ContainsSensitiveMaterial = classification.ContainsSensitiveMaterial,
            StorageDecision = decision.Kind
        });
        var memoryRecord = new SharedMemoryItemRecord
        {
            Id = memory.Id.Value,
            Title = memory.Title.Trim(),
            SafeSummary = memory.SafeSummary.Trim(),
            Sensitivity = memory.Sensitivity,
            CoreTags = memory.CoreTags.ToList(),
            ProjectKey = recallMetadata.ProjectKey,
            TaskKey = recallMetadata.TaskKey,
            TopicTags = recallMetadata.TopicTags.ToList(),
            Visibility = memory.Visibility,
            RetentionKind = memory.Retention.Kind,
            ExpiresAt = memory.Retention.ExpiresAt,
            SourceSessionId = memory.SourceSessionId?.Value,
            AllowsAgentContext = allowsAgentContext,
            CreatedAt = receivedAt,
            UpdatedAt = receivedAt,
            CreatedBy = actor,
            WorkspaceId = principal.WorkspaceId,
            OwnerUserId = principal.UserId
        };
        db.SharedMemoryItems.Add(memoryRecord);
        var originalPayload = projection.RetainsEncryptedOriginal
            ? new SensitiveMemoryPayload(
                SensitiveMemoryPayload.CurrentContractVersion,
                resolvedTitle,
                request.Summary.Trim(),
                normalizedTags,
                recallMetadata.ProjectKey,
                recallMetadata.TaskKey,
                recallMetadata.TopicTags,
                request.SessionId.Trim())
            : null;
        SensitiveMemoryPayloadRecord? protectedPayload = null;
        if (projection.RetainsEncryptedOriginal && allowsAgentContext)
        {
            protectedPayload = SensitiveMemoryPersistence.ProtectOriginalForSafeProjection(
                memoryRecord,
                originalPayload!,
                payloadProtector,
                projectionSelector.SensitiveDataDetector,
                receivedAt);
        }
        else if (SensitiveMemoryPersistence.RequiresProtection(memoryRecord))
        {
            var payload = originalPayload ?? SensitiveMemoryPersistence.FromRecord(memoryRecord);
            protectedPayload = SensitiveMemoryPersistence.Protect(
                memoryRecord,
                payload,
                payloadProtector,
                receivedAt);
        }

        string? sensitiveReferenceId = null;
        if (protectedPayload is not null)
        {
            sensitiveReferenceId = $"sensitive-{sourceEventId}";
            db.SensitiveMemoryPayloads.Add(protectedPayload);
            db.SensitiveRecordReferences.Add(new SensitiveRecordReferenceRecord
            {
                Id = sensitiveReferenceId,
                SourceEventId = sourceEventId,
                MemoryItemId = memoryRecord.Id,
                SourceSystem = request.SourceAgent.Trim(),
                SourceType = "turn-summary",
                ReceivedAt = receivedAt,
                ExpiresAt = memoryRecord.ExpiresAt,
                ContainsSensitiveMaterial = true,
                ReferenceLabel = $"sensitive-turn-summary:{sourceEventId}",
                RedactedSummary = allowsAgentContext ? memoryRecord.SafeSummary : "",
                WorkspaceId = principal.WorkspaceId,
                OwnerUserId = principal.UserId
            });
        }
        db.AuditEvents.Add(AuditEventFactory.ForWorkspace(
            principal,
            actor,
            "turn_summary.intake.classified",
            sourceEventId,
            "metadata-only",
            projection.RetainsEncryptedOriginal && allowsAgentContext
                ? "safe-projection-with-encrypted-original"
                : allowsAgentContext
                    ? "safe-projection-only"
                    : "encrypted-payload-only",
            receivedAt,
            subjectType: "source_event",
            outcome: "completed",
            id: auditEventId));
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            if (!await ExistingSourceEventExistsAsync(
                    db,
                    sourceEventId,
                    principal.WorkspaceId,
                    principal.UserId,
                    cancellationToken))
            {
                throw;
            }

            var existing = await BuildExistingResponseAsync(
                db,
                sourceEventId,
                principal.WorkspaceId,
                principal.UserId,
                cancellationToken);
            return TypedResults.Ok(existing);
        }

        var response = ToResponse(
            summaryId,
            sourceEventId,
            classificationResultId,
            memoryItemId,
            sensitiveReferenceId,
            auditEventId,
            allowsAgentContext,
            duplicate: false,
            classification,
            decision);

        return TypedResults.Created($"/api/agent/turn-summaries/{summaryId}", response);
    }

    private static async Task<TurnSummaryIntakeResponse> BuildExistingResponseAsync(
        LuthnDbContext db,
        string sourceEventId,
        string workspaceId,
        string ownerUserId,
        CancellationToken cancellationToken)
    {
        var classification = await (
                from classificationRecord in db.ClassificationResults.AsNoTracking()
                join source in db.SourceEvents.AsNoTracking()
                    on classificationRecord.SourceEventId equals source.Id
                where classificationRecord.SourceEventId == sourceEventId &&
                    source.WorkspaceId == workspaceId &&
                    source.OwnerUserId == ownerUserId
                select classificationRecord)
            .SingleAsync(cancellationToken);
        var memory = await db.SharedMemoryItems
            .AsNoTracking()
            .SingleOrDefaultAsync(
                record => record.Id == $"memory-{sourceEventId}" &&
                    record.WorkspaceId == workspaceId &&
                    record.OwnerUserId == ownerUserId,
                cancellationToken);
        var sensitiveReferenceId = await db.SensitiveRecordReferences
            .AsNoTracking()
            .Where(record => record.SourceEventId == sourceEventId &&
                record.WorkspaceId == workspaceId &&
                record.OwnerUserId == ownerUserId)
            .Select(record => record.Id)
            .SingleOrDefaultAsync(cancellationToken);
        var audit = await db.AuditEvents
            .AsNoTracking()
            .Where(record => record.SubjectId == sourceEventId &&
                record.Action == "turn_summary.intake.classified")
            .OrderByDescending(record => record.OccurredAt)
            .FirstOrDefaultAsync(cancellationToken);
        var decision = RehydrateDecision(classification);
        var result = new ClassificationResult(
            new PublicRecordId(sourceEventId),
            classification.Sensitivity,
            classification.Confidence,
            classification.Categories.ToHashSet(StringComparer.OrdinalIgnoreCase),
            classification.ContainsSensitiveMaterial);

        return ToResponse(
            sourceEventId,
            sourceEventId,
            classification.Id,
            memory?.Id,
            sensitiveReferenceId,
            audit?.Id ?? "",
            memory?.AllowsAgentContext ?? false,
            duplicate: true,
            result,
            decision);
    }

    private static async Task<bool> ExistingSourceEventExistsAsync(
        LuthnDbContext db,
        string sourceEventId,
        string workspaceId,
        string ownerUserId,
        CancellationToken cancellationToken)
    {
        db.ChangeTracker.Clear();
        return await db.SourceEvents
            .AsNoTracking()
            .AnyAsync(
                record => record.Id == sourceEventId &&
                    record.WorkspaceId == workspaceId &&
                    record.OwnerUserId == ownerUserId,
                cancellationToken);
    }

    private static StorageDecision RehydrateDecision(ClassificationResultRecord record) =>
        record.StorageDecision switch
        {
            StorageDecisionKind.WikiCandidate => new StorageDecision(
                StorageDecisionKind.WikiCandidate,
                ["Existing turn summary was already classified as wiki-safe."],
                AllowsWikiProjection: true,
                AllowsAgentContext: record.Sensitivity == SensitivityLevel.Public,
                RequiresHumanReview: record.Sensitivity == SensitivityLevel.Internal),
            StorageDecisionKind.SensitiveDbOnly => new StorageDecision(
                StorageDecisionKind.SensitiveDbOnly,
                ["Existing turn summary stays behind the memory boundary."],
                AllowsWikiProjection: false,
                AllowsAgentContext: false,
                RequiresHumanReview: record.Sensitivity == SensitivityLevel.Restricted),
            StorageDecisionKind.NeedsReview => new StorageDecision(
                StorageDecisionKind.NeedsReview,
                ["Existing turn summary requires review."],
                AllowsWikiProjection: false,
                AllowsAgentContext: false,
                RequiresHumanReview: true),
            _ => new StorageDecision(
                StorageDecisionKind.Ignore,
                ["Existing turn summary is not agent-visible."],
                AllowsWikiProjection: false,
                AllowsAgentContext: false,
                RequiresHumanReview: false)
        };

    private static TurnSummaryIntakeResponse ToResponse(
        string summaryId,
        string sourceEventId,
        string classificationResultId,
        string? memoryItemId,
        string? sensitiveReferenceId,
        string auditEventId,
        bool allowsAgentContext,
        bool duplicate,
        ClassificationResult classification,
        StorageDecision decision) =>
        new(
            summaryId,
            sourceEventId,
            classificationResultId,
            memoryItemId,
            sensitiveReferenceId,
            auditEventId,
            allowsAgentContext,
            duplicate,
            new ClassificationPreviewClassification(
                classification.Sensitivity,
                classification.Confidence,
                classification.Categories,
                classification.ContainsSensitiveMaterial),
            decision);

    private static ProblemDetails? Validate(TurnSummaryIntakeRequest request)
    {
        const string title = "Invalid turn summary intake request.";
        var sessionError = ApiValidation.ValidatePublicRecordId(
            request.SessionId,
            "sessionId",
            title,
            out _);
        if (sessionError is not null)
        {
            return sessionError;
        }

        var sourceAgentError = ApiValidation.ValidateRequiredText(
            request.SourceAgent,
            "sourceAgent",
            ApiValidation.SourceTextMaxLength,
            title);
        if (sourceAgentError is not null)
        {
            return sourceAgentError;
        }

        var summaryError = ApiValidation.ValidateRequiredText(
            request.Summary,
            "summary",
            ApiValidation.SafeSummaryMaxLength,
            title);
        if (summaryError is not null)
        {
            return summaryError;
        }

        var tagsError = ApiValidation.ValidateCoreTags(
            request.CoreTags,
            "coreTags",
            title,
            required: true);
        if (tagsError is not null)
        {
            return tagsError;
        }

        return ValidateOptionalPublicId(request.TurnId, "turnId", title) ??
            ValidateOptionalPublicId(request.IdempotencyKey, "idempotencyKey", title) ??
            ValidateOptionalBoundedText(request.TurnRange, "turnRange", ApiValidation.SourceTextMaxLength, title) ??
            ValidateOptionalBoundedText(request.Title, "title", ApiValidation.TitleMaxLength, title) ??
            ValidateOptionalDigest(request.ContentDigest, title);
    }

    private static ProblemDetails? ValidateOptionalPublicId(string? value, string fieldName, string title)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return ApiValidation.ValidatePublicRecordId(value, fieldName, title, out _);
    }

    private static ProblemDetails? ValidateOptionalBoundedText(
        string? value,
        string fieldName,
        int maxLength,
        string title)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim().Length > maxLength
            ? ApiValidation.CreateProblem(title, $"{fieldName} must be {maxLength} characters or fewer.")
            : null;
    }

    private static ProblemDetails? ValidateOptionalDigest(string? value, string title)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        if (trimmed.Length > 256 || trimmed.Any(char.IsWhiteSpace))
        {
            return ApiValidation.CreateProblem(title, "contentDigest must be 256 characters or fewer and cannot contain whitespace.");
        }

        return null;
    }

    private static string ResolveTitle(TurnSummaryIntakeRequest request) =>
        string.IsNullOrWhiteSpace(request.Title)
            ? $"{request.SourceAgent.Trim()} turn summary"
            : request.Title.Trim();

    private static string CreateStableSummaryId(
        TurnSummaryIntakeRequest request,
        string contentDigest,
        string workspaceId,
        string ownerUserId)
    {
        var stableInput = string.IsNullOrWhiteSpace(request.IdempotencyKey)
            ? EncodeStableKeyParts(
                "derived",
                workspaceId,
                ownerUserId,
                request.SourceAgent.Trim(),
                request.SessionId.Trim(),
                request.TurnId?.Trim(),
                request.TurnRange?.Trim(),
                contentDigest)
            : EncodeStableKeyParts("explicit", workspaceId, ownerUserId, request.IdempotencyKey.Trim());
        return $"turn-summary-{HashFragment(stableInput)}";
    }

    private static string EncodeStableKeyParts(params string?[] parts)
    {
        var builder = new StringBuilder();
        foreach (var part in parts)
        {
            if (part is null)
            {
                builder.Append("-1:");
                continue;
            }

            builder.Append(part.Length).Append(':').Append(part);
        }

        return builder.ToString();
    }

    private static string? NormalizeDigest(string? contentDigest) =>
        string.IsNullOrWhiteSpace(contentDigest) ? null : contentDigest.Trim();

    private static string ComputeSha256Digest(string content) =>
        $"sha256:{HashFragment(content, length: 64)}";

    private static string HashFragment(string value, int length = 32)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant()[..length];
    }

    private static List<string> NormalizeTags(IEnumerable<string> coreTags) =>
        coreTags
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Select(tag => tag.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}

public sealed record TurnSummaryIntakeRequest
{
    public string SessionId { get; init; } = "";
    public string? TurnId { get; init; }
    public string? TurnRange { get; init; }
    public string SourceAgent { get; init; } = "codex";
    public string? ProjectKey { get; init; }
    public string? TaskKey { get; init; }
    public IReadOnlyList<string>? TopicTags { get; init; }
    public string Summary { get; init; } = "";
    public IReadOnlyList<string>? CoreTags { get; init; }
    public string? ContentDigest { get; init; }
    public string? IdempotencyKey { get; init; }
    public string? Title { get; init; }
    public CollectionProvenanceClaims? Provenance { get; init; }
}

public sealed record TurnSummaryIntakeResponse(
    string SummaryId,
    string SourceEventId,
    string ClassificationResultId,
    string? MemoryItemId,
    string? SensitiveReferenceId,
    string AuditEventId,
    bool AllowsAgentContext,
    bool Duplicate,
    ClassificationPreviewClassification Classification,
    StorageDecision StorageDecision);
