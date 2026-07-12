namespace Luthn.Core.Classification;

public sealed record ClassificationPreviewRequest(
    string SourceId,
    string Content,
    string? SourceType = null);
