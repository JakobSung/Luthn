# Luthn Project Context

[한국어](project-context.ko.md)

This is the minimal committed architecture and safety context for contributors
and reviewers. Keep it public-safe and stable. Do not record development
status, private planning, analysis notes, run evidence, PR metadata, or
internal sequencing here.

## Document Policy

- `README.md` and `README.ko.md` are product-facing: philosophy, architecture,
  setup, and usage.
- `docs/project-context.md` is the committed safety and reference index.
- Generated `plan`, `architecture`, `analysis`, `review`, `report`,
  `handoff`, and `evidence` Markdown is private by default and must stay under
  ignored local paths unless the maintainer explicitly asks to commit it.
- Committed docs should describe the product or durable technical contracts, not
  the development process.

## Product Boundary

Luthn is a self-hostable shared memory layer for AI agents. It classifies
sensitive data and lets multiple agents share only policy-approved memory and
context.

- Agents read Core-filtered shared memory, context packs, and wiki-safe Markdown
  by default.
- Raw/private records stay behind Vault, policy, controlled access, and audit.
- The local operator console is the authority for sensitive-access review in
  Local and Hub modes. Operator detail may show only bounded safe reference
  metadata and redacted summaries; approve/deny decisions require an explicit
  reason and never expose raw Vault/source content.
- Sensitive-access approval and external-publication approval are independent
  decisions. Audit is a metadata-only investigation trail, not a content
  recovery or backup surface.
- Wiki Markdown is a projection over Core-managed knowledge, not the source of
  truth.
- Local/PostgreSQL storage is the default self-host memory path; external memory
  services are optional adapters behind Luthn policy.
- Local-only operation is the invariant. External publication requires an
  operator action and exports only a versioned public-safe projection through a
  durable local outbox. The public repository contains no active cloud client.
- The approved team topology uses one central OSS Hub per Organization, not one
  full installation per member PC. The public runtime now implements the
  opt-in Hub data-plane baseline: encrypted durable ingress, server-derived
  Workspace identity, bounded processing, dead-letter/replay, aggregate status,
  and disabled/fake relay boundaries. Cloud enrollment, Cloud identity control,
  and real outbound transport remain outside this repository. See
  [`cloud-hub-data-plane.md`](cloud-hub-data-plane.md).
- Local self-host smoke flows should run without provider credentials.
- The repository must remain safe to expose: no credentials, private source
  records, customer originals, local agent artifacts, local planning state, or
  run evidence.

## Runtime Shape

```text
Raw/private source
  -> Intake
  -> Classification + policy
  -> Vault / Core graph / shared memory / Wiki projection / Ignore / NeedsReview
  -> Agent API returns Core-filtered, wiki-safe memory and context
```

Optional future team sharing follows a separate boundary:

```text
Approved shared memory
  -> explicit external-publication approval
  -> versioned safe projection in local durable outbox
  -> disabled transport boundary
  -> future commercial cloud adapter outside this repository
```

The team Hub extension adds an asynchronous ingress and classification boundary
before the existing publication outbox. Member Agents use Cloud-issued remote
MCP/OAuth plus an Agent-native lifecycle integration rather than installing the
full runtime on every PC.

Runtime projects:

- `src/Luthn.Core/`
- `src/Luthn.Core.Persistence/`
- `src/Luthn.Host.Api/`
- `src/Luthn.Host.Worker/`
- `src/Luthn.Tools/`
- `src/Luthn.Sdk/`
- `src/Luthn.AgentConnector.Http/`
- `src/Luthn.McpServer/`

## Hard Rules

- Use `Core` for the implemented knowledge model.
- Use `coreTags` for Core-filtered context selection.
- Do not add raw Vault/source read routes, connector methods, or MCP tools by
  default.
- Keep sensitive-access and audit responses metadata-only by default. The only
  limited output exception is the explicit operator-approved, server-validated
  redacted summary returned by the sensitive-access result contract.
- Keep sensitive or non-agent-visible shared-memory user fields in the
  authenticated protected payload store. Keep its key ring outside PostgreSQL,
  and never expose ciphertext through agent, sync, publication, audit, log, or
  metric contracts.
- Store one immutable, versioned collection-provenance record atomically with
  every new source event or shared-memory item. Keep caller claims distinct
  from server-derived actor and owner identity, and expose provenance only to
  an authorized same-owner reader or explicit operator.
- Treat owner identity as server-derived authorization state. Filter every
  agent-safe persistence query, ranking path, idempotency key, publication,
  sensitive-access path, and retrieval cache by that owner before returning or
  reusing data.
- Keep `Luthn.McpServer` connector-side over HTTP; do not wire it directly to
  Core.
- Do not add one-off console apps. Prefer API endpoints, hosted services, MCP
  tools, SDK/client libraries, or bounded `Luthn.Tools` subcommands.

## Review Triggers

Treat these as high-risk changes:

- auth, authorization, service-token scopes
- sensitive-access, audit, raw-source, Vault, classification policy
- persistence or EF Core migrations
- MCP or agent boundary changes
- operator console token handling
- generated document or local artifact visibility

## Validation Profiles

Product code:

```bash
dotnet build Luthn.sln --no-restore
dotnet test Luthn.sln --no-restore
docker compose config
git diff --check
```

## References

- `README.md`: product overview and usage.
- `docs/api.md`: endpoint contracts and example payloads.
- `docs/local-development.md`: local run, Docker, migration, token digest, and
  smoke commands.
- `docs/operations.md`: self-host migration, backup/restore, and search
  adoption model.
- `docs/releases.md`: agent-neutral container release, installation channel,
  and immutable digest contract.
- `docs/agent-quickstart.md`: agent and MCP connection path.
- `docs/licensing.md`: package license boundary.
- `docs/architecture.md`: Core model reference.
- `docs/cloud-hub-data-plane.md`: implemented opt-in central Hub data-plane
  baseline, queue/identity/capacity evidence, and the remaining Cloud boundary.
- `docs/project-structure.md`: structure and historical mapping reference.
- `docs/data-boundaries.md`: concrete data classification examples.
- `docs/source-references.md`: source reference shape.
