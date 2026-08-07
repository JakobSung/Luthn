# Central Team Hub Data Plane

[한국어](cloud-hub-data-plane.ko.md)

Status: the public runtime implements the initial opt-in encrypted durable
ingress, bounded local worker, disabled/fake relay boundary, and deterministic
recovery harness. Cloud enrollment, identity control plane, and real outbound
transport remain unimplemented.

## Deployment boundary

Personal self-hosting keeps the current model: one user installs Luthn and
connects local Agents. Team Cloud mode will use one central OSS Hub per
Organization on an administrator-managed, always-on server or PC. Other members
will not install Docker, the complete OSS runtime, or a native Luthn client.

Members will join a Cloud Organization and Workspace and connect Codex or Claude
Code through a per-user, per-device, per-Agent connection. Remote MCP with OAuth
will provide recall and tools. Reliable turn capture will also require an
Agent-native Stop hook, plugin, or managed configuration; MCP registration alone
does not observe every Agent lifecycle event.

The public repository owns the Hub data plane and its local metadata-only audit
trail. Private Luthn Cloud owns the Organization control plane, member identity,
Agent connection enrollment, relay, managed shared-memory plane, Cloud-side
audit aggregation, subscription, and managed operations.

## Trusted identity hierarchy

```text
Organization                 Cloud control plane
  -> Workspace               Cloud binding, OSS authorization boundary
     -> HubInstallation      customer-operated OSS runtime
     -> Membership           human access
        -> AgentConnection   one member + device + Agent
           -> AgentSession
              -> Turn
```

Cloud credentials bind the Organization, Workspace, Membership, and
AgentConnection. At the Hub request boundary, trusted middleware derives and
stamps `WorkspaceId`, `UserId`, `ActorKind`, `ActorId`, `AgentConnectionId`,
`SessionId`, and the turn idempotency identity. A request body, SDK field,
connector field, MCP argument, or arbitrary header cannot override them.

The OSS persistence model does not become the source of truth for Organization
membership, billing, entitlement, or Cloud roles. It persists only the bounded
identity required to authorize, partition, attribute, deduplicate, and audit
Hub data-plane work.

## Target ingestion flow

```text
Agent lifecycle connector
  -> authenticated Hub ingress through the Cloud relay
  -> validate schema, size, and trusted identity
  -> idempotency check
  -> atomically persist encrypted raw capsule + ingress queue row
  -> 202 Accepted

Classification worker
  -> lease ingress item
  -> bounded provider classification + deterministic guard
  -> policy decision
     -> sensitive/private: encrypted Hub payload store
     -> safe/shareable: versioned safe projection
     -> uncertain: review-required

Safe publication worker
  -> current durable safe-projection outbox
  -> Cloud acknowledgement/checkpoint
  -> body-free revoke before delayed revisions
```

The Agent-facing public endpoint and certificate are managed by Cloud. The Hub
maintains an outbound authenticated relay connection, so the administrator does
not expose an inbound port or manage a public certificate. A raw capsule is
encrypted for the Hub before relay. Cloud can temporarily buffer only the
Hub-encrypted envelope under a bounded TTL; plaintext remains inside the Hub
boundary.

## Queue contracts

### Ingress queue

- Admission performs authentication, bounded size/schema validation, and
  idempotency validation before persistence.
- The encrypted raw capsule, trusted attribution, and queue row commit in one
  PostgreSQL transaction.
- `202 Accepted` means a durable owner has accepted the event, not that
  classification or Cloud sync has completed.
- Duplicate delivery is a successful no-op and returns the existing opaque
  receipt.
- Pending work survives Hub restart. Accepted events are never silently dropped.

### Classification queue

- Workers use leases, bounded global concurrency, and bounded per-Workspace
  concurrency.
- Provider timeout, retry, and exponential backoff do not hold open the ingress
  request.
- Exhausted work enters a metadata-only dead-letter state. Replay is explicit,
  audited, idempotent, and passes current policy again.
- Provider failure or uncertainty cannot downgrade data into a safe projection.

### Safe-projection outbox

- Only an approved versioned safe projection or body-free revoke is queued.
- Origin, local record, revision, operation, and Workspace form the ordering and
  idempotency boundary.
- Acknowledgement advances a durable checkpoint.
- Reconnect and restore apply revocation tombstones before delayed revisions so
  deleted memory cannot resurrect.

## Backpressure and fairness

The remote-IP limiter alone is not a sufficient team boundary because all
members can arrive through one Cloud relay address. The OSS Hub baseline already
applies Organization, Workspace, Membership, and AgentConnection request and
byte budgets. Future Cloud relay admission must preserve those scope limits and
also enforce outstanding-queue limits and global/per-Workspace worker
concurrency.

When a hard limit is reached, ingress returns an explicit retryable status,
stable error code, and `Retry-After`. It must not acknowledge and later discard
the event. One noisy Workspace must not starve another.

## Observability

Content-free metrics and status must include:

- accepted, duplicate, rejected, and backpressured ingress counts;
- ingress queue depth, bytes, and oldest pending age;
- active classification work, provider latency, retry/exhausted counts, and
  dead-letter depth;
- safe-projection outbox depth, oldest pending age, acknowledgement rate, and
  Cloud synchronization lag;
- relay heartbeat and connected, stale, disconnected, or revoked state;
- per-Workspace saturation without prompts, transcripts, summaries,
  credentials, local paths, or sensitive values.

## Implemented OSS baseline

The public runtime now contains the first Hub data-plane baseline. It remains
disabled by default and does not make a Cloud request. The shipped defaults are:

- maximum capsule size: `16384` bytes;
- pending limits: Organization `5000`, Workspace `1000`, Member `500`, Agent
  `250`;
- per-minute admission limits: Organization `6000`, Workspace `1200`, Member
  `600`, Agent `300`;
- worker batch `20`, per-Workspace batch `5`, poll interval `5` seconds, lease
  `120` seconds, maximum attempts `5`, and base retry delay `2` seconds.

Ingress derives Hub organization, Workspace, member, Agent, and session identity
from the trusted server token binding. It protects the capsule with the local
Data Protection key ring, commits the queue row and metadata-only audit event
atomically, returns a content-free `202` receipt, and never accepts caller
identity overrides. The worker recovers expired leases, retries bounded
provider failures, creates metadata-only dead letters, and permits explicit
same-Workspace operator replay. `/api/hub/status` exposes aggregate admission,
queue, worker, outbox, relay, and provider-latency status without identities,
capsules, prompts, transcripts, credentials, or local paths.

The deterministic test harness covers 10 normal users, 50 one-item users, a
50-request burst with explicit backpressure accounting, delayed providers,
lease recovery, dead-letter replay, zero-outbound behavior, and relay
reconnect/revoke-first ordering. These are correctness and recovery baselines,
not production capacity or latency SLOs.

## Remaining Cloud boundary

The next boundary is outside the current OSS runtime: versioned enrollment and
capability exchange, Cloud-issued connection authority, an authenticated relay
transport, remote MCP/OAuth lifecycle capture, and managed Organization
operations. Any future adapter must preserve the authenticated-installation
tenant derivation, metadata-only audit contract, safe-projection-only payload,
revoke-first ordering, and disabled-by-default personal self-host path.

## Future capacity and recovery evidence

Focused and automated evidence must cover:

| Scenario | Required result |
| --- | --- |
| 10 normal users | Stable ingress and classification without loss |
| 50 users, one turn/minute | Sustained throughput and Workspace fairness |
| 50 simultaneous completions | Explicit admission/backpressure and bounded drain |
| Provider delayed 5s and 30s | Ingress remains responsive; queue age is visible |
| Provider errors/rate limits | Bounded retry, dead-letter, audited replay |
| Cloud outage/recovery | Ordered outbox replay and revoke-first behavior |
| Duplicate/out-of-order events | Successful no-op and deterministic final state |
| Hub restart with pending work | Queue, lease, and checkpoint recovery |
| Noisy Workspace | Other Workspace stays within its objective |

Do not treat an ingress p95, queue-age, or design-partner target as a product
SLO yet. Before setting capacity targets, record hardware, PostgreSQL settings,
concurrency, provider, throughput, p50/p95/p99, CPU/memory, failures, retries,
and queue/sync lag while preserving zero acknowledged loss and idempotent
no-duplicate behavior.

## Current non-goals

- changing or removing the existing personal self-host workflow;
- installing the complete runtime, Docker, or a native Luthn client on every
  member PC;
- making the OSS Hub the source of truth for Organization membership or billing;
- accepting caller-selected tenant, Workspace, member, Agent, or session scope;
- sending raw or sensitive plaintext through the safe-projection contract;
- silently accepting data when no durable owner can guarantee recovery;
- multi-Hub high availability, multi-region data planes, SAML, SCIM, or custom
  enterprise roles.
