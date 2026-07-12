# Agent Quickstart

## Goal

Connect an agent to an installed self-host Luthn without a source checkout or a
locally installed .NET SDK. Install Luthn first with the
[installation guide](installation.md), then confirm:

```bash
luthn status
```

## Connection Principle

Codex uses one setup command while retaining two internal channels. The host
hook provides deterministic automatic turn-capsule delivery. MCP provides
model-triggered safe reads and explicit shared-memory writes. Both paths pass
through Luthn service-token scopes, classification, policy, and agent-safe
projection boundaries.

Allowed MCP tools are:

```text
get_context_pack
search_safe_context
get_wiki_proposal
classify_preview
create_shared_memory
query_shared_memory
get_shared_memory_item
```

Raw Vault, private-record dump, and unrestricted source-read tools are not part
of the default agent surface. Private details require a separate approval flow.

## Connect Codex

Run:

```bash
luthn connect codex
```

Restart Codex, open `/hooks`, and approve the Luthn `Stop` hook when prompted.
Then check both channels:

```bash
luthn connection status codex
```

The hook sends only the bounded final assistant response with hashed stable
identifiers. It does not read or upload the transcript, user prompts, working
directory, or transcript path. Upload is asynchronous and non-blocking. Luthn
redacts common credential patterns locally and classifies every capsule before
it can become shared context.

The same setup command registers the installed Docker-backed MCP stdio command.
The command loads the bearer token from Luthn's private config, not from Codex
configuration. MCP stdout remains reserved for JSON-RPC.

Disconnect with:

```bash
luthn disconnect codex
```

The operator console displays connection observations but does not install,
change, or remove agent configuration.

## Custom Agent Adapter

Custom integrations can still submit a caller-produced bounded summary:

```bash
printf '%s\n' '{"sessionId":"session-1","turnId":"turn-1","sourceAgent":"custom","summary":"Published a safe project decision.","coreTags":["decision"],"idempotencyKey":"session-1-turn-1"}' \
  | luthn adapter
```

Claude Code support is planned behind the same lifecycle contract. Hermes is a
separate planned integration using its official MemoryProvider interface.

Hosted cloud MCP is outside this self-host quickstart.

## Demo Context

The installer seeds only public-safe demo context. Real agent writes are still
treated as untrusted candidates and become visible only after Luthn's internal
classification and policy checks allow them.
