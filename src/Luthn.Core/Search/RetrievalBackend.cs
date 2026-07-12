using Luthn.Core.Context;
using Luthn.Core.Memory;

namespace Luthn.Core.Search;

public enum RetrievalBackendKind
{
    Deterministic,
    PgVector
}

public sealed record RetrievalBackendDescriptor(
    RetrievalBackendKind Kind,
    bool IsDefault,
    bool IsVectorProvider,
    string SearchableCorpus,
    string OperationalBoundary);

public interface IRetrievalBackend
{
    RetrievalBackendKind Kind { get; }

    SafeSearchResponse Search(
        SafeSearchRequest request,
        IEnumerable<ContextPackCandidate> candidates);
}

public sealed class DeterministicRetrievalBackend(SafeSearchIndex index) : IRetrievalBackend
{
    public RetrievalBackendKind Kind => RetrievalBackendKind.Deterministic;

    public SafeSearchResponse Search(
        SafeSearchRequest request,
        IEnumerable<ContextPackCandidate> candidates) =>
        index.Search(request, candidates);
}

public static class RetrievalBackendCatalog
{
    public const string PublicAgentAllowedProjection =
        ExternalMemoryAdapterCatalog.PublicAgentAllowedProjection;

    public static readonly RetrievalBackendDescriptor Deterministic = new(
        RetrievalBackendKind.Deterministic,
        IsDefault: true,
        IsVectorProvider: false,
        PublicAgentAllowedProjection,
        "in-process deterministic ranking");

    public static readonly RetrievalBackendDescriptor PgVector = new(
        RetrievalBackendKind.PgVector,
        IsDefault: false,
        IsVectorProvider: true,
        PublicAgentAllowedProjection,
        "PostgreSQL pgvector over public-safe projected records only");

    public static IReadOnlyList<RetrievalBackendDescriptor> Supported { get; } =
    [
        Deterministic,
        PgVector
    ];
}
