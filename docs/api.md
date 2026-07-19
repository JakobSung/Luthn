# API

[한국어](api.ko.md)

## Naming note

The public API should use `coreTags` for Core-filtered context selection and
`sourceId` for public source identifiers.

The current request/response contract uses `coreTags`; reserved legacy tag
aliases are not part of the public API. Source intake responses include
`sourceEventId` as a backward-compatible alias, but `sourceId` is the canonical
public source identifier for new API, SDK, connector, and MCP contracts.

## Agent turn-summary intake

```http
POST /api/agent/turn-summaries
```

Agent adapters can send bounded turn summaries after a conversation turn or
small batch of turns. This endpoint is for summaries, not raw transcripts.
Luthn classifies the submitted summary, resolved title, and Core tags as one
agent-visible projection before it becomes shared memory.

Request:

```json
{
  "sessionId": "session-1",
  "turnId": "turn-12",
  "sourceAgent": "codex",
  "projectPath": "/path/to/project",
  "summary": "Published release note for external contributors.",
  "coreTags": ["release", "codex"],
  "contentDigest": "sha256:...",
  "idempotencyKey": "session-1-turn-12",
  "title": "Codex release note"
}
```

Response:

```json
{
  "summaryId": "turn-summary-...",
  "sourceEventId": "turn-summary-...",
  "classificationResultId": "classification-turn-summary-...",
  "memoryItemId": "memory-turn-summary-...",
  "auditEventId": "audit-...",
  "allowsAgentContext": true,
  "duplicate": false,
  "classification": {
    "sensitivity": "Public",
    "confidence": 0.75,
    "categories": [],
    "containsSensitiveMaterial": false
  },
  "storageDecision": {
    "kind": "WikiCandidate",
    "reasons": ["Content is eligible for wiki-safe review and Core projection."],
    "allowsWikiProjection": true,
    "allowsAgentContext": true,
    "requiresHumanReview": false
  }
}
```

Safe public summaries become `SharedAcrossAgents` memory and can appear in
agent context/search results. Sensitive summaries are kept as private memory
boundary records and are not returned through default agent context APIs.
`idempotencyKey` prevents duplicate writes from retrying adapters.

## Agent connection observations

```http
GET /api/agent-connections
POST /api/agent-connections/{agentId}/observations
```

Agent connectors report metadata-only state for each supported channel. The
API replaces the latest row for an agent/channel pair; it is a status surface,
not a connection event log.

Observation request:

```json
{
  "agentName": "Codex",
  "integrationKind": "host-hook-mcp",
  "connectorVersion": "1",
  "channels": [
    {
      "channel": "automatic-ingestion",
      "configured": true,
      "verificationState": "Verified",
      "activityState": "Succeeded",
      "failureCode": null
    },
    {
      "channel": "mcp",
      "configured": true,
      "verificationState": "Verified",
      "activityState": "Unknown",
      "failureCode": null
    }
  ]
}
```

The server supplies observation timestamps. `failureCode` is a bounded machine
code and is accepted only for failed observations. Request and response models
do not include tokens, prompts, responses, transcripts, raw errors, or local
filesystem paths.

List response:

```json
{
  "connections": [
    {
      "agentId": "codex",
      "agentName": "Codex",
      "integrationKind": "host-hook-mcp",
      "connectorVersion": "1",
      "state": "Active",
      "lastSuccessfulActivityAt": "2026-01-01T00:00:00Z",
      "updatedAt": "2026-01-01T00:00:00Z",
      "channels": [
        {
          "channel": "automatic-ingestion",
          "configured": true,
          "state": "Active",
          "verificationState": "Verified",
          "activityState": "Succeeded",
          "lastVerifiedAt": "2026-01-01T00:00:00Z",
          "lastActivityAt": "2026-01-01T00:00:00Z",
          "lastSuccessfulActivityAt": "2026-01-01T00:00:00Z",
          "failureCode": null,
          "updatedAt": "2026-01-01T00:00:00Z"
        }
      ]
    }
  ]
}
```

Connection states are `Unknown`, `Configured`, `Verified`, `Active`,
`Degraded`, and `Disconnected`. Lack of recent activity does not change a
configured channel into a disconnected channel. Reading requires
`agent.connection.read`; reporting requires `agent.connection.write`.

## External publication control

```http
GET  /api/external-publication/status
GET  /api/external-publication/memory-items/{id}
POST /api/external-publication/memory-items/{id}/approve
POST /api/external-publication/memory-items/{id}/revoke
```

These endpoints operate on the local publication lifecycle. Approval is
accepted only for public, agent-visible, non-expired safe memory. It writes a
versioned safe-projection envelope to the local durable outbox; it does not
connect to a cloud service. Revoke queues a tombstone without title, safe
summary, expiration, or provenance body fields. Repeated approval or revocation
returns the existing state without creating another revision.

The initial upsert envelope exports the independently classified `safeSummary`.
`title` and `coreTags` are reserved DTO fields but remain empty because the
current memory intake classifier does not independently classify them for
external publication.

Example item status:

```json
{
  "memoryItemId": "memory-1",
  "publicationState": "ApprovedForExternal",
  "revision": 2,
  "updatedAt": "2026-07-13T00:00:00Z",
  "decidedAt": "2026-07-13T00:00:00Z",
  "syncState": "Pending"
}
```

The aggregate status reports `connectionState: Disabled` in this repository.
Reads require `external-publication.read`; approval and revocation require
`external-publication.write`.

## Health

The API host serves the self-host operator console at `/`.

```http
GET /healthz
```

Liveness only. It does not touch PostgreSQL and should stay live while dependencies are unavailable.

Returns:

```json
{ "status": "ok" }
```

```http
GET /readyz
```

Readiness checks the configured database dependency.

Ready response:

```json
{ "status": "ready", "dependency": "database" }
```

Unavailable database response:

```json
{ "status": "not_ready", "dependency": "database" }
```

## Classification preview

```http
POST /api/classification/preview
```

Request:

```json
{
  "sourceId": "source-1",
  "content": "Public implementation note.",
  "sourceType": "note"
}
```

The response returns classification metadata and a storage decision. It does not expose Vault raw content.

## Operator classification provider

```http
GET  /api/operator/classification-provider
PUT  /api/operator/classification-provider
POST /api/operator/classification-provider/test
```

These operator-only endpoints read, save, and test the active classification
provider configuration. All three require the `config.write` service-token
scope. Supported provider values are `Mock`, `ExternalHttp`, `OpenAi`,
`Anthropic`, `GoogleAi`, and `OpenRouter`.

Save request:

```json
{
  "provider": "ExternalHttp",
  "model": "",
  "endpoint": "https://provider.example/classify",
  "authHeaderName": "Authorization",
  "apiKey": "operator-supplied-secret",
  "clearApiKey": false
}
```

Responses include `provider`, `model`, `endpoint`, `authHeaderName`,
`payloadClass`, `redactionState`, `hasApiKey`, `providerBoundary`,
`localSensitiveDataGuardActive`, and `localSensitiveDataGuardVersion`. They
never return the API key or detector matches. `ExternalHttp` reports the
`self-hosted-capable-external-http` boundary.
The test endpoint accepts optional `content` and `sourceType`, runs the current
provider and policy engine, and returns the safe configuration view,
classification, and storage decision. Save and test operations write
metadata-only audit events.

## Operational metrics export

```http
GET /api/operator/metrics
GET /api/operator/metrics/export
```

These local operator endpoints require the `metrics.read` service-token scope.
They return the same bounded JSON snapshot; `/export` supplies it as a download.
The snapshot contains only aggregate, low-cardinality classification-provider
attempt duration/outcome, sensitive-access request/decision throughput, and
safe-search candidate-pressure values. It never contains query text, memory or
source identifiers, actor identities, prompts, raw content, paths, or tokens,
and it does not create an external-publication job.

## Source intake

```http
POST /api/sources
```

Request:

```json
{
  "sourceSystem": "local",
  "sourceType": "note",
  "content": "Public onboarding checklist.",
  "title": "Contributor onboarding",
  "safeSummary": "Public onboarding checklist for local contributors.",
  "coreTags": ["onboarding", "public"]
}
```

The endpoint computes a `sha256:` content digest and persists the digest, not the raw `content`, in the source event record. It classifies `content`, `title`, `safeSummary`, and every `coreTags` entry as one complete projection, runs the policy engine, persists the normalized classification result, and writes metadata-only audit events for provider invocation and the intake decision.

If policy allows wiki projection, the endpoint creates a wiki proposal from `title`, `safeSummary`, and `coreTags`. Agent context is allowed only when the storage decision allows it, and context-pack responses are limited to public agent-allowed wiki proposals. For sensitive records, intake does not persist caller-provided `safeSummary` as approved output; a decider can attach reviewed redacted output only during approval.

If policy routes content to sensitive storage, the endpoint creates only a sensitive record reference for the source event and does not create an agent-visible wiki proposal.

### Plugin ingestion contract

Plugins for email, messenger, documents, local files, and agent chat sources
should normalize their source metadata before calling Luthn source intake.
The plugin envelope is metadata-only and should include:

- `sourceIdentity`: plugin id, source system, source kind, external source id,
  and optional display name
- `consent`: consent kind, actor, and timestamp
- `contentDigest`: a `sha256:` digest for the payload
- `payloadClass`: `RawSource`, `RedactedSummary`, `MetadataOnly`, or
  `BinaryDigestOnly`
- `retry`: attempt count, max attempts, optional next attempt time, and optional
  error class
- `ordering`: optional partition key, monotonic sequence number, enqueue time,
  and ordered-processing flag for worker-safe sequencing
- `deadLetter`: optional metadata-only reason, time, error class, and diagnostic
  code for exhausted or rejected work items
- `receivedAt`, `coreTags`, optional media type, and optional payload size

The envelope does not replace policy classification and does not make plugin
content agent-visible by itself. Raw content remains an intake input only and is
not persisted in public source records.

Response:

```json
{
  "sourceId": "source-...",
  "sourceEventId": "source-...",
  "classificationResultId": "classification-...",
  "wikiProposalId": "wiki-...",
  "sensitiveReferenceId": null,
  "auditEventId": "audit-...",
  "classification": {
    "sensitivity": "Public",
    "confidence": 0.75,
    "categories": [],
    "containsSensitiveMaterial": false
  },
  "storageDecision": {
    "kind": "WikiCandidate",
    "reasons": ["Content is eligible for wiki-safe review and Core projection."],
    "allowsWikiProjection": true,
    "allowsAgentContext": true,
    "requiresHumanReview": false
  }
}
```

## Agent context pack

```http
POST /api/agent/context-packs
```

Request:

```json
{
  "query": "release runbook",
  "coreTags": ["runbook"],
  "maxItems": 20,
  "projectKey": "luthn",
  "taskKey": "release",
  "topicTags": ["delivery"]
}
```

`query` is optional. When provided, context-pack items are ranked through the
configured safe retrieval backend used by agent search. The default backend is
deterministic in-process ranking. The endpoint returns only public wiki
proposals and public shared-memory records where agent context is explicitly
allowed. When `projectKey` is present, matching and unscoped global records are
eligible while other-project records are excluded before ranking. Exact task
and topic matches and recent safe-projection timestamps receive bounded boosts.
Returned items carry the optional metadata and `projectionTimestamp`.

The MCP `get_context_pack` tool also accepts optional lightweight-recall
controls: `maxTokens`, `timeoutMs`, `cacheKey`, `cacheTtlSeconds`, and
`failOpen`. These controls bound and cache the already safe API response inside
the MCP process; they do not widen the API corpus or expose private records.
Project, task, and topic metadata is part of the cache identity.

## Agent safe search

```http
POST /api/agent/search
```

Request:

```json
{
  "query": "release runbook",
  "coreTags": ["runbook"],
  "maxItems": 20,
  "projectKey": "luthn",
  "taskKey": "release",
  "topicTags": ["delivery"]
}
```

Response:

```json
{
  "query": "release runbook",
  "coreTags": ["runbook"],
  "results": [
    {
      "id": "wiki-...",
      "title": "Release runbook",
      "safeSummary": "Public-safe release steps.",
      "sensitivity": "Public",
      "coreTags": ["runbook"],
      "projectKey": "luthn",
      "taskKey": "release",
      "topicTags": ["delivery"],
      "projectionTimestamp": "2026-07-19T12:00:00Z",
      "score": 1240
    }
  ]
}
```

Search uses the configured safe retrieval backend over public, agent-allowed
wiki proposal and shared-memory titles, safe summaries, `coreTags`, and safe
recall metadata. The
default backend is deterministic in-process ranking. `pgvector` is the first
planned vector provider, but it must index only public-safe projected records.
Search does not search or return raw Vault/source records.

External memory service adapters share this same safe corpus boundary. Adapter
payloads are limited to `public-agent-allowed-safe-projections` with
`metadata-only` payload class and `safe-projection-only` redaction state; they do
not receive raw source content, private memory, or Vault records.

## Safe memory items

```http
POST /api/memory/items
GET /api/memory/items/{id}
POST /api/memory/query
```

`POST /api/memory/items` persists metadata-only shared memory. It accepts safe
optional `projectKey`, `taskKey`, and `topicTags` values in addition to the
existing fields. These values are normalized, included in complete-projection
classification, and must not contain raw paths or sensitive identifiers.
summaries and Core tags, not raw source content:

```json
{
  "title": "Release runbook memory",
  "safeSummary": "Public-safe deployment memory.",
  "sensitivity": "Public",
  "coreTags": ["runbook", "release"],
  "visibility": "SharedAcrossAgents",
  "retentionKind": "Durable",
  "expiresAt": null,
  "sourceSessionId": null
}
```

Response:

```json
{
  "id": "memory-...",
  "title": "Release runbook memory",
  "safeSummary": "Public-safe deployment memory.",
  "sensitivity": "Public",
  "coreTags": ["runbook", "release"],
  "visibility": "SharedAcrossAgents",
  "retentionKind": "Durable",
  "expiresAt": null,
  "sourceSessionId": null,
  "allowsAgentContext": true,
  "createdAt": "2026-01-01T00:00:00Z"
}
```

Read and query endpoints return only public, non-expired, agent-allowed memory
projections. They do not expose private owner memory, restricted shared memory,
raw Vault/source data, or participant-specific private context.

Memory writes are classified before storage. The classifier receives the
combined `title`, `safeSummary`, and every `coreTags` entry. If any field is
sensitive, Luthn keeps the record behind the private memory boundary instead of
making it agent-visible.

## Wiki-safe proposal

```http
GET /api/wiki/proposals/{id}
```

Returns Markdown rendered from safe summaries and redacted source references only.

## Sensitive access requests

```http
GET /api/access-requests?status=Pending&limit=25
POST /api/access-requests
GET /api/access-requests/{id}
GET /api/access-requests/{id}/result
POST /api/access-requests/{id}/approve
POST /api/access-requests/{id}/deny
```

These endpoints create and decide metadata-only sensitive-access requests for existing sensitive record references. They require configured bearer service-token scopes in production/self-host mode and do not return raw Vault/source payloads. Listing and decision operations require the separate trusted `access.decide` scope; create/read operations require only `access.request`. The MCP server exposes only create, status, and result operations—never approval or denial.

List response:

```json
{
  "requests": [
    {
      "id": "access-...",
      "sensitiveReferenceId": "sensitive-ref-...",
      "status": "Pending",
      "requestedBy": "agent-service",
      "sessionId": "session-...",
      "createdAt": "2026-07-04T00:00:00Z",
      "expiresAt": "2026-07-04T00:10:00Z",
      "decidedBy": null,
      "decidedAt": null,
      "redactedOutputAvailable": false
    }
  ]
}
```

Create request:

```json
{
  "sensitiveReferenceId": "sensitive-ref-...",
  "reason": "Need approval for a redacted operational summary.",
  "sessionId": "session-...",
  "expiresInSeconds": 600
}
```

New callers should send both `sessionId` and `expiresInSeconds`. For compatibility
with the pre-expiry unversioned contract, omitted values receive a server-generated
`legacy-...` session id and a 600-second lifetime. Explicit lifetimes must remain
within 60–3600 seconds.

Response shape includes request/decision metadata only:

```json
{
  "id": "access-...",
  "sensitiveReferenceId": "sensitive-ref-...",
  "requestedBy": "agent-service",
  "status": "Pending",
  "redactedOutputAvailable": false
}
```

Approving or denying records decision metadata and audit events. Approval does not create a raw content read path. An approval request may include `redactedSummary`; the server enforces the 4000-character storage limit, reclassifies it, and stores it only when it is public agent-safe. Rejected approval summaries create metadata-only audit events. Approved result delivery is limited to the reviewed summary stored by the approval decision.

Approval request with reviewed output:

```json
{
  "reason": "Approved with reviewed output.",
  "redactedSummary": "Public-safe release steps."
}
```

Result response:

```json
{
  "id": "access-...",
  "sensitiveReferenceId": "sensitive-ref-...",
  "status": "Approved",
  "outputPolicy": "approved-redacted-output-available",
  "redactedOutputAvailable": true,
  "redactedOutput": "Public-safe release steps.",
  "payloadClass": "redacted-output",
  "redactionState": "approved-redacted-output-available",
  "reasons": [
    "Approved limited output is sourced from a public-safe redacted summary."
  ]
}
```

`GET /api/access-requests/{id}/result` is the explicit output policy contract. It requires the request scope and never returns raw Vault/source content. Pending requests use `pending-approval`; expired requests use `expired-no-output`; denied requests use `denied-no-output`; approved requests use `approved-redacted-output-available` only when bounded server-validated output is available, otherwise `approved-redacted-output-unavailable`. Explicit request lifetime is bounded to 60–3600 seconds; expiry records a metadata-only `sensitive_access.expired` audit event. Result reads create `sensitive_access.result_read` audit events whose payload and redaction fields mirror the returned result policy.

## Audit events

```http
GET /api/audit-events?subjectId=access-...&limit=50
```

Returns metadata-only audit entries:

```json
{
  "events": [
    {
      "id": "audit-...",
      "actor": "agent-service",
      "action": "sensitive_access.requested",
      "subjectId": "access-...",
      "payloadVersion": 1,
      "payloadClass": "metadata-only",
      "redactionState": "sensitive-boundary-only"
    }
  ]
}
```

`payloadVersion` identifies the metadata-only audit/control event payload
shape. Version `1` is the current shape; readers should preserve unknown future
versions as metadata and must not assume they include raw source or private
Vault content.

Audit responses must not contain raw source or private Vault content.

## Production auth boundary

Protected API surfaces can require bearer service tokens in production/self-host deployments. Configure token SHA-256 digests and scopes through external configuration such as environment variables; do not commit token values, real deployment digests, or local environment files. Local `Testing` mode remains credential-free unless token options are explicitly configured.

Operator identity is separate metadata, not an authorization bypass. Control-plane clients may send `X-Luthn-Operator` with a short operator label; the API records it in audit actor metadata only after the existing service-token scope check succeeds. The header does not grant scopes and must not contain secrets or raw/private source content.

Use `dotnet run --project src/Luthn.Tools -- token-digest --stdin` to generate the configured `sha256:<hex>` digest from a token supplied on standard input. Do not pass production token values as command-line arguments.

Supported scopes:

- `agent.read`
- `agent.write.summary`
- `agent.connection.read`
- `agent.connection.write`
- `classification.preview`
- `config.write`
- `source.write`
- `memory.write`
- `memory.read`
- `external-publication.read`
- `external-publication.write`
- `access.request`
- `access.decide`
- `audit.read`
- `metrics.read`
- `*`

## Vault boundary

Raw Vault reads are intentionally not exposed by default. Future restricted access should require approval and audit logging before returning limited redacted output.
