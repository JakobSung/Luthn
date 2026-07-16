# Data Boundaries Reference

Luthn separates private source data from the safe projections that agents can
retrieve. Agent visibility never implies access to the original record or
permission to publish it externally.

Canonical repository rules live in `docs/project-context.md`. This document
provides the operator-facing model and concrete classification examples.

## Boundary Model

```text
raw intake
  -> classify and apply policy
  -> private or sensitive record stays behind the storage boundary
  -> approved safe projection becomes available to agent APIs and MCP
  -> separately approved public projection may enter the publication outbox
```

- Raw intake may be inspected by the configured classifier, so provider choice
  is part of the data boundary.
- Storage classification and agent visibility are separate decisions. A stored
  record is not automatically agent-visible.
- Agent-facing reads return safe projections, not unrestricted source bodies.
- External publication requires a separate explicit approval even when a
  projection is already visible to agents.
- Revocation or expiry removes eligibility without copying the private source
  into audit or sync records.

Use this file for concrete classification examples.

## Codex Capture And Recall Boundary

On macOS, Linux, and Windows, the trusted Codex Stop hook accepts a bounded
host event and constructs a capsule from the final assistant response only. It
does not read or upload the transcript, user prompts, working-directory path,
or transcript path. Session and turn identifiers are hashed before delivery,
the summary is capped below the API limit, and recognized credential patterns
cause the entire capsule to be dropped locally.

The service token remains in Luthn's protected configuration. It is not copied
into Codex hook configuration, MCP registration, connector state, or
auto-recall instructions. Hook delivery is asynchronous and fail-open, so an
unavailable Luthn service does not block completion of a Codex turn.

Default auto-recall does not expose the private store. It asks the scoped MCP
surface for one small agent-safe context pack at a new task or material topic
change. The same classification, policy, and safe-projection rules apply to
automatic recall and explicit MCP reads.

## Public-Safe Knowledge May Contain

- product names intended for team/public use
- non-sensitive implementation notes
- safe summaries
- redacted source references
- runbooks
- policy-approved project metadata safe for agent context

## Sensitive Store Must Contain

- raw customer originals
- raw contracts, quotes, payment, tax, finance, and accounting records
- raw private emails/messages
- credential-bearing operational material
- unredacted incident logs containing private operational data
- any record that policy marks as private-only

## Provider Boundary

- The mock classifier is local, credential-free, and limited to tests and local
  experiments when no external provider is configured.
- Operator-configured provider secrets are server-side only; console/API
  responses expose only whether a key is present.
- External classification is explicit opt-in configuration.
- Direct third-party LLM providers (`ChatGPT API`, `Claude API`, `Google AI
  API`, and `OpenRouter API`) receive raw source content in the classification
  prompt before Luthn assigns sensitivity. Use them only when the operator
  accepts that external transfer; use a controlled `External HTTP` provider when
  raw intake content must stay in a self-hosted boundary.
- Direct third-party LLM provider endpoints must be HTTPS URLs on the expected
  provider host before Luthn sends the API key header.
- Provider calls carry payload class and redaction state metadata so intake
  remains auditable.
- Audit rows record provider boundary metadata and storage decisions, not raw
  source content.
- Normal automated tests must use mock or fake providers unless an integration
  test is explicitly enabled by an operator.

## External Publication Boundary

- Agent visibility does not imply permission to publish externally.
- Only an explicitly approved public, non-expired, agent-visible safe
  projection can enter the external-publication outbox.
- Revocation records contain identity, revision, operation, and bounded policy
  metadata only; they do not repeat the projection body.
- Raw source/Vault data, private memory, credentials, prompts, transcripts,
  local paths, and sensitive-access output must never enter the sync contract.
- The public self-host build has no active cloud transport and performs no
  external sync by default.
