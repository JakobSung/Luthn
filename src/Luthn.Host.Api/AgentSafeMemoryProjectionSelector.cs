using Luthn.Core.Classification;
using Luthn.Core.Common;
using Luthn.Core.Memory;
using Luthn.Core.Policy;

namespace Luthn.Host.Api;

public sealed class AgentSafeMemoryProjectionSelector(
    IContentClassifier classifier,
    DeterministicSensitiveDataDetector sensitiveDataDetector,
    IPolicyEngine policyEngine)
{
    internal const string QuotationFallbackTitle = "보호된 견적 정보 (quote)";
    internal const string QuotationFallbackSummary = "확인 후 열람할 수 있는 견적 정보가 있습니다.";

    private static readonly string[] MeaningfulEventMarkers =
    [
        "진행",
        "발행",
        "완료",
        "생성",
        "수정",
        "삭제",
        "결정",
        "논의",
        "요청",
        "승인",
        "거절",
        "배포",
        "출시",
        "처리",
        "등록",
        "작성",
        "전달",
        "확인",
        "변경",
        "검토",
        "조치",
        "교체",
        "발급",
        "수신",
        "발송",
        "설정",
        "연결",
        "시작",
        "종료",
        "예약",
        "할당",
        "해결",
        "approved",
        "created",
        "updated",
        "deleted",
        "decided",
        "discussed",
        "requested",
        "completed",
        "deployed",
        "released",
        "processed",
        "registered",
        "written",
        "sent",
        "received",
        "reviewed",
        "changed",
        "fixed",
        "issued",
        "renewed",
        "scheduled",
        "assigned",
        "resolved",
        "started",
        "finished"
    ];

    public ClassificationProviderBoundary Boundary => classifier.Boundary;
    internal DeterministicSensitiveDataDetector SensitiveDataDetector => sensitiveDataDetector;

    internal async ValueTask<AgentSafeMemoryProjectionSelection> SelectAsync(
        AgentSafeMemoryProjectionCandidate candidate,
        PublicRecordId sourceId,
        string sourceType,
        CancellationToken cancellationToken)
    {
        var originalTitle = candidate.Title.Trim();
        var originalSummary = candidate.SafeSummary.Trim();
        var originalInput = AgentVisibleClassificationInput.Compose(
            content: null,
            originalTitle,
            originalSummary,
            candidate.CoreTags,
            candidate.ProjectKey,
            candidate.TaskKey,
            candidate.TopicTags);
        var originalClassification = ApplyRequestedSensitivity(
            MergeLocalSourceSessionGuard(
                await ClassifyWithLocalGuardAsync(
                    sourceId,
                    originalInput,
                    sourceType,
                    cancellationToken),
                sourceId,
                candidate.SourceSessionId),
            candidate.RequestedSensitivity);
        var originalDecision = policyEngine.Decide(originalClassification);
        var original = new AgentSafeMemoryProjectionSelection(
            originalTitle,
            originalSummary,
            originalClassification,
            originalDecision,
            originalClassification,
            RetainsEncryptedOriginal: false);

        if (candidate.RequestedSensitivity != SensitivityLevel.Public ||
            candidate.Visibility is not (MemoryVisibility.PublicSafe or MemoryVisibility.SharedAcrossAgents) ||
            originalDecision.AllowsAgentContext)
        {
            return original;
        }

        var titleRedaction = sensitiveDataDetector.Redact(originalTitle);
        var summaryRedaction = sensitiveDataDetector.Redact(originalSummary);
        if (titleRedaction.IsComplete &&
            summaryRedaction.IsComplete &&
            (titleRedaction.Changed || summaryRedaction.Changed) &&
            HasMeaningfulProjectionText(titleRedaction.Text, minimumCharacters: 2) &&
            HasMeaningfulProjectionText(
                summaryRedaction.Text,
                minimumCharacters: 8,
                requiresEventSignal: true))
        {
            var meaningfulProjection = await TryCreateProjectionAsync(
                titleRedaction.Text,
                summaryRedaction.Text,
                candidate,
                sourceId,
                sourceType,
                originalClassification,
                cancellationToken);
            if (meaningfulProjection is not null)
            {
                return meaningfulProjection;
            }
        }

        var quotationFallback = await TryCreateQuotationFallbackAsync(
            candidate,
            sourceId,
            sourceType,
            originalClassification,
            cancellationToken);
        return quotationFallback ?? original;
    }

    private async ValueTask<AgentSafeMemoryProjectionSelection?> TryCreateProjectionAsync(
        string title,
        string summary,
        AgentSafeMemoryProjectionCandidate candidate,
        PublicRecordId sourceId,
        string sourceType,
        ClassificationResult originalClassification,
        CancellationToken cancellationToken)
    {
        var projectedInput = AgentVisibleClassificationInput.Compose(
            content: null,
            title,
            summary,
            candidate.CoreTags,
            candidate.ProjectKey,
            candidate.TaskKey,
            candidate.TopicTags);
        var projectedClassification = ApplyRequestedSensitivity(
            await ClassifyWithLocalGuardAsync(
                sourceId,
                projectedInput,
                sourceType,
                cancellationToken),
            candidate.RequestedSensitivity);
        var projectedDecision = policyEngine.Decide(projectedClassification);
        if (!projectedDecision.AllowsAgentContext)
        {
            return null;
        }

        return new AgentSafeMemoryProjectionSelection(
            title,
            summary,
            projectedClassification,
            projectedDecision,
            originalClassification,
            RetainsEncryptedOriginal: true);
    }

    private async ValueTask<AgentSafeMemoryProjectionSelection?> TryCreateQuotationFallbackAsync(
        AgentSafeMemoryProjectionCandidate candidate,
        PublicRecordId sourceId,
        string sourceType,
        ClassificationResult originalClassification,
        CancellationToken cancellationToken)
    {
        if (originalClassification.Sensitivity == SensitivityLevel.Restricted ||
            !originalClassification.Categories.Contains("finance", StringComparer.OrdinalIgnoreCase) ||
            !HasBoundedQuotationAmount(candidate))
        {
            return null;
        }

        return await TryCreateProjectionAsync(
            QuotationFallbackTitle,
            QuotationFallbackSummary,
            candidate,
            sourceId,
            sourceType,
            originalClassification,
            cancellationToken);
    }

    private bool HasBoundedQuotationAmount(AgentSafeMemoryProjectionCandidate candidate)
    {
        foreach (var value in new[] { candidate.Title, candidate.SafeSummary })
        {
            if (sensitiveDataDetector.HasBoundedQuotationAmount(value))
            {
                return true;
            }
        }

        return false;
    }

    private async ValueTask<ClassificationResult> ClassifyWithLocalGuardAsync(
        PublicRecordId sourceId,
        string input,
        string sourceType,
        CancellationToken cancellationToken)
    {
        var configured = ClassificationResultNormalizer.Normalize(await classifier.ClassifyAsync(
            sourceId,
            input,
            sourceType,
            cancellationToken));
        var local = sensitiveDataDetector.Detect(sourceId, input);
        return ConservativeClassificationMerger.Merge(configured, local);
    }

    private ClassificationResult MergeLocalSourceSessionGuard(
        ClassificationResult classification,
        PublicRecordId sourceId,
        string? sourceSessionId)
    {
        if (string.IsNullOrWhiteSpace(sourceSessionId))
        {
            return classification;
        }

        var sourceSessionValue = sourceSessionId.Trim();
        var deterministic = sensitiveDataDetector.Detect(sourceId, sourceSessionValue);
        var taxonomyCategories = ClassificationTaxonomy.DetectCategories(sourceSessionValue);
        var taxonomySensitivity = taxonomyCategories
            .Select(ClassificationTaxonomy.MinimumSensitivityFor)
            .Where(level => level is not null)
            .Select(level => level!.Value)
            .DefaultIfEmpty(SensitivityLevel.Public)
            .Max();
        var taxonomy = new ClassificationResult(
            sourceId,
            taxonomySensitivity,
            taxonomyCategories.Count == 0 ? 0 : 1,
            taxonomyCategories,
            taxonomySensitivity is SensitivityLevel.Confidential or SensitivityLevel.Restricted);

        return ConservativeClassificationMerger.Merge(
            classification,
            ConservativeClassificationMerger.Merge(deterministic, taxonomy));
    }

    private static ClassificationResult ApplyRequestedSensitivity(
        ClassificationResult classification,
        SensitivityLevel requestedSensitivity)
    {
        if (requestedSensitivity <= classification.Sensitivity)
        {
            return classification;
        }

        var categories = classification.Categories.ToHashSet(StringComparer.OrdinalIgnoreCase);
        categories.Add($"requested:{requestedSensitivity}");
        return classification with
        {
            Sensitivity = requestedSensitivity,
            Categories = categories,
            ContainsSensitiveMaterial = classification.ContainsSensitiveMaterial ||
                requestedSensitivity is SensitivityLevel.Confidential or SensitivityLevel.Restricted
        };
    }

    private static bool HasMeaningfulProjectionText(
        string value,
        int minimumCharacters,
        bool requiresEventSignal = false)
    {
        var withoutMarkers = value.Replace(
            DeterministicSensitiveDataDetector.RedactionMarker,
            "",
            StringComparison.Ordinal);
        return withoutMarkers.Count(char.IsLetterOrDigit) >= minimumCharacters &&
            (!requiresEventSignal || MeaningfulEventMarkers.Any(marker =>
                withoutMarkers.Contains(marker, StringComparison.OrdinalIgnoreCase)));
    }
}

internal sealed record AgentSafeMemoryProjectionCandidate(
    string Title,
    string SafeSummary,
    SensitivityLevel RequestedSensitivity,
    MemoryVisibility Visibility,
    IReadOnlyList<string> CoreTags,
    string? ProjectKey,
    string? TaskKey,
    IReadOnlyList<string> TopicTags,
    string? SourceSessionId);

internal sealed record AgentSafeMemoryProjectionSelection(
    string Title,
    string SafeSummary,
    ClassificationResult Classification,
    StorageDecision Decision,
    ClassificationResult OriginalClassification,
    bool RetainsEncryptedOriginal);
