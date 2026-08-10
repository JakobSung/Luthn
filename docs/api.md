# API

[한국어](api.ko.md)

## Naming note

The public API should use `coreTags` for Core-filtered context selection and
`sourceId` for public source identifiers.

The current request/response contract uses `coreTags`; reserved legacy tag
aliases are not part of the public API. Source intake responses include
`sourceEventId` as a backward-compatible alias, but `sourceId` is the canonical
public source identifier for new API, SDK, connector, and MCP contracts.

## Server-derived workspace boundary

`WorkspaceId` is the security and sharing boundary for protected product data.
The server derives workspace, user, actor kind, and actor ID from the matched
service-token configuration; request bodies, SDKs, connectors, and MCP tools
expose no workspace or owner override. `OwnerUserId` and
`AuthenticatedUserId` remain attribution and compatibility fields, not query
boundaries.

`SingleOwner` uses the `default` workspace. Existing `MultiUser` tokens without
an explicit workspace retain compatibility through `personal:{userId}`. To
share team data, bind distinct user and agent tokens to the same `WorkspaceId`
in server configuration. Memory, source, wiki, sensitive-access, publication,
safe-search, context-pack, and turn-summary idempotency are workspace-scoped;
`SharedAcrossAgents` means agents authenticated for that workspace. Operators
do not bypass the product-data workspace boundary and must be explicitly bound
to the workspace they administer. `/readyz` reports invalid or missing identity
bindings separately from service-token health.

Audit events now carry a `ScopeKind` (`Workspace` or `Installation`), the
server-derived `WorkspaceId` when applicable, actor attribution, bounded
subject/outcome metadata, and an optional opaque `CorrelationId`. The default
audit listing returns only workspace events for the caller's bound workspace;
operators can request installation events with `scope=installation`. Legacy
rows receive the `default` workspace during migration and remain metadata-only.
Provenance reads are limited to the caller's workspace.

## Agent turn-summary intake

```http
POST /api/agent/turn-summaries
```

Agent adapters can send bounded turn summaries after a conversation turn or
small batch of turns. This endpoint is for summaries, not raw transcripts.
Luthn classifies the submitted summary, resolved title, and Core tags as one
agent-visible projection before it becomes shared memory.

Newly accepted turn summaries use `Ephemeral` retention. Their `expiresAt` is
the server receipt time plus `Luthn:Memory:AutomaticTurnRetentionDays`, which
defaults to 30 days and accepts values from 1 through 365. In Docker
configuration, set `Luthn__Memory__AutomaticTurnRetentionDays`. Expired
summaries are excluded from recall and search at or after `expiresAt`.

The default API runtime also prunes eligible expired automatic turn capsules.
Cleanup is enabled by default, runs every 60 minutes, and processes at most 100
records per batch. It deletes only `Ephemeral` memory linked by immutable
provenance to a `turn-summary` source event when the memory remains `LocalOnly`
and has no safe-sync outbox history. The memory row, encrypted payload,
provenance, classification, and source event are removed in one transaction.
Prior audit events remain and one metadata-only
`turn_summary.retention.pruned` event records the cleanup. Configure the loop
with `Luthn__Memory__AutomaticTurnCleanupEnabled`,
`Luthn__Memory__AutomaticTurnCleanupIntervalMinutes` (1 through 1440), and
`Luthn__Memory__AutomaticTurnCleanupBatchSize` (1 through 1000). Existing
Durable rows are not migrated or rewritten.

This automatic-ingestion policy is separate from explicit
`POST /api/memory/items` writes. Curated memory continues to use the requested
`Durable`, `Session`, or `Ephemeral` retention contract without being changed
by the automatic-turn settings. Manually created Ephemeral memory, non-turn
sources, externally approved or revoked memory, and any outbox-linked record
are excluded from automatic physical cleanup.

Request:

```json
{
  "sessionId": "session-1",
  "turnId": "turn-12",
  "sourceAgent": "codex",
  "summary": "Published release note for external contributors.",
  "coreTags": ["release", "codex"],
  "contentDigest": "sha256:...",
  "idempotencyKey": "session-1-turn-12",
  "title": "Codex release note"
}
```

Raw project paths and free-form `sourceMetadata` are rejected. Use the bounded
`projectKey`, `taskKey`, `topicTags`, and structured `provenance` fields instead.

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

When deterministic field redaction can remove every detected high-confidence
sensitive value while preserving a meaningful event, Luthn reclassifies that
safe projection and may store it as `SharedAcrossAgents`. The response
classification and storage decision describe the selected safe projection,
while the source event remains marked as containing sensitive material and the
original title, summary, metadata, and session identifier remain only in the
owner-scoped encrypted payload. Incomplete, still-sensitive, or meaningless
redactions keep the whole turn summary behind the private inert boundary.

## Agent connection observations

```http
GET /api/agent-connections
POST /api/agent-connections/{agentId}/observations
```

Agent connectors report metadata-only state for each supported channel. The
API replaces the latest row for a workspace/agent/channel tuple; it is a status
surface, not a connection event log. Workspace identity is derived from the
matched service token and is never accepted from the observation body. Callers,
including operators, can read and update only rows in their bound workspace.
`workspaceId` keeps otherwise identical agent IDs unambiguous, while
`ownerUserId` records the observation author.

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
      "ownerUserId": "local-owner",
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

The browser console never accepts a raw provider credential. Configure
`Luthn__Classification__Credential` in the server runtime secret environment;
the console sends only non-secret provider settings and the clear-credential
choice. The optional `apiKey` request member remains accepted for existing API
clients, but it is never included in a response.

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
safe-search candidate pressure, request latency/outcome/cache status/result
count, zero-result count, cumulative latency buckets (`10`, `50`, `100`, `500`,
`1000`, `5000`, and `60000` ms), and helpful/unhelpful feedback. Metrics are in-memory
and reset when the API process restarts. It never contains query text, memory or
source identifiers, actor identities, prompts, raw content, paths, or tokens,
and it does not create an external-publication job.

MCP reports its bounded cache and timeout outcomes through
`POST /api/agent/search-telemetry/observations`; explicit feedback uses
`POST /api/agent/search-telemetry/feedback`. Both require `metrics.write`.
Observation accepts only allowlisted surface/outcome/cache values, duration,
result count, and an optional opaque `retrievalId`; when omitted, the response
returns a generated retrieval ID for later feedback. Feedback accepts only the
opaque ID and `helpful` or `unhelpful`. The local aggregate snapshot and
database do not store event rows, query content, tags, or projection data.
The host also exposes a vendor-neutral `ActivitySource` named
`Luthn.Host.Api` for `retrieval.completed`, `retrieval.observed`, and
`retrieval.feedback` events. No exporter is enabled by default; an
OpenTelemetry host integration can subscribe to that source and receive only
bounded fields plus the opaque retrieval correlation.

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
The response also carries an opaque `retrievalId` that can be used only for
explicit aggregate feedback.

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
combined `title`, `safeSummary`, every `coreTags` entry, and optional recall
metadata. When the deterministic local guard can remove every recognized
sensitive value, Luthn classifies the redacted projection again. A meaningful
public result may remain agent-visible while the original title and summary are
stored only as an authenticated protected payload in the separate
`sensitive_memory_payloads` table. Incomplete redaction, a non-meaningful
remainder, sensitive metadata, or a failed projection classification keeps the
whole item behind the private boundary. That fallback uses
`[protected-memory]` / `[protected-payload]` placeholders with empty tags and
recall metadata in the ordinary row, write response, and search indexes. No
public API returns the ciphertext or decrypts this payload. `/readyz` reports
`sensitive-memory-protection`; protected API routes return `503` when the key
ring or existing ciphertext cannot be verified.

Writes to `/api/memory/items`, `/api/sources`, and
`/api/agent/turn-summaries` accept the same optional structured `provenance`
object:

```json
{
  "provenance": {
    "userId": "owner.one",
    "agentId": "codex",
    "applicationId": "codex.desktop",
    "pluginId": "luthn.hook",
    "connectorId": "luthn.codex.connector",
    "connectorVersion": "2",
    "collectedAt": "2026-07-19T00:00:00Z"
  }
}
```

These values are bounded caller claims. Identifiers are normalized to lower
case, raw paths and free-form source metadata are not accepted, and a client
collection time more than five minutes ahead of server receipt is rejected.
The authenticated service-token actor, authenticated owner user, and
`receivedAt` are always derived by the server and cannot be overridden.

## Collection provenance

```http
GET /api/provenance/source-events/{sourceEventId}
GET /api/provenance/memory-items/{memoryItemId}
```

Both routes require `audit.read`; they are intentionally absent from the MCP
agent tool set and agent-only connector interface. Each source event and memory
item has exactly one versioned immutable provenance row. A turn summary uses
one row linked to both its source event and memory item. `actorTrust` is
`service-token`, `local-runtime`, or `legacy-unknown`; `claimsTrust` is
`caller-supplied`, `no-claims`, or `legacy-unknown`. Existing rows receive a
deterministic version-1 record with unknown claims during migration.
`authenticatedUserId` is the trusted server-derived owner identity;
`claimedUserId` is only caller-reported collection context. Non-operator
`audit.read` tokens can read provenance only for their own owner.

Provenance records collection origin state, while audit events record actions
and decisions over time. Provenance is not copied into audit payloads, agent
recall, search indexes, metrics, encrypted user payloads, safe sync, or external
publication.

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
GET /api/access-requests/{id}/operator-detail
GET /api/access-requests/{id}/result
POST /api/access-requests/{id}/approve
POST /api/access-requests/{id}/deny
```

These endpoints create and decide metadata-only sensitive-access requests for existing sensitive record references, with an optional bounded redacted output after server reclassification. They require configured bearer service-token scopes in production/self-host mode and do not return raw Vault/source payloads. A requester can create and read requests only for its server-derived owner. Listing and operator detail require `access.review`; approval and denial require the separate trusted `access.decide` scope. For existing clients, `access.decide` also implies review. An explicitly configured operator may administer another owner's request while audit records keep only bounded metadata. Create/read operations require `access.request`. The MCP server exposes only create, status, and result operations—never approval or denial.

`GET /api/access-requests/{id}/operator-detail` is a separate `access.review`
contract for local or self-hosted Hub consoles. It returns the request and decision
reasons plus the sensitive reference's existing label, source metadata, and redacted
summary. The response is marked `operator-sensitive-metadata` and
`local-operator-only`; it is not agent-safe and must not enter Cloud safe-projection
sync, logs, metrics, or general audit payloads. Authorization always enforces the
authenticated workspace. A non-operator decider is additionally restricted to its
server-derived owner, while an explicitly configured operator may review other owners
only inside that workspace. Successful reads emit a content-free, metadata-only
`sensitive_access.operator_detail_read` audit event. The response never includes raw
source/Vault data, protected payloads, credentials, workspace ids, or owner ids.

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

## Cloud-neutral synchronization contracts

`Luthn.Sdk` exposes additive version-two DTOs for installation enrollment,
capability negotiation, safe-projection batches, receipts, checkpoints,
bounded errors, and metadata-only audit pages. These are transport-neutral
contracts only: the OSS runtime still registers the disabled sync transport by
default and no Cloud endpoint or credential store is enabled by these types.

Version-two projection payloads deliberately omit Organization, Workspace, and
Installation identity. A receiver derives tenant scope from the authenticated
Installation authority instead of accepting caller-selected tenancy fields.
Each batch item carries an opaque `operationId` that the receiver returns in its
receipt, so acknowledgement and checkpoint advancement never depend on tenant
identity or content fields.
The strict input contract rejects unknown fields, including raw/Vault content,
encrypted payloads, credentials, prompts, transcripts, working directories,
and local paths. The existing `SafeProjectionSyncEnvelopeDto` version-one JSON
shape remains available for compatibility.

## Operator console profile

```http
GET /api/operator/console-profile
```

The read-only profile tells the shared OSS console whether the server is in
`Local` (un-enrolled `SingleOwner`) or `Hub` (`MultiUser` or enrolled) mode. It also returns the fixed
`cloudTransport: disabled`, `sensitiveAuthority: oss-console`, and
`tenancySource: authenticated-request` boundaries. The endpoint accepts no
request body or caller-selected tenant/mode identity and returns no workspace,
organization, installation, owner, or credential fields.

The browser uses only the allowlisted `en` and `ko` language preference for
static labels. Language choice does not change authorization, identity, audit,
or transport state. Sensitive-access approval and external-publication approval
remain separate API and console sections; both continue to use Host APIs rather
than direct database access.

## Console session and Cloud lifecycle boundaries

```http
GET  /api/operator/session
POST /api/operator/session/local/arm
POST /api/operator/session/local
POST /api/operator/session/logout
GET  /api/operator/enrollment
POST /api/operator/enrollment/start
POST /api/operator/enrollment/verify
GET  /api/operator/cloud-login
POST /api/operator/cloud-login
GET  /api/operator/lifecycle
POST /api/operator/lifecycle/reconnect
POST /api/operator/lifecycle/reclaim
```

The browser first receives an unprivileged HttpOnly candidate cookie. The installed
CLI then calls `/local/arm` with its OS-protected operator bearer. Exactly one active
candidate is approved; missing or multiple candidates fail closed. No bearer or raw
bootstrap value enters the browser, URL, or API body. The session cookie is opaque,
server-side, bounded by idle and absolute expiry,
HttpOnly, host-only, and SameSite. Cookie-authenticated mutations require the
same-origin `X-Luthn-CSRF` proof. LocalAuto is limited to an explicitly
local-only, loopback, un-enrolled `SingleOwner`; enrollment activation and Local
reclaim revoke existing authority first. Enrollment, login, lifecycle, and
recovery providers default to disabled. Fake providers are deterministic test
adapters with zero outbound traffic, not production Cloud endpoints.

Cloud login accepts plain HTTP only for a direct, local-only loopback request
with forwarded headers disabled. Every remote or forwarded deployment must use
HTTPS, and Cloud session cookies remain `Secure` in both cases.

These JSON contracts expose bounded state, capabilities, expiry, actions, and
server-derived labels only. They do not accept or return service credentials,
recovery proof values, caller-selected tenant identity, raw/Vault content,
prompts, transcripts, or local paths. Existing bearer-token API clients remain
independent and compatible.

## Audit events

```http
GET /api/audit-events?subjectId=access-...&limit=50&scope=workspace
GET /api/audit-events?category=Access&actionPrefix=sensitive_access.&outcome=approved&from=2026-08-06T00%3A00%3A00Z&to=2026-08-06T23%3A59%3A59Z
GET /api/audit-events/export?category=Access&subjectId=access-...
```

The endpoint supports exact metadata filters for `subjectId`, `action`,
`outcome`, `subjectType`, `actorKind`, and `correlationId`. `from` and `to` are
inclusive UTC timestamps. `actionPrefix` is limited to known event families:
`sensitive_access.`, `operator.classification_provider.`,
`classification.provider.`, `source.intake.`, `turn_summary.`, `memory.`,
`retrieval.`, `processing.`, `transport.`, and `audit.`. `category` accepts
`Access`, `Security`, `Configuration`, `Publication`, `Ingestion`, or
`Retention`. Filters never widen the
authenticated workspace or installation scope. Invalid, non-UTC, oversized,
or unrecognized filters return `400` before the database query runs.

Current `hub.ingress.*` events use the bounded `Security` category and are
queried with `category=Security` plus subject, correlation, and UTC filters;
the action-prefix allowlist does not yet expose a separate Hub family.

Pages are ordered by descending `occurredAt` and ascending `id`. When
`nextCursor` is non-null, pass it back with the exact same filters. The opaque
cursor contains no content or credentials; malformed cursors and cursors reused
with different filters return `400`.

Returns metadata-only audit entries:

```json
{
  "events": [
    {
      "id": "audit-...",
      "scopeKind": "Workspace",
      "workspaceId": "default",
      "actor": "agent-service",
      "actorUserId": "local-owner",
      "actorKind": "agent",
      "action": "sensitive_access.requested",
      "subjectId": "access-...",
      "subjectType": "sensitive_access_request",
      "outcome": "requested",
      "correlationId": null,
      "payloadVersion": 1,
      "payloadClass": "metadata-only",
      "redactionState": "sensitive-boundary-only",
      "category": "Access",
      "retentionClass": "access-365d",
      "retainedUntil": "2027-08-06T08:30:00Z"
    }
  ],
  "nextCursor": null
}
```

`GET /api/audit-events/export` reuses the same authorization and bounded
filters and returns at most 1000 events as a JSON attachment. The export omits
workspace, actor-user, and owner identifiers and declares the
`metadata-only-no-protected-content` boundary. It never exports raw source,
Vault or encrypted payloads, credentials, prompts, transcripts, or local paths.

`payloadVersion` identifies the metadata-only audit/control event payload
shape. Version `1` is the current shape; readers should preserve unknown future
versions as metadata and must not assume they include raw source or private
Vault content.

Audit responses must not contain raw source or private Vault content.

Use audit metadata for a specific operational purpose:

- Before and after a sensitive-access decision, filter by the request
  `subjectId` or the `sensitive_access.` family to verify the review sequence.
- When classification fails, start with `outcome=failed`, then narrow by
  `correlationId` and a UTC time range. Provider-failure audit events remain
  metadata-only and never include the classified content or provider error body.
- When classification behavior changes, use installation scope with the
  `operator.classification_provider.` family to review provider updates and
  tests. Installation scope remains operator-only.

Audit metadata is an accountability and investigation trail, not a content
recovery surface. Do not use it to store or retrieve prompts, transcripts,
credentials, raw source, Vault payloads, or protected memory.

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
- `access.review`
- `access.decide`
- `audit.read`
- `metrics.read`
- `hub.ingress.write`
- `hub.ingress.operate`
- `*`

## Central OSS Hub ingress (opt-in)

The public runtime includes an opt-in Hub data-plane foundation. It is disabled
by default and does not implement a Cloud HTTP transport. A Hub ingress token
must bind `HubOrganizationId`, `WorkspaceId`, `UserId`,
`HubAgentConnectionId`, `HubAgentId`, and `HubSessionId` in server
configuration. The request body cannot select or override those identities.

```http
POST /api/hub/ingress/capsules
Authorization: Bearer <hub-ingress-token>
```

```json
{
  "idempotencyKey": "turn-event-42",
  "contentDigest": "sha256:<64-lowercase-hex>",
  "capsule": "bounded agent lifecycle capsule"
}
```

The server verifies the digest and configured byte limit, protects the capsule
with the OSS Data Protection key ring, atomically persists the queue item and
metadata-only audit event, then returns `202 Accepted`. The receipt contains
only `receiptId`, state, duplicate status, acceptance time, and
`payloadClass=metadata-only`. An identical retry returns the same receipt;
reuse with a different digest returns `409`. Scope capacity or rate saturation
returns `429`, a stable `code`, `retryAfterSeconds`, and `Retry-After` without
acknowledging or dropping the capsule.

The local worker uses bounded Workspace-fair batches, leases, retry/backoff,
dead-letter state, and current-policy replay. Only a workspace-bound operator
with `hub.ingress.operate` can replay a dead letter:

```http
POST /api/hub/ingress/dead-letter/{receiptId}/replay
GET /api/hub/status
```

Hub status is aggregate and metadata-only: admission outcomes, protected queue
bytes/depth/oldest age, processing/retry/dead-letter counts, safe-projection
outbox age/checkpoints, bounded worker durations, and relay state. It omits
workspace, member, Agent, and session identities as well as capsule content,
credentials, prompts, transcripts, and local paths.

## Vault boundary

Raw Vault reads are intentionally not exposed. The implemented restricted-access
workflow requires operator approval and audit logging before returning the
limited, server-validated redacted output described above; an approval never
returns the protected Vault payload itself.
