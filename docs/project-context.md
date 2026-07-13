# Luthn Project Context

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
- Wiki Markdown is a projection over Core-managed knowledge, not the source of
  truth.
- Local/PostgreSQL storage is the default self-host memory path; external memory
  services are optional adapters behind Luthn policy.
- Local-only operation is the invariant. External publication requires an
  operator action and exports only a versioned public-safe projection through a
  durable local outbox. The public repository contains no active cloud client.
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
- Keep sensitive-access and audit responses metadata-only unless a future
  generated plan explicitly implements limited redacted output.
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
- `docs/agent-quickstart.md`: agent and MCP connection path.
- `docs/licensing.md`: package license boundary.
- `docs/architecture.md`: Core model reference.
- `docs/project-structure.md`: structure and historical mapping reference.
- `docs/data-boundaries.md`: concrete data classification examples.
- `docs/source-references.md`: source reference shape.
