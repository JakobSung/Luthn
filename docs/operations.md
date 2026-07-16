# Operations

[한국어](operations.ko.md)

This document defines the self-host operational model for schema migration,
backup, restore, and search adoption decisions. Keep it public-safe: do not add
environment-specific secrets, private source records, customer originals, or
one-off run evidence.

## Docker Distribution Lifecycle

The installed `luthn` CLI is the normal operator interface:

```bash
luthn status
luthn update
luthn reset --yes
luthn uninstall
```

The source-free deployment uses a fixed Compose project (`luthn`), fixed
default volumes (`luthn-postgres` and `luthn-operator`), private configuration
under `~/.config/luthn`, runtime under `~/.local/share/luthn`, and update state
plus backups under `~/.local/state/luthn`. Changing the caller directory must
not select another project or empty volume set.

`luthn update` records the running image ID, pulls the target image, refreshes
the lifecycle runtime, stops API and active MCP/adapter write paths, creates a
compressed PostgreSQL backup, runs migrations from the target image, starts the
API, and requires health and readiness before recording success. It never
removes volumes.

Do not use `docker compose down -v` during install, status, or update. Volume
deletion belongs only to `luthn reset --yes` and
`luthn uninstall --purge-data --yes`.

## Self-Host Migration Model

Luthn uses EF Core migrations against PostgreSQL. Operators should treat schema
changes as an explicit maintenance step, not as an implicit API startup side
effect.

### Preflight

Before applying migrations to a database with retained data:

- confirm the deployment image or checkout matches the migration set
- inspect the target image's migration script with
  `docker run --rm --env-file ~/.config/luthn/luthn.env <image> migration-script`
  or use the source-based Tools command during development
- take a database backup outside the repository
- restore that backup into a disposable database when validating backup
  integrity
- keep service-token values and connection strings in the operator secret store
  or runtime environment only

### Backup

For an installed self-host instance, `luthn update` creates the canonical
pre-migration backup automatically. Manual backup uses the installed Compose
bundle:

```bash
docker compose --project-name luthn \
  --env-file "$HOME/.config/luthn/luthn.env" \
  -f "$HOME/.local/share/luthn/compose.yaml" \
  exec -T postgres pg_dump -U luthn -d luthn -Fc > luthn.backup
```

Store backups outside the repository. Do not commit backup files, connection
strings, token values, raw source records, or customer originals.

### Apply

The normal update path applies pending migrations before routing traffic to the
new API:

```bash
luthn update
```

Source-based contributors can still run
`dotnet run --project src/Luthn.Tools -- migrate-db`.

Then verify dependency readiness:

```bash
curl http://localhost:8080/readyz
```

`/readyz` must report `ready` before the instance is treated as ready for
traffic. `/healthz` is liveness only and does not prove the database schema is
ready.

### Restore

Restore only after stopping API and MCP/adapter write paths. For a disposable
or operator-approved local restore:

```bash
compose=(docker compose --project-name luthn \
  --env-file "$HOME/.config/luthn/luthn.env" \
  -f "$HOME/.local/share/luthn/compose.yaml")
"${compose[@]}" stop api
"${compose[@]}" exec -T postgres \
  pg_restore -U luthn -d luthn --clean --if-exists < luthn.backup
"${compose[@]}" run --rm --no-deps migrate
"${compose[@]}" up -d api
curl -fsS http://127.0.0.1:8080/readyz
```

Prefer restore validation before production maintenance windows. A restore plan
is incomplete until the restored database can pass `/readyz`.

### Rollback

Rollback should be planned at the deployment boundary:

- read `PREVIOUS_IMAGE_ID` and `BACKUP_PATH` from
  `~/.local/state/luthn/install-state.env`
- keep the previous API image available locally
- keep the backup taken immediately before migration
- restore the backup if a schema downgrade is required
- avoid manual row edits except as an operator-approved emergency repair
- do not expose raw Vault/source data while diagnosing migration failures

Migration or readiness failure does not trigger an automatic database restore
or API downgrade. The CLI stops the API, restores the configured image
reference to the previous image ID when available, and leaves the backup and
state record for an operator-approved restore. Starting an old API against a
new schema is not assumed safe.

### Event Processing Boundary

Audit/control events are metadata-only. Migration and recovery tooling must not
introduce raw source payload columns, raw Vault read routes, or connector/MCP
tools that bypass the approval and audit boundary.

Ingestion worker retries must use metadata-only scheduling state. A worker can
process an item only when retry state is ready, must preserve partition order
when ordered processing is required, and must move exhausted or rejected items
to dead-letter metadata without storing raw source payloads in the dead-letter
record.

## Search Adoption Model

The default search implementation remains deterministic in-process safe search
over public, agent-allowed wiki records. It is the correct default while the
dataset is small and the safety boundary is more important than recall.

Retrieval backends share a single safe corpus boundary:
`public-agent-allowed-safe-projections`. Deterministic ranking is the default
backend. PostgreSQL `pgvector` is the first vector-provider candidate behind the
same boundary; it must not embed, index, log, or return raw Vault/source records.

Adopt a vector or external search engine only when deterministic safe search is
insufficient and the operator can preserve the same public-safe corpus boundary.

### Candidate Options

| Option | Fit | Operational Cost | Safety Boundary |
|---|---|---|---|
| Deterministic in-process search | Current default, smallest self-host footprint. | No extra service or extension. | Searches only public, agent-allowed wiki records. |
| PostgreSQL `pgvector` | Best first vector option if Luthn already depends on PostgreSQL and operators want one data plane. | Requires extension, embeddings, vector indexes, and migration planning. | Must store embeddings only for public-safe projected records. |
| External search engine | Consider only when query UX, hybrid ranking, typo tolerance, facets, or scale exceed PostgreSQL/in-process search. | Adds another service, backup path, auth boundary, and indexing pipeline. | Index only public-safe projections; never index raw Vault/source records. |

### Adoption Gate

Before replacing deterministic safe search:

- define the searchable corpus as wiki-safe projections only
- prove raw/private source content is not embedded, indexed, logged, or returned
- document backup and restore for the search index
- keep public API results compatible with `id`, `title`, `safeSummary`,
  `sensitivity`, `coreTags`, and `score`
- add tests that search results exclude confidential or agent-blocked records

Until those gates are met, `SEARCH-001` remains an evaluated but deferred
implementation path.

### Search References

- pgvector: PostgreSQL extension supporting exact and approximate vector search
  with HNSW and IVFFlat indexes: <https://github.com/pgvector/pgvector>
- Meilisearch hybrid search: keyword, semantic, and hybrid modes with
  `semanticRatio`: <https://meilisearch.com/docs/capabilities/hybrid_search/overview>
- Typesense vector and hybrid search: keyword and semantic search combined by
  rank fusion: <https://typesense.org/docs/30.2/api/vector-search.html>

## External Memory Service Adapter Boundary

External memory services are optional adapters. They are not a second raw
storage path and must not receive raw source records, private memory, Vault
payloads, sensitive-access decisions, or caller-provided source content.

The adapter contract exports only `public-agent-allowed-safe-projections` with
`metadata-only` payload class and `safe-projection-only` redaction state. A
shared memory item is eligible only when it is public, agent-visible through
`PublicSafe` or `SharedAcrossAgents`, and not expired.

External adapters should treat the exported ID, safe summary, projection kind,
payload class, redaction state, and expiration as the complete initial payload.
Title and Core tags are reserved in the versioned DTO but remain empty until they
have an independent safe-projection classification path. Any service-specific
embedding, indexing, backup, restore, or deletion workflow must preserve this
same safe projection boundary.

### Local outbox operation

The migration adds local installation identity, publication lifecycle columns,
a safe-projection outbox, and transport checkpoints. Existing memory rows are
backfilled as `LocalOnly`, revision `1`, with `UpdatedAt` copied from
`CreatedAt`; the migration does not queue historical data.
When a newer local revision exists, unsent older operations become
`Superseded`; they are retained for bounded audit/deduplication metadata but are
not sent later.

The Compose bundle contains an opt-in Worker profile:

```bash
docker compose --env-file .env --profile sync-worker up -d worker
```

The default Compose stack does not start this profile. Even when started, the
public build registers only the disabled transport, performs no outbound
connection, and leaves pending outbox rows untouched. Do not deploy a real
cloud adapter until its endpoint authentication, tenant isolation, deletion,
backup/restore, and audit boundaries are separately reviewed.
