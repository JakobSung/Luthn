using Luthn.Core.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Luthn.Host.Api.Tests;

public sealed class SensitiveAccessResolutionTests
{
    [Fact]
    public async Task RepeatedPendingRequestReusesOriginalRequestAcrossSessions()
    {
        await using var db = TestData.CreateDbContext();
        TestData.AddReference(db);
        await db.SaveChangesAsync();
        var workflow = TestData.CreateWorkflow(db, new ManualTimeProvider(TestData.ObservedAt));

        var first = await CreateAsync(workflow, "session-a");
        var sameSession = await CreateAsync(workflow, "session-a");
        var otherSession = await CreateAsync(workflow, "session-b");

        Assert.Equal(SensitiveAccessStatusCodes.RequestCreated, first.StatusCode);
        Assert.Equal(first.Id, sameSession.Id);
        Assert.Equal(SensitiveAccessStatusCodes.RequestPending, sameSession.StatusCode);
        Assert.Equal(first.Id, otherSession.Id);
        Assert.Equal(SensitiveAccessStatusCodes.RequestPending, otherSession.StatusCode);
        Assert.Equal(1, await db.SensitiveAccessRequests.CountAsync());
    }

    [Fact]
    public async Task TerminalStateIsReportedForSameSessionAndNewSessionCreatesANewRequest()
    {
        await using var db = TestData.CreateDbContext();
        TestData.AddReference(db);
        await db.SaveChangesAsync();
        var workflow = TestData.CreateWorkflow(db, new ManualTimeProvider(TestData.ObservedAt));
        var original = await CreateAsync(workflow, "session-terminal");
        await workflow.DecideRequestAsync(
            original.Id,
            new SensitiveAccessDecisionRequest { Reason = "denied" },
            SensitiveAccessRequestStatus.Denied,
            TestData.OperatorPrincipal,
            "operator",
            CancellationToken.None);

        var repeated = await CreateAsync(workflow, "session-terminal");
        var explicitNew = await CreateAsync(workflow, "session-new-request");

        Assert.Equal(original.Id, repeated.Id);
        Assert.Equal(SensitiveAccessStatusCodes.RequestDenied, repeated.StatusCode);
        Assert.NotEqual(original.Id, explicitNew.Id);
        Assert.Equal(SensitiveAccessStatusCodes.RequestCreated, explicitNew.StatusCode);
        Assert.Equal(2, await db.SensitiveAccessRequests.CountAsync());
    }

    private static async Task<SensitiveAccessRequestState> CreateAsync(
        SensitiveAccessWorkflow workflow,
        string sessionId) =>
        (await workflow.CreateRequestAsync(
            new SensitiveAccessRequestCreateRequest
            {
                SensitiveReferenceId = TestData.ReferenceId,
                Reason = "resolve current lifecycle",
                SessionId = sessionId
            },
            TestData.Principal,
            "agent",
            CancellationToken.None))!;
}

public sealed class SensitiveAccessLifecycleStatusTests
{
    [Fact]
    public async Task ActiveGrantReturnsResultThenReportsConsumedWithoutMoreOutput()
    {
        await using var db = TestData.CreateDbContext();
        TestData.AddApprovedGrant(db);
        await db.SaveChangesAsync();
        var workflow = TestData.CreateWorkflow(db, new ManualTimeProvider(TestData.ObservedAt));

        var active = await workflow.ReadRequestAsync(
            TestData.ApprovedRequestId,
            TestData.Principal,
            CancellationToken.None);
        var result = await workflow.ReadRequestResultAsync(
            TestData.ApprovedRequestId,
            TestData.Principal,
            "agent",
            CancellationToken.None);
        var consumed = await workflow.ReadRequestAsync(
            TestData.ApprovedRequestId,
            TestData.Principal,
            CancellationToken.None);
        var noSecondResult = await workflow.ReadRequestResultAsync(
            TestData.ApprovedRequestId,
            TestData.Principal,
            "agent",
            CancellationToken.None);

        Assert.Equal(SensitiveAccessStatusCodes.GrantActive, active!.StatusCode);
        Assert.Equal(1, active.RemainingReads);
        Assert.Equal(SensitiveAccessStatusCodes.ResultReturned, result!.StatusCode);
        Assert.Equal(TestData.SafeSummary, result.RedactedOutput);
        Assert.Equal(0, result.RemainingReads);
        Assert.Equal(SensitiveAccessStatusCodes.GrantConsumed, consumed!.StatusCode);
        Assert.Equal(SensitiveAccessStatusCodes.GrantConsumed, noSecondResult!.StatusCode);
        Assert.Null(noSecondResult.RedactedOutput);
    }

    [Fact]
    public async Task ExpiredGrantIsDerivedFromServerTimeWithoutMaterialization()
    {
        await using var db = TestData.CreateDbContext();
        TestData.AddApprovedGrant(db);
        await db.SaveChangesAsync();
        var workflow = TestData.CreateWorkflow(
            db,
            new ManualTimeProvider(TestData.ObservedAt.AddMinutes(10)));

        var status = await workflow.ReadRequestAsync(
            TestData.ApprovedRequestId,
            TestData.Principal,
            CancellationToken.None);
        var result = await workflow.ReadRequestResultAsync(
            TestData.ApprovedRequestId,
            TestData.Principal,
            "agent",
            CancellationToken.None);

        Assert.Equal(SensitiveAccessStatusCodes.GrantExpired, status!.StatusCode);
        Assert.Equal(SensitiveAccessStatusCodes.GrantExpired, result!.StatusCode);
        Assert.Null(result.RedactedOutput);
        Assert.Equal(1, result.RemainingReads);
    }
}

public sealed class SensitiveAccessResolutionConcurrencyTests
{
    [Fact]
    public async Task ConcurrentCreateRetriesProduceOnePendingRequest()
    {
        var databaseName = Guid.NewGuid().ToString("N");
        var options = new DbContextOptionsBuilder<LuthnDbContext>()
            .UseInMemoryDatabase(databaseName)
            .Options;
        await using (var seed = new LuthnDbContext(options))
        {
            TestData.AddReference(seed);
            await seed.SaveChangesAsync();
        }

        await using var firstDb = new LuthnDbContext(options);
        await using var secondDb = new LuthnDbContext(options);
        var first = TestData.CreateWorkflow(firstDb, new ManualTimeProvider(TestData.ObservedAt));
        var second = TestData.CreateWorkflow(secondDb, new ManualTimeProvider(TestData.ObservedAt));
        var request = new SensitiveAccessRequestCreateRequest
        {
            SensitiveReferenceId = TestData.ReferenceId,
            Reason = "idempotent retry",
            SessionId = "same-attempt"
        };

        var results = await Task.WhenAll(
            first.CreateRequestAsync(request, TestData.Principal, "agent", CancellationToken.None),
            second.CreateRequestAsync(request, TestData.Principal, "agent", CancellationToken.None));

        Assert.All(results, result => Assert.NotNull(result));
        Assert.Equal(results[0]!.Id, results[1]!.Id);
        await using var verify = new LuthnDbContext(options);
        Assert.Equal(1, await verify.SensitiveAccessRequests.CountAsync());
    }
}
