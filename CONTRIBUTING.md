# Contributing

Luthn welcomes public bug reports, feature requests, documentation feedback,
and security reports.

## Issues

Anyone may open an issue. Before submitting:

1. Search existing issues for the same problem or request.
2. Use the closest issue form and provide a minimal reproducible example when
   reporting a bug.
3. Remove tokens, credentials, private source records, customer data, local
   paths, and other sensitive material from logs and screenshots.
4. Use GitHub's private vulnerability reporting flow for security issues.

## Pull Requests

Pull request creation is temporarily restricted to repository collaborators.
If you are not an invited collaborator, open an issue instead. The maintainer
will triage the request and decide whether to schedule an implementation.

Invited collaborators should:

1. Read `docs/project-context.md`.
2. Add or update focused tests for behavior, schema, or contract changes.
3. Run the validation profile listed in `docs/project-context.md`.
4. Keep generated planning, analysis, review, report, handoff, evidence, and
   local agent state out of commits.

Changes to auth, authorization, service-token scopes, sensitive access, audit,
persistence, MCP, agent boundaries, raw-source or Vault boundaries, licensing,
or hosted-service boundaries require explicit maintainer review.

Contributions to AGPL-licensed components are licensed under AGPL-3.0-only.
Contributions to Apache-2.0 components are licensed under Apache-2.0.
