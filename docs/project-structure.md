# Project Structure Reference

[한국어](project-structure.ko.md)

Canonical project boundaries and dependency direction live in
`docs/project-context.md`. This file records the current solution layout and
naming conventions only.

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
