# Project Structure Reference

Canonical project boundaries and dependency direction live in
`docs/project-context.md`.

Use this file for historical mapping and structure-specific details only.

## Current Structure

```text
Luthn.sln

src/
  Luthn.Core/
  Luthn.Core.Persistence/
  Luthn.Host.Api/
  Luthn.Host.Worker/
  Luthn.Tools/
  Luthn.Sdk/
  Luthn.AgentConnector.Http/
  Luthn.McpServer/

tests/
  Luthn.Core.Tests/
  Luthn.Core.Persistence.Tests/
  Luthn.Host.Api.Tests/
  Luthn.Sdk.Tests/
  Luthn.AgentConnector.Tests/
  Luthn.McpServer.Tests/
  Luthn.Tools.Tests/
```

## Completed Initial Mapping

| Previous project/path | Current project/path | Reason |
|---|---|---|
| `src/Luthn.Domain` | `src/Luthn.Core` | Core model belongs in the engine package. |
| `src/Luthn.Application` | `src/Luthn.Core` | Previous application services are core use cases. |
| `src/Luthn.Infrastructure` | `src/Luthn.Core.Persistence` | Previous infrastructure is persistence-specific. |
| `src/Luthn.Api` | `src/Luthn.Host.Api` | API is a self-host runtime host. |
| `src/Luthn.Worker` | `src/Luthn.Host.Worker` | Worker is a self-host runtime host. |
| `src/Luthn.Console` | `src/Luthn.Tools` | Keep one bounded tools/admin host. |
| none | `src/Luthn.Sdk` | Public developer/plugin contracts. |
| none | `src/Luthn.AgentConnector.Http` | Thin client for agents, MCP server, and integrations. |
| none | `src/Luthn.McpServer` | Universal agent tool adapter over HTTP. |

## Naming Reference

Preferred implemented names:

```text
CoreEntity
CoreRelationship
CoreTags
coreTags
CoreGraphModelTests
```

Do not use reserved hosted product naming for implemented source/API contracts.
