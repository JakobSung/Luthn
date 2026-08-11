using Luthn.Core.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Luthn.Host.Api.Tests;

public sealed class SensitiveAccessPolicyWorkflowTests
{
    [Fact]
    public async Task RequestUsesLatestServerPolicyAndIgnoresLegacyExpiryOverride()
    {
        await using var db = TestData.CreateDbContext();
        TestData.AddReference(db);
        await db.SaveChangesAsync();
        var time = new ManualTimeProvider(TestData.ObservedAt);
        var workflow = TestData.CreateWorkflow(db, time);

        var initial = await workflow.GetPolicyAsync(TestData.Principal, CancellationToken.None);
        var revised = await workflow.CreatePolicyRevisionAsync(
            new SensitiveAccessPolicyUpdate(120, 180, 3),
            TestData.OperatorPrincipal,
            "operator",
            CancellationToken.None);
        var created = await workflow.CreateRequestAsync(
            new SensitiveAccessRequestCreateRequest
            {
                SensitiveReferenceId = TestData.ReferenceId,
                Reason = "Use the server policy.",
                SessionId = "session-policy",
                ExpiresInSeconds = 3600
            },
            TestData.Principal,
            "agent",
            CancellationToken.None);

        Assert.Equal(1, initial.Revision);
        Assert.Null(revised.ValidationError);
        Assert.Equal(2, revised.Policy!.Revision);
        Assert.NotNull(created);
        Assert.Equal(TestData.ObservedAt.AddSeconds(120), created.ExpiresAt);
        var record = await db.SensitiveAccessRequests.SingleAsync();
        Assert.Equal(2, record.PolicyRevision);
        Assert.Equal(120, record.RequestTimeoutSeconds);
    }

    [Fact]
    public async Task RejectsPolicyValuesOutsideTheServerContract()
    {
        await using var db = TestData.CreateDbContext();
        var workflow = TestData.CreateWorkflow(db, new ManualTimeProvider(TestData.ObservedAt));

        var result = await workflow.CreatePolicyRevisionAsync(
            new SensitiveAccessPolicyUpdate(59, 600, 1),
            TestData.OperatorPrincipal,
            "operator",
            CancellationToken.None);

        Assert.NotNull(result.ValidationError);
        Assert.Empty(await db.SensitiveAccessPolicyRevisions.ToArrayAsync());
    }
}

public sealed class SensitiveAccessGrantWorkflowTests
{
    [Fact]
    public async Task ApprovalCreatesOneBoundedGrantWhileDenialCreatesNone()
    {
        await using var db = TestData.CreateDbContext();
        TestData.AddReference(db);
        await db.SaveChangesAsync();
        var workflow = TestData.CreateWorkflow(db, new ManualTimeProvider(TestData.ObservedAt));
        var approvedRequest = await TestData.CreateRequestAsync(workflow, "approved-session");
        var deniedRequest = await TestData.CreateRequestAsync(workflow, "denied-session");

        var approved = await workflow.DecideRequestAsync(
            approvedRequest.Id,
            new SensitiveAccessDecisionRequest { Reason = "approved" },
            SensitiveAccessRequestStatus.Approved,
            TestData.OperatorPrincipal,
            "operator",
            CancellationToken.None);
        var denied = await workflow.DecideRequestAsync(
            deniedRequest.Id,
            new SensitiveAccessDecisionRequest { Reason = "denied" },
            SensitiveAccessRequestStatus.Denied,
            TestData.OperatorPrincipal,
            "operator",
            CancellationToken.None);

        Assert.Equal(SensitiveAccessDecisionOutcome.Succeeded, approved.Outcome);
        Assert.Equal(SensitiveAccessDecisionOutcome.Succeeded, denied.Outcome);
        var grant = await db.SensitiveAccessGrants.SingleAsync();
        Assert.Equal(approvedRequest.Id, grant.SensitiveAccessRequestId);
        Assert.Equal(1, grant.PolicyRevision);
        Assert.Equal(TestData.ObservedAt.AddMinutes(10), grant.ExpiresAt);
        Assert.Equal(1, grant.MaximumSuccessfulReads);
    }
}

public sealed class SensitiveAccessGrantConcurrencyTests
{
    [Fact]
    public async Task ConcurrentReadsCannotConsumeTheLastGrantReadTwice()
    {
        var databaseName = Guid.NewGuid().ToString("N");
        var options = new DbContextOptionsBuilder<LuthnDbContext>()
            .UseInMemoryDatabase(databaseName)
            .Options;
        await using (var seed = new LuthnDbContext(options))
        {
            TestData.AddApprovedGrant(seed);
            await seed.SaveChangesAsync();
        }

        await using var firstDb = new LuthnDbContext(options);
        await using var secondDb = new LuthnDbContext(options);
        var time = new ManualTimeProvider(TestData.ObservedAt);
        var first = TestData.CreateWorkflow(firstDb, time);
        var second = TestData.CreateWorkflow(secondDb, time);
        var firstPermit = await first.IssueReadPermitAsync(
            TestData.ApprovedRequestId,
            TestData.Principal,
            CancellationToken.None);
        var secondPermit = await second.IssueReadPermitAsync(
            TestData.ApprovedRequestId,
            TestData.Principal,
            CancellationToken.None);

        Assert.NotNull(firstPermit);
        Assert.NotNull(secondPermit);
        var results = await Task.WhenAll(
            first.ReadApprovedResultAsync(
                TestData.ApprovedRequestId,
                TestData.Principal,
                "agent",
                firstPermit,
                CancellationToken.None),
            second.ReadApprovedResultAsync(
                TestData.ApprovedRequestId,
                TestData.Principal,
                "agent",
                secondPermit,
                CancellationToken.None));

        Assert.Single(results, result => result == TestData.SafeSummary);
        Assert.Single(results, result => result is null);
        await using var verify = new LuthnDbContext(options);
        Assert.Equal(1, (await verify.SensitiveAccessGrants.SingleAsync()).SuccessfulReadCount);
    }
}

internal static class TestData
{
    internal const string ReferenceId = "sensitive-reference-a";
    internal const string ApprovedRequestId = "approved-request-a";
    internal const string SafeSummary = "Approved public-safe summary.";
    internal static readonly DateTimeOffset ObservedAt =
        DateTimeOffset.Parse("2026-08-11T00:00:00Z");
    internal static readonly LuthnRequestPrincipal Principal = new(
        "owner-a", "workspace-a", LuthnActorKind.Agent, "agent", IsOperator: false);
    internal static readonly LuthnRequestPrincipal OperatorPrincipal = new(
        "operator", "workspace-a", LuthnActorKind.User, "operator", IsOperator: true);

    internal static LuthnDbContext CreateDbContext() =>
        new(new DbContextOptionsBuilder<LuthnDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options);

    internal static SensitiveAccessWorkflow CreateWorkflow(LuthnDbContext db, TimeProvider time) =>
        new(db, NullOperationalMetrics.Instance, time);

    internal static void AddReference(LuthnDbContext db) =>
        db.SensitiveRecordReferences.Add(new SensitiveRecordReferenceRecord
        {
            Id = ReferenceId,
            SourceEventId = "source-a",
            SourceSystem = "local",
            SourceType = "vault",
            ReceivedAt = ObservedAt,
            ContainsSensitiveMaterial = true,
            ReferenceLabel = "sensitive-record:source-a",
            WorkspaceId = "workspace-a",
            OwnerUserId = "owner-a"
        });

    internal static async Task<SensitiveAccessRequestState> CreateRequestAsync(
        SensitiveAccessWorkflow workflow,
        string sessionId) =>
        (await workflow.CreateRequestAsync(
            new SensitiveAccessRequestCreateRequest
            {
                SensitiveReferenceId = ReferenceId,
                Reason = "bounded request",
                SessionId = sessionId
            },
            Principal,
            "agent",
            CancellationToken.None))!;

    internal static void AddApprovedGrant(LuthnDbContext db)
    {
        db.SensitiveAccessPolicyRevisions.Add(new SensitiveAccessPolicyRevisionRecord
        {
            WorkspaceId = "workspace-a",
            Revision = 1,
            RequestTimeoutSeconds = 600,
            GrantDurationSeconds = 600,
            MaximumSuccessfulReads = 1,
            CreatedAt = ObservedAt.AddMinutes(-2),
            CreatedBy = "operator"
        });
        db.SensitiveAccessRequests.Add(new SensitiveAccessRequestRecord
        {
            Id = ApprovedRequestId,
            SensitiveRecordReferenceId = ReferenceId,
            RequestedBy = "agent",
            SessionId = "approved-session",
            RequestReason = "bounded request",
            RedactedSummary = SafeSummary,
            Status = SensitiveAccessRequestStatus.Approved,
            CreatedAt = ObservedAt.AddMinutes(-2),
            ExpiresAt = ObservedAt.AddMinutes(8),
            UpdatedAt = ObservedAt.AddMinutes(-1),
            DecidedBy = "operator",
            DecidedAt = ObservedAt.AddMinutes(-1),
            WorkspaceId = "workspace-a",
            OwnerUserId = "owner-a",
            PolicyRevision = 1,
            RequestTimeoutSeconds = 600
        });
        db.SensitiveAccessGrants.Add(new SensitiveAccessGrantRecord
        {
            SensitiveAccessRequestId = ApprovedRequestId,
            WorkspaceId = "workspace-a",
            OwnerUserId = "owner-a",
            PolicyRevision = 1,
            GrantDurationSeconds = 600,
            StartsAt = ObservedAt.AddMinutes(-1),
            ExpiresAt = ObservedAt.AddMinutes(9),
            MaximumSuccessfulReads = 1
        });
    }
}

internal sealed class ManualTimeProvider(DateTimeOffset current) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => current;
}
