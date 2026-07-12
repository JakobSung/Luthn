# Architecture Reference

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
remain outside the default read surface.

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

## Future Questions

- Exact production auth/token model.
- Whether `Luthn.McpServer` can be permissively licensed after legal review.
- First production retrieval/indexing upgrade path.
- Whether `Luthn.Tools` remains long-term after API/MCP coverage matures.
- Hosted service boundaries for Luthn Ontology as a separate commercial service.
