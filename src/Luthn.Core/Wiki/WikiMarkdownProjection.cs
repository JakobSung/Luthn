using Luthn.Core.Classification;

namespace Luthn.Core.Wiki;

public sealed record WikiMarkdownProjection(
    string Id,
    string Title,
    string SafeSummary,
    SensitivityLevel Sensitivity,
    IReadOnlyList<string> CoreTags,
    IReadOnlyList<WikiSourceReference> SourceReferences);

public sealed record WikiSourceReference(
    string SourceId,
    string SourceType,
    string ReferenceKind,
    string RedactionState,
    string RedactionNote);
