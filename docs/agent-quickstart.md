# Agent Connection And Memory

[한국어](agent-quickstart.ko.md)

This guide explains how Codex and Claude Code connect to an installed self-hosted Luthn, how
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

## Connect Claude Code

Claude Code has the same MCP, automatic Stop capture, lightweight recall,
status, and disconnect lifecycle:

```bash
luthn connect claude
luthn connection status claude
```

Luthn owns only its `Stop` hook in `~/.claude/settings.json`, its managed
recall block in `~/.claude/CLAUDE.md`, and its user-scoped MCP registration.
It does not read Claude transcripts; the Stop hook uses Claude Code's bounded
`last_assistant_message` field. Remove it with `luthn disconnect claude`.

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
- call `get_context_pack` before answering questions about a named agent,
  another agent's work, prior work, a past decision, or current work status;
- retrieve at most 3 items with an estimated 600-token budget;
- when the pack is empty, irrelevant, over budget, times out, or fails, try one
  bounded `search_safe_context` lookup with the same safe metadata;
- say that Luthn could not verify the requested context when the fallback is
  empty or fails, rather than guessing;
- reuse the returned context during the same task;
- refresh after 10 minutes when the task continues;
- avoid automatic lookup on every turn.

When known, Codex may send normalized non-sensitive `projectKey`, `taskKey`,
and `topicTags`. A project-scoped request includes matching and global records,
excludes records assigned to another project, and applies bounded task, topic,
and recency boosts. Never use a raw workspace path, transcript path, transcript
content, credential, or customer identifier as recall metadata.

Use `search_safe_context` or `query_shared_memory` explicitly when a task needs
deeper recall beyond this bounded fallback. Do not replace Luthn recall with
local memory files or unverified conversation history.

### Agent mutation boundary

The agent-facing connector is intentionally unable to mutate existing memory,
source, or turn records. It also cannot approve or deny sensitive-data access.
If an agent or user asks for deletion, modification, overwrite, approval, or
denial, the agent must explicitly refuse and must not invent a tool or call an
operator route. Those decisions belong to a trusted operator or to bounded
system retention cleanup, not to an agent service token.

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

Delivery is asynchronous and non-blocking on macOS and Linux. On Windows, the
hook waits for the bounded upload so Codex cannot terminate a detached uploader;
the hook timeout is 10 seconds and failures remain fail-open. Luthn redacts
common credential patterns locally and classifies every capsule before it can
become shared context. Sensitive or disallowed content stays behind the memory
boundary.

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
submit_search_feedback
get_shared_memory_item
request_protected_information_access
get_protected_information_result
create_sensitive_access_request
get_sensitive_access_request
get_sensitive_access_result
```

Raw Vault dumps, unrestricted source reads, and private-record export tools are
not part of the default agent surface. The connector provisions the
`access.request` scope for the bounded request/status/result tools above and the
`metrics.write` scope for bounded cache observations and explicit feedback, but MCP
does not expose approval or denial. Private details require the separate trusted
operator decision path.

When the user asks for a specific detail missing from one relevant recalled
safe item, including a detail that may have been protected or omitted, the
managed Codex and Claude instructions tell the agent to use that safe item's ID
with `request_protected_information_access`. Luthn resolves the
related protected record inside the server-derived owner and workspace, then
creates a fresh requester-bound request and opaque handle. The handle must stay
inside the requesting task and must never be shown, logged, cached, or written
to memory. Before approval, this flow returns no protected value. After approval,
the same task calls `get_protected_information_result`; each successful call
consumes one of 1–3 allowed reads and the grant expires after at most 60 minutes.
The agent answers only the detail the user requested. Credentials, access keys,
and private keys are never returned. If the handle is lost, create a new request.
Internal type names, field names, reference IDs, tool names, handles, and raw tool
errors must not appear in the user-facing answer.

`submit_search_feedback` accepts only a `retrievalId` returned by Luthn and a
`helpful` or `unhelpful` judgment. It does not accept a query, result body, or
free-form comment. Search telemetry is best-effort and never changes recall
results or timeout/cache behavior.

## Verify And Disconnect

Verify the service and MCP surface on every host:

```bash
luthn status
luthn mcp --list-tools
```

Inspect the connector channels on every host with:

```bash
luthn connection status codex
luthn connection status claude
```

Disconnect with:

```bash
luthn disconnect codex
luthn disconnect claude
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

Claude Code currently uses the same lifecycle contract. Hermes remains a
separate planned integration using its official MemoryProvider interface.

The installer seeds only public-safe demo context. Real agent writes remain
untrusted candidates until classification and policy allow an agent-safe
projection.
