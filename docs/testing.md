# Test validation contract

This document (`docs/testing.md`) is the canonical test inventory and
validation contract for Luthn. It separates fast feedback, changed-surface
focused checks, delivery validation, and environment-dependent lifecycle
checks. A tier describes the
default execution path, not the importance of the coverage.

## Current baseline

Before this slice, the repository contained 53 test-related files: 36 C# test
sources, 7 .NET test projects, and 10 files under `scripts/tests/`. The latest
full .NET run covered 392 test cases. The inventory checker added by this
slice is itself mapped below, so the canonical inventory now contains 54
files; the .NET test-case baseline remains 392.

File count and test-case count are tracked separately. A future reduction must
show the retained security, sensitive-data, retrieval, recall, ownership, and
platform coverage before removing or consolidating cases.

## Tiers

| Tier | Contract | Typical use |
| --- | --- | --- |
| `fast` | Deterministic unit, contract, mock, or local script checks with no external service or container. | Run during ordinary implementation feedback. |
| `focused` | Deterministic tests for the changed project or behavior surface; use a class/name filter where possible. | Run after each slice or fix. |
| `full` | Solution-wide build, test, formatting, and delivery checks. | Run once on the clean reviewed delivery head. |
| `environmental` | PostgreSQL, Docker, Windows, distribution, or real connector lifecycle checks. | Run when the required environment is available and before a release-sensitive delivery. |

## Command matrix

The commands below define the default boundary between fast, focused, full,
and environmental validation. A slice may add a narrower command, but it must
not silently substitute a fast check for a required environmental or full
check.

| Tier | Command | Purpose |
| --- | --- | --- |
| `fast` | `./scripts/tests/test-test-inventory.sh` | Prove every file under `tests/` and `scripts/tests/` appears exactly once in the canonical matrix with an allowed tier. |
| `fast` | `dotnet test tests/Luthn.Core.Tests/Luthn.Core.Tests.csproj --no-restore` | Run deterministic core classification, policy, retrieval, projection, ingestion, and rendering contracts. |
| `fast` | `dotnet test tests/Luthn.AgentConnector.Tests/Luthn.AgentConnector.Tests.csproj --no-restore` | Run connector client contract tests without a live agent runtime. |
| `fast` | `dotnet test tests/Luthn.Sdk.Tests/Luthn.Sdk.Tests.csproj --no-restore` | Run SDK contract tests. |
| `fast` | `dotnet test tests/Luthn.Tools.Tests/Luthn.Tools.Tests.csproj --no-restore` | Run deterministic tool and token-digest tests. |
| `fast` | `python3 -m unittest discover -s scripts/tests -p 'test_*.py'` | Run local Python connector, release-container, and version-contract checks. |
| `fast` | `bash scripts/tests/test-local-script-safety.sh` | Check local script safety and generated configuration boundaries. |
| `focused` | `dotnet test tests/Luthn.Host.Api.Tests/Luthn.Host.Api.Tests.csproj --no-restore --filter FullyQualifiedName~MemoryEndpointTests` | Validate memory write/read/query behavior when memory endpoints are changed. |
| `focused` | `dotnet test tests/Luthn.Host.Api.Tests/Luthn.Host.Api.Tests.csproj --no-restore --filter FullyQualifiedName~SensitiveMemoryProtectionTests` | Validate encryption, migration, tamper, and fail-closed behavior when sensitive-memory code is changed. |
| `focused` | `dotnet test tests/Luthn.Host.Api.Tests/Luthn.Host.Api.Tests.csproj --no-restore --filter FullyQualifiedName~OwnershipIsolationTests` | Validate owner derivation and mutation/read isolation when authorization boundaries are changed. |
| `focused` | `dotnet test tests/Luthn.McpServer.Tests/Luthn.McpServer.Tests.csproj --no-restore --filter FullyQualifiedName~McpToolBoundaryTests` | Validate MCP tool exposure and boundary behavior when the connector surface is changed. |
| `focused` | `dotnet test tests/Luthn.Core.Persistence.Tests/Luthn.Core.Persistence.Tests.csproj --no-restore` | Validate persistence contracts when schema or projection publication code is changed. |
| `focused` | `python3 -m unittest scripts/tests/test_release_container.py` | Validate release-container behavior without starting the external lifecycle. |
| `full` | `dotnet build Luthn.sln --no-restore` | Prove the complete solution still builds on the delivery head. |
| `full` | `dotnet test Luthn.sln --no-restore` | Run the complete .NET regression suite once for the delivery batch. |
| `full` | `dotnet format Luthn.sln --no-restore --verify-no-changes` | Prove the reviewed delivery head has no formatting drift. |
| `environmental` | `bash scripts/tests/test-agent-connector-lifecycle.sh` | Exercise the connector lifecycle against the local distribution runtime, including restart and ownership behavior. |
| `environmental` | `bash scripts/tests/test-claude-connector-lifecycle.sh` | Exercise the Claude connector lifecycle against the local distribution runtime. |
| `environmental` | `bash scripts/tests/test-distribution-lifecycle.sh` | Exercise Docker distribution startup, migration, update, rollback, safe projection, and persistence behavior. |
| `environmental` | `bash scripts/tests/test-postgres-integration-smoke.sh` | Start an isolated PostgreSQL container and run the opt-in integration smoke test. |
| `environmental` | `pwsh -File scripts/tests/test-windows-codex-hook-smoke.ps1` | Validate the Windows Codex hook smoke path. |
| `environmental` | `pwsh -File scripts/tests/test-windows-lifecycle.ps1 -RepoRoot $PWD` | Validate Windows install, update, migration, backup, rollback, and cleanup behavior. |

The full and environmental commands are not required for every local edit.
They are required when the changed surface or the delivery contract calls for
them. The full solution command is intentionally retained as a delivery
regression even when focused checks are green.

## Canonical inventory

Every file under `tests/` and `scripts/tests/` must have exactly one row. The
inventory checker parses the path and tier columns, compares them with the
live filesystem, and rejects missing, duplicate, or unknown-tier entries.

| Path | Tier | Primary command | Retained coverage boundary |
| --- | --- | --- | --- |
| `scripts/tests/test-agent-connector-lifecycle.sh` | `environmental` | `bash scripts/tests/test-agent-connector-lifecycle.sh` | Live connector lifecycle, restart, ownership, and rollback behavior. |
| `scripts/tests/test-claude-connector-lifecycle.sh` | `environmental` | `bash scripts/tests/test-claude-connector-lifecycle.sh` | Claude connector lifecycle and failure handling. |
| `scripts/tests/test-distribution-lifecycle.sh` | `environmental` | `bash scripts/tests/test-distribution-lifecycle.sh` | Docker distribution startup, update, rollback, persistence, and safe projection. |
| `scripts/tests/test-local-script-safety.sh` | `fast` | `bash scripts/tests/test-local-script-safety.sh` | Local script safety and generated configuration checks. |
| `scripts/tests/test-postgres-integration-smoke.sh` | `environmental` | `bash scripts/tests/test-postgres-integration-smoke.sh` | Opt-in PostgreSQL integration smoke coverage. |
| `scripts/tests/test-test-inventory.sh` | `fast` | `./scripts/tests/test-test-inventory.sh` | Exact synchronization between the live test tree and this matrix. |
| `scripts/tests/test-windows-codex-hook-smoke.ps1` | `environmental` | `pwsh -File scripts/tests/test-windows-codex-hook-smoke.ps1` | Windows Codex hook smoke behavior. |
| `scripts/tests/test-windows-lifecycle.ps1` | `environmental` | `pwsh -File scripts/tests/test-windows-lifecycle.ps1 -RepoRoot $PWD` | Windows lifecycle, update, migration, backup, rollback, and cleanup. |
| `scripts/tests/test_codex_connector.py` | `fast` | `python3 -m unittest discover -s scripts/tests -p 'test_*.py'` | Deterministic Codex hook, instruction, and turn-capsule contracts. |
| `scripts/tests/test_release_container.py` | `focused` | `python3 -m unittest scripts/tests/test_release_container.py` | Release-container command and configuration contracts. |
| `scripts/tests/test_version_contract.py` | `fast` | `python3 -m unittest discover -s scripts/tests -p 'test_*.py'` | Version and release metadata contracts. |
| `tests/Luthn.AgentConnector.Tests/Luthn.AgentConnector.Tests.csproj` | `fast` | `dotnet test tests/Luthn.AgentConnector.Tests/Luthn.AgentConnector.Tests.csproj --no-restore` | Connector client project-level contracts. |
| `tests/Luthn.AgentConnector.Tests/LuthnClientTests.cs` | `fast` | `dotnet test tests/Luthn.AgentConnector.Tests/Luthn.AgentConnector.Tests.csproj --no-restore` | Connector client request, response, and error mapping. |
| `tests/Luthn.Core.Persistence.Tests/Luthn.Core.Persistence.Tests.csproj` | `focused` | `dotnet test tests/Luthn.Core.Persistence.Tests/Luthn.Core.Persistence.Tests.csproj --no-restore` | Persistence project-level contracts. |
| `tests/Luthn.Core.Persistence.Tests/PersistenceContractTests.cs` | `focused` | `dotnet test tests/Luthn.Core.Persistence.Tests/Luthn.Core.Persistence.Tests.csproj --no-restore` | Persistence model and storage contract behavior. |
| `tests/Luthn.Core.Persistence.Tests/SafeProjectionPublicationTests.cs` | `focused` | `dotnet test tests/Luthn.Core.Persistence.Tests/Luthn.Core.Persistence.Tests.csproj --no-restore` | Safe projection publication and persistence boundaries. |
| `tests/Luthn.Core.Tests/ClassificationContractTests.cs` | `fast` | `dotnet test tests/Luthn.Core.Tests/Luthn.Core.Tests.csproj --no-restore` | Classifier taxonomy, projection, and Korean/mixed contract cases. |
| `tests/Luthn.Core.Tests/ClassificationGoldenEvaluationTests.cs` | `fast` | `dotnet test tests/Luthn.Core.Tests/Luthn.Core.Tests.csproj --no-restore` | Golden dataset schema, deterministic evaluation, and mismatch accounting. |
| `tests/Luthn.Core.Tests/ContextPackBuilderTests.cs` | `fast` | `dotnet test tests/Luthn.Core.Tests/Luthn.Core.Tests.csproj --no-restore` | Bounded context-pack construction. |
| `tests/Luthn.Core.Tests/CoreGraphModelTests.cs` | `fast` | `dotnet test tests/Luthn.Core.Tests/Luthn.Core.Tests.csproj --no-restore` | Core graph model invariants. |
| `tests/Luthn.Core.Tests/DeterministicSensitiveDataDetectorTests.cs` | `fast` | `dotnet test tests/Luthn.Core.Tests/Luthn.Core.Tests.csproj --no-restore` | Deterministic sensitive-data detection. |
| `tests/Luthn.Core.Tests/Luthn.Core.Tests.csproj` | `fast` | `dotnet test tests/Luthn.Core.Tests/Luthn.Core.Tests.csproj --no-restore` | Core unit and contract project-level checks. |
| `tests/Luthn.Core.Tests/PluginIngestionContractTests.cs` | `fast` | `dotnet test tests/Luthn.Core.Tests/Luthn.Core.Tests.csproj --no-restore` | Plugin ingestion contract behavior. |
| `tests/Luthn.Core.Tests/PolicyEngineTests.cs` | `fast` | `dotnet test tests/Luthn.Core.Tests/Luthn.Core.Tests.csproj --no-restore` | Policy decisions and safety boundaries. |
| `tests/Luthn.Core.Tests/RetrievalBackendTests.cs` | `fast` | `dotnet test tests/Luthn.Core.Tests/Luthn.Core.Tests.csproj --no-restore` | Retrieval backend behavior and bounded selection. |
| `tests/Luthn.Core.Tests/SafeProjectionSyncTests.cs` | `fast` | `dotnet test tests/Luthn.Core.Tests/Luthn.Core.Tests.csproj --no-restore` | Safe projection synchronization. |
| `tests/Luthn.Core.Tests/SafeSearchIndexTests.cs` | `fast` | `dotnet test tests/Luthn.Core.Tests/Luthn.Core.Tests.csproj --no-restore` | Safe search index invariants. |
| `tests/Luthn.Core.Tests/SharedMemoryModelTests.cs` | `fast` | `dotnet test tests/Luthn.Core.Tests/Luthn.Core.Tests.csproj --no-restore` | Shared-memory model and validation behavior. |
| `tests/Luthn.Core.Tests/WikiMarkdownRendererTests.cs` | `fast` | `dotnet test tests/Luthn.Core.Tests/Luthn.Core.Tests.csproj --no-restore` | Wiki proposal markdown rendering. |
| `tests/Luthn.Host.Api.Tests/AgentConnectionEndpointTests.cs` | `focused` | `dotnet test tests/Luthn.Host.Api.Tests/Luthn.Host.Api.Tests.csproj --no-restore --filter FullyQualifiedName~AgentConnectionEndpointTests` | Agent connection observation, auth, and owner-scoped state. |
| `tests/Luthn.Host.Api.Tests/AgentSafeEndpointTests.cs` | `focused` | `dotnet test tests/Luthn.Host.Api.Tests/Luthn.Host.Api.Tests.csproj --no-restore --filter FullyQualifiedName~AgentSafeEndpointTests` | Safe agent search, context, and wiki proposal exposure. |
| `tests/Luthn.Host.Api.Tests/AuthApprovalAuditTests.cs` | `focused` | `dotnet test tests/Luthn.Host.Api.Tests/Luthn.Host.Api.Tests.csproj --no-restore --filter FullyQualifiedName~AuthApprovalAuditTests` | Authorization approval and audit behavior. |
| `tests/Luthn.Host.Api.Tests/AutomaticTurnRetentionCleanupTests.cs` | `focused` | `dotnet test tests/Luthn.Host.Api.Tests/Luthn.Host.Api.Tests.csproj --no-restore --filter FullyQualifiedName~AutomaticTurnRetentionCleanupTests` | Automatic-turn retention and cleanup behavior. |
| `tests/Luthn.Host.Api.Tests/ClassificationPreviewTests.cs` | `focused` | `dotnet test tests/Luthn.Host.Api.Tests/Luthn.Host.Api.Tests.csproj --no-restore --filter FullyQualifiedName~ClassificationPreviewTests` | Classification preview endpoint boundary. |
| `tests/Luthn.Host.Api.Tests/CollectionProvenanceTests.cs` | `focused` | `dotnet test tests/Luthn.Host.Api.Tests/Luthn.Host.Api.Tests.csproj --no-restore --filter FullyQualifiedName~CollectionProvenanceTests` | Collection provenance and source lineage. |
| `tests/Luthn.Host.Api.Tests/ExternalPublicationEndpointTests.cs` | `focused` | `dotnet test tests/Luthn.Host.Api.Tests/Luthn.Host.Api.Tests.csproj --no-restore --filter FullyQualifiedName~ExternalPublicationEndpointTests` | External publication approval and safe projection behavior. |
| `tests/Luthn.Host.Api.Tests/Luthn.Host.Api.Tests.csproj` | `full` | `dotnet test tests/Luthn.Host.Api.Tests/Luthn.Host.Api.Tests.csproj --no-restore` | Host API project-wide regression; source files are focused by class when iterating. |
| `tests/Luthn.Host.Api.Tests/MemoryEndpointTests.cs` | `focused` | `dotnet test tests/Luthn.Host.Api.Tests/Luthn.Host.Api.Tests.csproj --no-restore --filter FullyQualifiedName~MemoryEndpointTests` | Memory write/read/query, redaction, retention, and bounded candidate recall. |
| `tests/Luthn.Host.Api.Tests/OperationalMetricsTests.cs` | `focused` | `dotnet test tests/Luthn.Host.Api.Tests/Luthn.Host.Api.Tests.csproj --no-restore --filter FullyQualifiedName~OperationalMetricsTests` | Bounded operational metrics and search feedback. |
| `tests/Luthn.Host.Api.Tests/OwnershipIsolationTests.cs` | `focused` | `dotnet test tests/Luthn.Host.Api.Tests/Luthn.Host.Api.Tests.csproj --no-restore --filter FullyQualifiedName~OwnershipIsolationTests` | Multi-user owner isolation and forbidden agent mutation. |
| `tests/Luthn.Host.Api.Tests/PostgresIntegrationSmokeTests.cs` | `environmental` | `bash scripts/tests/test-postgres-integration-smoke.sh` | PostgreSQL-backed integration path; opt-in and reset-gated. |
| `tests/Luthn.Host.Api.Tests/RetrievalCandidateSelectorTests.cs` | `focused` | `dotnet test tests/Luthn.Host.Api.Tests/Luthn.Host.Api.Tests.csproj --no-restore --filter FullyQualifiedName~RetrievalCandidateSelectorTests` | Bounded candidate preselection, scope, safety, and recency. |
| `tests/Luthn.Host.Api.Tests/RetrievalEndpointTests.cs` | `focused` | `dotnet test tests/Luthn.Host.Api.Tests/Luthn.Host.Api.Tests.csproj --no-restore --filter FullyQualifiedName~RetrievalEndpointTests` | Search endpoint retrieval, latency, zero-result, and telemetry resilience. |
| `tests/Luthn.Host.Api.Tests/SensitiveMemoryProtectionTests.cs` | `focused` | `dotnet test tests/Luthn.Host.Api.Tests/Luthn.Host.Api.Tests.csproj --no-restore --filter FullyQualifiedName~SensitiveMemoryProtectionTests` | Sensitive payload encryption, migration, tamper detection, and fail-closed writes. |
| `tests/Luthn.Host.Api.Tests/SourceIntakeTests.cs` | `focused` | `dotnet test tests/Luthn.Host.Api.Tests/Luthn.Host.Api.Tests.csproj --no-restore --filter FullyQualifiedName~SourceIntakeTests` | Source intake and safe publication boundary. |
| `tests/Luthn.Host.Api.Tests/TestSensitiveMemoryProtection.cs` | `focused` | `dotnet test tests/Luthn.Host.Api.Tests/Luthn.Host.Api.Tests.csproj --no-restore --filter FullyQualifiedName~SensitiveMemoryProtectionTests` | Sensitive-memory test fixture and protection setup. |
| `tests/Luthn.Host.Api.Tests/TurnSummaryEndpointTests.cs` | `focused` | `dotnet test tests/Luthn.Host.Api.Tests/Luthn.Host.Api.Tests.csproj --no-restore --filter FullyQualifiedName~TurnSummaryEndpointTests` | Turn-summary intake and retention behavior. |
| `tests/Luthn.McpServer.Tests/Luthn.McpServer.Tests.csproj` | `focused` | `dotnet test tests/Luthn.McpServer.Tests/Luthn.McpServer.Tests.csproj --no-restore` | MCP server project-level boundary checks. |
| `tests/Luthn.McpServer.Tests/McpToolBoundaryTests.cs` | `focused` | `dotnet test tests/Luthn.McpServer.Tests/Luthn.McpServer.Tests.csproj --no-restore --filter FullyQualifiedName~McpToolBoundaryTests` | MCP tool names, schemas, and safe boundary behavior. |
| `tests/Luthn.Sdk.Tests/Luthn.Sdk.Tests.csproj` | `fast` | `dotnet test tests/Luthn.Sdk.Tests/Luthn.Sdk.Tests.csproj --no-restore` | SDK project-level contracts. |
| `tests/Luthn.Sdk.Tests/SdkContractTests.cs` | `fast` | `dotnet test tests/Luthn.Sdk.Tests/Luthn.Sdk.Tests.csproj --no-restore` | SDK request and response contracts. |
| `tests/Luthn.Tools.Tests/ClassificationEvaluationCommandTests.cs` | `fast` | `dotnet test tests/Luthn.Tools.Tests/Luthn.Tools.Tests.csproj --no-restore` | Classification evaluation command, mock, external-provider, and output contracts. |
| `tests/Luthn.Tools.Tests/Luthn.Tools.Tests.csproj` | `fast` | `dotnet test tests/Luthn.Tools.Tests/Luthn.Tools.Tests.csproj --no-restore` | Tools project-level deterministic checks. |
| `tests/Luthn.Tools.Tests/ServiceTokenDigestTests.cs` | `fast` | `dotnet test tests/Luthn.Tools.Tests/Luthn.Tools.Tests.csproj --no-restore` | Service-token digest and secret-handling contracts. |

## Duplicate candidates retained for follow-up

These are candidates for consolidation, not approved deletions. The first
slice records the evidence and the coverage that must remain. A later slice
may parameterize or merge cases only after it proves the retained boundary and
compares focused execution time with the current baseline.

| Candidate | Evidence for review | Retained coverage that must remain |
| --- | --- | --- |
| `ClassificationContractTests.cs` / `ClassificationGoldenEvaluationTests.cs` / `ClassificationEvaluationCommandTests.cs` | All three exercise classification taxonomy or evaluation outcomes, but at core contract, corpus validation, and tool/CLI boundaries respectively. | Korean and mixed-language classification contract cases, versioned golden validation and mismatch counts, and command output/provider opt-in behavior. |
| `MemoryEndpointTests.cs` / `AgentSafeEndpointTests.cs` / `SensitiveMemoryProtectionTests.cs` | The files all assert safe projection or sensitive-memory exposure boundaries through different layers. | Memory endpoint redaction and recall, agent-safe route filtering, and encryption/migration/tamper/fail-closed invariants. |
| `RetrievalEndpointTests.cs` / `RetrievalCandidateSelectorTests.cs` / `OperationalMetricsTests.cs` | Search results, bounded candidate selection, and search telemetry are adjacent retrieval concerns with overlapping fixtures. | Endpoint result and zero-result behavior, per-corpus bounded selection and ownership/safety scope, and bounded latency/feedback metrics. |
| `OwnershipIsolationTests.cs` / `AgentConnectionEndpointTests.cs` | Both cover owner-derived agent state and multi-user authorization, but one protects memory/publication state while the other protects connection observations. | Owner-scoped memory, sensitive references, and forbidden mutation; independent connection read/write scopes and owner-scoped channel state. |
| `test-agent-connector-lifecycle.sh` / `test-claude-connector-lifecycle.sh` / `test-windows-lifecycle.ps1` | They share connector/lifecycle failure themes while covering different agents or platforms. | Agent-specific connector behavior, Windows lifecycle and rollback semantics, and distribution/runtime persistence checks. |

No candidate is deleted or merged by this slice. The next optimization slice
must attach a coverage map and before/after timing evidence to any change.
