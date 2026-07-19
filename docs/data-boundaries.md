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
auto-recall instructions. Hook delivery is asynchronous and fail-open, so an
unavailable Luthn service does not block completion of a Codex turn.

Default auto-recall does not expose the private store. It asks the scoped MCP
surface for one small agent-safe context pack at a new task or material topic
change. The same classification, policy, and safe-projection rules apply to
automatic recall and explicit MCP reads. Optional `projectKey`, `taskKey`, and
`topicTags` values are normalized, bounded, classified with the complete safe
projection, and may contain only non-sensitive identifiers. Raw workspace and
transcript paths are neither recall metadata nor persisted capture fields.

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

## Provider Boundary

- Fresh packaged installs use an explicit `unconfigured` state. Classification
  fails with a bounded provider-unavailable response before raw content is
  persisted or projected.
- The mock classifier is local, credential-free, and limited to tests and local
  experiments. Both `Provider=mock` and `AllowMock=true` are required; a stored
  mock selection remains blocked after upgrade without that explicit opt-in.
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
