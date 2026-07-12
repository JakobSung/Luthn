namespace Luthn.Core.Classification;

public sealed record ClassificationProviderBoundary(
    string ProviderName,
    string PayloadClass,
    string RedactionState);
