# Codex Connection And Memory

[한국어](agent-quickstart.ko.md)

This guide explains how Codex connects to an installed self-hosted Luthn, how
completed work becomes reusable memory, and how later tasks retrieve it.

Install Luthn first with the [installation guide](installation.md). A source
checkout and local .NET SDK are not required.

## The Ongoing Memory Loop

Luthn keeps automatic capture separate from model-triggered access:

```text
1. Codex completes a turn
2. A trusted Stop hook submits a bounded final-response capsule
3. Luthn redacts, classifies, and stores the permitted safe projection
4. A later task retrieves relevant context through auto-recall or MCP
5. Codex reuses that context while working on the task
```

The hook makes capture deterministic after a completed turn. MCP keeps reads
and writes explicit and policy-controlled. Default auto-recall adds a small,
bounded lookup at the start of a new task or material topic instead of querying
memory on every turn.

This loop does not give Codex unrestricted access to Luthn's private store. All
agent-facing results pass through service-token scopes, classification, policy,
and agent-safe projection boundaries. See [Data boundaries](data-boundaries.md).

## Platform Support

| Host | MCP safe reads/writes | Automatic turn hook | Auto-recall |
|---|---|---|---|
| macOS and Linux | Supported | Supported after user Trust | Enabled by default |
| Windows | Supported | Supported after user Trust | Enabled by default |

macOS and Linux use the shell/Python connector, while Windows uses a
PowerShell-native connector with the same defaults, ownership, and
data-boundary contract.

## Connect Codex

Run on any supported host:

```bash
luthn connect codex
```

The command preserves unrelated Codex hooks, instructions, and MCP
registrations. The bearer token remains in Luthn's private configuration and is
not copied into Codex configuration.

### Complete Hook Trust On Any Host

The connection command installs the Luthn-owned Stop hook and registers MCP.
Codex requires a one-time security decision before it will run the hook:

1. Restart Codex.
2. Open `/hooks`.
3. Open `Stop > luthn.agent-connector.v1` and choose **Trust**.
4. Complete one Codex turn.
5. Verify that `automatic-ingestion` reports `Active`:

```bash
luthn connection status codex
```

The operator console reports connection observations but does not install,
change, trust, or remove agent configuration.

### Windows Codex Recovery

If Codex CLI discovery fails, follow the Windows recovery section in the
[installation guide](installation.md). Do not copy an executable from
`WindowsApps` or change its ACLs.

## Lightweight Auto-Recall

Auto-recall is enabled by default by `luthn connect codex` and by the
`--connect-codex` installation flow. The older explicit-enable form remains
accepted for compatibility:

```bash
luthn connect codex --auto-recall
```

Disable it explicitly when needed:

```bash
luthn connect codex --no-auto-recall
```

The command adds only a Luthn-managed block to Codex instructions and preserves
unrelated user instructions. That block asks Codex to:

- call `get_context_pack` once at a new task or material topic change;
- retrieve at most 3 items with an estimated 600-token budget;
- use a 200 ms deadline and continue without memory on timeout or failure;
- reuse the returned context during the same task;
- refresh after 10 minutes when the task continues;
- avoid automatic lookup on every turn.

When known, Codex may send normalized non-sensitive `projectKey`, `taskKey`,
and `topicTags`. A project-scoped request includes matching and global records,
excludes records assigned to another project, and applies bounded task, topic,
and recency boosts. Never use a raw workspace path, transcript path, transcript
content, credential, or customer identifier as recall metadata.

Use `search_safe_context` or `query_shared_memory` explicitly when a task needs
deeper recall.

## What The Hook Captures

The Stop hook sends only a bounded capsule derived from the final assistant
response, together with hashed stable identifiers needed for idempotent
delivery.

It does not read or upload:

- the full transcript;
- user prompts;
- the working directory;
- the transcript path;
- credential files or the Luthn service token.

Delivery is asynchronous and non-blocking. Luthn redacts common credential
patterns locally and classifies every capsule before it can become shared
context. Sensitive or disallowed content stays behind the memory boundary.

## What MCP Provides

The installed Docker-backed stdio MCP server exposes the agent-safe tool
surface:

```text
get_context_pack
search_safe_context
get_wiki_proposal
classify_preview
create_shared_memory
query_shared_memory
get_shared_memory_item
create_sensitive_access_request
get_sensitive_access_request
get_sensitive_access_result
```

Raw Vault dumps, unrestricted source reads, and private-record export tools are
not part of the default agent surface. The connector provisions the
`access.request` scope for the three metadata-only request tools above, but MCP
does not expose approval or denial. Private details require the separate trusted
operator decision path.

## Verify And Disconnect

Verify the service and MCP surface on every host:

```bash
luthn status
luthn mcp --list-tools
```

Inspect the connector channels on every host with:

```bash
luthn connection status codex
```

Disconnect with:

```bash
luthn disconnect codex
```

Disconnect removes only the Luthn-owned hook, optional Luthn-managed
auto-recall block, matching MCP registration, and non-secret ownership state.
Unrelated hooks, instructions, and MCP registrations are preserved.

## Custom Agent Adapter

Custom integrations can submit a caller-produced bounded summary:

```bash
printf '%s\n' '{"sessionId":"session-1","turnId":"turn-1","sourceAgent":"custom","summary":"Published a safe project decision.","coreTags":["decision"],"idempotencyKey":"session-1-turn-1"}' \
  | luthn adapter
```

Claude Code support is planned behind the same lifecycle contract. Hermes is a
separate planned integration using its official MemoryProvider interface.

The installer seeds only public-safe demo context. Real agent writes remain
untrusted candidates until classification and policy allow an agent-safe
projection.
