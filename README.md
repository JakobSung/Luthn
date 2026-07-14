<p align="center">
  <img src="docs/assets/luthn-brand.png" alt="Luthn - Safe context for AI agents." width="920">
</p>

<p align="center">
  <strong>A shared memory space for AI agents that you can run yourself.</strong>
</p>

<p align="center">
  <a href="README.ko.md">한국어</a> ·
  <a href="docs/installation.md">Installation</a> ·
  <a href="docs/api.md">API</a> ·
  <a href="docs/agent-quickstart.md">Agent Quickstart</a> ·
  <a href="docs/local-development.md">Local Development</a> ·
  <a href="docs/licensing.md">License</a>
</p>

# Luthn

Luthn is a shared memory space for AI agents.

It lets several agents look at the same useful context without treating every
source document or private note as something the model should remember by
default.

The default setup is meant to be run by you. You keep the data in local storage
or PostgreSQL, define rules for classification, redact sensitive parts, keep a
usage history, and let agents fetch only the context they need.

## Luthn Philosophy

Shared memory should actually help.
You should be able to check later what information it was based on.
And there should be a clear line between what an agent can see and what it
cannot.

Agents should only receive information they are allowed to use. Sensitive
originals, private records, credentials, and run history should stay behind
clear protection rules.

## Why Luthn

AI agents are more useful when they can remember the shape of a project and
reuse that context later.

But if private source data is copied into many agents and sessions, the risk
grows with it.

Luthn splits that problem up:

- raw sources and private records stay out of default agent memory;
- agents get only the summaries, references, and context packs that are allowed
  to be shared;
- the system keeps a record of what was used and what access was allowed;
- the boundary runs inside infrastructure you manage.

## How It Works

<p align="center">
  <img src="docs/assets/luthn-flow-diagram.png" alt="Luthn data flow from sources through classification, private storage, approval, safe shared memory, and agent access." width="920">
</p>

```text
Sources
  -> intake
  -> classification and policy
  -> redact sensitive parts and review
  -> create shared memory, wiki Markdown, and context packs
  -> serve through agent APIs and MCP tools
```

What agents see by default is cleaned-up context. Luthn can keep and use
sensitive records internally, but the default API and MCP paths do not let
agents open the original store or read the full source text directly.

## Quick Start

### Recommended: Docker

Install Docker with Docker Compose.

```bash
curl -fsSL https://raw.githubusercontent.com/JakobSung/Luthn/main/scripts/install.sh | bash -s -- --connect-codex
```

This one command installs Luthn and configures the Codex connector. The final
Codex restart and `/hooks` Trust step is an intentional user security review.

### Optional: Ask an agent

Give Codex or another coding agent this prompt:

```text
Install Luthn with Docker by following:
https://raw.githubusercontent.com/JakobSung/Luthn/refs/heads/main/docs/installation.md

Verify the installation, show the operator console URL, and connect Codex to
Luthn with `luthn connect codex`. Do not print the service token.
```

### Manage

```bash
luthn status
luthn update
luthn reset --yes
luthn uninstall
luthn uninstall --purge-data --yes
```

`update` pulls the target runtime, stops write paths, backs up PostgreSQL, and
migrates. `reset` deletes the database and operator volumes. A normal
`uninstall` preserves persistent data, configuration, and backups; purge removes
them only when both destructive flags are present.

| Command | PostgreSQL and operator volumes | Config and token | Backups | CLI/runtime |
|---|---|---|---|---|
| `luthn update` | Preserved | Preserved | New backup added | Refreshed |
| `luthn reset --yes` | Deleted and recreated | Preserved | Preserved | Preserved |
| `luthn uninstall` | Preserved | Preserved | Preserved | Removed |
| `luthn uninstall --purge-data --yes` | Deleted | Deleted | Deleted | Removed |

### Connect agents

Connect Codex with one command:

```bash
luthn connect codex
```

Add `--auto-recall` to retrieve one small cached context pack when a new task or
topic starts:

```bash
luthn connect codex --auto-recall
```

Follow the command's required steps: restart Codex, open `/hooks`, trust
`Stop > luthn.agent-connector.v1`, complete one turn, and confirm that
`automatic-ingestion` reports `Active`. Then inspect or remove the connection with:

```bash
luthn connection status codex
luthn disconnect codex
```

The single setup command preserves two internal channels: a Codex hook sends a
bounded final-response capsule through classified HTTP intake, while MCP
provides model-triggered safe reads and explicit shared-memory writes. It
preserves unrelated Codex hooks and MCP registrations, and the token stays in
Luthn's private configuration. The operator console only displays read-only
connection status.

Codex is the current host connector. A Claude Code connector using the same
lifecycle contract and a separate Hermes integration using its official memory
provider interface are planned, not currently installed by this command.

Source builds, in-memory mode, and contributor commands remain in
[Local Development](docs/local-development.md).

## Data Boundary

Agent-safe shared memory may include:

- reviewed summaries;
- redacted source references;
- runbooks and implementation notes that do not contain secrets;
- policy-approved project metadata;
- safe conversation conclusions.

Sensitive storage is for:

- raw customer or user originals;
- private emails, messages, contracts, quotes, finance, or payment records;
- credentials and credential-bearing operational material;
- unredacted incident logs;
- policy-private records.

Sensitive records can inform reviewed summaries, but they are not default shared
memory for agents.

## Documentation

- [Installation and lifecycle](docs/installation.md)
- [API](docs/api.md)
- [Agent quickstart](docs/agent-quickstart.md)
- [Local development](docs/local-development.md)
- [Operations and recovery](docs/operations.md)
- [Data boundaries](docs/data-boundaries.md)
- [Licensing model](docs/licensing.md)

## License

Luthn uses a component-based licensing model.

| Component | License |
|---|---|
| Core and self-host runtime | AGPL-3.0-only |
| SDKs, HTTP connectors, and public plugin templates | Apache-2.0 |

See [docs/licensing.md](docs/licensing.md) for the full package boundary.

## Contributing

Public bug reports and feature requests are welcome through GitHub Issues.
Pull request creation is temporarily restricted to invited collaborators; if
you are not a collaborator, open an issue instead. Read
[CONTRIBUTING.md](CONTRIBUTING.md) for the current policy.

Keep the repository public-safe. Do not commit credentials, private source
records, customer originals, local agent artifacts, run evidence, or local
planning state.
