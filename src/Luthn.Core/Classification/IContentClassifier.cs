using Luthn.Core.Classification;
using Luthn.Core.Common;

namespace Luthn.Core.Classification;

public interface IContentClassifier
{
    ClassificationProviderBoundary Boundary { get; }

    ValueTask<ClassificationResult> ClassifyAsync(
        PublicRecordId sourceId,
        string content,
        string? sourceType,
        CancellationToken cancellationToken = default);
}
