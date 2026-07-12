using System.Net;
using System.Reflection;
using Luthn.AgentConnector.Http;
using Luthn.Sdk.Access;
using Luthn.Sdk.Agent;
using Luthn.Sdk.AgentConnections;
using Luthn.Sdk.Classification;
using Luthn.Sdk.Memory;
using Luthn.Sdk.Source;

namespace Luthn.AgentConnector.Tests;

public sealed class LuthnClientTests
{
    [Fact]
    public async Task GetContextPackPostsCoreTagsToSafeAgentEndpoint()
    {
        using var handler = new CapturingHandler("""
            {
              "coreTags": ["demo"],
              "items": [
                {
                  "id": "wiki-demo-runbook",
                  "title": "Demo agent runbook",
                  "safeSummary": "Public-safe demo context.",
                  "sensitivity": "Public",
                  "coreTags": ["runbook", "demo"]
                }
              ]
            }
            """);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8080") };
        var client = new LuthnClient(http);

        var pack = await client.GetContextPackAsync(["demo"], 5, "demo runbook");
        var body = await handler.RequestContent!.ReadAsStringAsync();

        Assert.Equal(HttpMethod.Post, handler.Request!.Method);
        Assert.Equal("/api/agent/context-packs", handler.Request.RequestUri!.AbsolutePath);
        Assert.Contains("\"query\"", body, StringComparison.Ordinal);
        Assert.Contains("\"coreTags\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("ontology", body, StringComparison.OrdinalIgnoreCase);
        var item = Assert.Single(pack.Items);
        Assert.Equal("wiki-demo-runbook", item.Id);
    }

    [Fact]
    public async Task SearchPostsQueryAndCoreTagsToSafeAgentSearchEndpoint()
    {
        using var handler = new CapturingHandler("""
            {
              "query": "billing outage",
              "coreTags": ["runbook"],
              "results": [
                {
                  "id": "wiki-billing-outage",
                  "title": "Billing outage runbook",
                  "safeSummary": "Public-safe recovery steps.",
                  "sensitivity": "Public",
                  "coreTags": ["runbook"],
                  "score": 100
                }
              ]
            }
            """);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8080") };
        var client = new LuthnClient(http);

        var search = await client.SearchAsync("billing outage", ["runbook"], 5);
        var body = await handler.RequestContent!.ReadAsStringAsync();

        Assert.Equal(HttpMethod.Post, handler.Request!.Method);
        Assert.Equal("/api/agent/search", handler.Request.RequestUri!.AbsolutePath);
        Assert.Contains("\"query\"", body, StringComparison.Ordinal);
        Assert.Contains("\"coreTags\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("ontology", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("vault", handler.Request.RequestUri.ToString(), StringComparison.OrdinalIgnoreCase);
        var result = Assert.Single(search.Results);
        Assert.Equal("wiki-billing-outage", result.Id);
    }

    [Fact]
    public async Task GetWikiProposalReadsOnlySafeWikiProposalEndpoint()
    {
        using var handler = new CapturingHandler("# Demo\n\nPublic-safe demo context.");
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8080") };
        var client = new LuthnClient(http);

        var proposal = await client.GetWikiProposalAsync("wiki-demo-runbook");

        Assert.Equal(HttpMethod.Get, handler.Request!.Method);
        Assert.Equal("/api/wiki/proposals/wiki-demo-runbook", handler.Request.RequestUri!.AbsolutePath);
        Assert.Equal("wiki-demo-runbook", proposal.Id);
        Assert.Contains("Public-safe demo context.", proposal.Markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("vault", handler.Request.RequestUri.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ClassifyPreviewPostsToClassificationPreviewEndpoint()
    {
        using var handler = new CapturingHandler("""
            {
              "sourceId": "source-1",
              "classification": {
                "sensitivity": "Public",
                "confidence": 0.95,
                "categories": ["runbook"],
                "containsSensitiveMaterial": false
              },
              "storageDecision": {
                "kind": "WikiAndCore",
                "reasons": ["public-safe"],
                "allowsWikiProjection": true,
                "allowsAgentContext": true,
                "requiresHumanReview": false
              }
            }
            """);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8080") };
        var client = new LuthnClient(http);

        var response = await client.ClassifyPreviewAsync(
            new ClassificationPreviewRequestDto("source-1", "Public implementation note.", "note"));

        Assert.Equal(HttpMethod.Post, handler.Request!.Method);
        Assert.Equal("/api/classification/preview", handler.Request.RequestUri!.AbsolutePath);
        Assert.Equal("source-1", response.SourceId);
        Assert.Equal("Public", response.Classification.Sensitivity);
    }

    [Fact]
    public async Task IntakeSourcePostsCanonicalSourceContract()
    {
        using var handler = new CapturingHandler("""
            {
              "sourceId": "source-1",
              "sourceEventId": "source-1",
              "classificationResultId": "classification-1",
              "wikiProposalId": "wiki-1",
              "sensitiveReferenceId": null,
              "auditEventId": "audit-1",
              "classification": {
                "sensitivity": "Public",
                "confidence": 0.95,
                "categories": ["runbook"],
                "containsSensitiveMaterial": false
              },
              "storageDecision": {
                "kind": "WikiCandidate",
                "reasons": ["public-safe"],
                "allowsWikiProjection": true,
                "allowsAgentContext": true,
                "requiresHumanReview": false
              }
            }
            """);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8080") };
        var client = new LuthnClient(http);

        var response = await client.IntakeSourceAsync(new SourceIntakeRequestDto(
            "local",
            "note",
            "Public implementation note.",
            "Implementation note",
            "Public-safe summary.",
            ["runbook"]));
        var body = await handler.RequestContent!.ReadAsStringAsync();

        Assert.Equal(HttpMethod.Post, handler.Request!.Method);
        Assert.Equal("/api/sources", handler.Request.RequestUri!.AbsolutePath);
        Assert.Contains("\"coreTags\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("sourceEventId", body, StringComparison.Ordinal);
        Assert.Equal("source-1", response.SourceId);
        Assert.Equal(response.SourceId, response.SourceEventId);
    }

    [Fact]
    public async Task IntakeTurnSummaryPostsToAgentSummaryEndpoint()
    {
        using var handler = new CapturingHandler("""
            {
              "summaryId": "turn-summary-1",
              "sourceEventId": "turn-summary-1",
              "classificationResultId": "classification-1",
              "memoryItemId": "memory-turn-summary-1",
              "auditEventId": "audit-1",
              "allowsAgentContext": true,
              "duplicate": false,
              "classification": {
                "sensitivity": "Public",
                "confidence": 0.95,
                "categories": ["release"],
                "containsSensitiveMaterial": false
              },
              "storageDecision": {
                "kind": "WikiCandidate",
                "reasons": ["public-safe"],
                "allowsWikiProjection": true,
                "allowsAgentContext": true,
                "requiresHumanReview": false
              }
            }
            """);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8080") };
        var client = new LuthnClient(http);

        var response = await client.IntakeTurnSummaryAsync(new TurnSummaryIntakeRequestDto(
            "session-1",
            "codex",
            "Published release note for contributors.",
            ["release"],
            TurnId: "turn-1",
            IdempotencyKey: "summary-1"));
        var body = await handler.RequestContent!.ReadAsStringAsync();

        Assert.Equal(HttpMethod.Post, handler.Request!.Method);
        Assert.Equal("/api/agent/turn-summaries", handler.Request.RequestUri!.AbsolutePath);
        Assert.Contains("\"sessionId\"", body, StringComparison.Ordinal);
        Assert.Contains("\"summary\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("raw", body, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("turn-summary-1", response.SummaryId);
        Assert.True(response.AllowsAgentContext);
        Assert.False(response.Duplicate);
    }

    [Fact]
    public async Task ReportAgentConnectionObservationPostsMetadataOnlyContract()
    {
        using var handler = new CapturingHandler("""
            {
              "agentId": "codex",
              "agentName": "Codex",
              "integrationKind": "host-hook-mcp",
              "connectorVersion": "1",
              "state": "Verified",
              "lastSuccessfulActivityAt": null,
              "updatedAt": "2026-07-11T00:00:00+00:00",
              "channels": [
                {
                  "channel": "mcp",
                  "configured": true,
                  "state": "Verified",
                  "verificationState": "Verified",
                  "activityState": "Unknown",
                  "lastVerifiedAt": "2026-07-11T00:00:00+00:00",
                  "lastActivityAt": null,
                  "lastSuccessfulActivityAt": null,
                  "failureCode": null,
                  "updatedAt": "2026-07-11T00:00:00+00:00"
                }
              ]
            }
            """);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8080") };
        var client = new LuthnClient(http);

        var response = await client.ReportAgentConnectionObservationAsync(
            "codex",
            new AgentConnectionObservationRequestDto(
                "Codex",
                "host-hook-mcp",
                "1",
                [new AgentConnectionChannelObservationDto("mcp", true, "Verified")]));
        var body = await handler.RequestContent!.ReadAsStringAsync();

        Assert.Equal(HttpMethod.Post, handler.Request!.Method);
        Assert.Equal(
            "/api/agent-connections/codex/observations",
            handler.Request.RequestUri!.AbsolutePath);
        Assert.Contains("\"connectorVersion\"", body, StringComparison.Ordinal);
        Assert.Contains("\"channel\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("token", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("transcript", body, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Verified", response.State);
    }

    [Fact]
    public async Task ListAgentConnectionsReadsMetadataOnlyStatusEndpoint()
    {
        using var handler = new CapturingHandler("""
            {
              "connections": [
                {
                  "agentId": "codex",
                  "agentName": "Codex",
                  "integrationKind": "host-hook-mcp",
                  "connectorVersion": "1",
                  "state": "Active",
                  "lastSuccessfulActivityAt": "2026-07-11T00:00:00+00:00",
                  "updatedAt": "2026-07-11T00:00:00+00:00",
                  "channels": []
                }
              ]
            }
            """);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8080") };
        var client = new LuthnClient(http);

        var response = await client.ListAgentConnectionsAsync();

        Assert.Equal(HttpMethod.Get, handler.Request!.Method);
        Assert.Equal("/api/agent-connections", handler.Request.RequestUri!.AbsolutePath);
        Assert.Equal("Active", Assert.Single(response.Connections).State);
    }

    [Fact]
    public async Task CreateSharedMemoryPostsSafeProjectionToMemoryEndpoint()
    {
        using var handler = new CapturingHandler("""
            {
              "id": "memory-1",
              "title": "Release memory",
              "safeSummary": "Public-safe release summary.",
              "sensitivity": "Public",
              "coreTags": ["release", "runbook"],
              "visibility": "SharedAcrossAgents",
              "retentionKind": "Durable",
              "expiresAt": null,
              "sourceSessionId": "session-1",
              "allowsAgentContext": true,
              "createdAt": "2026-07-07T00:00:00+00:00"
            }
            """);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8080") };
        var client = new LuthnClient(http);

        var response = await client.CreateSharedMemoryItemAsync(
            new CreateSharedMemoryItemRequestDto(
                "Release memory",
                "Public-safe release summary.",
                ["release", "runbook"],
                SourceSessionId: "session-1"));
        var body = await handler.RequestContent!.ReadAsStringAsync();

        Assert.Equal(HttpMethod.Post, handler.Request!.Method);
        Assert.Equal("/api/memory/items", handler.Request.RequestUri!.AbsolutePath);
        Assert.Contains("\"safeSummary\"", body, StringComparison.Ordinal);
        Assert.Contains("\"coreTags\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("raw", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("vault", handler.Request.RequestUri.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("memory-1", response.Id);
        Assert.True(response.AllowsAgentContext);
    }

    [Fact]
    public async Task QuerySharedMemoryPostsToSafeMemoryQueryEndpoint()
    {
        using var handler = new CapturingHandler("""
            {
              "query": "release",
              "coreTags": ["runbook"],
              "items": [
                {
                  "id": "memory-1",
                  "title": "Release memory",
                  "safeSummary": "Public-safe release summary.",
                  "sensitivity": "Public",
                  "coreTags": ["release", "runbook"],
                  "visibility": "SharedAcrossAgents",
                  "retentionKind": "Durable",
                  "expiresAt": null,
                  "sourceSessionId": null,
                  "allowsAgentContext": true,
                  "createdAt": "2026-07-07T00:00:00+00:00"
                }
              ]
            }
            """);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8080") };
        var client = new LuthnClient(http);

        var response = await client.QuerySharedMemoryAsync(
            new SharedMemoryQueryRequestDto("release", ["runbook"], 5));
        var body = await handler.RequestContent!.ReadAsStringAsync();

        Assert.Equal(HttpMethod.Post, handler.Request!.Method);
        Assert.Equal("/api/memory/query", handler.Request.RequestUri!.AbsolutePath);
        Assert.Contains("\"query\"", body, StringComparison.Ordinal);
        Assert.Contains("\"coreTags\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("raw", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("vault", handler.Request.RequestUri.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("memory-1", Assert.Single(response.Items).Id);
    }

    [Fact]
    public async Task GetSharedMemoryItemReadsOnlySafeMemoryItemEndpoint()
    {
        using var handler = new CapturingHandler("""
            {
              "id": "memory-1",
              "title": "Release memory",
              "safeSummary": "Public-safe release summary.",
              "sensitivity": "Public",
              "coreTags": ["release", "runbook"],
              "visibility": "PublicSafe",
              "retentionKind": "Durable",
              "expiresAt": null,
              "sourceSessionId": null,
              "allowsAgentContext": true,
              "createdAt": "2026-07-07T00:00:00+00:00"
            }
            """);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8080") };
        var client = new LuthnClient(http);

        var response = await client.GetSharedMemoryItemAsync("memory-1");

        Assert.Equal(HttpMethod.Get, handler.Request!.Method);
        Assert.Equal("/api/memory/items/memory-1", handler.Request.RequestUri!.AbsolutePath);
        Assert.DoesNotContain("vault", handler.Request.RequestUri.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Public-safe release summary.", response.SafeSummary);
        Assert.True(response.AllowsAgentContext);
    }

    [Fact]
    public async Task GetSensitiveAccessResultReadsApprovedOutputContract()
    {
        using var handler = new CapturingHandler("""
            {
              "id": "access-1",
              "sensitiveReferenceId": "sensitive-ref-1",
              "status": "Approved",
              "outputPolicy": "approved-redacted-output-available",
              "redactedOutputAvailable": true,
              "redactedOutput": "Public-safe release steps.",
              "payloadClass": "redacted-output",
              "redactionState": "approved-redacted-output-available",
              "reasons": ["Approved limited output is sourced from a public-safe redacted summary."]
            }
            """);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8080") };
        var client = new LuthnClient(http);

        var response = await client.GetSensitiveAccessResultAsync("access-1");

        Assert.Equal(HttpMethod.Get, handler.Request!.Method);
        Assert.Equal("/api/access-requests/access-1/result", handler.Request.RequestUri!.AbsolutePath);
        Assert.Equal("approved-redacted-output-available", response.OutputPolicy);
        Assert.True(response.RedactedOutputAvailable);
        Assert.Equal("Public-safe release steps.", response.RedactedOutput);
        Assert.DoesNotContain("vault/raw", handler.Request.RequestUri.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ApproveSensitiveAccessRequestCanSendReviewedRedactedSummary()
    {
        using var handler = new CapturingHandler("""
            {
              "id": "access-1",
              "sensitiveReferenceId": "sensitive-ref-1",
              "status": "Approved",
              "requestedBy": "agent-service",
              "createdAt": "2026-07-05T00:00:00+00:00",
              "decidedBy": "operator",
              "decidedAt": "2026-07-05T00:01:00+00:00",
              "redactedOutputAvailable": true,
              "outputPolicy": "approved-redacted-output-available"
            }
            """);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8080") };
        var client = new LuthnClient(http);

        var response = await client.ApproveSensitiveAccessRequestAsync(
            "access-1",
            new SensitiveAccessDecisionRequestDto(
                "Approved with reviewed output.",
                "Public-safe release steps."));
        var body = await handler.RequestContent!.ReadAsStringAsync();

        Assert.Equal(HttpMethod.Post, handler.Request!.Method);
        Assert.Equal("/api/access-requests/access-1/approve", handler.Request.RequestUri!.AbsolutePath);
        Assert.Contains("\"reason\"", body, StringComparison.Ordinal);
        Assert.Contains("\"redactedSummary\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("raw", body, StringComparison.OrdinalIgnoreCase);
        Assert.True(response.RedactedOutputAvailable);
    }

    [Fact]
    public async Task DenySensitiveAccessRequestPostsDecisionWithoutRawRoute()
    {
        using var handler = new CapturingHandler("""
            {
              "id": "access-1",
              "sensitiveReferenceId": "sensitive-ref-1",
              "status": "Denied",
              "requestedBy": "agent-service",
              "createdAt": "2026-07-05T00:00:00+00:00",
              "decidedBy": "operator",
              "decidedAt": "2026-07-05T00:01:00+00:00",
              "redactedOutputAvailable": false,
              "outputPolicy": "denied-no-output"
            }
            """);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8080") };
        var client = new LuthnClient(http);

        var response = await client.DenySensitiveAccessRequestAsync(
            "access-1",
            new SensitiveAccessDecisionRequestDto("Denied by policy."));

        Assert.Equal(HttpMethod.Post, handler.Request!.Method);
        Assert.Equal("/api/access-requests/access-1/deny", handler.Request.RequestUri!.AbsolutePath);
        Assert.Equal("denied-no-output", response.OutputPolicy);
        Assert.DoesNotContain("vault", handler.Request.RequestUri.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OptionsBearerValueAddsAuthorizationHeaderToSafeRequests()
    {
        using var handler = new CapturingHandler("""
            {
              "query": "release",
              "coreTags": ["runbook"],
              "results": []
            }
            """);
        var client = new LuthnClient(new LuthnClientOptions
        {
            BaseUrl = new Uri("http://localhost:8080"),
            BearerToken = "agent-safe-read-local",
            ConfigureHttpMessageHandler = () => handler
        });

        await client.SearchAsync("release", ["runbook"], 5);

        Assert.Equal("Bearer", handler.Request!.Headers.Authorization!.Scheme);
        Assert.Equal("agent-safe-read-local", handler.Request.Headers.Authorization.Parameter);
        Assert.Equal("/api/agent/search", handler.Request.RequestUri!.AbsolutePath);
        Assert.DoesNotContain("vault", handler.Request.RequestUri.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ConnectorDependsOnSdkButNotCoreAndExposesNoRawReadMethods()
    {
        var references = typeof(LuthnClient).Assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .ToArray();
        var methodNames = typeof(LuthnClient)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Select(method => method.Name)
            .ToArray();

        Assert.Contains("Luthn.Sdk", references);
        Assert.DoesNotContain("Luthn.Core", references);
        Assert.DoesNotContain("ReadRawVaultAsync", methodNames);
        Assert.DoesNotContain("DumpSourceRecordsAsync", methodNames);
        Assert.DoesNotContain("QueryPrivateRecordsAsync", methodNames);
        Assert.Contains("SearchAsync", methodNames);
        Assert.Contains("CreateSharedMemoryItemAsync", methodNames);
        Assert.Contains("QuerySharedMemoryAsync", methodNames);
        Assert.Contains("GetSharedMemoryItemAsync", methodNames);
        Assert.Contains("GetSensitiveAccessResultAsync", methodNames);
        Assert.Contains("IntakeTurnSummaryAsync", methodNames);
        Assert.Contains("ListAgentConnectionsAsync", methodNames);
        Assert.Contains("ReportAgentConnectionObservationAsync", methodNames);
    }

    private sealed class CapturingHandler(string responseContent) : HttpMessageHandler, IDisposable
    {
        public HttpRequestMessage? Request { get; private set; }
        public HttpContent? RequestContent { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Request = request;
            RequestContent = request.Content;

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseContent)
            });
        }
    }
}
