# Data Boundaries Reference

[한국어](data-boundaries.ko.md)

Luthn separates private source data from the safe projections that agents can
retrieve. Agent visibility never implies access to the original record or
permission to publish it externally.

Canonical repository rules live in `docs/project-context.md`. This document
provides the operator-facing model and concrete classification examples.

## Boundary Model

```text
raw intake
  -> classify and apply policy
  -> private or sensitive record stays behind the storage boundary
  -> approved safe projection becomes available to agent APIs and MCP
  -> separately approved public projection may enter the publication outbox
```

- Raw intake may be inspected by the configured classifier, so provider choice
  is part of the data boundary.
- Storage classification and agent visibility are separate decisions. A stored
  record is not automatically agent-visible.
- Agent-facing reads return safe projections, not unrestricted source bodies.
- External publication requires a separate explicit approval even when a
  projection is already visible to agents.
- Revocation or expiry removes eligibility without copying the private source
  into audit or sync records.

Use this file for concrete classification examples.

## Classification Contract

Before policy routing, Luthn classifies the complete projection that could
later appear in wiki, shared memory, search, MCP, or agent context: `content`,
`title`, `safeSummary`, and every `coreTags` entry. A sensitive signal in any
one field makes the combined projection sensitive.

Sensitivity levels have these bounded meanings:

- `Public`: deliberately public or team-shareable material with no known
  sensitive signal. It may be eligible for agent context and wiki review.
- `Internal`: non-sensitive operational knowledge that is not intended to be
  public by default. It requires review and is not automatically agent-visible.
- `Confidential`: personal, customer, contractual, financial, accounting, or
  private communication material. It remains behind the sensitive boundary.
- `Restricted`: credentials, access or private keys, and raw customer
  originals. It remains behind the sensitive boundary and requires human
  review.

Category taxonomy version `1` uses stable canonical category names:

- Restricted: `credential`, `private key`, `access key`, `customer original`
- Confidential: `contract`, `invoice`, `payment`, `tax`, `customer`, `email`,
  `personal identifier`, `finance`, `accounting`, `private message`,
  `incident log`

The local mock recognizes bounded English and Korean marker phrases for this
taxonomy. It remains a test and experiment classifier, not a production
quality claim.

Every configured operational provider is combined with local sensitive-data
guard version `1`. The guard recognizes bounded high-confidence private-key,
access-token, assigned-secret, email, Korean phone and resident-registration,
and Luhn-valid payment-card shapes. It returns canonical category names only;
matched values and excerpts are not added to classification results, logs,
metrics, audits, or persistence metadata. Provider errors still fail before
storage and never fall back to detector-only acceptance.

`ExternalHttp` is the self-hosted-capable classifier boundary. Operators may
point it at a local or private-network AI service; any non-local transfer is an
explicit operator configuration decision. The local guard applies after every
valid provider response and can only make routing more restrictive.

Provider output is normalized before policy evaluation. A sensitive category
raises sensitivity to at least its taxonomy minimum; `containsSensitiveMaterial`
raises sensitivity to at least `Confidential`; and `Confidential` or
`Restricted` always sets `containsSensitiveMaterial`. Contradictory fields can
therefore only make routing more restrictive, never more public.

## Classification Golden Evaluation

The versioned synthetic corpus at `data/classification/golden-v1.json` is the
baseline quality contract. It is Korean-majority and also includes English and
mixed-language cases, positive and negative examples, and signals that appear
only in `title`, `safeSummary`, or `coreTags`. It contains no production,
customer, or real credential data.

The evaluator validates dataset and taxonomy versions, unique bounded case
identifiers, canonical categories, and consistent sensitivity and routing
expectations before running any classifier. Its JSON report contains only case
identifiers, expected and actual classifications, routing decisions, and
aggregate false-negative, false-positive, and mismatch counts. Raw corpus text
is not copied into the report.

Evaluation uses the local deterministic mock by default and performs no network
request. `--provider guarded-mock` exercises the same local hybrid guard without
creating an API client. Testing a configured API requires both `--provider configured-api` and
the explicit `--allow-external-provider` acknowledgement because the API may
relay corpus text to its configured classifier. An optional bearer token can be
read only from the named environment variable supplied with `--token-env`; the
token value is never included in evaluator output.

## Codex Capture And Recall Boundary

On macOS, Linux, and Windows, the trusted Codex Stop hook accepts a bounded
host event and constructs a capsule from the final assistant response only. It
does not read or upload the transcript, user prompts, working-directory path,
or transcript path. Session and turn identifiers are hashed before delivery,
the summary is capped below the API limit, and recognized credential patterns
cause the entire capsule to be dropped locally.

The service token remains in Luthn's protected configuration. It is not copied
into Codex hook configuration, MCP registration, connector state, or
auto-recall instructions. Hook delivery is asynchronous on macOS and Linux. On
Windows it is synchronous within a 10-second hook timeout so the host cannot
terminate a detached uploader. Delivery remains fail-open on every platform,
although an unavailable service can delay a Windows turn until the bounded
request fails.

Each newly accepted automatic turn capsule becomes an `Ephemeral` memory with
a bounded expiry based on the server receipt time. The default is 30 days and
operators may configure 1 through 365 days. At expiry the memory is no longer a
recall, search, sync, or publication candidate. The default API cleanup loop
then physically removes only local-only expired capsules that remain linked to
their `turn-summary` source through provenance and have no outbox history. The
memory, encrypted payload, provenance, classification, and source event are
deleted transactionally. Existing audit history remains and one metadata-only
cleanup event is added. Historical Durable rows, explicit memory, other source
types, externally approved or revoked records, and outbox-linked records are
not cleaned automatically. Explicit curated memory retains its independently
requested `Durable`, `Session`, or `Ephemeral` lifecycle.

Default auto-recall does not expose the private store. It asks the scoped MCP
surface for one small agent-safe context pack at a new task or material topic
change. The same classification, policy, and safe-projection rules apply to
automatic recall and explicit MCP reads. Optional `projectKey`, `taskKey`, and
`topicTags` values are normalized, bounded, classified with the complete safe
projection, and may contain only non-sensitive identifiers. Raw workspace and
transcript paths are neither recall metadata nor persisted capture fields.
Search-quality telemetry is aggregate and in-memory. It uses allowlisted
surface, outcome, cache-status, result-count, duration, and feedback-judgment
values only. Queries, tags, project/task/topic keys, cache keys, titles,
summaries, result identifiers, raw errors, and free-form feedback are excluded.

## Public-Safe Knowledge May Contain

- product names intended for team/public use
- non-sensitive implementation notes
- safe summaries
- redacted source references
- runbooks
- policy-approved project metadata safe for agent context

## Sensitive Store Must Contain

- raw customer originals
- raw contracts, quotes, payment, tax, finance, and accounting records
- raw private emails/messages
- credential-bearing operational material
- unredacted incident logs containing private operational data
- any record that policy marks as private-only

## Sensitive Shared-Memory Encryption

Shared-memory content that is sensitive or not eligible for agent context uses
a separate `sensitive_memory_payloads` table. Its title, summary, tags,
project/task/topic metadata, and source-session correlation are serialized as a
versioned payload and protected with authenticated ASP.NET Core Data Protection
using a purpose bound to the memory record ID. The ordinary
`shared_memory_items` row contains only fixed inert placeholders and routing
metadata; its search fields contain no user text. Ciphertext is not returned by
agent APIs and is not copied into recall, sync, publication, audit, logs, or
metrics.

The Data Protection key ring lives in the separate `luthn-operator` volume,
not PostgreSQL. This protects a database dump or PostgreSQL-volume-only
compromise. It does not protect against host administrator/root access or an
attacker who obtains both the PostgreSQL data and operator key volume. Existing
private or sensitive rows are converted transactionally before product traffic
is admitted. A missing key ring, wrong purpose, unsupported payload version, or
invalid ciphertext leaves the data unchanged, makes `/readyz` fail, and blocks
product routes while `/healthz` stays available.

Database recovery therefore requires the matching operator key volume. Back up
and restore the PostgreSQL database and `luthn-operator` key material as one
recovery set. Never commit, print, or copy key XML into ordinary logs or an
unencrypted repository. Losing the key ring makes encrypted memory
unrecoverable; generating a new key ring does not decrypt existing payloads.

## Server-Trusted Ownership Boundary

Authorization ownership is a server-side property, not collection metadata.
`SingleOwner` maps existing anonymous and service-token operation to one
normalized local owner. `MultiUser` fails closed unless each non-operator
product token has a bounded configured user identity. Caller-supplied
`provenance.userId`, request JSON, headers, agent names, application names, and
connector metadata never select or change the owner.

Owned source events, shared memory, wiki proposals, sensitive references and
requests, provenance, safe-sync outbox rows, and agent-connection state are
stamped in their write transaction. Every agent-safe read and ranking query
filters by owner before selection. Agent-connection upsert and status grouping
use owner plus agent plus channel; only explicit operators can list all owners,
with bounded owner attribution. Turn-summary idempotency includes the owner partition, and MCP
context-pack cache keys include a non-reversible credential partition. No user
identity, bearer digest, or provenance claim enters safe projections or cache
status output. Sensitive-access status polling uses the same partition with a
one-second bounded cache so state changes cannot remain stale beyond that
window. Operators are explicit token configuration, not a caller
header; their cross-owner actions stay limited to management routes and
metadata-only audit records.

## Collection Provenance Boundary

Every source event and shared-memory item has one versioned immutable
`collection_provenance` row. It stores the server-derived authenticated ingest
actor, authenticated owner user, and receipt time separately from optional caller claims for user, agent,
application, plugin, connector, connector version, and client collection time.
Caller claims are not authentication or tenancy evidence. Legacy rows use
explicit `legacy-unknown` trust and null origin claims.

Provenance identifiers are bounded and normalized. Raw workspace or transcript
paths, device fingerprints, prompts, query text, credentials, free-form source
metadata, and source content are excluded. Provenance follows its source or
memory retention lifecycle and has no update/delete API. It is available only
through operator-authorized `audit.read` routes and is excluded from agent
recall, search indexes, encrypted user payloads, safe sync, publication, audit
payloads, logs, and metrics. Audit remains the event history of actions and
decisions; provenance remains the immutable origin statement.

## Provider Boundary

- Fresh packaged installs use the local `mock` classifier, so classification
  works without a separate provider setup.
- The mock classifier is local and credential-free. Both `Provider=mock` and
  `AllowMock=true` are set by the installation default; replace it with an
  operator-configured provider when provider-backed classification is required.
- Operator-configured provider secrets are server-side only; console/API
  responses expose only whether a key is present.
- External classification is explicit opt-in configuration.
- Direct third-party LLM providers (`ChatGPT API`, `Claude API`, `Google AI
  API`, and `OpenRouter API`) receive raw source content in the classification
  prompt before Luthn assigns sensitivity. Use them only when the operator
  accepts that external transfer; use a controlled `External HTTP` provider when
  raw intake content must stay in a self-hosted boundary.
- Direct third-party LLM provider endpoints must be HTTPS URLs on the expected
  provider host before Luthn sends the API key header.
- Provider calls carry payload class and redaction state metadata so intake
  remains auditable.
- Audit rows record provider boundary metadata and storage decisions, not raw
  source content.
- Normal automated tests must use mock or fake providers unless an integration
  test is explicitly enabled by an operator.

## External Publication Boundary

- Agent visibility does not imply permission to publish externally.
- Only an explicitly approved public, non-expired, agent-visible safe
  projection can enter the external-publication outbox.
- Revocation records contain identity, revision, operation, and bounded policy
  metadata only; they do not repeat the projection body.
- Raw source/Vault data, private memory, credentials, prompts, transcripts,
  local paths, and sensitive-access output must never enter the sync contract.
- The public self-host build has no active cloud transport and performs no
  external sync by default.
