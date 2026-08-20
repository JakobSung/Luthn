# Multiple Agent Connections

[한국어](agent-connections.ko.md)

Luthn can connect Codex and Claude Code to one installation at the same time.
Both agents use the same Luthn API and policy-approved safe memory, while their
host configuration and connection lifecycle remain independent.

This document describes the implemented personal/self-hosted connection, the
opt-in OSS Hub baseline, and the Apache-2.0 AgentDevice client boundary used by
the separately licensed Luthn Cloud service. The Hub can accept encrypted
durable ingress with server-derived identity and metadata-only audit, but it is
disabled by default. Cloud connection is a separate, explicit operation and
never disables the local MCP server. MCP provides recall and tools; automatic
lifecycle capture remains an independent Agent-native hook, plugin, or managed
configuration. See
[`cloud-hub-data-plane.md`](cloud-hub-data-plane.md).

## Add Cloud Without Replacing Local Luthn

After an administrator provides the Workspace ID and Cloud origin, connect a
second MCP server:

```bash
luthn cloud connect codex \
  --workspace 00000000-0000-0000-0000-000000000000 \
  --cloud-url https://cloud.example

luthn cloud status codex
```

The command creates three distinct device key pairs, stores private keys and
credentials in an AES-256-GCM encrypted local state file, keeps its randomly
generated decryption key in a separate owner-only host configuration file, and opens a code-based device
approval page, and registers the approved remote MCP as `luthn-cloud`. The
browser URL and user code are displayed separately so the code does not enter
browser history, referrer data, or proxy URL logs. The existing local `luthn`
registration is preserved and continues working when Cloud is unavailable.

Codex completes OAuth after MCP registration. Claude Code uses the same remote
MCP endpoint and performs its supported OAuth flow when connecting. The Cloud
consent screen binds the exact Organization, Workspace, device, and Agent
connection; no Organization-wide shared token is installed on the PC.

Back up the state file and its separate key through the same protected host
backup policy. Losing the key makes the AgentDevice state intentionally
unrecoverable and requires device revocation and re-enrollment.

Local removal is ownership-safe:

```bash
luthn cloud disconnect codex
```

This removes only the Luthn-owned local `luthn-cloud` registration. Server-side
authority must be revoked in the Cloud customer console. Cloud mode remains
off until a user runs `luthn cloud connect` explicitly.

## Connect Both Agents

The commands can be run in either order:

```bash
luthn connect codex
luthn connect claude

luthn connection status codex
luthn connection status claude
```

Connecting Claude Code after Codex does not modify the Codex hook, MCP
registration, or recall instructions. When Codex has already provisioned the
required Luthn API scopes, the second connection does not require an API
restart.

## Shared And Independent State

| Scope | Behavior |
|---|---|
| Luthn server and agent-safe memory | Shared by both agents |
| Classification, redaction, policy, and service-token scopes | Same server policy applies |
| Stored provenance | Distinguished as `codex` or `claude-code` |
| Stop hook | Managed independently in each agent's configuration |
| MCP registration | Managed independently inside each agent CLI |
| Auto-recall instructions | Codex `AGENTS.md` and Claude Code `CLAUDE.md` |
| Ownership state | Recorded in a separate file for each agent |

Codex and Claude Code can therefore run concurrently, and either can retrieve
safe project context previously stored by the other. This does not make the two
CLIs talk directly to each other or automatically orchestrate a multi-agent job
inside one terminal.

## MCP Name Conflicts

Each agent can have only one MCP registration named `luthn`. If the agent being
connected already has a user-created registration with that name and Luthn has
no ownership record for it, Luthn stops that agent's connection without
overwriting the existing registration.

```bash
claude mcp get luthn
```

Remove the existing registration only after confirming that it is no longer
needed, then connect again:

```bash
claude mcp remove luthn
luthn connect claude
```

A name conflict inside Claude Code does not affect an existing Codex
connection. If a Luthn-owned registration is changed after connection, Luthn
also reports a conflict instead of silently overwriting or deleting it.

## Status, Disconnect, And Uninstall

Status and disconnect operations are agent-specific:

```bash
luthn connection status codex
luthn connection status claude

luthn disconnect codex
luthn disconnect claude
```

`disconnect claude` removes only the Luthn-owned Claude Code hook, recall block,
and MCP registration; Codex remains connected. `disconnect codex` likewise
preserves Claude Code. If cleanup fails, Luthn retains the ownership state so a
later disconnect can retry safely.

`luthn uninstall` cleans up all recorded Codex and Claude Code connections
before removing the runtime. If either connection cannot be cleaned up safely,
uninstall stops and preserves ownership information.

## Platform Support

Simultaneous connections and independent lifecycle commands are available on
Windows, macOS, and Linux. Windows uses the PowerShell connector; macOS and
Linux use the shell/Python connector. See [Agent connection and memory](agent-quickstart.md)
for the hook, auto-recall, and data-boundary details.
