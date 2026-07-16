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
and metadata-only audit viewing. Agent installation, reconfiguration, and
disconnect remain host CLI operations.

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
readiness is not considered complete when no active service token is configured
or when the mock classifier is still active.

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

Supported scopes include `agent.read`, `agent.write.summary`,
`agent.connection.read`, `agent.connection.write`, `classification.preview`,
`config.write`,
`external-publication.read`, `external-publication.write`, `source.write`,
`memory.read`, `memory.write`, `access.request`,
`access.decide`, `audit.read`, and `*` for operator-controlled admin use. Local
`Testing` mode remains credential-free unless token options are configured.
`ExpiresAt` is optional. Expired tokens are ignored by the authorization filter
and make `/readyz` fail when no other active token is available.

## Classification provider configuration

The operator console can configure the active classification provider at
`/api/operator/classification-provider`. Self-host operators can select `Mock`,
`ChatGPT API`, `Claude API`, `Google AI API`, `OpenRouter API`, or `External
HTTP`, enter a model and API key, and run a provider test from the console.
Provider API keys are stored server-side and are not returned by the API or
rendered back into the console after save.

Direct third-party LLM providers receive raw source content in the classification
prompt before Luthn knows the content sensitivity. Configure `ChatGPT API`,
`Claude API`, `Google AI API`, or `OpenRouter API` only when that provider
transfer is acceptable for the deployment. Use `External HTTP` for a self-hosted
or otherwise controlled classifier boundary. Direct third-party provider
endpoints must be HTTPS URLs on the expected provider host before Luthn sends an
API key header.

The API defaults to the local mock classifier only when no operator provider or
external provider is configured. `mock` is for tests and local experiments only;
it is a deterministic keyword classifier, not a production safety or
multilingual classification system.

```bash
Luthn__Classification__Provider=mock
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

External classification is opt-in and uses the `external-http` provider. When
`Luthn__Classification__ExternalHttp__Endpoint` is configured, the API must not
run the mock provider; set `Luthn__Classification__Provider=external-http`.
The provider receives the source id, source type, content, payload class, and
redaction state, and must return sensitivity, confidence, categories, and
whether sensitive material was detected. `External HTTP` may use plain HTTP only
for credential-free local or private-network smoke flows; if an API key is
configured, the endpoint must be HTTPS.

```bash
Luthn__Classification__Provider=external-http
Luthn__Classification__ExternalHttp__Endpoint=https://provider.example/classify
Luthn__Classification__ExternalHttp__CredentialEnvironmentVariable=LUTHN_PROVIDER_AUTH
Luthn__Classification__ExternalHttp__AuthHeaderName=Authorization
```

Only the environment variable name is configured in Luthn settings. The value itself must come from the operator environment or secret manager and must not be committed. If the credential environment variable name is omitted, the provider call is sent without an auth header, which keeps fake-provider tests and local smoke flows credential-free.

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
