using System.Security.Cryptography;
using System.Text;
using Luthn.Core.Classification;
using Luthn.Core.Common;
using Luthn.Core.Persistence;
using Luthn.Core.Policy;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Luthn.Host.Api;

public static class SourceIntakeEndpoints
{
    public static IEndpointRouteBuilder MapSourceIntake(this IEndpointRouteBuilder app)
    {
        var sources = app.MapGroup("/api/sources");

        sources.MapPost("", IntakeSource)
            .RequireServiceScope(ServiceScopes.SourceWrite)
            .WithName("IntakeSource");

        return app;
    }

    public static async Task<Results<Created<SourceIntakeResponse>, BadRequest<ProblemDetails>, ProblemHttpResult>> IntakeSource(
        SourceIntakeRequest request,
        IContentClassifier classifier,
        IPolicyEngine policyEngine,
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
            "Invalid source intake request.",
            out var recallMetadata);
        if (metadataError is not null)
        {
            return TypedResults.BadRequest(metadataError);
        }

        var sourceEventId = $"source-{Guid.NewGuid():N}";
        var receivedAt = DateTimeOffset.UtcNow;
        var sourceId = new PublicRecordId(sourceEventId);
        var normalizedTags = NormalizeTags(request.CoreTags!);
        var classificationInput = AgentVisibleClassificationInput.Compose(
            request.Content,
            request.Title,
            request.SafeSummary,
            normalizedTags,
            recallMetadata.ProjectKey,
            recallMetadata.TaskKey,
            recallMetadata.TopicTags);
        var providerAuditEventId = $"audit-{Guid.NewGuid():N}";
        db.AuditEvents.Add(new AuditEventRecord
        {
            Id = providerAuditEventId,
            OccurredAt = receivedAt,
            Actor = ServiceTokenAuthorization.GetActor(httpContext),
            Action = "classification.provider.invoked",
            SubjectId = sourceEventId,
            PayloadClass = classifier.Boundary.PayloadClass,
            RedactionState = classifier.Boundary.RedactionState
        });
        await db.SaveChangesAsync(cancellationToken);

        ClassificationResult classification;
        try
        {
            classification = ClassificationResultNormalizer.Normalize(await classifier.ClassifyAsync(
                sourceId,
                classificationInput,
                request.SourceType,
                cancellationToken));
        }
        catch (ClassificationProviderException error)
        {
            return ApiProblems.ClassificationProviderUnavailable(error);
        }
        var decision = policyEngine.Decide(classification);
        db.SourceEvents.Add(new SourceEventRecord
        {
            Id = sourceEventId,
            SourceSystem = request.SourceSystem.Trim(),
            SourceType = request.SourceType.Trim(),
            ReceivedAt = receivedAt,
            ContentDigest = ComputeSha256Digest(request.Content),
            ContainsSensitiveMaterial = classification.ContainsSensitiveMaterial
        });

        var classificationResultId = $"classification-{Guid.NewGuid():N}";
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

        string? wikiProposalId = null;
        if (decision.AllowsWikiProjection)
        {
            wikiProposalId = $"wiki-{Guid.NewGuid():N}";
            db.WikiProposals.Add(new WikiProposalRecord
            {
                Id = wikiProposalId,
                SourceEventId = sourceEventId,
                Title = request.Title.Trim(),
                SafeSummary = request.SafeSummary.Trim(),
                Sensitivity = classification.Sensitivity,
                CoreTags = normalizedTags,
                ProjectKey = recallMetadata.ProjectKey,
                TaskKey = recallMetadata.TaskKey,
                TopicTags = recallMetadata.TopicTags.ToList(),
                AllowsAgentContext = decision.AllowsAgentContext,
                CreatedAt = receivedAt
            });
        }

        string? sensitiveReferenceId = null;
        if (decision.Kind == StorageDecisionKind.SensitiveDbOnly)
        {
            sensitiveReferenceId = $"sensitive-{Guid.NewGuid():N}";
            db.SensitiveRecordReferences.Add(new SensitiveRecordReferenceRecord
            {
                Id = sensitiveReferenceId,
                SourceEventId = sourceEventId,
                SourceSystem = request.SourceSystem.Trim(),
                SourceType = request.SourceType.Trim(),
                ReceivedAt = receivedAt,
                ContainsSensitiveMaterial = classification.ContainsSensitiveMaterial,
                ReferenceLabel = $"sensitive-record:{sourceEventId}",
                RedactedSummary = ""
            });
        }

        var auditEventId = $"audit-{Guid.NewGuid():N}";
        db.AuditEvents.Add(new AuditEventRecord
        {
            Id = auditEventId,
            OccurredAt = receivedAt,
            Actor = ServiceTokenAuthorization.GetActor(httpContext),
            Action = "source.intake.classified",
            SubjectId = sourceEventId,
            PayloadClass = decision.AllowsWikiProjection ? "metadata-only" : "sensitive-reference-only",
            RedactionState = decision.AllowsWikiProjection ? "safe-projection-only" : "sensitive-boundary-only"
        });

        await db.SaveChangesAsync(cancellationToken);

        var response = new SourceIntakeResponse(
            sourceEventId,
            sourceEventId,
            classificationResultId,
            wikiProposalId,
            sensitiveReferenceId,
            auditEventId,
            new ClassificationPreviewClassification(
                classification.Sensitivity,
                classification.Confidence,
                classification.Categories,
                classification.ContainsSensitiveMaterial),
            decision)
        {
            ProjectKey = recallMetadata.ProjectKey,
            TaskKey = recallMetadata.TaskKey,
            TopicTags = recallMetadata.TopicTags
        };

        return TypedResults.Created($"/api/sources/{sourceEventId}", response);
    }

    private static ProblemDetails? Validate(SourceIntakeRequest request)
    {
        var title = "Invalid source intake request.";
        var sourceSystemError = ApiValidation.ValidateRequiredText(
            request.SourceSystem,
            "sourceSystem",
            ApiValidation.SourceTextMaxLength,
            title);
        if (sourceSystemError is not null)
        {
            return sourceSystemError;
        }

        var sourceTypeError = ApiValidation.ValidateRequiredText(
            request.SourceType,
            "sourceType",
            ApiValidation.SourceTextMaxLength,
            title);
        if (sourceTypeError is not null)
        {
            return sourceTypeError;
        }

        var contentError = ApiValidation.ValidateRequiredText(
            request.Content,
            "content",
            ApiValidation.SourceContentMaxLength,
            title);
        if (contentError is not null)
        {
            return contentError;
        }

        if (request.Content.Length > ApiValidation.SourceContentMaxLength)
        {
            return CreateValidationProblem($"content must be {ApiValidation.SourceContentMaxLength} characters or fewer.");
        }

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

        return null;
    }

    private static ProblemDetails CreateValidationProblem(string detail) =>
        ApiValidation.CreateProblem("Invalid source intake request.", detail);

    private static List<string> NormalizeTags(IEnumerable<string> coreTags) =>
        coreTags
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Select(tag => tag.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static string ComputeSha256Digest(string content)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return $"sha256:{Convert.ToHexString(bytes).ToLowerInvariant()}";
    }
}

public sealed record SourceIntakeRequest
{
    public string SourceSystem { get; init; } = "";
    public string SourceType { get; init; } = "";
    public string Content { get; init; } = "";
    public string Title { get; init; } = "";
    public string SafeSummary { get; init; } = "";
    public IReadOnlyList<string>? CoreTags { get; init; }
    public string? ProjectKey { get; init; }
    public string? TaskKey { get; init; }
    public IReadOnlyList<string>? TopicTags { get; init; }
}

public sealed record SourceIntakeResponse(
    string SourceId,
    string SourceEventId,
    string ClassificationResultId,
    string? WikiProposalId,
    string? SensitiveReferenceId,
    string AuditEventId,
    ClassificationPreviewClassification Classification,
    StorageDecision StorageDecision)
{
    public string? ProjectKey { get; init; }
    public string? TaskKey { get; init; }
    public IReadOnlyList<string> TopicTags { get; init; } = [];
}
