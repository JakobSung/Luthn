<p align="center">
  <img src="docs/assets/luthn-brand.png" alt="Luthn - Safe context for AI agents." width="920">
</p>

<p align="center">
  <strong>Self-hosted shared memory for AI agents, with a clear data boundary.</strong>
</p>

<p align="center">
  <a href="README.ko.md">한국어</a> ·
  <a href="docs/installation.md">Installation</a> ·
  <a href="docs/agent-quickstart.md">Codex connection and memory</a> ·
  <a href="docs/data-boundaries.md">Data boundaries</a> ·
  <a href="docs/local-development.md">Development</a>
</p>

# Luthn

Luthn gives multiple AI agents a shared, reusable project memory without making
raw private data part of the model's default context.

- Run it in infrastructure you manage with Docker and PostgreSQL.
- Classify and redact intake before exposing agent-safe summaries and context.
- Audit what was stored, shared, and retrieved.

## How The Memory Loop Works

On macOS and Linux, a trusted Codex hook can submit a bounded capsule of the
final assistant response after a turn. Luthn redacts and classifies that capsule
before anything becomes agent-visible. MCP provides safe reads and explicit
shared-memory writes.

Optional lightweight auto-recall can fetch one small context pack when a new
task or material topic begins. It reuses that context during the task instead
of querying on every turn.

```text
completed turn -> bounded capsule -> classify and store safe context
new task       -> auto-recall or MCP -> reuse relevant context
```

Windows currently connects the MCP channel only; the automatic hook and
auto-recall instructions are not installed there. See
[Codex connection and memory](docs/agent-quickstart.md) for setup, trust steps,
privacy guarantees, recall limits, and platform differences.

## Recommended Installation: Give This To An Agent

Copy the following prompt into Codex or another coding agent. The installation
guide is written as an execution and recovery guide, not just a command list.

```text
Install and configure Luthn by following this document:
https://raw.githubusercontent.com/JakobSung/Luthn/refs/heads/main/docs/installation.md

Detect whether this host is macOS, Linux, or Windows and use only the matching
Docker self-host procedure. Inspect existing prerequisites and preserve Docker
volumes, Luthn configuration, Codex configuration, hooks, and unrelated MCP
registrations. Repair recoverable PowerShell, PATH, Docker daemon/context, and
Codex CLI discovery problems, then finish the installation.

Connect Codex, verify health and readiness, confirm that the MCP tool list
contains get_context_pack, and show the operator console URL. On macOS or Linux,
explain the optional --auto-recall mode but do not enable it unless I request
it. Never print or copy the service token or credential-bearing files. Stop
only for a user-owned license, privilege, restart, or trust decision and tell
me the exact action required.
```

For manual commands, prerequisites, Windows recovery, lifecycle behavior, and
uninstallation, use the [installation guide](docs/installation.md).

## Verify

```bash
luthn status
luthn mcp --list-tools
```

Health and readiness should report `ready`, and the MCP tool list should include
`get_context_pack`. The operator console is available at
<http://127.0.0.1:8080/> by default.

## Data Boundary

Raw customer records, private messages, credentials, and unredacted operational
data stay behind the private boundary. Agents receive only policy-approved safe
projections such as reviewed summaries, redacted references, and approved
project context. External publication is a separate, explicit approval path.

Read [Data boundaries](docs/data-boundaries.md) for classification examples,
provider-transfer implications, agent visibility, and publication rules.

## Documentation

- [Installation, recovery, and lifecycle](docs/installation.md)
- [Codex connection, hook, MCP, and recall](docs/agent-quickstart.md)
- [Data boundaries](docs/data-boundaries.md)
- [Operations and recovery](docs/operations.md)
- [API](docs/api.md)
- [Architecture](docs/architecture.md)
- [Local development](docs/local-development.md)
- [Licensing](docs/licensing.md)

## License And Contributing

The self-host runtime is AGPL-3.0-only; SDKs, HTTP connectors, and public plugin
templates are Apache-2.0. See [Licensing](docs/licensing.md) for the package
boundary and [CONTRIBUTING.md](CONTRIBUTING.md) before proposing changes.
