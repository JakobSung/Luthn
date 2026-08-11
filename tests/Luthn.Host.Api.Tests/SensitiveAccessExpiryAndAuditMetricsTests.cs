using Luthn.Core.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Luthn.Host.Api.Tests;

public sealed class SensitiveAccessExpiryMaterializationTests
{
    [Fact]
    public async Task SystemOperationMaterializesRequestAndGrantExpiryExactlyOnce()
    {
        await using var db = TestData.CreateDbContext();
        TestData.AddApprovedGrant(db);
        var grant = db.SensitiveAccessGrants.Local.Single();
        grant.ExpiresAt = TestData.ObservedAt.AddSeconds(-1);
        db.SensitiveAccessRequests.Add(new SensitiveAccessRequestRecord
        {
            Id = "expired-request",
            SensitiveRecordReferenceId = TestData.ReferenceId,
            RequestedBy = "agent",
            SessionId = "expired-session",
            RequestReason = "bounded request",
            Status = SensitiveAccessRequestStatus.Pending,
            CreatedAt = TestData.ObservedAt.AddMinutes(-20),
            ExpiresAt = TestData.ObservedAt.AddMinutes(-10),
            UpdatedAt = TestData.ObservedAt.AddMinutes(-20),
            WorkspaceId = "workspace-a",
            OwnerUserId = "owner-a"
        });
        await db.SaveChangesAsync();
        var metrics = new OperationalMetrics();
        var workflow = new SensitiveAccessWorkflow(db, metrics, new ManualTimeProvider(TestData.ObservedAt));

        var first = await workflow.MaterializeExpiriesAsync(TestData.ObservedAt, 10, CancellationToken.None);
        var second = await workflow.MaterializeExpiriesAsync(TestData.ObservedAt, 10, CancellationToken.None);

        Assert.Equal(new SensitiveAccessExpiryMaterializationResult(1, 1), first);
        Assert.Equal(new SensitiveAccessExpiryMaterializationResult(0, 0), second);
        Assert.Equal(
            SensitiveAccessRequestStatus.Expired,
            (await db.SensitiveAccessRequests.SingleAsync(request => request.Id == "expired-request")).Status);
        Assert.Single(await db.AuditEvents.Where(audit =>
            audit.Action == "sensitive_access.expired" && audit.SubjectId == "expired-request").ToArrayAsync());
        Assert.Single(await db.AuditEvents.Where(audit =>
            audit.Action == "sensitive_access.grant_expired" &&
            audit.SubjectId == TestData.ApprovedRequestId).ToArrayAsync());
        Assert.Equal(1, metrics.Snapshot().SensitiveAccess.Lifecycle.Single(item => item.Event == "request_expired").Count);
        Assert.Equal(1, metrics.Snapshot().SensitiveAccess.Lifecycle.Single(item => item.Event == "grant_expired").Count);
    }
}

public sealed class SensitiveAccessAuditMetricsTests
{
    [Fact]
    public async Task ReuseConsumptionResultAndBypassUseBoundedAuditAndMetrics()
    {
        await using var db = TestData.CreateDbContext();
        TestData.AddReference(db, "sensitive-reference-reuse");
        TestData.AddApprovedGrant(db);
        await db.SaveChangesAsync();
        var metrics = new OperationalMetrics();
        var workflow = new SensitiveAccessWorkflow(db, metrics, new ManualTimeProvider(TestData.ObservedAt));

        await TestData.CreateRequestAsync(workflow, "reuse-session", "sensitive-reference-reuse");
        await TestData.CreateRequestAsync(workflow, "reuse-session", "sensitive-reference-reuse");
        var result = await workflow.ReadRequestResultAsync(
            TestData.ApprovedRequestId,
            TestData.Principal,
            "agent",
            CancellationToken.None);
        await workflow.ReadApprovedResultAsync(
            TestData.ApprovedRequestId,
            TestData.Principal,
            "agent",
            permit: null,
            CancellationToken.None);

        Assert.Equal(SensitiveAccessStatusCodes.ResultReturned, result!.StatusCode);
        var audits = await db.AuditEvents.ToArrayAsync();
        Assert.Contains(audits, audit => audit.Action == "sensitive_access.request_reused" && audit.Outcome == "reused");
        Assert.Contains(audits, audit => audit.Action == "sensitive_access.grant_consumed" && audit.Outcome == "consumed");
        Assert.Contains(audits, audit => audit.Action == "sensitive_access.result_read" && audit.Outcome == "returned");
        Assert.Contains(audits, audit => audit.Action == "sensitive_access.read_bypass_rejected" && audit.Outcome == "rejected");
        Assert.All(audits, audit => Assert.True(audit.PayloadClass is "metadata-only" or "redacted-output"));

        var lifecycle = metrics.Snapshot().SensitiveAccess.Lifecycle.ToDictionary(item => item.Event, item => item.Count);
        Assert.Equal(1, lifecycle["request_reused"]);
        Assert.Equal(1, lifecycle["grant_consumed"]);
        Assert.Equal(1, lifecycle["result_returned"]);
        Assert.Equal(1, lifecycle["bypass_rejected"]);
    }
}
