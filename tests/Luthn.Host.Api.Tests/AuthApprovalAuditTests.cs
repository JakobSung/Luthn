using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Luthn.Core.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Luthn.Host.Api.Tests;

public sealed class AuthApprovalAuditTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string AgentBearer = "agent-safe-read-local";
    private const string SummaryBearer = "agent-summary-write-local";
    private const string RequestBearer = "request-sensitive-access-local";
    private const string DeciderBearer = "decide-sensitive-access-local";
    private const string AuditBearer = "audit-read-local";

    private readonly WebApplicationFactory<Program> _factory;

    public AuthApprovalAuditTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task ProductionProtectedAgentEndpointRequiresServiceTokenByDefault()
    {
        using var factory = _factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.UseSetting(
                "ConnectionStrings:LuthnDb",
                "Host=127.0.0.1;Port=1;Database=luthn;Username=luthn;Timeout=1;Command Timeout=1");
            builder.UseSetting("Luthn:Database:EnableRetries", "false");
        });
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/api/agent/search", new
        {
            query = "runbook",
            coreTags = new[] { "runbook" },
            maxItems = 5
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ConfiguredAgentTokenAuthorizesSafeReadButNotRawRoutes()
    {
        using var factory = CreateAuthFactory();
        using var client = factory.CreateClient();
        client.SetBearer(AgentBearer);

        using var response = await client.PostAsJsonAsync("/api/agent/search", new
        {
            query = "release",
            coreTags = new[] { "runbook" },
            maxItems = 5
        });
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        using var rawResponse = await client.GetAsync("/api/vault/raw/sensitive-ref-1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = Assert.Single(body.RootElement.GetProperty("results").EnumerateArray());
        Assert.Equal("wiki-public-runbook", result.GetProperty("id").GetString());
        Assert.DoesNotContain("private customer", body.RootElement.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.NotFound, rawResponse.StatusCode);
    }

    [Fact]
    public async Task TokenWithoutRequiredScopeIsForbidden()
    {
        using var factory = CreateAuthFactory();
        using var client = factory.CreateClient();
        client.SetBearer(RequestBearer);
        client.DefaultRequestHeaders.Add("X-Luthn-Operator", "console-operator");

        using var response = await client.PostAsJsonAsync("/api/agent/search", new
        {
            query = "release",
            coreTags = new[] { "runbook" },
            maxItems = 5
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AgentSummaryWriteScopeAuthorizesTurnSummaryIntake()
    {
        using var factory = CreateAuthFactory();
        using var readOnlyClient = factory.CreateClient();
        readOnlyClient.SetBearer(AgentBearer);
        using var writeClient = factory.CreateClient();
        writeClient.SetBearer(SummaryBearer);

        using var forbidden = await readOnlyClient.PostAsJsonAsync("/api/agent/turn-summaries", new
        {
            sessionId = "session-auth-1",
            sourceAgent = "codex",
            summary = "Published release note for contributors.",
            coreTags = new[] { "release" },
            idempotencyKey = "summary-auth-forbidden-1"
        });
        using var created = await writeClient.PostAsJsonAsync("/api/agent/turn-summaries", new
        {
            sessionId = "session-auth-1",
            sourceAgent = "codex",
            summary = "Published release note for contributors.",
            coreTags = new[] { "release" },
            idempotencyKey = "summary-auth-created-1"
        });

        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
    }

    [Fact]
    public async Task ExpiredServiceTokenIsUnauthorized()
    {
        using var factory = _factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Luthn:TestingDatabaseName", Guid.NewGuid().ToString("N"));
            builder.UseSetting("Luthn:Auth:RequireServiceToken", "true");
            ConfigureToken(builder, 0, "expired-agent", AgentBearer, "agent.read");
            builder.UseSetting("Luthn:Auth:Tokens:0:ExpiresAt", "2000-01-01T00:00:00Z");
        });
        using var client = factory.CreateClient();
        client.SetBearer(AgentBearer);

        using var response = await client.PostAsJsonAsync("/api/agent/search", new
        {
            query = "runbook",
            coreTags = new[] { "runbook" },
            maxItems = 5
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public void ProductionReadinessReportsMissingActiveServiceTokens()
    {
        var issue = ServiceTokenAuthorization.GetProductionReadinessIssue(
            new FakeHostEnvironment("Production"),
            new LuthnAuthOptions(),
            DateTimeOffset.UtcNow);

        Assert.Equal("No active service tokens are configured.", issue);
    }

    [Fact]
    public async Task OperatorIdentityIsAuditMetadataAfterScopeAuthorization()
    {
        using var factory = CreateAuthFactory();
        using var client = factory.CreateClient();
        client.SetBearer(RequestBearer);
        client.DefaultRequestHeaders.Add("X-Luthn-Operator", " console \t operator ");

        using var response = await client.PostAsJsonAsync("/api/access-requests", new
        {
            sensitiveReferenceId = "sensitive-ref-1",
            reason = "Need approval for a redacted operational summary."
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LuthnDbContext>();
        var request = await db.SensitiveAccessRequests.SingleAsync();
        var audit = await db.AuditEvents.SingleAsync(record => record.Action == "sensitive_access.requested");

        Assert.Equal("requester operator:console operator", request.RequestedBy);
        Assert.Equal("requester operator:console operator", audit.Actor);
        Assert.DoesNotContain("private customer", audit.Actor, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("raw vault", audit.Actor, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SensitiveAccessRequestStartsPendingAndReturnsMetadataOnly()
    {
        using var factory = CreateAuthFactory();
        using var client = factory.CreateClient();
        client.SetBearer(RequestBearer);

        using var response = await client.PostAsJsonAsync("/api/access-requests", new
        {
            sensitiveReferenceId = "sensitive-ref-1",
            reason = "Need approval for a redacted operational summary."
        });
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("Pending", body.RootElement.GetProperty("status").GetString());
        Assert.Equal("sensitive-ref-1", body.RootElement.GetProperty("sensitiveReferenceId").GetString());
        Assert.False(body.RootElement.GetProperty("redactedOutputAvailable").GetBoolean());
        Assert.DoesNotContain("private customer", body.RootElement.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("raw vault", body.RootElement.GetRawText(), StringComparison.OrdinalIgnoreCase);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LuthnDbContext>();
        var request = await db.SensitiveAccessRequests.SingleAsync();
        var audit = await db.AuditEvents.SingleAsync(record => record.Action == "sensitive_access.requested");
        Assert.Equal(SensitiveAccessRequestStatus.Pending, request.Status);
        Assert.Equal("requester", request.RequestedBy);
        Assert.Equal(request.Id, audit.SubjectId);
        Assert.Equal("metadata-only", audit.PayloadClass);
        Assert.Equal("sensitive-boundary-only", audit.RedactionState);
    }

    [Fact]
    public async Task SensitiveAccessApprovalAndDenialTransitionsPersistDecisions()
    {
        using var factory = CreateAuthFactory();
        using var client = factory.CreateClient();

        var approveId = await CreateSensitiveAccessRequestAsync(client);
        var denyId = await CreateSensitiveAccessRequestAsync(client);

        client.SetBearer(DeciderBearer);
        using var approveResponse = await client.PostAsJsonAsync($"/api/access-requests/{approveId}/approve", new
        {
            reason = "Approved for limited redacted handling."
        });
        using var approveBody = await JsonDocument.ParseAsync(await approveResponse.Content.ReadAsStreamAsync());
        using var denyResponse = await client.PostAsJsonAsync($"/api/access-requests/{denyId}/deny", new
        {
            reason = "Denied by default because the justification was insufficient."
        });
        using var denyBody = await JsonDocument.ParseAsync(await denyResponse.Content.ReadAsStreamAsync());

        Assert.Equal(HttpStatusCode.OK, approveResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, denyResponse.StatusCode);
        Assert.Equal("Approved", approveBody.RootElement.GetProperty("status").GetString());
        Assert.Equal("Denied", denyBody.RootElement.GetProperty("status").GetString());
        Assert.False(approveBody.RootElement.GetProperty("redactedOutputAvailable").GetBoolean());
        Assert.Equal("approved-redacted-output-unavailable", approveBody.RootElement.GetProperty("outputPolicy").GetString());
        Assert.False(denyBody.RootElement.GetProperty("redactedOutputAvailable").GetBoolean());
        Assert.Equal("denied-no-output", denyBody.RootElement.GetProperty("outputPolicy").GetString());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LuthnDbContext>();
        Assert.Equal(2, await db.SensitiveAccessDecisions.CountAsync());
        Assert.Equal(1, await db.SensitiveAccessRequests.CountAsync(record => record.Status == SensitiveAccessRequestStatus.Approved));
        Assert.Equal(1, await db.SensitiveAccessRequests.CountAsync(record => record.Status == SensitiveAccessRequestStatus.Denied));
    }

    [Fact]
    public async Task SensitiveAccessResultRequiresDecisionAndStaysMetadataOnly()
    {
        using var factory = CreateAuthFactory();
        using var client = factory.CreateClient();
        var approveId = await CreateSensitiveAccessRequestAsync(client);
        var denyId = await CreateSensitiveAccessRequestAsync(client);

        client.SetBearer(RequestBearer);
        using var pendingResponse = await client.GetAsync($"/api/access-requests/{approveId}/result");
        using var pendingBody = await JsonDocument.ParseAsync(await pendingResponse.Content.ReadAsStreamAsync());

        Assert.Equal(HttpStatusCode.OK, pendingResponse.StatusCode);
        Assert.Equal("Pending", pendingBody.RootElement.GetProperty("status").GetString());
        Assert.Equal("pending-approval", pendingBody.RootElement.GetProperty("outputPolicy").GetString());
        Assert.False(pendingBody.RootElement.GetProperty("redactedOutputAvailable").GetBoolean());
        Assert.Equal(JsonValueKind.Null, pendingBody.RootElement.GetProperty("redactedOutput").ValueKind);

        client.SetBearer(DeciderBearer);
        using var approveResponse = await client.PostAsJsonAsync($"/api/access-requests/{approveId}/approve", new
        {
            reason = "Approved for limited redacted handling."
        });
        using var denyResponse = await client.PostAsJsonAsync($"/api/access-requests/{denyId}/deny", new
        {
            reason = "Denied because the requester does not need the private detail."
        });

        Assert.Equal(HttpStatusCode.OK, approveResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, denyResponse.StatusCode);

        client.SetBearer(RequestBearer);
        using var approvedResultResponse = await client.GetAsync($"/api/access-requests/{approveId}/result");
        using var approvedBody = await JsonDocument.ParseAsync(await approvedResultResponse.Content.ReadAsStreamAsync());
        using var deniedResultResponse = await client.GetAsync($"/api/access-requests/{denyId}/result");
        using var deniedBody = await JsonDocument.ParseAsync(await deniedResultResponse.Content.ReadAsStreamAsync());

        Assert.Equal(HttpStatusCode.OK, approvedResultResponse.StatusCode);
        Assert.Equal("Approved", approvedBody.RootElement.GetProperty("status").GetString());
        Assert.Equal("approved-redacted-output-unavailable", approvedBody.RootElement.GetProperty("outputPolicy").GetString());
        Assert.False(approvedBody.RootElement.GetProperty("redactedOutputAvailable").GetBoolean());
        Assert.Equal(JsonValueKind.Null, approvedBody.RootElement.GetProperty("redactedOutput").ValueKind);
        Assert.Equal("metadata-only", approvedBody.RootElement.GetProperty("payloadClass").GetString());

        Assert.Equal(HttpStatusCode.OK, deniedResultResponse.StatusCode);
        Assert.Equal("Denied", deniedBody.RootElement.GetProperty("status").GetString());
        Assert.Equal("denied-no-output", deniedBody.RootElement.GetProperty("outputPolicy").GetString());
        Assert.False(deniedBody.RootElement.GetProperty("redactedOutputAvailable").GetBoolean());
        Assert.Equal(JsonValueKind.Null, deniedBody.RootElement.GetProperty("redactedOutput").ValueKind);

        Assert.DoesNotContain("private customer", approvedBody.RootElement.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("raw vault", approvedBody.RootElement.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private customer", deniedBody.RootElement.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("raw vault", deniedBody.RootElement.GetRawText(), StringComparison.OrdinalIgnoreCase);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LuthnDbContext>();
        var resultReads = await db.AuditEvents
            .Where(record => record.Action == "sensitive_access.result_read")
            .OrderBy(record => record.OccurredAt)
            .ToArrayAsync();
        Assert.Equal(3, resultReads.Length);
        Assert.Contains(resultReads, record =>
            record.SubjectId == approveId &&
            record.PayloadClass == "metadata-only" &&
            record.RedactionState == "approved-redacted-output-unavailable");
        Assert.Contains(resultReads, record =>
            record.SubjectId == denyId &&
            record.PayloadClass == "metadata-only" &&
            record.RedactionState == "denied-no-output");
    }

    [Fact]
    public async Task SensitiveAccessResultStaysUnavailableWithoutPublicSafeOutput()
    {
        using var factory = CreateAuthFactory();
        using var client = factory.CreateClient();
        var requestId = await CreateSensitiveAccessRequestAsync(client, "sensitive-ref-without-safe-output");

        client.SetBearer(DeciderBearer);
        using var approveResponse = await client.PostAsJsonAsync($"/api/access-requests/{requestId}/approve", new
        {
            reason = "Approved only for policy transition verification."
        });
        using var approveBody = await JsonDocument.ParseAsync(await approveResponse.Content.ReadAsStreamAsync());

        Assert.Equal(HttpStatusCode.OK, approveResponse.StatusCode);
        Assert.False(approveBody.RootElement.GetProperty("redactedOutputAvailable").GetBoolean());
        Assert.Equal("approved-redacted-output-unavailable", approveBody.RootElement.GetProperty("outputPolicy").GetString());

        client.SetBearer(RequestBearer);
        using var resultResponse = await client.GetAsync($"/api/access-requests/{requestId}/result");
        using var resultBody = await JsonDocument.ParseAsync(await resultResponse.Content.ReadAsStreamAsync());

        Assert.Equal(HttpStatusCode.OK, resultResponse.StatusCode);
        Assert.Equal("Approved", resultBody.RootElement.GetProperty("status").GetString());
        Assert.Equal("approved-redacted-output-unavailable", resultBody.RootElement.GetProperty("outputPolicy").GetString());
        Assert.False(resultBody.RootElement.GetProperty("redactedOutputAvailable").GetBoolean());
        Assert.Equal(JsonValueKind.Null, resultBody.RootElement.GetProperty("redactedOutput").ValueKind);
        Assert.DoesNotContain("private customer", resultBody.RootElement.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("raw vault", resultBody.RootElement.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ApprovalCanAttachPublicSafeRedactedSummary()
    {
        using var factory = CreateAuthFactory();
        using var client = factory.CreateClient();
        var requestId = await CreateSensitiveAccessRequestAsync(client, "sensitive-ref-without-safe-output");

        client.SetBearer(DeciderBearer);
        using var approveResponse = await client.PostAsJsonAsync($"/api/access-requests/{requestId}/approve", new
        {
            reason = "Approved with reviewed output.",
            redactedSummary = "Public-safe release steps."
        });
        using var approveBody = await JsonDocument.ParseAsync(await approveResponse.Content.ReadAsStreamAsync());

        Assert.Equal(HttpStatusCode.OK, approveResponse.StatusCode);
        Assert.True(approveBody.RootElement.GetProperty("redactedOutputAvailable").GetBoolean());
        Assert.Equal("approved-redacted-output-available", approveBody.RootElement.GetProperty("outputPolicy").GetString());

        client.SetBearer(RequestBearer);
        using var resultResponse = await client.GetAsync($"/api/access-requests/{requestId}/result");
        using var resultBody = await JsonDocument.ParseAsync(await resultResponse.Content.ReadAsStreamAsync());

        Assert.Equal(HttpStatusCode.OK, resultResponse.StatusCode);
        Assert.Equal("approved-redacted-output-available", resultBody.RootElement.GetProperty("outputPolicy").GetString());
        Assert.True(resultBody.RootElement.GetProperty("redactedOutputAvailable").GetBoolean());
        Assert.Equal("Public-safe release steps.", resultBody.RootElement.GetProperty("redactedOutput").GetString());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LuthnDbContext>();
        var reference = await db.SensitiveRecordReferences
            .SingleAsync(record => record.Id == "sensitive-ref-without-safe-output");
        Assert.Equal("Public-safe release steps.", reference.RedactedSummary);
    }

    [Fact]
    public async Task ApprovalRejectsSensitiveRedactedSummary()
    {
        using var factory = CreateAuthFactory();
        using var client = factory.CreateClient();
        var requestId = await CreateSensitiveAccessRequestAsync(client, "sensitive-ref-without-safe-output");

        client.SetBearer(DeciderBearer);
        using var approveResponse = await client.PostAsJsonAsync($"/api/access-requests/{requestId}/approve", new
        {
            reason = "Approved with unsafe output.",
            redactedSummary = "Private customer raw vault terms."
        });
        using var body = await JsonDocument.ParseAsync(await approveResponse.Content.ReadAsStreamAsync());

        Assert.Equal(HttpStatusCode.BadRequest, approveResponse.StatusCode);
        Assert.Equal(
            "redactedSummary must classify as public agent-safe content.",
            body.RootElement.GetProperty("detail").GetString());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LuthnDbContext>();
        var audit = await db.AuditEvents.SingleAsync(
            record => record.Action == "sensitive_access.redacted_summary_rejected");
        Assert.Equal(requestId, audit.SubjectId);
        Assert.Equal("metadata-only", audit.PayloadClass);
        Assert.Equal("rejected-no-output", audit.RedactionState);
    }

    [Fact]
    public async Task ApprovalRejectsOversizedRedactedSummary()
    {
        using var factory = CreateAuthFactory();
        using var client = factory.CreateClient();
        var requestId = await CreateSensitiveAccessRequestAsync(client, "sensitive-ref-without-safe-output");

        client.SetBearer(DeciderBearer);
        using var approveResponse = await client.PostAsJsonAsync($"/api/access-requests/{requestId}/approve", new
        {
            reason = "Approved with oversized output.",
            redactedSummary = new string('a', 4001)
        });
        using var body = await JsonDocument.ParseAsync(await approveResponse.Content.ReadAsStreamAsync());

        Assert.Equal(HttpStatusCode.BadRequest, approveResponse.StatusCode);
        Assert.Equal(
            "redactedSummary must be 4000 characters or fewer.",
            body.RootElement.GetProperty("detail").GetString());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LuthnDbContext>();
        var audit = await db.AuditEvents.SingleAsync(
            record => record.Action == "sensitive_access.redacted_summary_rejected");
        Assert.Equal(requestId, audit.SubjectId);
    }

    [Fact]
    public async Task DecisionRejectsOversizedReasonBeforePersistingDecision()
    {
        using var factory = CreateAuthFactory();
        using var client = factory.CreateClient();
        var requestId = await CreateSensitiveAccessRequestAsync(client, "sensitive-ref-without-safe-output");

        client.SetBearer(DeciderBearer);
        using var response = await client.PostAsJsonAsync($"/api/access-requests/{requestId}/deny", new
        {
            reason = new string('r', 1001)
        });
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("reason must be 1000 characters or fewer.", body.RootElement.GetProperty("detail").GetString());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LuthnDbContext>();
        Assert.Empty(await db.SensitiveAccessDecisions.ToArrayAsync());
        Assert.Equal(
            SensitiveAccessRequestStatus.Pending,
            (await db.SensitiveAccessRequests.SingleAsync()).Status);
    }

    [Fact]
    public async Task ApprovedResultTruncatesWithoutSplittingSurrogatePair()
    {
        using var factory = CreateAuthFactory();
        using var client = factory.CreateClient();
        var requestId = await CreateSensitiveAccessRequestAsync(client, "sensitive-ref-without-safe-output");
        var boundaryEmoji = char.ConvertFromUtf32(0x1F600);

        client.SetBearer(DeciderBearer);
        using var approveResponse = await client.PostAsJsonAsync($"/api/access-requests/{requestId}/approve", new
        {
            reason = "Approved with long reviewed output.",
            redactedSummary = new string('a', 999) + boundaryEmoji + "tail"
        });

        Assert.Equal(HttpStatusCode.OK, approveResponse.StatusCode);

        client.SetBearer(RequestBearer);
        using var resultResponse = await client.GetAsync($"/api/access-requests/{requestId}/result");
        using var resultBody = await JsonDocument.ParseAsync(await resultResponse.Content.ReadAsStreamAsync());

        var output = resultBody.RootElement.GetProperty("redactedOutput").GetString();
        Assert.Equal(HttpStatusCode.OK, resultResponse.StatusCode);
        Assert.NotNull(output);
        Assert.Equal(999, output!.Length);
        Assert.All(output, character => Assert.Equal('a', character));
        Assert.False(char.IsSurrogate(output[^1]));
    }

    [Fact]
    public async Task SensitiveAccessRequestsEndpointListsMetadataOnly()
    {
        using var factory = CreateAuthFactory();
        using var client = factory.CreateClient();
        var requestId = await CreateSensitiveAccessRequestAsync(client);

        client.SetBearer(DeciderBearer);
        using var response = await client.GetAsync("/api/access-requests?status=Pending&limit=10");
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var request = Assert.Single(body.RootElement.GetProperty("requests").EnumerateArray());
        Assert.Equal(requestId, request.GetProperty("id").GetString());
        Assert.Equal("sensitive-ref-1", request.GetProperty("sensitiveReferenceId").GetString());
        Assert.Equal("Pending", request.GetProperty("status").GetString());
        Assert.Equal("requester", request.GetProperty("requestedBy").GetString());
        Assert.False(request.GetProperty("redactedOutputAvailable").GetBoolean());
        Assert.Equal("pending-approval", request.GetProperty("outputPolicy").GetString());
        Assert.DoesNotContain("private customer", body.RootElement.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("raw vault", body.RootElement.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SensitiveAccessRequestsEndpointRejectsInvalidStatusFilter()
    {
        using var factory = CreateAuthFactory();
        using var client = factory.CreateClient();
        client.SetBearer(DeciderBearer);

        using var response = await client.GetAsync("/api/access-requests?status=unknown");
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("status must be Pending, Approved, or Denied.", body.RootElement.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task AuditEventsEndpointReturnsMetadataOnly()
    {
        using var factory = CreateAuthFactory();
        using var client = factory.CreateClient();
        var requestId = await CreateSensitiveAccessRequestAsync(client);

        client.SetBearer(AuditBearer);
        using var response = await client.GetAsync($"/api/audit-events?subjectId={requestId}");
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var auditEvent = Assert.Single(body.RootElement.GetProperty("events").EnumerateArray());
        Assert.Equal("sensitive_access.requested", auditEvent.GetProperty("action").GetString());
        Assert.Equal(1, auditEvent.GetProperty("payloadVersion").GetInt32());
        Assert.Equal("metadata-only", auditEvent.GetProperty("payloadClass").GetString());
        Assert.Equal("sensitive-boundary-only", auditEvent.GetProperty("redactionState").GetString());
        Assert.DoesNotContain("private customer", body.RootElement.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("raw vault", body.RootElement.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AuditEventsEndpointPreservesFuturePayloadVersion()
    {
        using var factory = CreateAuthFactory();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LuthnDbContext>();
        db.AuditEvents.Add(new AuditEventRecord
        {
            Id = "audit-future-version",
            OccurredAt = DateTimeOffset.UtcNow,
            Actor = "future-control-plane",
            Action = "control.future_event",
            SubjectId = "future-subject",
            PayloadVersion = 2,
            PayloadClass = "metadata-only",
            RedactionState = "safe-projection-only"
        });
        await db.SaveChangesAsync();

        using var client = factory.CreateClient();
        client.SetBearer(AuditBearer);
        using var response = await client.GetAsync("/api/audit-events?subjectId=future-subject");
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var auditEvent = Assert.Single(body.RootElement.GetProperty("events").EnumerateArray());
        Assert.Equal("control.future_event", auditEvent.GetProperty("action").GetString());
        Assert.Equal(2, auditEvent.GetProperty("payloadVersion").GetInt32());
        Assert.Equal("metadata-only", auditEvent.GetProperty("payloadClass").GetString());
        Assert.DoesNotContain("private customer", body.RootElement.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("raw vault", body.RootElement.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SensitiveAccessRequestForUnknownReferenceIsDeniedByDefault()
    {
        using var factory = CreateAuthFactory();
        using var client = factory.CreateClient();
        client.SetBearer(RequestBearer);

        using var response = await client.PostAsJsonAsync("/api/access-requests", new
        {
            sensitiveReferenceId = "missing-reference",
            reason = "Unknown records must not create an implicit access path."
        });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private async Task<string> CreateSensitiveAccessRequestAsync(
        HttpClient client,
        string sensitiveReferenceId = "sensitive-ref-1")
    {
        client.SetBearer(RequestBearer);
        using var response = await client.PostAsJsonAsync("/api/access-requests", new
        {
            sensitiveReferenceId,
            reason = "Need approval for a redacted operational summary."
        });
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return body.RootElement.GetProperty("id").GetString()!;
    }

    private WebApplicationFactory<Program> CreateAuthFactory()
    {
        return _factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Luthn:TestingDatabaseName", Guid.NewGuid().ToString("N"));
            builder.UseSetting("Luthn:Auth:RequireServiceToken", "true");
            ConfigureToken(builder, 0, "agent", AgentBearer, "agent.read");
            ConfigureToken(builder, 1, "summary-writer", SummaryBearer, "agent.write.summary");
            ConfigureToken(builder, 2, "requester", RequestBearer, "access.request");
            ConfigureToken(builder, 3, "decider", DeciderBearer, "access.decide");
            ConfigureToken(builder, 4, "auditor", AuditBearer, "audit.read");
            builder.ConfigureServices(services =>
            {
                using var provider = services.BuildServiceProvider();
                using var scope = provider.CreateScope();
                using var db = scope.ServiceProvider.GetRequiredService<LuthnDbContext>();
                db.Database.EnsureCreated();
                SeedSensitiveReference(db);
            });
        });
    }

    private static void ConfigureToken(
        IWebHostBuilder builder,
        int index,
        string name,
        string bearer,
        string scope)
    {
        builder.UseSetting($"Luthn:Auth:Tokens:{index}:Name", name);
        builder.UseSetting($"Luthn:Auth:Tokens:{index}:Sha256Digest", Sha256Digest(bearer));
        builder.UseSetting($"Luthn:Auth:Tokens:{index}:Scopes:0", scope);
    }

    private static void SeedSensitiveReference(LuthnDbContext db)
    {
        db.SourceEvents.Add(new SourceEventRecord
        {
            Id = "source-sensitive-1",
            SourceSystem = "local",
            SourceType = "note",
            ReceivedAt = DateTimeOffset.UtcNow,
            ContentDigest = "sha256:sensitive-source",
            ContainsSensitiveMaterial = true
        });
        db.SourceEvents.Add(new SourceEventRecord
        {
            Id = "source-sensitive-without-safe-output",
            SourceSystem = "local",
            SourceType = "note",
            ReceivedAt = DateTimeOffset.UtcNow,
            ContentDigest = "sha256:sensitive-source-without-safe-output",
            ContainsSensitiveMaterial = true
        });
        db.SensitiveRecordReferences.Add(new SensitiveRecordReferenceRecord
        {
            Id = "sensitive-ref-1",
            SourceEventId = "source-sensitive-1",
            SourceSystem = "local",
            SourceType = "note",
            ReceivedAt = DateTimeOffset.UtcNow,
            ContainsSensitiveMaterial = true,
            ReferenceLabel = "sensitive-record:source-sensitive-1"
        });
        db.SensitiveRecordReferences.Add(new SensitiveRecordReferenceRecord
        {
            Id = "sensitive-ref-without-safe-output",
            SourceEventId = "source-sensitive-without-safe-output",
            SourceSystem = "local",
            SourceType = "note",
            ReceivedAt = DateTimeOffset.UtcNow,
            ContainsSensitiveMaterial = true,
            ReferenceLabel = "sensitive-record:source-sensitive-without-safe-output",
            RedactedSummary = ""
        });
        db.WikiProposals.Add(new WikiProposalRecord
        {
            Id = "wiki-public-runbook",
            SourceEventId = "source-sensitive-1",
            Title = "Release runbook",
            SafeSummary = "Public-safe release steps.",
            Sensitivity = Luthn.Core.Classification.SensitivityLevel.Public,
            CoreTags = ["runbook"],
            AllowsAgentContext = true,
            CreatedAt = DateTimeOffset.UtcNow
        });
        db.WikiProposals.Add(new WikiProposalRecord
        {
            Id = "wiki-private-blocked",
            SourceEventId = "source-sensitive-1",
            Title = "Blocked private source terms",
            SafeSummary = "Private customer raw vault terms.",
            Sensitivity = Luthn.Core.Classification.SensitivityLevel.Confidential,
            CoreTags = ["blocked"],
            AllowsAgentContext = false,
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(1)
        });
        db.SaveChanges();
    }

    private static string Sha256Digest(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return $"sha256:{Convert.ToHexString(hash).ToLowerInvariant()}";
    }
}

internal sealed class FakeHostEnvironment(string environmentName) : IWebHostEnvironment
{
    public string EnvironmentName { get; set; } = environmentName;
    public string ApplicationName { get; set; } = "Luthn.Host.Api.Tests";
    public string WebRootPath { get; set; } = "";
    public Microsoft.Extensions.FileProviders.IFileProvider WebRootFileProvider { get; set; } =
        new Microsoft.Extensions.FileProviders.NullFileProvider();
    public string ContentRootPath { get; set; } = Directory.GetCurrentDirectory();
    public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
        new Microsoft.Extensions.FileProviders.NullFileProvider();
}

internal static class HttpClientAuthExtensions
{
    public static void SetBearer(this HttpClient client, string bearer)
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
    }
}
