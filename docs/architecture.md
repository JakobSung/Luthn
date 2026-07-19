# Architecture Reference

[한국어](architecture.ko.md)

Canonical project direction, runtime boundaries, default agent surfaces, and
review focus live in `docs/project-context.md`.

Use this file only for architecture details that are too specific for the
canonical context.

## Core Model

Initial entity types:

```text
Product
Customer
Inquiry
Job
Approval
Event
Agent
Runner
VaultRecord
KnowledgeItem
Decision
ImplementationResult
WikiDocument
```

Initial relationship types:

```text
SubmittedBy
BelongsToProduct
CreatesJob
RequiresApproval
TriggersRunner
ProducesResult
SummarizedAs
References
DerivedFrom
UpdatesKnowledge
```

Example flow:

```text
Customer
  -> submits -> Inquiry
Inquiry
  -> creates -> Job
Job
  -> requires -> Approval
Approval
  -> triggers -> Runner
Runner
  -> produces -> ImplementationResult
ImplementationResult
  -> updates -> KnowledgeItem
VaultRecord
  -> summarized_as -> KnowledgeItem
KnowledgeItem
  -> projected_as -> WikiDocument
```

## Shared Memory Model

Shared memory is a Core-managed safe projection layer for agents. It models:

- `MemoryActor`: a user, agent, or peer that can participate in memory-bearing
  sessions.
- `MemorySession`: a bounded interaction with an owner participant and optional
  agent/peer participants.
- `MemoryMessage`: safe text from a session message plus source references, not
  raw/private source content.
- `SharedMemoryItem`: a policy-approved memory item with safe summary,
  sensitivity, Core tags, visibility, retention, and optional source session.
- `MemoryConclusion`: a derived statement tied to a memory item and optional
  source message ids.
- `MemoryVisibility`: owner-only, participant-shared, agent-shared, or public-safe
  visibility.
- `MemoryRetentionPolicy`: ephemeral/session memory must expire; durable memory
  must not carry an expiration.

Restricted memory stays private to the owner by default. Wider sharing must be a
future explicit policy workflow, not an implicit model default.

Safe memory APIs persist shared memory as metadata-only records in local or
PostgreSQL storage. API reads expose only public, non-expired,
agent-allowed projections; private, restricted, and raw source/Vault content
remain outside the default read surface. Optional normalized project, task, and
topic metadata is persisted on wiki and shared-memory safe projections. Recall
filters project scope before ranking, preselects bounded candidates newest-first,
and uses `CreatedAt` or `UpdatedAt` for deterministic bounded recency scoring.

## Cloud-Ready Local-First Foundation

Every shared-memory record starts as `LocalOnly`. Agent visibility and external
publication are independent decisions: an agent-safe record is not queued for
external publication until an operator changes it to `ApprovedForExternal`.
`Revoked` creates a new revision and a body-free tombstone so a future remote
adapter can remove the previously published projection.

The safe sync envelope is versioned and identified by `originInstanceId`, local
record id, revision, and operation. The same tuple is the idempotency boundary.
The initial upsert policy exports only the independently classified safe summary,
bounded policy metadata and timestamps. Provenance fields and digests are excluded. Title and
Core tags remain empty until those fields have an independent safe-projection
classification path. Raw source,
private/Vault content, credentials, prompts, transcripts, and local paths have
no fields in the contract.

Publication state, audit metadata, and the durable outbox row are committed
together. The Worker claims ready rows with a bounded lease, retries with
backoff, supersedes unsent older revisions, stores acknowledgements/checkpoints,
and emits revocation tombstones. A newer revision is not sent while an older
revision for the same local record is still processing.
The only transport registered by this repository is `disabled`; it makes no
network request and leaves queued records untouched. A real Luthn Ontology
transport, tenant/auth service, billing, and shared team data plane belong to a
separate commercial repository.

## Plugin Ingestion Contract

Plugin ingestion starts as a metadata contract rather than a new storage path.
Plugins should identify the source, consent basis, digest, payload class, retry
state, received time, and Core tags before handing content to source intake.
Supported source kinds are email, messenger, document, local file, and agent
chat.

Retry state is explicit about readiness and exhaustion so workers can delay
work until `nextAttemptAt` and stop after `maxAttempts`. Ordered processing is
described by a metadata-only partition key and monotonic sequence number, and
dead-letter state records only reason, time, error class, and diagnostic code.

The contract is intentionally digest-first. It can describe raw source payloads,
redacted summaries, metadata-only events, or binary digest-only payloads, but it
does not persist raw content in public records or bypass classification, policy,
Vault, source references, or audit.

## Open Design Questions

- Whether `Luthn.McpServer` can be permissively licensed after legal review.
- First production retrieval/indexing upgrade path.
- Whether `Luthn.Tools` remains long-term after API/MCP coverage matures.
- Hosted service boundaries for Luthn Ontology as a separate commercial service.
