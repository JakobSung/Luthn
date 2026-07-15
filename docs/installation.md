# Luthn Installation

This is the source-free self-host installation path for users. It uses the same
Linux Compose runtime on macOS, Linux, and Windows; Git, a Luthn source checkout,
and the .NET SDK are not required.

## Give This To An Agent

Give this prompt to Codex or another coding agent:

```text
Install and configure Luthn locally by following the instructions here:
https://raw.githubusercontent.com/JakobSung/Luthn/refs/heads/main/docs/installation.md

Detect whether this host is macOS, Linux, or Windows and use only the matching
Docker self-host procedure. Do not clone the source or install the .NET SDK.
Inspect existing prerequisites and preserve Docker volumes, Luthn
configuration, Codex configuration, hooks, and unrelated MCP registrations.
Repair recoverable PowerShell, PATH, Docker daemon/context, and Codex CLI
discovery problems, then finish the installation.

Connect Codex, verify health and readiness, confirm that `luthn mcp
--list-tools` includes `get_context_pack`, and show me the operator console URL.
On macOS or Linux, explain the optional `--auto-recall` mode but do not enable
it unless I request it. Never print, copy, or commit the service token or other
credential-bearing files. Stop only for a user-owned UI, license, privilege,
restart, or trust decision and tell me the exact action required.
```

## Agent Execution Contract

An agent following this document should execute the work, not only repeat the
commands to the user.

1. Detect the host OS and use only the matching procedure below.
2. On macOS and Linux, keep using the existing `install.sh --connect-codex`
   flow below. The Windows recovery steps and PowerShell changes do not apply
   to those hosts.
3. Inspect the existing host prerequisites, Docker, Luthn, and Codex state
   before installing or replacing anything. PowerShell checks are Windows-only.
4. Treat a stopped Docker daemon, a stale Docker context, a stale shell `PATH`,
   and an unusable Codex Desktop `WindowsApps` executable as recoverable errors.
5. Preserve existing Docker volumes, Luthn configuration, Codex configuration,
   hooks, and unrelated MCP registrations.
6. Never display `luthn.env`, `service-token`, a token value, or other
   credential-bearing files. It is safe to report file paths and non-secret
   status.
7. Ask the user only when installation requires an interactive Docker Desktop
   agreement, administrator approval, macOS privacy approval, or Codex hook
   trust. Continue automatically after the user completes that gate.
8. Do not report success until all applicable completion checks pass.

Installation is complete only when:

- `luthn status` reports both health and readiness as `ready`;
- the operator console URL is reported without opening secret files;
- `luthn mcp --list-tools` includes `get_context_pack`;
- Codex MCP registration is present;
- any remaining user-owned Codex restart or hook trust step is reported
  explicitly.

## Requirements

### macOS and Linux

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

### Windows

- Windows 11
- PowerShell 7.4 or later (`pwsh`)
- Docker Desktop with Docker Compose, running in Linux-container mode
- a runnable Codex CLI when connecting Codex; the Windows recovery procedure
  below also checks the Codex Desktop runtime location when `PATH` is unusable

Check the runtime from PowerShell:

```powershell
docker info --format '{{.OSType}}'
docker compose version
```

The first command must print `linux`. Windows containers are not supported.

## Install on macOS or Linux

```bash
curl -fsSL https://raw.githubusercontent.com/JakobSung/Luthn/main/scripts/install.sh | bash -s -- --connect-codex
```

The bootstrap installs the CLI at `~/.local/bin/luthn`. It downloads the
source-free Compose bundle, pulls `ghcr.io/jakobsung/luthn:main`, creates a
local service token, starts PostgreSQL, applies migrations before API startup,
seeds public-safe demo data, and waits for health and readiness.
With `--connect-codex`, the same bootstrap also configures the Codex hook and MCP
registration, then prints the required restart and `/hooks` Trust steps.

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

## Install On Windows

### 1. Select PowerShell 7

Check PowerShell 7 from any terminal:

```powershell
pwsh -NoProfile -Command '$PSVersionTable.PSVersion'
```

If `pwsh` is missing or older than 7.4, install or upgrade the stable release
with WinGet:

```powershell
winget install --id Microsoft.PowerShell --source winget
# If WinGet reports that PowerShell is already installed but outdated:
winget upgrade --id Microsoft.PowerShell --source winget
```

Installing PowerShell 7 does not convert an already-open Windows PowerShell 5.1
session. Start `pwsh` or use the explicit `pwsh -NoProfile` command below. Do
not retry the bootstrap in `powershell.exe` after receiving
`PowerShell 7.4 or later is required`.

### 2. Check Docker Desktop

Docker being installed is not enough; its Linux engine must be running. Check:

```powershell
docker context show
docker info --format '{{.OSType}}'
docker compose version
```

The installer starts Docker Desktop automatically from its standard install
location when the engine is unreachable, then waits up to two minutes for it.
If Docker Desktop is missing, install it and complete its interactive agreement
or administrator step before continuing. If Docker reports `windows`, use the
Docker Desktop menu to switch to Linux containers. If the active context is
stale, inspect `docker context ls` and select the working Desktop context,
normally `docker context use desktop-linux`.

### 3. Install And Connect In One Run

Run this from PowerShell. It explicitly uses PowerShell 7, installs Luthn, waits
for Docker, verifies the service, and connects Codex before returning:

```powershell
$installer = Join-Path ([IO.Path]::GetTempPath()) "luthn-install.ps1"
try {
    irm https://raw.githubusercontent.com/JakobSung/Luthn/main/scripts/install.ps1 -OutFile $installer
    pwsh -NoProfile -File $installer -ConnectCodex
    if ($LASTEXITCODE -ne 0) { throw "Luthn installation failed with exit code $LASTEXITCODE" }
} finally {
    Remove-Item -LiteralPath $installer -ErrorAction SilentlyContinue
}
```

The bootstrap validates Docker before replacing an existing CLI, installs a
`luthn.cmd` shim, downloads the same Compose bundle used by macOS and Linux,
creates a local service token, applies migrations, seeds demo data, and waits
for health and readiness. It adds the CLI directory to the current user's
`PATH`.

An already-open parent terminal cannot receive a child process's updated
`PATH`. Do not reinstall Luthn. Open a new terminal or repair only the current
session:

```powershell
$env:Path = "$env:LOCALAPPDATA\Luthn\bin;$env:Path"
luthn status
```

The CLI can always be invoked directly while diagnosing `PATH`:

```powershell
& "$env:LOCALAPPDATA\Luthn\bin\luthn.ps1" status
```

### 4. Codex CLI Recovery

The Windows CLI checks, in order, `LUTHN_CODEX_COMMAND`, `CODEX_CLI_PATH`, the
Codex Desktop runtime under `%LOCALAPPDATA%\OpenAI\Codex\bin`, and then `PATH`.
Each candidate must successfully run `codex --version` before it is used. This
avoids the inaccessible executable sometimes exposed under
`C:\Program Files\WindowsApps`.

If no runnable candidate is found, install or repair the Codex CLI, restart the
terminal, or point Luthn at a known runnable executable for this operation:

```powershell
$env:LUTHN_CODEX_COMMAND = 'C:\path\to\codex.exe'
& "$env:LOCALAPPDATA\Luthn\bin\luthn.ps1" connect codex
```

Do not copy executables out of `WindowsApps` or change WindowsApps ACLs.

### 5. Windows Failure Map

| Symptom | Agent action |
|---|---|
| `PowerShell 7.4 or later is required` | Install/upgrade PowerShell, then invoke the bootstrap through `pwsh`. |
| `dockerDesktopLinuxEngine` pipe not found | Let the installer start Docker Desktop, or start it manually and wait for `docker info`. |
| Docker reports `windows` | Ask the user to switch Docker Desktop to Linux containers, then retry. |
| `luthn` is not recognized | Refresh the current `PATH` or use the direct CLI path; do not reinstall. |
| `Index was outside the bounds of the array` | Rerun the current bootstrap to replace the older CLI; the current CLI reports a specific missing-command error. |
| `SeSecurityPrivilege` during a repeated install | Rerun the current bootstrap; the current CLI updates access rules without resetting file ownership. |
| `No runnable Codex CLI was found` | Repair Codex CLI discovery or set `LUTHN_CODEX_COMMAND` to a tested executable. |

After registration, restart Codex and use `/mcp` to verify the `luthn` server.
On Windows this release installs MCP only; it does not install the automatic
turn-capsule hook.

Windows state uses these stable locations by default:

| Purpose | Default path |
|---|---|
| CLI and shim | `%LOCALAPPDATA%\Luthn\bin\` |
| Compose runtime | `%LOCALAPPDATA%\Luthn\data\compose.yaml` |
| Private configuration | `%LOCALAPPDATA%\Luthn\config\luthn.env` |
| Service token secret | `%LOCALAPPDATA%\Luthn\config\service-token` |
| Connector state | `%LOCALAPPDATA%\Luthn\state\connectors\` |
| Update state and backups | `%LOCALAPPDATA%\Luthn\state\update-windows.json`, `%LOCALAPPDATA%\Luthn\state\backups\` |
| PostgreSQL volume | `luthn-postgres` |
| Operator configuration volume | `luthn-operator` |

The token and configuration files are written as UTF-8 without BOM and receive
an inheritance-disabled, current-user-only NTFS ACL.

## Status And Console

```bash
luthn status
```

Status reports Compose service state, health, readiness, console URL, image
reference, image ID, and registry digest when available.

Open the operator console at <http://127.0.0.1:8080/>. PostgreSQL is kept on the
internal Compose network and the console/API port is loopback-bound by default.

## Lifecycle Commands

The following full lifecycle is currently available on macOS and Linux:

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

Windows currently supports:

```powershell
luthn status
luthn update
luthn connect codex
luthn disconnect codex
luthn uninstall
```

Windows `update [image]` follows the backup, migration, restart, and readiness
contract below and refreshes both the PowerShell CLI and Compose runtime.
Windows `uninstall` removes the owned Codex MCP registration, Compose services,
CLI, and downloaded runtime while preserving Docker volumes, configuration,
token, connector-independent state, and backups. It stops without removing the
runtime if Codex cleanup fails. Windows `reset`, purge uninstall, and automatic
hook installation are deferred.

### Windows release smoke

The repository's Windows CI injects fake Docker and Codex commands to verify
failure handling without requiring a privileged Docker Desktop runner. Before a
Windows release, run the documented bootstrap on Windows 11 with Docker Desktop
in Linux-container mode, then verify `luthn status`, a successful `luthn
update`, update failure recovery, `codex mcp get luthn`, an MCP
`get_context_pack` probe, default uninstall, reinstall, and preservation of the
two named Docker volumes and `%LOCALAPPDATA%\Luthn\config`.

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
and records the previous image ID. macOS/Linux records update state in
`~/.local/state/luthn/install-state.env`; Windows records it in
`%LOCALAPPDATA%\Luthn\state\update-windows.json`. Luthn does not automatically
restore or downgrade the database because a schema rollback can destroy
retained data. Use [Operations](operations.md) for restore and rollback
procedure.

## Agent Connections

Connect Codex with one host command:

```bash
luthn connect codex
```

On Windows this registers Codex directly to the Docker Compose stdio MCP
service. It verifies the registration and probes `get_context_pack`; the bearer
token remains in Luthn's private configuration and is not copied into Codex
arguments or configuration. Repeated setup is idempotent, unrelated MCP
registrations are preserved, and automatic hooks are not installed.

The remaining hook and automatic-recall instructions in this section apply to
macOS and Linux.

Enable lightweight automatic recall explicitly when wanted:

```bash
luthn connect codex --auto-recall
```

The opt-in adds a marked Luthn block to
`${CODEX_HOME:-~/.codex}/AGENTS.md` without replacing existing instructions.
It requests one `get_context_pack` lookup for a new task or material topic
change with 3 items, an estimated 600-token budget, a 200 ms fail-open
deadline, and a 10-minute in-process cache. Continued work reuses the context
already in the conversation instead of searching every turn.

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
registration, marked automatic-recall instruction block, and non-secret
connector state. A normal Luthn uninstall also
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

On Windows, the corresponding private files are
`%LOCALAPPDATA%\Luthn\config\luthn.env` and
`%LOCALAPPDATA%\Luthn\config\service-token`.

Contributors who need source builds, in-memory mode, or local migration tooling
should use [Local Development](local-development.md).
