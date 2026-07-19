using System.Text.Json;
using Luthn.Sdk.Access;
using Luthn.Sdk.Agent;
using Luthn.Sdk.Classification;
using Luthn.Sdk.Context;
using Luthn.Sdk.Memory;
using Luthn.Sdk.Provenance;
using Luthn.Sdk.Plugins;
using Luthn.Sdk.Source;
using Luthn.Sdk.Sync;
using Luthn.Sdk.Telemetry;
using Luthn.Sdk.Wiki;

namespace Luthn.Sdk.Tests;

public sealed class SdkContractTests
{
    [Fact]
    public void SensitiveAccessRequestPreservesLegacyConstructorAndDeconstructContract()
    {
        var request = new SensitiveAccessRequestDto(
            "access-1",
            "sensitive-ref-1",
            "Pending",
            "requester",
            DateTimeOffset.UnixEpoch,
            null,
            null,
            false,
            "pending-approval")
        {
            SessionId = "session-1",
            ExpiresAt = DateTimeOffset.UnixEpoch.AddMinutes(10)
        };

        var (_, _, _, _, _, _, _, outputAvailable, outputPolicy) = request;

        Assert.False(outputAvailable);
        Assert.Equal("pending-approval", outputPolicy);
        Assert.Equal("session-1", request.SessionId);
        Assert.Equal(DateTimeOffset.UnixEpoch.AddMinutes(10), request.ExpiresAt);
    }

    [Fact]
    public void SafeProjectionSyncEnvelopeUsesVersionedPublicSafeContract()
    {
        var envelope = new SafeProjectionSyncEnvelopeDto(
            1,
            "instance-1",
            "memory-1",
            2,
            "Upsert",
            "Release decision",
            "Use the approved release process.",
            ["release"],
            "shared-memory-safe-projection",
            "metadata-only",
            "safe-projection-only",
            DateTimeOffset.Parse("2026-07-12T00:00:00Z"),
            DateTimeOffset.Parse("2026-07-13T00:00:00Z"),
            DateTimeOffset.Parse("2026-07-13T00:00:00Z"),
            DateTimeOffset.Parse("2027-07-13T00:00:00Z"),
            "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");

        var json = JsonSerializer.Serialize(envelope);
        using var document = JsonDocument.Parse(json);
        var actualProperties = document.RootElement
            .EnumerateObject()
            .Select(property => property.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        var expectedProperties = new[]
        {
            "contractVersion", "originInstanceId", "localRecordId", "revision", "operation",
            "title", "safeSummary", "coreTags", "projectionKind", "payloadClass",
            "redactionState", "createdAt", "updatedAt", "decidedAt", "expiresAt",
            "provenanceDigest"
        }.OrderBy(name => name, StringComparer.Ordinal).ToArray();

        Assert.Equal(expectedProperties, actualProperties);
        Assert.Contains("\"contractVersion\":1", json, StringComparison.Ordinal);
        Assert.Contains("\"originInstanceId\"", json, StringComparison.Ordinal);
        Assert.Contains("\"localRecordId\"", json, StringComparison.Ordinal);
        Assert.Contains("\"revision\":2", json, StringComparison.Ordinal);
        Assert.Contains("\"redactionState\":\"safe-projection-only\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("vault", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("credential", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sourceContent", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SafeProjectionRevokeDtoOmitsProjectionBody()
    {
        var envelope = new SafeProjectionSyncEnvelopeDto(
            1,
            "instance-1",
            "memory-1",
            3,
            "Revoke",
            null,
            null,
            [],
            "shared-memory-safe-projection",
            "metadata-only",
            "safe-projection-only",
            DateTimeOffset.Parse("2026-07-12T00:00:00Z"),
            DateTimeOffset.Parse("2026-07-13T00:00:00Z"),
            DateTimeOffset.Parse("2026-07-13T00:00:00Z"),
            null,
            null);

        var json = JsonSerializer.Serialize(envelope);

        Assert.DoesNotContain("\"title\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"safeSummary\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"expiresAt\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"provenanceDigest\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void ContextPackRequestSerializesCoreTagsContract()
    {
        var request = new ContextPackRequestDto(
            ["runbook", "demo"],
            5,
            "billing outage",
            "luthn",
            "ranking",
            ["quality"]);

        var json = JsonSerializer.Serialize(request);

        Assert.Contains("\"query\"", json, StringComparison.Ordinal);
        Assert.Contains("\"coreTags\"", json, StringComparison.Ordinal);
        Assert.Contains("\"maxItems\"", json, StringComparison.Ordinal);
        Assert.Contains("\"projectKey\":\"luthn\"", json, StringComparison.Ordinal);
        Assert.Contains("\"taskKey\":\"ranking\"", json, StringComparison.Ordinal);
        Assert.Contains("\"topicTags\":[\"quality\"]", json, StringComparison.Ordinal);
        Assert.DoesNotContain("CoreTags", json, StringComparison.Ordinal);
        Assert.DoesNotContain("ontology", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SafeSearchRequestSerializesQueryAndCoreTagsContract()
    {
        var request = new SafeSearchRequestDto("billing outage", ["runbook"], 5);

        var json = JsonSerializer.Serialize(request);

        Assert.Contains("\"query\"", json, StringComparison.Ordinal);
        Assert.Contains("\"coreTags\"", json, StringComparison.Ordinal);
        Assert.Contains("\"maxItems\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("ontology", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("raw", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LegacyContextPackResponseDeserializesWithEmptyOptionalMetadata()
    {
        var response = JsonSerializer.Deserialize<ContextPackDto>(
            """{"coreTags":["runbook"],"items":[{"id":"wiki-1","title":"Runbook","safeSummary":"Safe summary.","sensitivity":"Public","coreTags":["runbook"]}]}""");

        Assert.NotNull(response);
        Assert.Null(response.ProjectKey);
        Assert.Null(response.TaskKey);
        Assert.Empty(response.TopicTags);
        var item = Assert.Single(response.Items);
        Assert.Null(item.ProjectKey);
        Assert.Null(item.TaskKey);
        Assert.Empty(item.TopicTags);
        Assert.Equal(default, item.ProjectionTimestamp);
        Assert.Null(response.RetrievalId);
    }

    [Fact]
    public void SearchTelemetryDtosSerializeOnlyBoundedMetadataFields()
    {
        var observation = JsonSerializer.Serialize(
            new SearchObservationRequestDto("mcp_context_pack", "succeeded", "hit", 8, 2));
        var feedback = JsonSerializer.Serialize(
            new SearchFeedbackRequestDto("retrieval-0123456789abcdef0123456789abcdef", "helpful"));

        Assert.Equal(
            """{"surface":"mcp_context_pack","outcome":"succeeded","cacheStatus":"hit","durationMilliseconds":8,"resultCount":2}""",
            observation);
        Assert.Equal(
            """{"retrievalId":"retrieval-0123456789abcdef0123456789abcdef","judgment":"helpful"}""",
            feedback);
        Assert.DoesNotContain("query", observation, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("resultId", feedback, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SensitiveAccessDecisionSerializesReviewedOutputContract()
    {
        var request = new SensitiveAccessDecisionRequestDto(
            "Approved with reviewed output.",
            "Public-safe release steps.");

        var json = JsonSerializer.Serialize(request);

        Assert.Contains("\"reason\"", json, StringComparison.Ordinal);
        Assert.Contains("\"redactedSummary\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("RedactedSummary", json, StringComparison.Ordinal);
        Assert.DoesNotContain("vault", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("raw", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SensitiveAccessResultDeserializesApprovedRedactedOutputContract()
    {
        var result = JsonSerializer.Deserialize<SensitiveAccessResultDto>("""
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

        Assert.NotNull(result);
        Assert.Equal("access-1", result.Id);
        Assert.True(result.RedactedOutputAvailable);
        Assert.Equal("Public-safe release steps.", result.RedactedOutput);
    }

    [Fact]
    public void SourceIntakeResponseKeepsCanonicalSourceIdAndLegacyAlias()
    {
        var result = JsonSerializer.Deserialize<SourceIntakeResponseDto>("""
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

        Assert.NotNull(result);
        Assert.Equal("source-1", result.SourceId);
        Assert.Equal(result.SourceId, result.SourceEventId);
        Assert.Equal("wiki-1", result.WikiProposalId);
    }

    [Fact]
    public void TurnSummaryDtosSerializeAgentSummaryContract()
    {
        var request = new TurnSummaryIntakeRequestDto(
            "session-1",
            "codex",
            "Published release note for contributors.",
            ["release"],
            TurnId: "turn-1",
            IdempotencyKey: "summary-1");
        var response = JsonSerializer.Deserialize<TurnSummaryIntakeResponseDto>("""
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

        var json = JsonSerializer.Serialize(request);

        Assert.Contains("\"sessionId\"", json, StringComparison.Ordinal);
        Assert.Contains("\"sourceAgent\"", json, StringComparison.Ordinal);
        Assert.Contains("\"coreTags\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("raw", json, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(response);
        Assert.Equal("turn-summary-1", response.SummaryId);
        Assert.True(response.AllowsAgentContext);
    }

    [Fact]
    public void SharedMemoryDtosSerializeSafeProjectionContract()
    {
        var create = new CreateSharedMemoryItemRequestDto(
            "Release memory",
            "Public-safe release summary.",
            ["release", "runbook"],
            Provenance: new CollectionProvenanceClaimsDto(
                "owner.one",
                "codex",
                "codex.desktop",
                "luthn.plugin",
                "luthn.codex.connector",
                "2"));
        var query = new SharedMemoryQueryRequestDto("release", ["runbook"], 5);
        var item = new SharedMemoryItemDto(
            "memory-1",
            "Release memory",
            "Public-safe release summary.",
            "Public",
            ["release", "runbook"],
            "SharedAcrossAgents",
            "Durable",
            null,
            "session-1",
            true,
            DateTimeOffset.UnixEpoch);

        var json = JsonSerializer.Serialize(new { create, query, item });

        Assert.Contains("\"safeSummary\"", json, StringComparison.Ordinal);
        Assert.Contains("\"coreTags\"", json, StringComparison.Ordinal);
        Assert.Contains("\"allowsAgentContext\"", json, StringComparison.Ordinal);
        Assert.Contains("\"provenance\"", json, StringComparison.Ordinal);
        Assert.Contains("\"connectorId\":\"luthn.codex.connector\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("raw", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("vault", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PluginIngestionEnvelopeSerializesMetadataOnlyContract()
    {
        var envelope = new PluginIngestionEnvelopeDto(
            new IngestionSourceIdentityDto(
                "plugin-mail",
                "gmail",
                "Email",
                "message-1",
                "Support thread"),
            new IngestionConsentDto(
                "UserGranted",
                "user-1",
                DateTimeOffset.Parse("2026-01-01T00:00:00Z")),
            "sha256:abc123",
            "MetadataOnly",
            new IngestionRetryStateDto(1, 3, DateTimeOffset.Parse("2026-01-01T01:00:00Z"), "timeout"),
            DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
            ["support", "email"],
            "message/rfc822",
            1024,
            new IngestionOrderingStateDto(
                "plugin-mail:gmail:message-1",
                7,
                DateTimeOffset.Parse("2026-01-01T00:00:00Z")),
            new IngestionDeadLetterStateDto(
                "RetryExhausted",
                DateTimeOffset.Parse("2026-01-01T03:00:00Z"),
                "timeout",
                "retry-exhausted"));

        var json = JsonSerializer.Serialize(envelope);

        Assert.Contains("\"sourceIdentity\"", json, StringComparison.Ordinal);
        Assert.Contains("\"consent\"", json, StringComparison.Ordinal);
        Assert.Contains("\"contentDigest\":\"sha256:abc123\"", json, StringComparison.Ordinal);
        Assert.Contains("\"payloadClass\":\"MetadataOnly\"", json, StringComparison.Ordinal);
        Assert.Contains("\"retry\"", json, StringComparison.Ordinal);
        Assert.Contains("\"ordering\"", json, StringComparison.Ordinal);
        Assert.Contains("\"partitionKey\":\"plugin-mail:gmail:message-1\"", json, StringComparison.Ordinal);
        Assert.Contains("\"deadLetter\"", json, StringComparison.Ordinal);
        Assert.Contains("\"diagnosticCode\":\"retry-exhausted\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"content\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("raw customer", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PluginIngestionEnvelopeKeepsLegacyNinePartDeconstructContract()
    {
        var sourceIdentity = new IngestionSourceIdentityDto(
            "plugin-mail",
            "gmail",
            "Email",
            "message-1",
            "Support thread");
        var consent = new IngestionConsentDto(
            "UserGranted",
            "user-1",
            DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var retry = new IngestionRetryStateDto(1, 3, DateTimeOffset.Parse("2026-01-01T01:00:00Z"), "timeout");
        string[] coreTags = ["support", "email"];
        var receivedAt = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var envelope = new PluginIngestionEnvelopeDto(
            sourceIdentity,
            consent,
            "sha256:abc123",
            "MetadataOnly",
            retry,
            receivedAt,
            coreTags,
            "message/rfc822",
            1024,
            new IngestionOrderingStateDto(
                "plugin-mail:gmail:message-1",
                7,
                DateTimeOffset.Parse("2026-01-01T00:00:00Z")),
            new IngestionDeadLetterStateDto(
                "RetryExhausted",
                DateTimeOffset.Parse("2026-01-01T03:00:00Z"),
                "timeout",
                "retry-exhausted"));

        envelope.Deconstruct(
            out var deconstructedSourceIdentity,
            out var deconstructedConsent,
            out var deconstructedContentDigest,
            out var deconstructedPayloadClass,
            out var deconstructedRetry,
            out var deconstructedReceivedAt,
            out var deconstructedCoreTags,
            out var deconstructedPayloadMediaType,
            out var deconstructedPayloadSizeBytes);

        var parameterTypes = typeof(PluginIngestionEnvelopeDto)
            .GetMethods()
            .Where(method => method.Name == nameof(PluginIngestionEnvelopeDto.Deconstruct))
            .Select(method => method.GetParameters().Select(parameter => parameter.ParameterType).ToArray())
            .ToArray();

        Assert.Contains(parameterTypes, parameters =>
            parameters.SequenceEqual(
                [
                    typeof(IngestionSourceIdentityDto).MakeByRefType(),
                    typeof(IngestionConsentDto).MakeByRefType(),
                    typeof(string).MakeByRefType(),
                    typeof(string).MakeByRefType(),
                    typeof(IngestionRetryStateDto).MakeByRefType(),
                    typeof(DateTimeOffset).MakeByRefType(),
                    typeof(IReadOnlyList<string>).MakeByRefType(),
                    typeof(string).MakeByRefType(),
                    typeof(long?).MakeByRefType()
                ]));
        Assert.Same(sourceIdentity, deconstructedSourceIdentity);
        Assert.Same(consent, deconstructedConsent);
        Assert.Equal("sha256:abc123", deconstructedContentDigest);
        Assert.Equal("MetadataOnly", deconstructedPayloadClass);
        Assert.Same(retry, deconstructedRetry);
        Assert.Equal(receivedAt, deconstructedReceivedAt);
        Assert.Same(coreTags, deconstructedCoreTags);
        Assert.Equal("message/rfc822", deconstructedPayloadMediaType);
        Assert.Equal(1024, deconstructedPayloadSizeBytes);
    }

    [Fact]
    public void SdkAssemblyDoesNotReferenceCore()
    {
        var references = typeof(ContextPackDto).Assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .ToArray();

        Assert.DoesNotContain("Luthn.Core", references);
    }

    [Fact]
    public void PublicDtosAreProviderNeutralAndSafeSummaryBased()
    {
        var context = new ContextPackDto(
            ["runbook"],
            [
                new ContextPackItemDto(
                    "wiki-demo-runbook",
                    "Demo agent runbook",
                    "Public-safe demo context.",
                    "Public",
                    ["runbook", "demo"])
            ]);
        var wiki = new WikiProposalDto("wiki-demo-runbook", "# Demo", ["runbook", "demo"]);
        var search = new SafeSearchResponseDto(
            "demo",
            ["runbook"],
            [
                new SafeSearchResultDto(
                    "wiki-demo-runbook",
                    "Demo agent runbook",
                    "Public-safe demo context.",
                    "Public",
                    ["runbook", "demo"],
                    100)
            ]);
        var preview = new ClassificationPreviewDto(
            "source-1",
            new ClassificationResultDto("Public", 0.95, ["runbook"], false),
            new StorageDecisionDto("WikiAndCore", ["public-safe"], true, true, false));
        var accessResult = new SensitiveAccessResultDto(
            "access-1",
            "sensitive-ref-1",
            "Approved",
            "approved-redacted-output-available",
            true,
            "Public-safe release steps.",
            "redacted-output",
            "approved-redacted-output-available",
            ["Approved limited output is sourced from a public-safe redacted summary."]);

        var json = JsonSerializer.Serialize(new { context, wiki, search, preview, accessResult });

        Assert.Contains("safeSummary", json, StringComparison.Ordinal);
        Assert.Contains("coreTags", json, StringComparison.Ordinal);
        Assert.DoesNotContain("provider", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("raw", json, StringComparison.OrdinalIgnoreCase);
        Assert.True(typeof(ILuthnPlugin).IsInterface);
    }
}
