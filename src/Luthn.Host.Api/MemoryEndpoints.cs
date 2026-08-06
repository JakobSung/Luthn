using Luthn.Core.Classification;
using Luthn.Core.Common;
using Luthn.Core.Context;
using Luthn.Core.Memory;
using Luthn.Core.Persistence;
using Luthn.Core.Policy;
using Luthn.Core.Search;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Luthn.Host.Api;

public static class MemoryEndpoints
{
    public static IEndpointRouteBuilder MapMemoryItems(this IEndpointRouteBuilder app)
    {
        var memory = app.MapGroup("/api/memory");

        memory.MapPost("/items", CreateMemoryItem)
            .RequireServiceScope(ServiceScopes.MemoryWrite)
            .WithName("CreateMemoryItem");

        memory.MapGet("/items/{id}", ReadMemoryItem)
            .RequireServiceScope(ServiceScopes.MemoryRead)
            .WithName("ReadMemoryItem");

        memory.MapPost("/query", QueryMemoryItems)
            .RequireServiceScope(ServiceScopes.MemoryRead)
            .WithName("QueryMemoryItems");

        return app;
    }

    public static async Task<Results<Created<MemoryItemResponse>, BadRequest<ProblemDetails>, ProblemHttpResult>> CreateMemoryItem(
        CreateMemoryItemRequest request,
        AgentSafeMemoryProjectionSelector projectionSelector,
        ISensitiveMemoryPayloadProtector payloadProtector,
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
            "Invalid memory item request.",
            out var recallMetadata);
        if (metadataError is not null)
        {
            return TypedResults.BadRequest(metadataError);
        }

        var createdAt = DateTimeOffset.UtcNow;
        var memoryId = $"memory-{Guid.NewGuid():N}";
        var actor = ServiceTokenAuthorization.GetActor(httpContext);
        var principal = ServiceTokenAuthorization.GetPrincipal(httpContext);
        var provenanceError = CollectionProvenance.TryCreate(
            sourceEventId: null,
            memoryId,
            request.Provenance,
            actor,
            principal.UserId,
            principal.WorkspaceId,
            ServiceTokenAuthorization.IsServiceTokenAuthenticated(httpContext),
            createdAt,
            out var provenance);
        if (provenanceError is not null)
        {
            return TypedResults.BadRequest(provenanceError);
        }
        var sourceId = new PublicRecordId(memoryId);
        var normalizedTags = NormalizeTags(request.CoreTags!);
        AgentSafeMemoryProjectionSelection projection;
        try
        {
            projection = await projectionSelector.SelectAsync(
                new AgentSafeMemoryProjectionCandidate(
                    request.Title,
                    request.SafeSummary,
                    request.Sensitivity,
                    request.Visibility,
                    normalizedTags,
                    recallMetadata.ProjectKey,
                    recallMetadata.TaskKey,
                    recallMetadata.TopicTags,
                    request.SourceSessionId),
                sourceId,
                "shared-memory",
                cancellationToken);
        }
        catch (ClassificationProviderException error)
        {
            db.AuditEvents.Add(AuditEventFactory.ForWorkspace(
                principal,
                actor,
                "memory.item.classification_failed",
                memoryId,
                "metadata-only",
                projectionSelector.Boundary.RedactionState,
                DateTimeOffset.UtcNow,
                subjectType: "memory_item",
                outcome: "failed"));
            await db.SaveChangesAsync(cancellationToken);
            return ApiProblems.ClassificationProviderUnavailable(error);
        }
        var visibility = projection.Decision.AllowsAgentContext
            ? request.Visibility
            : MemoryVisibility.PrivateToOwner;
        var retention = BuildRetentionPolicy(request.RetentionKind, request.ExpiresAt);
        var item = new SharedMemoryItem(
            new PublicRecordId(memoryId),
            projection.Title,
            projection.SafeSummary,
            projection.Classification.Sensitivity,
            normalizedTags,
            visibility,
            retention,
            projection.RetainsEncryptedOriginal || string.IsNullOrWhiteSpace(request.SourceSessionId)
                ? null
                : new PublicRecordId(request.SourceSessionId.Trim()));

        var allowsAgentContext = projection.Decision.AllowsAgentContext && AllowsAgentContext(item, createdAt);
        var record = new SharedMemoryItemRecord
        {
            Id = item.Id.Value,
            Title = item.Title.Trim(),
            SafeSummary = item.SafeSummary.Trim(),
            Sensitivity = item.Sensitivity,
            CoreTags = item.CoreTags.ToList(),
            ProjectKey = recallMetadata.ProjectKey,
            TaskKey = recallMetadata.TaskKey,
            TopicTags = recallMetadata.TopicTags.ToList(),
            Visibility = item.Visibility,
            RetentionKind = item.Retention.Kind,
            ExpiresAt = item.Retention.ExpiresAt,
            SourceSessionId = item.SourceSessionId?.Value,
            AllowsAgentContext = allowsAgentContext,
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
            CreatedBy = actor,
            WorkspaceId = principal.WorkspaceId,
            OwnerUserId = principal.UserId
        };
        db.SharedMemoryItems.Add(record);
        db.CollectionProvenance.Add(provenance);
        var originalPayload = projection.RetainsEncryptedOriginal
            ? new SensitiveMemoryPayload(
                SensitiveMemoryPayload.CurrentContractVersion,
                request.Title.Trim(),
                request.SafeSummary.Trim(),
                normalizedTags,
                recallMetadata.ProjectKey,
                recallMetadata.TaskKey,
                recallMetadata.TopicTags,
                string.IsNullOrWhiteSpace(request.SourceSessionId)
                    ? null
                    : request.SourceSessionId.Trim())
            : null;
        if (projection.RetainsEncryptedOriginal && allowsAgentContext)
        {
            db.SensitiveMemoryPayloads.Add(SensitiveMemoryPersistence.ProtectOriginalForSafeProjection(
                record,
                originalPayload!,
                payloadProtector,
                projectionSelector.SensitiveDataDetector,
                createdAt));
        }
        else if (SensitiveMemoryPersistence.RequiresProtection(record))
        {
            var payload = originalPayload ?? SensitiveMemoryPersistence.FromRecord(record);
            db.SensitiveMemoryPayloads.Add(SensitiveMemoryPersistence.Protect(
                record,
                payload,
                payloadProtector,
                createdAt));
        }

        db.AuditEvents.Add(AuditEventFactory.ForWorkspace(
            principal,
            actor,
            "memory.item.classified",
            item.Id.Value,
            "metadata-only",
            projection.RetainsEncryptedOriginal && allowsAgentContext
                ? "safe-projection-with-encrypted-original"
                : allowsAgentContext
                    ? "safe-projection-only"
                    : "encrypted-payload-only",
            createdAt,
            subjectType: "memory_item",
            outcome: "completed"));

        await db.SaveChangesAsync(cancellationToken);

        var response = ToResponse(record);

        return TypedResults.Created($"/api/memory/items/{memoryId}", response);
    }

    public static async Task<Results<Ok<MemoryItemResponse>, NotFound>> ReadMemoryItem(
        string id,
        LuthnDbContext db,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var record = await ReadAgentSafeMemoryItems(
                db,
                now,
                ServiceTokenAuthorization.GetPrincipal(httpContext).WorkspaceId)
            .Where(item => item.Id == id)
            .SingleOrDefaultAsync(cancellationToken);

        if (record is null)
        {
            return TypedResults.NotFound();
        }

        return TypedResults.Ok(ToResponse(record));
    }

    public static async Task<Results<Ok<MemoryQueryResponse>, BadRequest<ProblemDetails>>> QueryMemoryItems(
        MemoryQueryRequest request,
        IRetrievalBackend retrievalBackend,
        IRetrievalCandidateSelector candidateSelector,
        LuthnDbContext db,
        IOperationalMetrics metrics,
        TimeProvider timeProvider,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var validationError = ValidateQuery(request);
        if (validationError is not null)
        {
            return TypedResults.BadRequest(validationError);
        }

        SafeSearchRequest searchRequest;
        try
        {
            searchRequest = new SafeSearchRequest(
                request.Query,
                request.CoreTags,
                request.MaxItems,
                request.ProjectKey,
                request.TaskKey,
                request.TopicTags);
        }
        catch (ArgumentException error)
        {
            return TypedResults.BadRequest(ApiValidation.CreateProblem(
                "Invalid memory query request.",
                error.Message));
        }
        var telemetry = new SearchTelemetryScope(metrics, timeProvider, "memory_query");
        try
        {
            var candidates = await candidateSelector.SelectSharedMemoryAsync(
                searchRequest,
                ServiceTokenAuthorization.GetPrincipal(httpContext).WorkspaceId,
                cancellationToken);
            var search = retrievalBackend.Search(
                searchRequest,
                candidates);
            var resultIds = search.Results.Select(result => result.Id).ToArray();
            var now = timeProvider.GetUtcNow();
            var records = resultIds.Length == 0
                ? []
                : await ReadAgentSafeMemoryItems(
                        db,
                        now,
                        ServiceTokenAuthorization.GetPrincipal(httpContext).WorkspaceId)
                    .Where(record => resultIds.Contains(record.Id))
                    .ToArrayAsync(cancellationToken);
            var recordsById = records.ToDictionary(record => record.Id, StringComparer.Ordinal);
            var items = search.Results
                .Where(result => recordsById.ContainsKey(result.Id))
                .Select(result => ToResponse(recordsById[result.Id]))
                .ToArray();

            var response = new MemoryQueryResponse(search.Query, search.CoreTags, items)
            {
                RetrievalId = telemetry.RetrievalId,
                ProjectKey = search.ProjectKey,
                TaskKey = search.TaskKey,
                TopicTags = search.TopicTags
            };
            telemetry.Complete(items.Length);
            return TypedResults.Ok(response);
        }
        catch (TimeoutException)
        {
            telemetry.Timeout();
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            telemetry.Canceled();
            throw;
        }
        catch
        {
            telemetry.Error();
            throw;
        }
    }

    private static IQueryable<SharedMemoryItemRecord> ReadAgentSafeMemoryItems(
        LuthnDbContext db,
        DateTimeOffset now,
        string workspaceId) =>
        db.SharedMemoryItems
            .AsNoTracking()
            .Where(record => record.WorkspaceId == workspaceId &&
                record.AllowsAgentContext &&
                record.Sensitivity == SensitivityLevel.Public &&
                (record.Visibility == MemoryVisibility.PublicSafe ||
                    record.Visibility == MemoryVisibility.SharedAcrossAgents) &&
                (record.ExpiresAt == null || record.ExpiresAt > now));

    private static ProblemDetails? Validate(CreateMemoryItemRequest request)
    {
        var title = "Invalid memory item request.";
        var titleError = ApiValidation.ValidateRequiredText(
            request.Title,
            "title",
            ApiValidation.TitleMaxLength,
            title);
        if (titleError is not null)
        {
            return titleError;
        }

        var safeSummaryError = ApiValidation.ValidateRequiredText(
            request.SafeSummary,
            "safeSummary",
            ApiValidation.SafeSummaryMaxLength,
            title);
        if (safeSummaryError is not null)
        {
            return safeSummaryError;
        }

        var coreTagsError = ApiValidation.ValidateCoreTags(
            request.CoreTags,
            "coreTags",
            title,
            required: true);
        if (coreTagsError is not null)
        {
            return coreTagsError;
        }

        if (!string.IsNullOrWhiteSpace(request.SourceSessionId) &&
            request.SourceSessionId.Trim().Length > ApiValidation.PublicRecordIdMaxLength)
        {
            return CreateValidationProblem(
                $"sourceSessionId must be {ApiValidation.PublicRecordIdMaxLength} characters or fewer.");
        }

        try
        {
            _ = BuildRetentionPolicy(request.RetentionKind, request.ExpiresAt);
            _ = new SharedMemoryItem(
                new PublicRecordId("memory-validation"),
                request.Title,
                request.SafeSummary,
                request.Sensitivity,
                NormalizeTags(request.CoreTags!),
                request.Visibility,
                BuildRetentionPolicy(request.RetentionKind, request.ExpiresAt),
                string.IsNullOrWhiteSpace(request.SourceSessionId)
                    ? null
                    : new PublicRecordId(request.SourceSessionId.Trim()));
        }
        catch (ArgumentException exception)
        {
            return CreateValidationProblem(exception.Message);
        }

        return null;
    }

    private static ProblemDetails? ValidateQuery(MemoryQueryRequest request)
    {
        var title = "Invalid memory query request.";
        var queryError = ApiValidation.ValidateOptionalSearchQuery(request.Query, title);
        if (queryError is not null)
        {
            return queryError;
        }

        return ApiValidation.ValidateCoreTags(request.CoreTags, "coreTags", title, required: false);
    }

    private static MemoryRetentionPolicy BuildRetentionPolicy(
        MemoryRetentionKind retentionKind,
        DateTimeOffset? expiresAt) =>
        retentionKind switch
        {
            MemoryRetentionKind.Durable => expiresAt is not null
                ? throw new ArgumentException("expiresAt must be omitted for durable memory.", nameof(expiresAt))
                : MemoryRetentionPolicy.Durable(),
            MemoryRetentionKind.Session => expiresAt is null
                ? throw new ArgumentException("expiresAt is required for session memory.", nameof(expiresAt))
                : MemoryRetentionPolicy.Session(expiresAt.Value),
            MemoryRetentionKind.Ephemeral => expiresAt is null
                ? throw new ArgumentException("expiresAt is required for ephemeral memory.", nameof(expiresAt))
                : MemoryRetentionPolicy.Ephemeral(expiresAt.Value),
            _ => throw new ArgumentOutOfRangeException(nameof(retentionKind), retentionKind, "Unsupported memory retention kind.")
        };

    private static bool AllowsAgentContext(SharedMemoryItem item, DateTimeOffset now) =>
        ExternalMemoryProjectionPolicy.AllowsExternalMemoryExport(
            item.Sensitivity,
            item.Visibility,
            item.Retention.ExpiresAt,
            now);

    private static ProblemDetails CreateValidationProblem(string detail) =>
        ApiValidation.CreateProblem("Invalid memory item request.", detail);

    private static List<string> NormalizeTags(IEnumerable<string> coreTags) =>
        coreTags
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Select(tag => tag.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static MemoryItemResponse ToResponse(SharedMemoryItemRecord record) =>
        ToResponse(
            record.Id,
            record.Title,
            record.SafeSummary,
            record.Sensitivity,
            record.CoreTags,
            record.Visibility,
            record.RetentionKind,
            record.ExpiresAt,
            record.SourceSessionId,
            record.AllowsAgentContext,
            record.CreatedAt,
            record.ProjectKey,
            record.TaskKey,
            record.TopicTags);

    private static MemoryItemResponse ToResponse(
        string id,
        string title,
        string safeSummary,
        SensitivityLevel sensitivity,
        IReadOnlyList<string> coreTags,
        MemoryVisibility visibility,
        MemoryRetentionKind retentionKind,
        DateTimeOffset? expiresAt,
        string? sourceSessionId,
        bool allowsAgentContext,
        DateTimeOffset createdAt,
        string? projectKey,
        string? taskKey,
        IReadOnlyList<string> topicTags) =>
        new(
            id,
            title,
            safeSummary,
            sensitivity,
            coreTags,
            visibility,
            retentionKind,
            expiresAt,
            sourceSessionId,
            allowsAgentContext,
            createdAt)
        {
            ProjectKey = projectKey,
            TaskKey = taskKey,
            TopicTags = topicTags
        };
}

public sealed record CreateMemoryItemRequest
{
    public string Title { get; init; } = "";
    public string SafeSummary { get; init; } = "";
    public SensitivityLevel Sensitivity { get; init; } = SensitivityLevel.Public;
    public IReadOnlyList<string>? CoreTags { get; init; }
    public MemoryVisibility Visibility { get; init; } = MemoryVisibility.PrivateToOwner;
    public MemoryRetentionKind RetentionKind { get; init; } = MemoryRetentionKind.Durable;
    public DateTimeOffset? ExpiresAt { get; init; }
    public string? SourceSessionId { get; init; }
    public string? ProjectKey { get; init; }
    public string? TaskKey { get; init; }
    public IReadOnlyList<string>? TopicTags { get; init; }
    public CollectionProvenanceClaims? Provenance { get; init; }
}

public sealed record MemoryQueryRequest(
    string? Query = null,
    IReadOnlyList<string>? CoreTags = null,
    int MaxItems = 20,
    string? ProjectKey = null,
    string? TaskKey = null,
    IReadOnlyList<string>? TopicTags = null);

public sealed record MemoryQueryResponse(
    string? Query,
    IReadOnlyList<string> CoreTags,
    IReadOnlyList<MemoryItemResponse> Items)
{
    public string? RetrievalId { get; init; }
    public string? ProjectKey { get; init; }
    public string? TaskKey { get; init; }
    public IReadOnlyList<string> TopicTags { get; init; } = [];
}

public sealed record MemoryItemResponse(
    string Id,
    string Title,
    string SafeSummary,
    SensitivityLevel Sensitivity,
    IReadOnlyList<string> CoreTags,
    MemoryVisibility Visibility,
    MemoryRetentionKind RetentionKind,
    DateTimeOffset? ExpiresAt,
    string? SourceSessionId,
    bool AllowsAgentContext,
    DateTimeOffset CreatedAt)
{
    public string? ProjectKey { get; init; }
    public string? TaskKey { get; init; }
    public IReadOnlyList<string> TopicTags { get; init; } = [];
}
