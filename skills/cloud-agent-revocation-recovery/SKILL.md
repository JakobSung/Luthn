---
name: cloud-agent-revocation-recovery
description: Verify and maintain the public Luthn Cloud AgentDevice recovery path after a server-side device or connection revocation.
---

# Cloud Agent revocation recovery

Use this repository-local skill when changing or validating public Luthn's
`luthn cloud connect` behavior after the Cloud console revokes an
AgentConnection or AgentDevice.

## Scope boundary

- This applies only to the public Luthn client. Cloud tenant authorization and
  the Cloud console remain in the private Cloud repository.
- Never add Cloud credentials, local vault content, prompts, transcripts, or
  local paths to the Cloud AgentDevice state or connection ownership files.
- A Cloud revocation must not remove or interrupt the existing local `luthn`
  MCP registration.

## Required behavior

1. When the remote AgentConnection is no longer active, `cloud-agent` must
   clear the matching connection, pending enrollment, and session from its
   encrypted local state and return `revoked`.
2. `luthn cloud connect` must retain its ownership record while it checks that
   remote state. On `revoked`, `denied`, or `expired`, remove only the owned
   `luthn-cloud` MCP registration and ownership record.
3. The command must exit without silently creating a replacement connection.
   A second explicit invocation begins a new device-approval flow.
4. If the Cloud status check is unavailable or malformed, preserve both local
   MCP registrations and the ownership record. Do not guess that revocation
   occurred.
5. Keep macOS/Linux and Windows scripts behaviorally equivalent.

## Implementation locations

- `src/Luthn.Tools/CloudAgentDeviceCommand.cs`
- `scripts/luthn`
- `scripts/luthn.ps1`
- `tests/Luthn.Tools.Tests/CloudAgentDeviceCommandTests.cs`
- `scripts/tests/test-cloud-agent-connection-lifecycle.sh`
- `scripts/tests/test-windows-cloud-agent-lifecycle.ps1`

## Verification

Run the targeted state-machine and shell lifecycle tests first:

```bash
dotnet test tests/Luthn.Tools.Tests/Luthn.Tools.Tests.csproj --configuration Release
bash scripts/tests/test-cloud-agent-connection-lifecycle.sh
```

Run the complete public suite before delivery:

```bash
dotnet test Luthn.sln --configuration Release
```

The Windows lifecycle test runs in the Windows CI environment:

```powershell
pwsh -NoProfile -File scripts/tests/test-windows-cloud-agent-lifecycle.ps1
```

Confirm all of the following in the lifecycle tests:

- the first connection is additive and keeps local `luthn` registered;
- revocation removes only owned `luthn-cloud` state;
- the next explicit connection receives device approval before a new remote
  MCP registration is added;
- a Cloud outage preserves local state and local MCP tools.
