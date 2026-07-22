# Multiple Agent Connections

[한국어](agent-connections.ko.md)

Luthn can connect Codex and Claude Code to one installation at the same time.
Both agents use the same Luthn API and policy-approved safe memory, while their
host configuration and connection lifecycle remain independent.

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
