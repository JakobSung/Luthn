# Data Boundaries Reference

Canonical data-boundary rules live in `docs/project-context.md`.

Use this file for concrete classification examples.

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

- The mock classifier is local, credential-free, and limited to tests and local
  experiments when no external provider is configured.
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
