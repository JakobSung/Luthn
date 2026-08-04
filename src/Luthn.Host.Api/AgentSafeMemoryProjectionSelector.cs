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
        if (!titleRedaction.IsComplete ||
            !summaryRedaction.IsComplete ||
            (!titleRedaction.Changed && !summaryRedaction.Changed) ||
            !HasMeaningfulProjectionText(titleRedaction.Text, minimumCharacters: 2) ||
            !HasMeaningfulProjectionText(
                summaryRedaction.Text,
                minimumCharacters: 8,
                requiresEventSignal: true))
        {
            return original;
        }

        var projectedInput = AgentVisibleClassificationInput.Compose(
            content: null,
            titleRedaction.Text,
            summaryRedaction.Text,
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
            return original;
        }

        return new AgentSafeMemoryProjectionSelection(
            titleRedaction.Text,
            summaryRedaction.Text,
            projectedClassification,
            projectedDecision,
            originalClassification,
            RetainsEncryptedOriginal: true);
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
