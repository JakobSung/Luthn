# Licensing Model

[한국어](licensing.ko.md)

## Repository License Scope

Luthn uses a component-based licensing model.

This repository licenses only the source code contained in this repository.

## Component Licenses

| Component | License | Notes |
|---|---|---|
| Luthn.Core | AGPL-3.0-only | Core engine and safe knowledge model |
| Luthn.Core.Persistence | AGPL-3.0-only | Persistence and PostgreSQL wiring |
| Luthn.Host.Api | AGPL-3.0-only | Self-host API and operator console |
| Luthn.Host.Worker | AGPL-3.0-only | Background host scaffold |
| Luthn.Tools | AGPL-3.0-only | Bounded admin and diagnostic commands |
| Luthn.Sdk | Apache-2.0 | Public DTOs and client contracts |
| Luthn.AgentConnector.Http | Apache-2.0 | HTTP connector for external agents |
| Public plugin templates | Apache-2.0 | Templates for external services |
| Public agent recipes/skills, where possible | Apache-2.0 | Integration material intended for broad reuse |

## AGPL Boundary

Core and self-host runtime components contain the knowledge model, policy logic, persistence, and self-host service boundary. They are AGPL-3.0-only.

## Apache Boundary

SDKs, HTTP connectors, and public plugin templates should remain thin protocol/client boundaries. They must not depend directly on Luthn.Core unless the license boundary is reviewed.

## MCP Boundary

Luthn.McpServer should remain an HTTP adapter over the agent-safe API. Do not directly link it to Core unless the licensing boundary is reviewed.

Do not claim Apache-2.0 for Luthn.McpServer in this change.

## Out of Scope: Luthn Ontology

Luthn Ontology is a separate commercial service and is not included in this repository. This repository does not grant rights to non-public Luthn Ontology source code, managed service infrastructure, billing systems, tenant management systems, private first-party services, or official hosted operations code.

## Trademarks

The Luthn name, logos, wordmarks, and service marks are governed separately by the project trademark policy.

## Contributor Note

Contributions to AGPL-licensed components are licensed under AGPL-3.0-only.

Contributions to Apache-2.0 components are licensed under Apache-2.0.

Do not add CLA terms unless a CLA file and review process are actually added.

## Third-Party Inspiration Policy

- Do not copy third-party source code, examples, diagrams, README prose, command text, brand assets, badges, or marketing claims.
- Do not import dependencies unless their license is reviewed and documented.
- Do not model Luthn's product identity around another project's brand, terminology, or proprietary positioning.
- Generic patterns may be studied, but implementation and documentation must be original.
