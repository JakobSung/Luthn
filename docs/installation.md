# Luthn Installation

This is the source-free self-host installation path for users. It requires
Docker and curl, not Git, a Luthn source checkout, or the .NET SDK. The optional
one-command Codex connector also requires Python 3 on the host.

## Agent Prompt

Give this prompt to Codex or another coding agent:

```text
Install and configure Luthn locally by following the instructions here:
https://raw.githubusercontent.com/JakobSung/Luthn/refs/heads/main/docs/installation.md

Use the Docker self-host path. Install Luthn without cloning the source or
installing the .NET SDK, verify health and readiness, show me the operator
console URL, and run `luthn connect codex`. Do not print or commit the generated
service token.
```

## Requirements

- Docker with Docker Compose
- curl
- a running Docker daemon, such as Docker Desktop or OrbStack
- Python 3 when connecting Codex

Check Docker before installation:

```bash
docker info
docker compose version
```

If Docker points at a stale socket, select a working context with
`docker context ls` and `docker context use <context-name>`.

## Install

```bash
curl -fsSL https://raw.githubusercontent.com/JakobSung/Luthn/main/scripts/install.sh | bash
```

The bootstrap installs the CLI at `~/.local/bin/luthn`. It downloads the
source-free Compose bundle, pulls `ghcr.io/jakobsung/luthn:main`, creates a
local service token, starts PostgreSQL, applies migrations before API startup,
seeds public-safe demo data, and waits for health and readiness.

If `~/.local/bin` is not already on `PATH`, use the export command printed by
the installer and add it to your shell profile.

Installed state uses stable locations independent of the current directory:

| Purpose | Default path |
|---|---|
| CLI | `~/.local/bin/luthn` |
| Compose runtime | `~/.local/share/luthn/compose.yaml` |
| Private configuration | `~/.config/luthn/luthn.env` |
| Service token secret | `~/.config/luthn/service-token` |
| Update state and backups | `~/.local/state/luthn/` |
| Connector runtime | `~/.local/share/luthn/runtime/` |
| Connector state | `~/.local/state/luthn/connectors/` |
| PostgreSQL volume | `luthn-postgres` |
| Operator configuration volume | `luthn-operator` |

## Status And Console

```bash
luthn status
```

Status reports Compose service state, health, readiness, console URL, image
reference, image ID, and registry digest when available.

Open the operator console at <http://127.0.0.1:8080/>. PostgreSQL is kept on the
internal Compose network and the console/API port is loopback-bound by default.

## Lifecycle Commands

```bash
luthn update
luthn reset --yes
luthn uninstall
luthn uninstall --purge-data --yes
```

| Command | Services | PostgreSQL and operator volumes | Config and token | Backups | CLI/runtime |
|---|---|---|---|---|---|
| `luthn update` | Restarted after migration | Preserved | Preserved | New backup added | Refreshed |
| `luthn reset --yes` | Recreated | Deleted and recreated | Preserved | Preserved | Preserved |
| `luthn uninstall` | Removed | Preserved | Preserved | Preserved | Removed |
| `luthn uninstall --purge-data --yes` | Removed | Deleted | Deleted | Deleted | Removed |

`reset` does nothing without `--yes`. Persistent data and configuration are
deleted by uninstall only when `--purge-data` and `--yes` are both present.
When a managed Codex connector exists, reset and update re-report its current
metadata-only channel state after the API becomes ready.

### Update Behavior

`luthn update` performs this sequence:

1. pulls the target image and downloads the matching CLI and Compose runtime;
2. starts/checks PostgreSQL;
3. stops API and active MCP/adapter write paths;
4. creates a compressed `pg_dump` backup and records the current image ID and backup path;
5. runs migrations from the same target image that will run the API;
6. starts the API and requires both `/healthz` and `/readyz` to pass;
7. records success only after readiness.

Pin an immutable published image when needed:

```bash
luthn update ghcr.io/jakobsung/luthn:sha-<full-commit-sha>
```

Migration or readiness failure leaves the API stopped, preserves the backup,
and records the previous image ID in
`~/.local/state/luthn/install-state.env`. Luthn does not automatically restore
or downgrade the database because a schema rollback can destroy retained data.
Use [Operations](operations.md) for restore and rollback procedure.

## Agent Connections

Connect Codex with one host command:

```bash
luthn connect codex
```

The command verifies Luthn and Codex prerequisites, merges one Luthn-owned
`Stop` hook into `~/.codex/hooks.json`, registers `luthn mcp`, probes the MCP
tools, and reports metadata-only channel state to Luthn. Repeated runs are
idempotent and unrelated hooks or MCP registrations are preserved.

Restart Codex after setup. Codex requires the operator to review and trust a
new hook; open `/hooks` and approve the Luthn hook when prompted. Luthn does not
bypass that host trust decision.

Inspect or remove the connector:

```bash
luthn connection status codex
luthn disconnect codex
```

Disconnect removes only the Luthn-marked hook, matching `luthn mcp`
registration, and non-secret connector state. A normal Luthn uninstall also
performs this ownership-aware cleanup before removing the runtime. Uninstall
stops without removing the CLI when connector cleanup fails, avoiding a stale
Codex hook that points to a missing command.

The one-command setup preserves two internal paths with different jobs:

- Codex `Stop` hook: automatic bounded turn-capsule writes over the HTTP API;
- `luthn mcp`: model-triggered safe reads and explicit shared-memory writes.

The server still applies service-token scopes, classification, policy, and
agent-safe projection rules to both paths.

### Automatic Turn Capsule

The synchronous Codex hook wrapper reads a bounded `Stop` event from standard
input, hands it to a detached uploader, and returns immediately. The uploader
submits the final assistant message only, capped below the API's 4000-character
summary limit, with deterministic hashed session/turn identifiers. It does not
read or upload the Codex transcript, user prompts, working-directory path, or
transcript path. Delivery failure does not block the Codex turn.
If the message contains a recognized credential pattern, including URI
user-info, Basic/Bearer authentication, JWTs, cloud credentials, secret
assignments, or private keys, the entire capsule is dropped before delivery.
The server classification and policy boundary still applies to every delivered
capsule.

Every capsule still enters the existing turn-summary classifier and policy
boundary. Sensitive material does not become default shared agent context.

### Manual Adapter

The adapter remains available for custom agents and diagnostics. It reads one
caller-produced bounded JSON summary from standard input. It is no longer a
separate setup step for Codex.

```bash
printf '%s\n' '{"sessionId":"session-1","turnId":"turn-1","sourceAgent":"codex","summary":"Published a safe project decision.","coreTags":["decision"],"idempotencyKey":"session-1-turn-1"}' \
  | luthn adapter
```

### MCP Stdio

List the installed MCP tools:

```bash
luthn mcp --list-tools
```

`luthn connect codex` registers the Docker-backed stdio command. The installed
command loads the local bearer token from the private Luthn
configuration, so the token is not copied into Codex configuration. The MCP
container runs without a TTY and reserves stdout for JSON-RPC.

Expected tools:

```text
get_context_pack
search_safe_context
get_wiki_proposal
classify_preview
create_shared_memory
query_shared_memory
get_shared_memory_item
```

### Additional Agents

- Claude Code is planned to use the same connector lifecycle and status
  contract with Claude-native hooks/plugin registration plus MCP.
- Hermes is planned as a separate integration through its official
  MemoryProvider interface, with MCP only where that provider does not cover an
  active operation.

Neither integration is installed by the current Codex command. The operator
console is a read-only status surface; it cannot install, reconfigure, or
disconnect host agents.

## Secrets

Do not print or commit `~/.config/luthn/luthn.env`,
`~/.config/luthn/service-token`, provider API keys, database backups, or
credential-bearing operator configuration. The API receives only the token
digest; adapter and MCP containers receive the original token as a mounted
Compose secret file at runtime.

Contributors who need source builds, in-memory mode, or local migration tooling
should use [Local Development](local-development.md).
