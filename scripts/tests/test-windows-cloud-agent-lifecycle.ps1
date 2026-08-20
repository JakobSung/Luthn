#requires -Version 7.4

[CmdletBinding()]
param(
    [string]$RepoRoot = (Split-Path -Parent (Split-Path -Parent $PSScriptRoot))
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw "assertion failed: $Message" }
}

function Invoke-LuthnProcess {
    param([string]$CliPath, [string[]]$Arguments)
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = (Get-Command pwsh -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    foreach ($argument in @("-NoProfile", "-File", $CliPath) + $Arguments) {
        [void]$startInfo.ArgumentList.Add($argument)
    }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    try {
        [void]$process.Start()
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        $process.WaitForExit()
        return [pscustomobject]@{
            ExitCode = $process.ExitCode
            Output = $stdoutTask.GetAwaiter().GetResult() + $stderrTask.GetAwaiter().GetResult()
        }
    } finally {
        $process.Dispose()
    }
}

$testRoot = Join-Path ([IO.Path]::GetTempPath()) "Luthn Cloud Windows $([Guid]::NewGuid().ToString('N'))"
$windowsRoot = Join-Path $testRoot "installed root"
$fakeDocker = Join-Path $testRoot "fake-docker.ps1"
$fakeCodex = Join-Path $testRoot "fake-codex.ps1"
$localMcpState = Join-Path $testRoot "local-mcp"
$cloudMcpState = Join-Path $testRoot "cloud-mcp"
$dockerLog = Join-Path $testRoot "docker.log"
$codexLog = Join-Path $testRoot "codex.log"
$cliPath = Join-Path $RepoRoot "scripts/luthn.ps1"

try {
    [void][IO.Directory]::CreateDirectory($testRoot)
    [void][IO.Directory]::CreateDirectory((Join-Path $windowsRoot "data"))
    [void][IO.Directory]::CreateDirectory((Join-Path $windowsRoot "config"))
    [IO.File]::WriteAllText((Join-Path $windowsRoot "data/compose.yaml"), "services: {}`n", [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText((Join-Path $windowsRoot "config/luthn.env"), "LUTHN_IMAGE=test/luthn:local`n", [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText($localMcpState, "local", [Text.UTF8Encoding]::new($false))

    [IO.File]::WriteAllText($fakeDocker, @'
$ErrorActionPreference = "Stop"
[IO.File]::AppendAllText($env:FAKE_DOCKER_LOG, (($args -join " ") + "`n"))
$joined = $args -join " "
if ($args.Count -ge 2 -and $args[0] -ceq "compose" -and $args[1] -ceq "version") { "Docker Compose version v2.fake"; exit 0 }
if ($args.Count -ge 1 -and $args[0] -ceq "info") { "linux"; exit 0 }
if ($joined -match " cloud-agent cloud-agent ") {
    if ($env:FAKE_CLOUD_UNAVAILABLE -ceq "true") { [Console]::Error.WriteLine("Cloud unavailable"); exit 42 }
    '{"state":"connected","agentConnectionId":"81000000-0000-0000-0000-000000000001","organizationId":"20000000-0000-0000-0000-000000000001","workspaceId":"30000000-0000-0000-0000-000000000001","agentKind":"codex","capabilityPreset":"reader","remoteMcpUrl":"https://cloud.example/mcp"}'
    exit 0
}
if ($joined -match " mcp --list-tools$") { "get_context_pack"; "search_safe_context"; exit 0 }
exit 2
'@, [Text.UTF8Encoding]::new($false))

    [IO.File]::WriteAllText($fakeCodex, @'
$ErrorActionPreference = "Stop"
[IO.File]::AppendAllText($env:FAKE_CODEX_LOG, (($args -join " ") + "`n"))
if ($args.Count -eq 1 -and $args[0] -ceq "--version") { "codex-cli 0.test"; exit 0 }
if ($args.Count -lt 2 -or $args[0] -cne "mcp") { exit 2 }
$name = if ($args.Count -ge 3) { $args[2] } else { "" }
switch ($args[1]) {
    "get" {
        if ($name -ceq "luthn" -and [IO.File]::Exists($env:FAKE_LOCAL_MCP_STATE)) { '{"name":"luthn"}'; exit 0 }
        if ($name -ceq "luthn-cloud" -and [IO.File]::Exists($env:FAKE_CLOUD_MCP_STATE)) { '{"name":"luthn-cloud"}'; exit 0 }
        [Console]::Error.WriteLine("MCP registration not found"); exit 1
    }
    "add" {
        if ($name -cne "luthn-cloud" -or $args.Count -ne 5 -or $args[3] -cne "--url" -or $args[4] -cne "https://cloud.example/mcp") { exit 3 }
        [IO.File]::WriteAllText($env:FAKE_CLOUD_MCP_STATE, "cloud"); exit 0
    }
    "login" {
        if ($name -cne "luthn-cloud" -or $args.Count -ne 5 -or $args[3] -cne "--scopes" -or $args[4] -cne "openid,email") { exit 4 }
        exit 0
    }
    "remove" {
        if ($name -cne "luthn-cloud") { exit 5 }
        if ([IO.File]::Exists($env:FAKE_CLOUD_MCP_STATE)) { [IO.File]::Delete($env:FAKE_CLOUD_MCP_STATE) }
        exit 0
    }
}
exit 2
'@, [Text.UTF8Encoding]::new($false))

    $env:LOCALAPPDATA = Join-Path $testRoot "local app data"
    $env:USERPROFILE = Join-Path $testRoot "user profile"
    $env:LUTHN_WINDOWS_ROOT = $windowsRoot
    $env:LUTHN_DOCKER_COMMAND = $fakeDocker
    $env:LUTHN_CODEX_COMMAND = $fakeCodex
    $env:FAKE_DOCKER_LOG = $dockerLog
    $env:FAKE_CODEX_LOG = $codexLog
    $env:FAKE_LOCAL_MCP_STATE = $localMcpState
    $env:FAKE_CLOUD_MCP_STATE = $cloudMcpState
    $env:FAKE_CLOUD_UNAVAILABLE = "false"

    $connect = Invoke-LuthnProcess $cliPath @(
        "cloud", "connect", "codex",
        "--workspace", "30000000-0000-0000-0000-000000000001",
        "--cloud-url", "https://cloud.example")
    Assert-True ($connect.ExitCode -eq 0) "Cloud connection should succeed: $($connect.Output)"
    Assert-True ($connect.Output -match "existing local 'luthn' MCP server was not changed") "connection should state the additive local boundary"
    Assert-True ([IO.File]::Exists($localMcpState)) "local MCP registration should remain"
    Assert-True ([IO.File]::Exists($cloudMcpState)) "Cloud MCP registration should be added"
    $ownershipState = Join-Path $windowsRoot "state/connectors/cloud-codex-windows.json"
    Assert-True ([IO.File]::Exists($ownershipState)) "Cloud ownership state should be written"
    $stateKey = Join-Path $windowsRoot "config/cloud-agent-state-key"
    Assert-True ([IO.File]::Exists($stateKey)) "Cloud AgentDevice state key should be created"
    Assert-True (([Convert]::FromBase64String([IO.File]::ReadAllText($stateKey).Trim())).Length -eq 32) "Cloud state key should be 256 bits"

    $status = Invoke-LuthnProcess $cliPath @("cloud", "status", "codex")
    Assert-True ($status.ExitCode -eq 0 -and $status.Output -match "registered for codex") "Cloud status should report the owned registration"

    $disconnect = Invoke-LuthnProcess $cliPath @("cloud", "disconnect", "codex")
    Assert-True ($disconnect.ExitCode -eq 0) "Cloud disconnect should succeed: $($disconnect.Output)"
    Assert-True ([IO.File]::Exists($localMcpState)) "Cloud disconnect should preserve local MCP"
    Assert-True (-not [IO.File]::Exists($cloudMcpState)) "Cloud disconnect should remove Cloud MCP"
    Assert-True (-not [IO.File]::Exists($ownershipState)) "Cloud disconnect should remove ownership state"

    $env:FAKE_CLOUD_UNAVAILABLE = "true"
    $failedConnect = Invoke-LuthnProcess $cliPath @(
        "cloud", "connect", "codex",
        "--workspace", "30000000-0000-0000-0000-000000000001",
        "--cloud-url", "https://cloud.example")
    Assert-True ($failedConnect.ExitCode -ne 0) "Cloud outage should fail only the Cloud connection"
    Assert-True ([IO.File]::Exists($localMcpState)) "Cloud outage should preserve local MCP"
    Assert-True (-not [IO.File]::Exists($cloudMcpState)) "Cloud outage should not create a Cloud MCP registration"
    Assert-True (-not [IO.File]::Exists($ownershipState)) "Cloud outage should not create ownership state"
    $localTools = Invoke-LuthnProcess $cliPath @("mcp", "--list-tools")
    Assert-True ($localTools.ExitCode -eq 0 -and $localTools.Output -match "get_context_pack") "local MCP should remain callable during a Cloud outage"

    $env:FAKE_CLOUD_UNAVAILABLE = "false"
    [IO.File]::WriteAllText($cloudMcpState, "unrelated", [Text.UTF8Encoding]::new($false))
    $conflict = Invoke-LuthnProcess $cliPath @(
        "cloud", "connect", "codex",
        "--workspace", "30000000-0000-0000-0000-000000000001",
        "--cloud-url", "https://cloud.example")
    Assert-True ($conflict.ExitCode -ne 0) "an unrelated luthn-cloud registration should block setup"
    Assert-True ([IO.File]::ReadAllText($cloudMcpState) -ceq "unrelated") "an unrelated registration should be preserved"
    Assert-True (-not [IO.File]::Exists($ownershipState)) "conflict should not claim ownership"

    Write-Host "Windows Cloud Agent connection lifecycle tests passed."
} finally {
    foreach ($name in @(
        "FAKE_CLOUD_MCP_STATE", "FAKE_CLOUD_UNAVAILABLE", "FAKE_CODEX_LOG",
        "FAKE_DOCKER_LOG", "FAKE_LOCAL_MCP_STATE", "LUTHN_CODEX_COMMAND",
        "LUTHN_DOCKER_COMMAND", "LUTHN_WINDOWS_ROOT")) {
        Remove-Item "Env:$name" -ErrorAction SilentlyContinue
    }
    if ([IO.Directory]::Exists($testRoot)) { [IO.Directory]::Delete($testRoot, $true) }
}
