# Local Development

[한국어](local-development.ko.md)

## Prerequisites

- .NET SDK matching the solution target framework
- Docker, for the Postgres-backed self-host path

End users should use the source-free [installation guide](installation.md).
The commands below are contributor workflows and intentionally require a
source checkout and the .NET SDK.

## Source-Based One-Command Local Install

For the open-source self-host path, run:

```bash
./scripts/install-local.sh
```

This command creates `.env` from `.env.example` when needed, restores packages,
builds the solution, starts the local PostgreSQL service, applies migrations,
seeds public-safe demo data, and starts the API.

Open the operator console at:

```text
http://localhost:8080/
```

Check the service with:

```bash
./scripts/check-local.sh
```

The check script prints Docker Compose service state, `/healthz`, `/readyz`,
and the operator console URL.

For a credential-free in-memory API setup without PostgreSQL, run:

```bash
./scripts/install-local.sh testing
```

Then start the API with the command printed by the installer.

To remove local Docker state created by the self-host quickstart:

```bash
./scripts/reset-local.sh --yes
```

This deletes local PostgreSQL and operator-console Docker volumes.

## Build and test

```bash
dotnet build Luthn.sln
dotnet test Luthn.sln
```

## Run API with local in-memory test mode

```bash
DOTNET_ENVIRONMENT=Testing dotnet run --project src/Luthn.Host.Api/Luthn.Host.Api.csproj --urls http://127.0.0.1:5089
```

Open the operator console at:

```text
http://127.0.0.1:5089/
```

The console uses the same API host. It can run credential-free in `Testing`
mode and can attach an operator-supplied bearer service token for protected
self-host routes. Current operator workflows cover health/readiness, read-only
agent connection status, classification preview, controlled source intake,
sensitive-access request review, approved-result state, approve/deny decisions,
and purpose-oriented metadata-only audit investigation. Select a sensitive
request before deciding it; the console loads only the operator-detail
allowlist and requires an explicit decision reason. The audit center provides
sensitive-access, classification-failure, configuration-change, publication,
ingress, worker, and retention presets plus bounded custom metadata filters. It
is not a raw-content viewer.

The opt-in Hub baseline is disabled by default. To exercise it locally, use
`MultiUser` identity with server-bound Hub scopes, then enable
`Luthn__Hub__Ingress__Enabled=true` and optionally
`Luthn__Hub__Ingress__WorkerEnabled=true`. Ingress encrypts the bounded capsule,
derives organization/workspace/member/agent/session identity from the trusted
token, and returns only a metadata receipt. The disabled/fake relay makes no
external request.

Agent installation, reconfiguration, and disconnect remain host CLI operations.

## Use the operator console

Open `Console access` before choosing a workflow. Development and packaged
personal installs explicitly set `Luthn__Console__LocalOnly=true` and bind the
published port to `127.0.0.1`. On macOS and Linux, an un-enrolled `SingleOwner`
receives a bounded server-side LocalAuto session after the installed Host Helper
approves exactly one explicit HttpOnly browser candidate. `luthn console` remains
the local recovery path and is the current Windows console access path. The browser
does not read, store, or send
a service/decision bearer or bootstrap value. Cookie-authenticated mutations require the
same-origin antiforgery header returned by the Host.

The source self-host installer creates `LUTHN_SERVICE_VALUE` and
`LUTHN_OPERATOR_VALUE` in the ignored, permission-restricted `.env` file. The
source install's operator token is decision-only by default. Packaged installs
keep the equivalent secrets at `~/.config/luthn/service-token` and
`~/.config/luthn/operator-token` (Windows: `%LOCALAPPDATA%\\Luthn\\config\\service-token`
and `operator-token`). Do not print or commit these files.

Those credentials remain necessary for agents and direct API clients. They are
not human-console sessions and are not upgraded into one.

Use the menu by task:

- **Overview**: deployment boundary, health/readiness, and connector status.
- **Access approvals**: inspect bounded operator detail, then approve or deny
  with an explicit reason. No raw Vault/source payload is shown.
- **Publication**: handle the separate external-publication decision path.
- **Classify & intake**: preview classification or submit a safe source intake.
- **Audit center**: investigate metadata-only events with presets, filters,
  cursor pagination, and export.

If a direct bearer client returns `403`, keep the credential value unchanged and
add the scope required by that client to the server-configured token. Never solve a permission
error by putting a broader token into an agent connector.

## Run Docker self-host stack

```bash
docker compose up --build
```

Then check:

```bash
curl http://localhost:8080/healthz
curl http://localhost:8080/readyz
```

`/healthz` is liveness only and does not touch PostgreSQL. `/readyz` checks the configured database dependency.
It also reports first-run configuration checks for service tokens,
classification provider readiness, and transport hardening. In production,
readiness is not considered complete when no active service token is configured.
The repository Compose defaults to `LocalDeterministic`.

The Docker stack also serves the operator console at `http://localhost:8080/`.

## Production service tokens

Production/self-host deployments can require bearer service tokens for protected API surfaces by setting `Luthn:Auth:RequireServiceToken=true` and supplying token SHA-256 digests through external configuration. Do not commit token values or digest-bearing production configuration.

Generate a digest without passing the token as a command-line argument:

```bash
printf '%s' "$LUTHN_SERVICE_VALUE" \
  | dotnet run --project src/Luthn.Tools -- token-digest --stdin
```

The command prints a `sha256:<hex>` value for external configuration. Keep the original token in the operator secret store or runtime environment only.

Operator identity is optional metadata for self-host control-plane audit clarity.
Send `X-Luthn-Operator` with a short operator label when you want audit actor
fields to distinguish the human/local operator from the bearer service token.
This header does not grant authorization and is only recorded after the existing
service-token scope check succeeds.

Example environment variable shape:

```bash
Luthn__Auth__RequireServiceToken=true
Luthn__Auth__Tokens__0__Name=agent-service
Luthn__Auth__Tokens__0__Sha256Digest=sha256:<hex digest from operator secret store>
Luthn__Auth__Tokens__0__Scopes__0=agent.read
Luthn__Auth__Tokens__0__ExpiresAt=2026-12-31T23:59:59Z
```

Fresh installs use the backward-compatible single-owner identity boundary:

```bash
Luthn__Identity__Mode=SingleOwner
Luthn__Identity__SingleOwnerUserId=local-owner
Luthn__Auth__Tokens__0__UserId=local-owner
Luthn__Auth__Tokens__0__WorkspaceId=default
Luthn__Auth__Tokens__0__ActorKind=Agent
Luthn__Auth__Tokens__0__IsOperator=false
```

For a local multi-user deployment, switch the mode and bind every non-operator
product token to one bounded user ID. IDs are lower-cased and may contain ASCII
letters, digits, `.`, `_`, `:`, `@`, and `-`; the first character must be a
letter or digit and the maximum is 128 characters. Missing or invalid bindings
return `503`, and caller JSON cannot override them.

```bash
Luthn__Identity__Mode=MultiUser
Luthn__Auth__Tokens__0__UserId=alice
Luthn__Auth__Tokens__0__WorkspaceId=team-alpha
Luthn__Auth__Tokens__0__ActorKind=Agent
Luthn__Auth__Tokens__0__IsOperator=false
Luthn__Auth__Tokens__1__Name=local-operator
Luthn__Auth__Tokens__1__UserId=operator
Luthn__Auth__Tokens__1__WorkspaceId=team-alpha
Luthn__Auth__Tokens__1__ActorKind=Service
Luthn__Auth__Tokens__1__IsOperator=true
```

Use a distinct least-privilege token per user or connector. Bind tokens that
share team data to the same `WorkspaceId`; tokens bound to other workspaces stay
isolated. `IsOperator=true` does not bypass the product-data workspace boundary.
The `X-Luthn-Operator` header remains audit metadata and never grants that role.
Verify `/readyz` after any identity configuration change.

Supported scopes include `agent.read`, `agent.write.summary`,
`agent.connection.read`, `agent.connection.write`, `classification.preview`,
`config.write`,
`external-publication.read`, `external-publication.write`, `source.write`,
`memory.read`, `memory.write`, `access.request`,
`access.review`, `access.decide`, `audit.read`, `metrics.read`, `metrics.write`, and `*` for operator-controlled admin use. Local
`Testing` mode remains credential-free unless token options are configured.
`ExpiresAt` is optional. Expired tokens are ignored by the authorization filter
and make `/readyz` fail when no other active token is available.

## Classification provider configuration

The operator console configures `LocalDeterministic` or optional `LocalHttp` at
`/api/operator/classification-provider` and can run a provider test. Commercial
providers, credentials, model names, and authentication headers are not
supported. `LocalHttp` accepts only absolute HTTP(S) endpoints on `localhost`,
IPv4 or IPv6 loopback, or `host.docker.internal`; redirects fail closed.

The packaged and Compose runtime defaults to `LocalDeterministic`, so a new
installation works immediately without a model process or network call:

```bash
Luthn__Classification__Provider=LocalDeterministic
```

Provider HTTP calls use bounded runtime defaults so a stalled classifier does
not hold API requests open indefinitely:

```bash
Luthn__Classification__Runtime__TimeoutSeconds=30
Luthn__Classification__Runtime__MaxAttempts=2
Luthn__Classification__Runtime__RetryDelayMilliseconds=200
```

Only transient provider failures such as timeout, HTTP 408, HTTP 429, and HTTP
5xx are retried. Provider failure details returned to clients do not include
provider response bodies.

The Host API records .NET metrics for classifier attempts, retries, failures,
and safe-search candidate counts:

- `luthn.classification_provider.attempts`
- `luthn.classification_provider.retries`
- `luthn.classification_provider.failures`
- `luthn.safe_search.candidates`

Use these metrics to decide when deterministic full-corpus ranking needs the
next `pgvector` or DB-backed candidate-selection slice.

### Classification golden evaluation

Run the versioned synthetic Korean-majority corpus against `LocalDeterministic` with
no network request:

```bash
dotnet run --project src/Luthn.Tools -- classification-eval
```

Write the same stable JSON report to a file when an artifact is needed:

```bash
dotnet run --project src/Luthn.Tools -- classification-eval \
  --output artifacts/classification-eval.json
```

Exercise the local deterministic guard combined with the local baseline, still
without making a network request:

```bash
dotnet run --project src/Luthn.Tools -- classification-eval \
  --provider guarded-local
```

To evaluate a same-device Host API, start it on an allowed local URL. Pass only
an environment variable name for a protected API token; do not place the token
value on the command line:

```bash
export LUTHN_EVAL_TOKEN='<operator-provided-token>'
dotnet run --project src/Luthn.Tools -- classification-eval \
  --provider local-http \
  --api-url http://127.0.0.1:5089 \
  --token-env LUTHN_EVAL_TOKEN
```

The report intentionally omits corpus text and reports bounded case IDs,
per-case classification/routing comparisons, and aggregate mismatch counts.

The runtime combines every `LocalHttp` result with local deterministic guard
version `1`. Provider failures remain fail-closed and do not fall back to
detector-only storage. The local endpoint receives source id, source type,
content, payload class, and redaction state, and returns sensitivity,
confidence, categories, and `containsSensitiveMaterial`.

```bash
Luthn__Classification__Provider=LocalHttp
Luthn__Classification__LocalHttp__Endpoint=http://host.docker.internal:11434/classify
```

Legacy commercial, `Mock`, `ExternalHttp`, and remote `LocalHttp` settings become
`Unconfigured`; endpoint, model, authentication, and credential fields are
cleared without decrypting or using secrets.

Operator console settings are stored under `.luthn/operator` by default. Override
that location with:

```bash
Luthn__OperatorConfig__Directory=/var/lib/luthn/operator
```

Persist this directory in container deployments if provider settings should
survive restarts.

The external provider response shape is:

```json
{
  "sensitivity": "Confidential",
  "confidence": 0.92,
  "categories": ["contract"],
  "containsSensitiveMaterial": true
}
```

## PostgreSQL migrations

The current EF Core migration creates the public-safe persistence schema from an empty PostgreSQL database. It stores digests, safe summaries, Core tags, and sensitive-record references only; it does not add raw Vault/source content columns.

Apply migrations with the existing tools host:

```bash
dotnet run --project src/Luthn.Tools -- migrate-db
```

Print an idempotent schema script to stdout:

```bash
dotnet run --project src/Luthn.Tools -- migration-script
```

For model changes, install `dotnet-ef` outside the repository and add migrations in `src/Luthn.Core.Persistence`:

```bash
dotnet ef migrations add <Name> \
  --project src/Luthn.Core.Persistence/Luthn.Core.Persistence.csproj \
  --startup-project src/Luthn.Core.Persistence/Luthn.Core.Persistence.csproj \
  --context LuthnDbContext \
  --output-dir Persistence/Migrations
```

Current audit/control event rows include `PayloadVersion` with a database
default of `1`. The field is metadata-only and exists so future event payload
shapes can be read without changing existing audit consumers.

The current schema also includes operational indexes for public-safe wiki
projection search, shared-memory search, sensitive-access queue filtering, and
subject-scoped audit reads. These indexes support the MVP deterministic
retrieval path while leaving the later `pgvector` candidate-selection slice
separate.

## Optional PostgreSQL integration smoke

The default test suite does not reset or require a local PostgreSQL database.
To run the opt-in migration and `/readyz` smoke test, point it at a disposable
database whose name starts with `luthn_test` and explicitly allow reset:

```bash
LUTHN_POSTGRES_TEST_CONNECTION='Host=localhost;Port=5432;Database=luthn_test;Username=luthn' \
LUTHN_POSTGRES_TEST_ALLOW_RESET=true \
dotnet test tests/Luthn.Host.Api.Tests/Luthn.Host.Api.Tests.csproj --filter PostgresIntegrationSmokeTests
```

The test drops and recreates the configured disposable database.

## Backup and restore notes

The durable self-host migration and recovery model lives in
`docs/operations.md`. Keep database backups outside the repository and do not
commit them. For the local Docker stack:

```bash
docker compose exec postgres pg_dump -U luthn -d luthn -Fc > luthn.backup
docker compose exec -T postgres pg_restore -U luthn -d luthn --clean --if-exists < luthn.backup
```

Take a backup before applying new migrations to a database that contains data. Restore into a disposable database first when validating backup integrity.

## Docker Compose production caveats

The provided Compose file is a local self-host smoke stack, not a production template.

- It uses local development defaults and a single PostgreSQL volume.
- It does not configure production authentication, TLS, secret storage, high availability, monitoring, or managed backup retention.
- Do not commit deployment credentials or key-bearing connection strings.
- Replace local trust-style PostgreSQL access before exposing the stack beyond a private development machine.
- Run `migrate-db` before routing production traffic, and use `/readyz` for dependency-aware readiness checks.
- Configure the production host transport explicitly. For direct TLS
  termination in Kestrel use `Luthn__Host__EnforceHttps=true`; behind a reverse
  proxy use `Luthn__Host__EnableForwardedHeaders=true` and configure the proxy
  boundary so scheme and remote IP are trustworthy. `TrustAllForwardedHeaders`
  exists only for tightly controlled private-network smoke environments and
  makes `/readyz` report a warning in production.
- Request timeout and rate limit defaults are configurable with
  `Luthn__Host__RequestTimeoutSeconds`, `Luthn__Host__RateLimitPermitLimit`,
  and `Luthn__Host__RateLimitWindowSeconds`.

## Tools smoke commands

Do not add more console apps for one-off workflows; consolidate bounded admin/diagnostic commands into this tools host or expose product behavior through API/MCP.

```bash
dotnet run --project src/Luthn.Tools -- preview source-1 "Public implementation note."
dotnet run --project src/Luthn.Tools -- context
dotnet run --project src/Luthn.Tools -- wiki-render
dotnet run --project src/Luthn.Tools -- migrate-db
dotnet run --project src/Luthn.Tools -- migration-script
dotnet run --project src/Luthn.Tools -- seed-demo
printf '%s' "$LUTHN_SERVICE_VALUE" | dotnet run --project src/Luthn.Tools -- token-digest --stdin
```

`seed-demo` applies pending migrations first, then writes only public-safe demo context records to the configured Luthn database. It is intended for the Docker self-host path where PostgreSQL is available on `localhost:5432`.

## MCP skeleton smoke command

```bash
LUTHN_BASE_URL=http://localhost:8080 \
  dotnet run --project src/Luthn.McpServer -- --list-tools
```

## Public-Safety Check

Before committing, verify that local runtime configuration files,
development-agent artifacts, private source records, and key-bearing
configuration are not staged.
