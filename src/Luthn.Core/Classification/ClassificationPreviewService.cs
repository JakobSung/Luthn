using Luthn.Core.Policy;
using Luthn.Core.Common;

namespace Luthn.Core.Classification;

public sealed class ClassificationPreviewService(
    IContentClassifier classifier,
    IPolicyEngine policyEngine)
{
    public ClassificationProviderBoundary ProviderBoundary => classifier.Boundary;

    public async ValueTask<ClassificationPreviewResponse> PreviewAsync(
        ClassificationPreviewRequest request,
        CancellationToken cancellationToken = default)
    {
        var sourceId = new PublicRecordId(request.SourceId);
        var classification = ClassificationResultNormalizer.Normalize(await classifier.ClassifyAsync(
            sourceId,
            request.Content,
            request.SourceType,
            cancellationToken));
        var decision = policyEngine.Decide(classification);
        var previewClassification = new ClassificationPreviewClassification(
            classification.Sensitivity,
            classification.Confidence,
            classification.Categories,
            classification.ContainsSensitiveMaterial);

        return new ClassificationPreviewResponse(sourceId.Value, previewClassification, decision);
    }
}
