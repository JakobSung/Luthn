# Source References

[한국어](source-references.ko.md)

Source references connect safe knowledge records back to private source records without exposing raw content by default.

## Repository rule

Keep only public-safe product architecture, data boundaries, and implementation code that belongs to Luthn itself.

Do not commit private source documents, development-agent coordination notes, handoff payloads, run evidence, local workspace paths, local orchestration files, or raw sensitive source material.

## Source reference shape

A source reference should contain:

- `sourceId`: the canonical public source identifier for API and SDK contracts
- `sourceType`: a public-safe source class such as `source-event`
- `referenceKind`: the projection kind, such as `redacted-summary`
- `redactionState`: the public-safe redaction state, such as
  `safe-projection-only`
- sensitivity level on the containing Core/wiki projection
- audit metadata by ID or subject only
- safe or redacted summary when allowed by the API contract

It should not contain raw private content unless the record is inside the protected Vault boundary.

## Core relationship

Core-managed records may refer to source references by ID. Agent-facing projections should use redacted summaries or safe labels only.

## Public ID naming

Use `sourceId` as the canonical public source identifier in new API, SDK,
connector, and MCP contracts. `sourceEventId` may remain in existing response
payloads as a backward-compatible alias for the same value, but new caller input
should use `sourceId`.
