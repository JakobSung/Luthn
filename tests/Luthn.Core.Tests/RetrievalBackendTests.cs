using Luthn.Core.Classification;
using Luthn.Core.Context;
using Luthn.Core.Memory;
using Luthn.Core.Search;

namespace Luthn.Core.Tests;

public sealed class RetrievalBackendTests
{
    [Fact]
    public void CatalogKeepsDeterministicDefaultAndPgVectorAsFirstVectorProvider()
    {
        Assert.Equal(RetrievalBackendKind.Deterministic, RetrievalBackendCatalog.Deterministic.Kind);
        Assert.True(RetrievalBackendCatalog.Deterministic.IsDefault);
        Assert.False(RetrievalBackendCatalog.Deterministic.IsVectorProvider);

        Assert.Equal(RetrievalBackendKind.PgVector, RetrievalBackendCatalog.PgVector.Kind);
        Assert.False(RetrievalBackendCatalog.PgVector.IsDefault);
        Assert.True(RetrievalBackendCatalog.PgVector.IsVectorProvider);
        Assert.Equal(
            RetrievalBackendCatalog.PublicAgentAllowedProjection,
            RetrievalBackendCatalog.PgVector.SearchableCorpus);
        Assert.Equal(
            ExternalMemoryAdapterCatalog.PublicAgentAllowedProjection,
            RetrievalBackendCatalog.PublicAgentAllowedProjection);

        Assert.Equal(
            [RetrievalBackendKind.Deterministic, RetrievalBackendKind.PgVector],
            RetrievalBackendCatalog.Supported.Select(backend => backend.Kind).ToArray());
        Assert.Single(RetrievalBackendCatalog.Supported, backend => backend.IsDefault);
    }

    [Fact]
    public void ExternalMemoryAdapterCatalogKeepsCustomAdaptersBehindSafeProjectionBoundary()
    {
        var descriptor = Assert.Single(ExternalMemoryAdapterCatalog.Supported);

        Assert.Equal(ExternalMemoryAdapterKind.CustomService, descriptor.Kind);
        Assert.False(descriptor.IsDefault);
        Assert.Equal(ExternalMemoryAdapterCatalog.PublicAgentAllowedProjection, descriptor.ExportedCorpus);
        Assert.Equal(ExternalMemoryAdapterCatalog.MetadataOnlyPayload, descriptor.PayloadClass);
        Assert.Equal(ExternalMemoryAdapterCatalog.SafeProjectionOnly, descriptor.RedactionState);
    }

    [Fact]
    public void ExternalMemoryProjectionPolicyExportsOnlyPublicAgentVisibleMemory()
    {
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");

        var projection = ExternalMemoryProjectionPolicy.CreateProjection(
            "memory-1",
            " Release runbook ",
            " Public-safe deployment memory. ",
            SensitivityLevel.Public,
            [" runbook ", "Runbook", "release"],
            MemoryVisibility.SharedAcrossAgents,
            expiresAt: null,
            now);
        var batch = ExternalMemoryProjectionPolicy.CreateBatch([projection]);

        Assert.Equal("Release runbook", projection.Title);
        Assert.Equal("Public-safe deployment memory.", projection.SafeSummary);
        Assert.Equal(["runbook", "release"], projection.CoreTags);
        Assert.Equal(ExternalMemoryAdapterCatalog.SharedMemoryProjection, projection.ProjectionKind);
        Assert.Equal(ExternalMemoryAdapterCatalog.MetadataOnlyPayload, projection.PayloadClass);
        Assert.Equal(ExternalMemoryAdapterCatalog.SafeProjectionOnly, projection.RedactionState);
        Assert.Equal(ExternalMemoryAdapterCatalog.PublicAgentAllowedProjection, batch.ExportedCorpus);

        Assert.False(ExternalMemoryProjectionPolicy.AllowsExternalMemoryExport(
            SensitivityLevel.Internal,
            MemoryVisibility.SharedAcrossAgents,
            expiresAt: null,
            now));
        Assert.False(ExternalMemoryProjectionPolicy.AllowsExternalMemoryExport(
            SensitivityLevel.Public,
            MemoryVisibility.PrivateToOwner,
            expiresAt: null,
            now));
        Assert.False(ExternalMemoryProjectionPolicy.AllowsExternalMemoryExport(
            SensitivityLevel.Public,
            MemoryVisibility.PublicSafe,
            now.AddSeconds(-1),
            now));
    }

    [Fact]
    public void ExternalMemoryProjectionContractDoesNotExposeRawSourceFields()
    {
        var fieldNames = typeof(ExternalMemoryProjection)
            .GetProperties()
            .Select(property => property.Name)
            .ToArray();

        Assert.DoesNotContain(fieldNames, name => name.Contains("Raw", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(fieldNames, name => name.Contains("Content", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(fieldNames, name => name.Contains("Source", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(nameof(ExternalMemoryProjection.SafeSummary), fieldNames);
        Assert.Contains(nameof(ExternalMemoryProjection.RedactionState), fieldNames);
    }

    [Fact]
    public void DeterministicBackendPreservesSafeSearchBoundary()
    {
        IRetrievalBackend backend = new DeterministicRetrievalBackend(new SafeSearchIndex());
        var candidates = new[]
        {
            Candidate("public-memory", "Release runbook", "Public deployment summary.", ["release"]),
            Candidate(
                "private-memory",
                "Release raw notes",
                "Private source summary.",
                ["release"],
                SensitivityLevel.Confidential),
            Candidate(
                "blocked-memory",
                "Release draft",
                "Blocked projection summary.",
                ["release"],
                allowsAgentContext: false)
        };

        var response = backend.Search(new SafeSearchRequest("release", ["release"], 10), candidates);

        var result = Assert.Single(response.Results);
        Assert.Equal("public-memory", result.Id);
        Assert.Equal(RetrievalBackendKind.Deterministic, backend.Kind);
    }

    private static ContextPackCandidate Candidate(
        string id,
        string title,
        string safeSummary,
        IReadOnlyList<string> coreTags,
        SensitivityLevel sensitivity = SensitivityLevel.Public,
        bool allowsAgentContext = true) =>
        new(id, title, safeSummary, sensitivity, coreTags, allowsAgentContext);
}
