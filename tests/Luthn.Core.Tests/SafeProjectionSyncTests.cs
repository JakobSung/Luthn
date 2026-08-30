using Luthn.Core.Classification;
using Luthn.Core.Memory;

namespace Luthn.Core.Tests;

public sealed class SafeProjectionSyncTests
{
    [Theory]
    [InlineData("/Users/alice/private.txt")]
    [InlineData("..")]
    [InlineData("record with spaces")]
    public void CreateUpsertRejectsPathLikeOrNonOpaqueLocalRecordId(string localRecordId)
    {
        Assert.Throws<ArgumentException>(() => SafeProjectionSyncPolicy.CreateUpsert(
            workspaceId: "workspace-1",
            originInstanceId: "instance-1",
            localRecordId: localRecordId,
            revision: 1,
            safeSummary: "Approved safe summary.",
            publicationState: ExternalPublicationState.ApprovedForExternal,
            sensitivity: SensitivityLevel.Public,
            visibility: MemoryVisibility.SharedAcrossAgents,
            createdAt: DateTimeOffset.UtcNow,
            updatedAt: DateTimeOffset.UtcNow,
            decidedAt: DateTimeOffset.UtcNow,
            expiresAt: null));
    }

    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-07-13T00:00:00Z");

    [Fact]
    public void UpsertRequiresExplicitApprovalAndSafeProjection()
    {
        Assert.Throws<ArgumentException>(() => CreateUpsert(ExternalPublicationState.LocalOnly));
        Assert.Throws<ArgumentException>(() => CreateUpsert(
            ExternalPublicationState.ApprovedForExternal,
            SensitivityLevel.Confidential));
        Assert.Throws<ArgumentException>(() => CreateUpsert(
            ExternalPublicationState.ApprovedForExternal,
            visibility: MemoryVisibility.PrivateToOwner));

        var envelope = CreateUpsert(ExternalPublicationState.ApprovedForExternal);

        Assert.Equal(SafeProjectionSyncOperation.Upsert, envelope.Operation);
        Assert.Equal(2, envelope.ContractVersion);
        Assert.Equal("default", envelope.WorkspaceId);
        Assert.Equal("default:instance-1:memory-1:1:Upsert", SafeProjectionSyncPolicy.CreateIdempotencyKey(envelope));
        Assert.Null(envelope.Title);
        Assert.Empty(envelope.CoreTags);
    }

    [Fact]
    public void RevokeCarriesNoProjectionBody()
    {
        var envelope = SafeProjectionSyncPolicy.CreateRevoke(
            "default",
            "instance-1",
            "memory-1",
            revision: 2,
            Now.AddDays(-1),
            Now,
            Now);

        Assert.Equal(SafeProjectionSyncOperation.Revoke, envelope.Operation);
        Assert.Null(envelope.Title);
        Assert.Null(envelope.SafeSummary);
        Assert.Empty(envelope.CoreTags);
    }

    [Fact]
    public void UpsertRetainsTheLocalExtensionWorkspaceIdentityGrammar()
    {
        var envelope = SafeProjectionSyncPolicy.CreateUpsert(
            workspaceId: "personal:user@example.com",
            originInstanceId: "instance-1",
            localRecordId: "memory-1",
            revision: 1,
            safeSummary: "Approved safe summary.",
            publicationState: ExternalPublicationState.ApprovedForExternal,
            sensitivity: SensitivityLevel.Public,
            visibility: MemoryVisibility.SharedAcrossAgents,
            createdAt: Now.AddMinutes(-2),
            updatedAt: Now.AddMinutes(-1),
            decidedAt: Now,
            expiresAt: null);

        Assert.Equal("personal:user@example.com", envelope.WorkspaceId);
    }

    [Fact]
    public void UpsertRejectsOutOfOrderTimeline()
    {
        Assert.Throws<ArgumentException>(() => SafeProjectionSyncPolicy.CreateUpsert(
            workspaceId: "workspace-1",
            originInstanceId: "instance-1",
            localRecordId: "memory-1",
            revision: 1,
            safeSummary: "Approved safe summary.",
            publicationState: ExternalPublicationState.ApprovedForExternal,
            sensitivity: SensitivityLevel.Public,
            visibility: MemoryVisibility.SharedAcrossAgents,
            createdAt: Now,
            updatedAt: Now.AddMinutes(-1),
            decidedAt: Now,
            expiresAt: null));
    }

    [Fact]
    public async Task DisabledTransportNeverAcceptsAnEnvelope()
    {
        ISafeProjectionSyncTransport transport = new DisabledSafeProjectionSyncTransport();

        var result = await transport.SendAsync(
            CreateUpsert(ExternalPublicationState.ApprovedForExternal),
            CancellationToken.None);

        Assert.Equal(SafeProjectionSyncTransportState.Disabled, transport.State);
        Assert.False(result.Accepted);
        Assert.Equal("transport.disabled", result.ErrorCode);
    }

    [Fact]
    public void SyncEnvelopeContractDoesNotExposeForbiddenFields()
    {
        var fieldNames = typeof(SafeProjectionSyncEnvelope)
            .GetProperties()
            .Select(property => property.Name)
            .ToArray();

        Assert.DoesNotContain(fieldNames, name => name.Contains("Raw", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(fieldNames, name => name.Contains("Vault", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(fieldNames, name => name.Contains("Credential", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(fieldNames, name => name.Contains("Token", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(fieldNames, name => name.Contains("Provenance", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(fieldNames, name => name.Equals("SourceContent", StringComparison.OrdinalIgnoreCase));
    }

    private static SafeProjectionSyncEnvelope CreateUpsert(
        ExternalPublicationState state,
        SensitivityLevel sensitivity = SensitivityLevel.Public,
        MemoryVisibility visibility = MemoryVisibility.SharedAcrossAgents) =>
        SafeProjectionSyncPolicy.CreateUpsert(
            "default",
            "instance-1",
            "memory-1",
            revision: 1,
            " Use PostgreSQL for durable storage. ",
            state,
            sensitivity,
            visibility,
            Now.AddDays(-1),
            Now,
            Now,
            expiresAt: null);
}
