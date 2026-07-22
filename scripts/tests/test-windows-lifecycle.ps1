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

function Invoke-InstallerProcess {
    param([string]$InstallerPath, [switch]$ConnectCodex)
    $captured = [Collections.Generic.List[string]]::new()
    try {
        if ($ConnectCodex) {
            & $InstallerPath -ConnectCodex *>&1 | ForEach-Object { $captured.Add($_.ToString()) }
        } else {
            & $InstallerPath *>&1 | ForEach-Object { $captured.Add($_.ToString()) }
        }
        return [pscustomobject]@{ ExitCode = 0; Output = ($captured -join "`n") }
    } catch {
        $captured.Add(($_ | Out-String))
        return [pscustomobject]@{ ExitCode = 1; Output = ($captured -join "`n") }
    }
}

function Invoke-CodexHookProcess {
    param([string]$CliPath, [string]$HookJson)
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = (Get-Command pwsh -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardInput = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    foreach ($argument in @("-NoProfile", "-File", $CliPath, "codex-hook")) { [void]$startInfo.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    try {
        [void]$process.Start()
        $process.StandardInput.Write($HookJson)
        $process.StandardInput.Close()
        $stdout = $process.StandardOutput.ReadToEnd()
        $stderr = $process.StandardError.ReadToEnd()
        $process.WaitForExit()
        return [pscustomobject]@{ ExitCode = $process.ExitCode; Output = "$stdout$stderr" }
    } finally {
        $process.Dispose()
    }
}

$testRoot = Join-Path ([IO.Path]::GetTempPath()) "Luthn Windows 한글 $([Guid]::NewGuid().ToString('N'))"
$windowsRoot = Join-Path $testRoot "installed root"
$fakeDocker = Join-Path $testRoot $(if ($IsWindows) { "fake-docker.ps1" } else { "fake-docker" })
$fakeCodex = Join-Path $testRoot $(if ($IsWindows) { "fake-codex.ps1" } else { "fake-codex" })
$fakeHealth = Join-Path $testRoot $(if ($IsWindows) { "fake-health.ps1" } else { "fake-health" })
$fakeDockerDesktop = Join-Path $testRoot $(if ($IsWindows) { "fake-docker-desktop.ps1" } else { "fake-docker-desktop" })
$fakeDockerLog = Join-Path $testRoot "docker.log"
$fakeCodexLog = Join-Path $testRoot "codex.log"
$fakeDockerReadyMarker = Join-Path $testRoot "docker-ready"
$fakeCodexState = Join-Path $testRoot "codex-state.json"
$fakeCodexTemplate = Join-Path $testRoot "codex-template.json"
$invalidCli = Join-Path $testRoot "invalid.ps1"
$failingCli = Join-Path $testRoot "failing.ps1"
$updatedCli = Join-Path $testRoot "updated.ps1"
$connectorUpdateCli = Join-Path $testRoot "connector-update.ps1"
$legacyCli = Join-Path $testRoot "legacy.ps1"
$sharedBinDir = Join-Path $testRoot "shared 도구 bin"
$sharedBinSentinel = Join-Path $sharedBinDir "unrelated-tool.txt"
$installedCli = Join-Path $sharedBinDir "luthn.ps1"
$codexOwnershipState = Join-Path $windowsRoot "state/connectors/codex-windows.json"
$codexPendingState = Join-Path $windowsRoot "state/connectors/codex-windows.pending.json"
$codexHome = Join-Path $testRoot "codex home"
$codexHooksFile = Join-Path $codexHome "hooks.json"
$codexInstructionsFile = Join-Path $codexHome "AGENTS.md"
$codexHookCapture = Join-Path $testRoot "hook-capsule.json"
$claudeHome = Join-Path $testRoot "claude home"
$claudeSettingsFile = Join-Path $claudeHome "settings.json"
$claudeInstructionsFile = Join-Path $claudeHome "CLAUDE.md"
$originalPath = $env:Path

try {
    [void][IO.Directory]::CreateDirectory($testRoot)
    if ($env:LUTHN_TEST_TRACE -ceq "true") { Write-Host "test root: $testRoot" }
    if ($IsWindows) {
        [IO.File]::WriteAllText($fakeDocker, @'
$ErrorActionPreference = "Stop"
[IO.File]::AppendAllText($env:FAKE_DOCKER_LOG, (($args -join " ") + "`n"))
$joined = $args -join " "
if ($args.Count -ge 2 -and $args[0] -ceq "compose" -and $args[1] -ceq "version") { if ($env:FAKE_DOCKER_COMPOSE_FAIL -ceq "true") { exit 12 }; "Docker Compose version v2.fake"; exit 0 }
if ($args.Count -ge 1 -and $args[0] -ceq "info") {
    if ($env:FAKE_DOCKER_INFO_FAIL -ceq "true" -and -not [IO.File]::Exists($env:FAKE_DOCKER_READY_MARKER)) { exit 13 }
    if ($env:FAKE_INSTALLER_DOCKER_OS) { $env:FAKE_INSTALLER_DOCKER_OS } else { "linux" }
    exit 0
}
if ($args.Count -ge 3 -and $args[0] -ceq "buildx" -and $args[1] -ceq "imagetools" -and $args[2] -ceq "inspect") {
    if ($env:FAKE_DOCKER_REMOTE_FAIL -ceq "true") { exit 20 }
    if ($joined -match '\.Manifest') { '{"digest":"sha256:fake"}'; exit 0 }
if ($joined -match '\.Image') { '{"linux/amd64":{"config":{"Labels":{"org.opencontainers.image.revision":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","org.opencontainers.image.version":"main","io.luthn.cli-template.version":"3","io.luthn.connector-template.version":"3","io.luthn.mcp-schema.version":"3"}}}}'; exit 0 }
}
if ($args.Count -ge 1 -and $args[0] -ceq "pull") { if ($env:FAKE_DOCKER_PULL_FAIL -ceq "true") { exit 16 }; "pulled"; exit 0 }
if ($args.Count -ge 2 -and $args[0] -ceq "image" -and $args[1] -ceq "inspect") {
    if ($joined -match "io.luthn.mcp-schema.version") { }
    elseif ($joined -match "org.opencontainers.image.revision") { "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" }
    elseif ($joined -match "RepoDigests") { "ghcr.io/jakobsung/luthn@sha256:fake" }
    else { "sha256:fake" }
    exit 0
}
if ($args.Count -ge 1 -and $args[0] -ceq "inspect") { "sha256:fake"; exit 0 }
if ($args.Count -ge 1 -and $args[0] -ceq "run") {
    if ($args[-1] -ceq "mcp") { [void][Console]::In.ReadToEnd(); '{"jsonrpc":"2.0","id":1,"result":{"serverInfo":{"version":"0.1.0"}}}'; exit 0 }
    [void][Console]::In.ReadToEnd()
    "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"; exit 0
}
if ($args.Count -ge 1 -and $args[0] -ceq "ps") { exit 0 }
if ($args.Count -ge 1 -and $args[0] -in @("stop", "kill")) { exit 0 }
if ($args.Count -ge 1 -and $args[0] -ceq "compose") {
    if ($args -ccontains "--list-tools") { if ($env:FAKE_MCP_PROBE_FAIL -ceq "true") { exit 14 }; "get_context_pack"; "search_safe_context"; exit 0 }
    if ($args[-1] -ceq "mcp") { [void][Console]::In.ReadToEnd(); '{"jsonrpc":"2.0","id":1,"result":{"serverInfo":{"version":"0.1.0"}}}'; exit 0 }
    if ($args -ccontains "pg_isready") { exit 0 }
    if ($args -ccontains "pg_dump") { if ($env:FAKE_DOCKER_BACKUP_FAIL -ceq "true") { [Console]::Error.WriteLine("backup failed"); exit 17 }; "fake-postgres-backup"; exit 0 }
    if ($args -ccontains "migrate" -and $env:FAKE_DOCKER_MIGRATE_FAIL -ceq "true") { exit 18 }
    if ($joined -match " up -d api$" -and $env:FAKE_DOCKER_API_START_FAIL -ceq "true") { exit 19 }
    if ($args -ccontains "ps") { if ($args -ccontains "-q") { "fake-api-container" } else { "api running"; "postgres running" }; exit 0 }
    exit 0
}
exit 1
'@, [Text.UTF8Encoding]::new($false))
    } else {
        [IO.File]::WriteAllText($fakeDocker, @'
#!/bin/sh
printf '%s\n' "$*" >> "$FAKE_DOCKER_LOG"
joined="$*"
if [ "$1" = "compose" ] && [ "$2" = "version" ]; then [ "${FAKE_DOCKER_COMPOSE_FAIL:-false}" = "true" ] && exit 12; echo "Docker Compose version v2.fake"; exit 0; fi
if [ "$1" = "info" ]; then
  if [ "${FAKE_DOCKER_INFO_FAIL:-false}" = "true" ] && [ ! -f "$FAKE_DOCKER_READY_MARKER" ]; then exit 13; fi
  echo "${FAKE_INSTALLER_DOCKER_OS:-linux}"; exit 0
fi
if [ "$1" = "buildx" ] && [ "$2" = "imagetools" ] && [ "$3" = "inspect" ]; then
  [ "${FAKE_DOCKER_REMOTE_FAIL:-false}" = "true" ] && exit 20
  case "$joined" in
    *'.Manifest'*) printf '%s\n' '{"digest":"sha256:fake"}'; exit 0 ;;
    *'.Image'*) printf '%s\n' '{"linux/amd64":{"config":{"Labels":{"org.opencontainers.image.revision":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","org.opencontainers.image.version":"main","io.luthn.cli-template.version":"3","io.luthn.connector-template.version":"3","io.luthn.mcp-schema.version":"3"}}}}'; exit 0 ;;
  esac
fi
if [ "$1" = "pull" ]; then [ "${FAKE_DOCKER_PULL_FAIL:-false}" = "true" ] && exit 16; echo "pulled"; exit 0; fi
if [ "$1" = "image" ] && [ "$2" = "inspect" ]; then
  case "$joined" in
    *io.luthn.mcp-schema.version*) : ;;
    *org.opencontainers.image.revision*) echo "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" ;;
    *RepoDigests*) echo "ghcr.io/jakobsung/luthn@sha256:fake" ;;
    *) echo "sha256:fake" ;;
  esac
  exit 0
fi
if [ "$1" = "inspect" ]; then echo "sha256:fake"; exit 0; fi
if [ "$1" = "run" ]; then
  case "$joined" in
    *' mcp') cat >/dev/null; printf '%s\n' '{"jsonrpc":"2.0","id":1,"result":{"serverInfo":{"version":"0.1.0"}}}' ;;
    *) cat >/dev/null; echo "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" ;;
  esac
  exit 0
fi
if [ "$1" = "ps" ]; then exit 0; fi
if [ "$1" = "stop" ] || [ "$1" = "kill" ]; then exit 0; fi
if [ "$1" = "compose" ]; then
  case "$joined" in
    *--list-tools*) [ "${FAKE_MCP_PROBE_FAIL:-false}" = "true" ] && exit 14; printf 'get_context_pack\nsearch_safe_context\n'; exit 0 ;;
    *' mcp') cat >/dev/null; printf '%s\n' '{"jsonrpc":"2.0","id":1,"result":{"serverInfo":{"version":"0.1.0"}}}'; exit 0 ;;
    *pg_isready*) exit 0 ;;
    *pg_dump*) [ "${FAKE_DOCKER_BACKUP_FAIL:-false}" = "true" ] && { echo "backup failed" >&2; exit 17; }; echo "fake-postgres-backup"; exit 0 ;;
    *migrate*) [ "${FAKE_DOCKER_MIGRATE_FAIL:-false}" = "true" ] && exit 18; exit 0 ;;
    *" up -d api") [ "${FAKE_DOCKER_API_START_FAIL:-false}" = "true" ] && exit 19; exit 0 ;;
    *" ps -q api"*) echo "fake-api-container"; exit 0 ;;
    *" ps"*) printf 'api running\npostgres running\n'; exit 0 ;;
    *) exit 0 ;;
  esac
fi
exit 1
'@, [Text.UTF8Encoding]::new($false))
    }

    if ($IsWindows) {
        [IO.File]::WriteAllText($fakeCodex, @'
$ErrorActionPreference = "Stop"
[IO.File]::AppendAllText($env:FAKE_CODEX_LOG, (($args -join " ") + "`n"))
if ($args.Count -eq 1 -and $args[0] -ceq "--version") { "codex-cli 0.test"; exit 0 }
if ($args.Count -lt 2 -or $args[0] -cne "mcp") { exit 2 }
switch ($args[1]) {
    "get" {
        if (-not [IO.File]::Exists($env:FAKE_CODEX_STATE)) { [Console]::Error.WriteLine("Error: No MCP server named 'luthn' found."); exit 1 }
        [Console]::Out.Write([IO.File]::ReadAllText($env:FAKE_CODEX_STATE)); exit 0
    }
    "add" {
        if ($env:FAKE_CODEX_ADD_FAIL -ceq "true") { exit 7 }
        [IO.File]::Copy($env:FAKE_CODEX_TEMPLATE, $env:FAKE_CODEX_STATE, $true); exit 0
    }
    "remove" {
        if ($env:FAKE_CODEX_REMOVE_FAIL -ceq "true") { exit 9 }
        if ([IO.File]::Exists($env:FAKE_CODEX_STATE)) { [IO.File]::Delete($env:FAKE_CODEX_STATE) }
        exit 0
    }
}
exit 2
'@, [Text.UTF8Encoding]::new($false))
    } else {
        [IO.File]::WriteAllText($fakeCodex, @'
#!/bin/sh
printf '%s\n' "$*" >> "$FAKE_CODEX_LOG"
if [ "$1" = "--version" ]; then echo "codex-cli 0.test"; exit 0; fi
if [ "$1" != "mcp" ]; then exit 2; fi
case "$2" in
  get)
    if [ ! -f "$FAKE_CODEX_STATE" ]; then echo "Error: No MCP server named 'luthn' found." >&2; exit 1; fi
    cat "$FAKE_CODEX_STATE"; exit 0 ;;
  add)
    if [ "$FAKE_CODEX_ADD_FAIL" = "true" ]; then exit 7; fi
    cp "$FAKE_CODEX_TEMPLATE" "$FAKE_CODEX_STATE"; exit 0 ;;
  remove)
    if [ "$FAKE_CODEX_REMOVE_FAIL" = "true" ]; then exit 9; fi
    rm -f "$FAKE_CODEX_STATE"; exit 0 ;;
esac
exit 2
'@, [Text.UTF8Encoding]::new($false))
    }

    if ($IsWindows) {
        [IO.File]::WriteAllText($fakeHealth, @'
if ($args.Count -ne 1 -or $args[0] -notmatch "/(healthz|readyz)$") { exit 1 }
exit 0
'@, [Text.UTF8Encoding]::new($false))
        [IO.File]::WriteAllText($fakeDockerDesktop, @'
[IO.File]::WriteAllText($env:FAKE_DOCKER_READY_MARKER, "ready")
'@, [Text.UTF8Encoding]::new($false))
    } else {
        [IO.File]::WriteAllText($fakeHealth, @'
#!/bin/sh
case "$1" in
  */healthz|*/readyz) exit 0 ;;
  *) exit 1 ;;
esac
'@, [Text.UTF8Encoding]::new($false))
        [IO.File]::WriteAllText($fakeDockerDesktop, @'
#!/bin/sh
: > "$FAKE_DOCKER_READY_MARKER"
'@, [Text.UTF8Encoding]::new($false))
    }
    if (-not $IsWindows) {
        & chmod +x $fakeDocker $fakeCodex $fakeHealth $fakeDockerDesktop
        if ($LASTEXITCODE -ne 0) { throw "failed to make fake tools executable" }
    }

    $env:LOCALAPPDATA = Join-Path $testRoot "local app data"
    $env:LUTHN_WINDOWS_ROOT = $windowsRoot
    $env:LUTHN_BIN_DIR = $sharedBinDir
    $env:LUTHN_TEST_NO_EXIT = "true"
    $env:LUTHN_DOCKER_COMMAND = $fakeDocker
    $env:LUTHN_INSTALLER_DOCKER_COMMAND = $fakeDocker
    $env:LUTHN_CODEX_COMMAND = $fakeCodex
    $env:CODEX_HOME = $codexHome
    $env:LUTHN_CODEX_HOOKS_FILE = $codexHooksFile
    $env:LUTHN_CODEX_INSTRUCTIONS_FILE = $codexInstructionsFile
    $env:LUTHN_CODEX_SKIP_OBSERVATION = "true"
    $env:CLAUDE_CONFIG_DIR = $claudeHome
    $env:LUTHN_CLAUDE_SETTINGS_FILE = $claudeSettingsFile
    $env:LUTHN_CLAUDE_INSTRUCTIONS_FILE = $claudeInstructionsFile
    $env:LUTHN_HTTP_CHECK_COMMAND = $fakeHealth
    $env:LUTHN_COMPOSE_SOURCE_FILE = Join-Path $RepoRoot "deploy/compose.yaml"
    $env:LUTHN_WINDOWS_CLI_SOURCE_FILE = Join-Path $RepoRoot "scripts/luthn.ps1"
    $env:LUTHN_PORT = "18080"
    $env:FAKE_DOCKER_LOG = $fakeDockerLog
    $env:FAKE_CODEX_LOG = $fakeCodexLog
    $env:FAKE_CODEX_STATE = $fakeCodexState
    $env:FAKE_CODEX_TEMPLATE = $fakeCodexTemplate
    $env:FAKE_DOCKER_READY_MARKER = $fakeDockerReadyMarker
    [void][IO.Directory]::CreateDirectory($sharedBinDir)
    [void][IO.Directory]::CreateDirectory($codexHome)
    [IO.File]::WriteAllText($sharedBinSentinel, "preserve me", [Text.UTF8Encoding]::new($false))
    $unrelatedHooks = [ordered]@{
        hooks = [ordered]@{
            SessionStart = @()
            Stop = @([ordered]@{
                matcher = "other.owner"
                hooks = @([ordered]@{ type = "command"; command = "other-tool" })
            })
        }
    } | ConvertTo-Json -Depth 8
    [IO.File]::WriteAllText($codexHooksFile, ($unrelatedHooks + "`n"), [Text.UTF8Encoding]::new($false))
    $originalInstructions = "# User instructions`r`n`r`nPreserve this text.`r`n"
    [IO.File]::WriteAllText($codexInstructionsFile, $originalInstructions, [Text.UTF8Encoding]::new($false))

    $expectedDockerCommand = $fakeDocker
    $expectedMcpArguments = @(
        "compose", "--project-name", "luthn",
        "--env-file", (Join-Path $windowsRoot "config/luthn.env"),
        "-f", (Join-Path $windowsRoot "data/compose.yaml"),
        "--profile", "tools", "run", "--rm", "--no-deps", "-T", "mcp"
    )
    if ($IsWindows) {
        $expectedDockerCommand = @(Get-Command pwsh -CommandType Application -ErrorAction Stop)[0].Source
        $expectedMcpArguments = @("-NoProfile", "-File", $fakeDocker) + $expectedMcpArguments
    }
    $template = [ordered]@{
        name = "luthn"
        enabled = $true
        transport = [ordered]@{ type = "stdio"; command = $expectedDockerCommand; args = $expectedMcpArguments }
    }
    [IO.File]::WriteAllText($fakeCodexTemplate, (($template | ConvertTo-Json -Depth 6) + "`n"), [Text.UTF8Encoding]::new($false))

    $installerPath = Join-Path $RepoRoot "scripts/install.ps1"
    $install = Invoke-InstallerProcess $installerPath -ConnectCodex
    if ($env:LUTHN_TEST_TRACE -ceq "true" -and $install.ExitCode -ne 0) { Write-Host $install.Output }
    Assert-True ($install.ExitCode -eq 0) "bootstrap install and Codex connection should succeed: $($install.Output)"
    Assert-True ($install.Output -match "Codex connector files are configured") "one-step bootstrap should connect Codex"
    Assert-True ([IO.File]::Exists($installedCli)) "bootstrap should install luthn.ps1"
    $installedShim = Join-Path $sharedBinDir "luthn.cmd"
    Assert-True ([IO.File]::Exists($installedShim)) "bootstrap should install luthn.cmd"
    Assert-True ([IO.File]::ReadAllText($installedShim).Contains('%~dp0luthn.ps1')) "shim should resolve the CLI relative to its own non-ASCII-safe path"
    Assert-True (-not [IO.File]::ReadAllText($installedShim).Contains($windowsRoot)) "shim should not embed a potentially non-ASCII installation path"
    if ($IsWindows) {
        $shimHelp = & $installedShim help *>&1 | Out-String
        Assert-True ($LASTEXITCODE -eq 0 -and $shimHelp -match "usage: luthn") "installed command shim should execute from a non-ASCII path"
    }
    Assert-True ($install.Output -match "Luthn is ready") "install should report that the default classifier is ready"

    $configFile = Join-Path $windowsRoot "config/luthn.env"
    $tokenFile = Join-Path $windowsRoot "config/service-token"
    $operatorTokenFile = Join-Path $windowsRoot "config/operator-token"
    Assert-True ([IO.File]::Exists($configFile)) "config should exist"
    Assert-True ([IO.File]::Exists($tokenFile)) "token should exist"
    Assert-True ([IO.File]::Exists($operatorTokenFile)) "operator token should exist"
    $token = [IO.File]::ReadAllText($tokenFile)
    $operatorToken = [IO.File]::ReadAllText($operatorTokenFile)
    Assert-True ($token -cmatch "^[0-9a-f]{48}$") "token should be a 24-byte hex value"
    Assert-True ($operatorToken -cmatch "^[0-9a-f]{48}$" -and $operatorToken -cne $token) "operator token should be a distinct 24-byte hex value"
    $configBytes = [IO.File]::ReadAllBytes($configFile)
    Assert-True (-not ($configBytes.Length -ge 3 -and $configBytes[0] -eq 0xEF -and $configBytes[1] -eq 0xBB -and $configBytes[2] -eq 0xBF)) "config should be UTF-8 without BOM"
    Assert-True ([IO.File]::ReadAllText($configFile) -cmatch "Luthn__Auth__Tokens__0__Sha256Digest=sha256:[0-9a-f]{64}") "config should preserve the token-digest sha256 prefix"
    Assert-True ([IO.File]::ReadAllText($configFile) -cmatch "(?m)^Luthn__Identity__Mode=SingleOwner$" -and [IO.File]::ReadAllText($configFile) -cmatch "(?m)^Luthn__Identity__SingleOwnerUserId=local-owner$") "new installs should preserve the single-owner compatibility boundary"
    Assert-True ([IO.File]::ReadAllText($configFile) -cmatch "(?m)^Luthn__Auth__Tokens__0__UserId=local-owner$" -and [IO.File]::ReadAllText($configFile) -cmatch "(?m)^Luthn__Auth__Tokens__0__IsOperator=false$") "new installs should bind the product token to the local owner"
    Assert-True ([IO.File]::ReadAllText($configFile) -cmatch "(?m)^Luthn__Auth__Tokens__0__Scopes__7=access\.request$") "new installs should provision the MCP sensitive-access request scope"
    Assert-True ([IO.File]::ReadAllText($configFile) -cmatch "(?m)^Luthn__Auth__Tokens__0__Scopes__8=metrics\.write$") "new installs should provision the MCP search telemetry write scope"
    Assert-True ([IO.File]::ReadAllText($configFile) -cmatch "(?m)^Luthn__Auth__Tokens__1__Name=local-operator$") "new installs should provision a distinct local operator credential"
    Assert-True ([IO.File]::ReadAllText($configFile) -cmatch "(?m)^Luthn__Auth__Tokens__1__IsOperator=true$") "the local operator credential should have the explicit operator role"
    Assert-True ([IO.File]::ReadAllText($configFile) -cmatch "(?m)^Luthn__Auth__Tokens__1__Scopes__0=access\.decide$" -and [IO.File]::ReadAllText($configFile) -cmatch "(?m)^Luthn__Auth__Tokens__1__Scopes__1=config\.write$") "the local operator credential should allow operator configuration"
    Assert-True ([IO.File]::ReadAllText($configFile) -cmatch "(?m)^LUTHN_ENVIRONMENT=Production$") "new installs should use the Production environment"
    Assert-True ([IO.File]::ReadAllText($configFile) -cmatch "(?m)^Luthn__Classification__Provider=mock$") "new installs should select the mock classifier"
    Assert-True ([IO.File]::ReadAllText($configFile) -cmatch "(?m)^Luthn__Classification__AllowMock=true$") "new installs should enable mock classification"
    Assert-True ([IO.File]::ReadAllText($configFile) -cmatch "(?m)^LUTHN_OPERATOR_VOLUME=luthn-operator$") "new installs should use the separate persistent Data Protection key volume"
    Assert-True (-not ([IO.File]::ReadAllText($fakeDockerLog).Contains($token))) "Docker arguments and logs should not contain the token"
    Assert-True (-not ([IO.File]::ReadAllText($fakeDockerLog).Contains($operatorToken))) "Docker arguments and logs should not contain the operator token"
    Assert-True (-not (([IO.File]::ReadAllText($fakeCodexState)).Contains($token))) "one-step Codex registration should not contain the token"
    $installedHooks = [IO.File]::ReadAllText($codexHooksFile) | ConvertFrom-Json
    $luthnHook = @($installedHooks.hooks.Stop | Where-Object { $_.matcher -ceq "luthn.agent-connector.v1" })
    Assert-True ($luthnHook.Count -eq 1) "one-step setup should install one Luthn Stop hook"
    Assert-True ($luthnHook[0].hooks[0].commandWindows -ceq $luthnHook[0].hooks[0].command) "Windows hook should include a matching commandWindows entry"
    Assert-True ($luthnHook[0].hooks[0].statusMessage -ceq "Luthn 메모리 저장 예약 중…") "one-step setup should describe the upload as scheduled"
    Assert-True ([int]$luthnHook[0].hooks[0].timeout -eq 10) "Windows Stop hook should cover two sequential four-second API calls plus process overhead"
    Assert-True (@($installedHooks.hooks.Stop | Where-Object { $_.matcher -ceq "other.owner" }).Count -eq 1) "one-step setup should preserve unrelated hooks"
    Assert-True (-not ([IO.File]::ReadAllText($codexHooksFile).Contains($token))) "Codex hooks should not contain the token"
    Assert-True ([IO.File]::ReadAllText($codexInstructionsFile).Contains("luthn:auto-recall:start")) "one-step setup should enable auto-recall by default"
    $connectorState = [IO.File]::ReadAllText($codexOwnershipState) | ConvertFrom-Json
    Assert-True ($connectorState.version -eq 2 -and $connectorState.integration -ceq "host-hook-mcp") "Windows connector state should record the hook and MCP integration"
    Assert-True ($connectorState.connectorVersion -ceq "3") "Windows connector state should record the managed template version"
    Assert-True ($connectorState.helperDigest -cmatch "^[0-9a-f]{64}$") "Windows connector state should record the selected CLI digest"
    Assert-True ($connectorState.templateDigest -cmatch "^[0-9a-f]{64}$") "Windows connector state should record the managed template digest"
    Assert-True ($connectorState.hookInstalled -and $connectorState.autoRecall) "connector state should record default auto-recall"
    Assert-True (-not ([IO.File]::ReadAllText($codexOwnershipState).Contains($token))) "connector ownership state should not contain the token"
    if ($IsWindows) {
        $tokenAcl = Get-Acl -LiteralPath $tokenFile
        Assert-True ($tokenAcl.AreAccessRulesProtected) "token ACL inheritance should be disabled"
        $allowedSids = @(
            [Security.Principal.WindowsIdentity]::GetCurrent().User.Value,
            ([Security.Principal.SecurityIdentifier]::new([Security.Principal.WellKnownSidType]::LocalSystemSid, $null)).Value,
            ([Security.Principal.SecurityIdentifier]::new([Security.Principal.WellKnownSidType]::BuiltinAdministratorsSid, $null)).Value
        )
        $unexpectedAllowRule = @($tokenAcl.Access | Where-Object {
            $_.AccessControlType -eq [Security.AccessControl.AccessControlType]::Allow -and
            $allowedSids -notcontains $_.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value
        })
        Assert-True ($unexpectedAllowRule.Count -eq 0) "token ACL should not grant access to unrelated identities"
    }

    $initialDisconnect = Invoke-LuthnProcess $installedCli @("disconnect", "codex")
    Assert-True ($initialDisconnect.ExitCode -eq 0) "one-step Codex registration should disconnect cleanly: $($initialDisconnect.Output)"
    $disconnectedHooks = [IO.File]::ReadAllText($codexHooksFile) | ConvertFrom-Json
    Assert-True (@($disconnectedHooks.hooks.Stop | Where-Object { $_.matcher -ceq "luthn.agent-connector.v1" }).Count -eq 0) "disconnect should remove the Luthn Stop hook"
    Assert-True (@($disconnectedHooks.hooks.Stop | Where-Object { $_.matcher -ceq "other.owner" }).Count -eq 1) "disconnect should preserve unrelated hooks"
    Assert-True ([IO.File]::ReadAllText($codexInstructionsFile) -ceq $originalInstructions) "disconnect should preserve unrelated instructions"

    $claudeOwnershipState = Join-Path $windowsRoot "state/connectors/claude-code-windows.json"
    [void][IO.Directory]::CreateDirectory($claudeHome)
    [IO.File]::WriteAllText($claudeSettingsFile, ($unrelatedHooks + "`n"), [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText($claudeInstructionsFile, $originalInstructions, [Text.UTF8Encoding]::new($false))
    $env:LUTHN_CLAUDE_COMMAND = $fakeCodex
    $env:LUTHN_CLAUDE_SETTINGS_FILE = $claudeSettingsFile
    $env:LUTHN_CLAUDE_INSTRUCTIONS_FILE = $claudeInstructionsFile

    $claudeConnect = Invoke-LuthnProcess $installedCli @("connect", "claude")
    Assert-True ($claudeConnect.ExitCode -eq 0) "Claude Code connection should succeed: $($claudeConnect.Output)"
    Assert-True ([IO.File]::Exists($claudeOwnershipState)) "Claude connection should record ownership state"
    $claudeSettings = [IO.File]::ReadAllText($claudeSettingsFile) | ConvertFrom-Json
    Assert-True (@($claudeSettings.hooks.Stop | Where-Object { $_.matcher -ceq "luthn.claude-agent-connector.v1" }).Count -eq 1) "Claude connection should install one managed Stop hook"
    Assert-True (@($claudeSettings.hooks.Stop | Where-Object { $_.matcher -ceq "other.owner" }).Count -eq 1) "Claude connection should preserve unrelated hooks"
    Assert-True ([IO.File]::ReadAllText($claudeInstructionsFile).Contains("luthn:auto-recall:start")) "Claude connection should enable lightweight recall by default"
    Assert-True (-not ([IO.File]::ReadAllText($claudeSettingsFile).Contains($token)) -and -not ([IO.File]::ReadAllText($claudeOwnershipState).Contains($token))) "Claude configuration should not contain the service token"
    $claudeStatus = Invoke-LuthnProcess $installedCli @("connection", "status", "claude")
    Assert-True ($claudeStatus.ExitCode -eq 0 -and $claudeStatus.Output -match "automatic-ingestion: configured" -and $claudeStatus.Output -match "mcp: configured") "Claude status should expose hook and MCP state: $($claudeStatus.Output)"

    $env:FAKE_CODEX_REMOVE_FAIL = "true"
    $failedClaudeDisconnect = Invoke-LuthnProcess $installedCli @("disconnect", "claude")
    Assert-True ($failedClaudeDisconnect.ExitCode -ne 0) "Claude MCP cleanup failure should fail disconnect"
    Assert-True ([IO.File]::Exists($claudeOwnershipState)) "failed Claude cleanup should preserve ownership state"
    Assert-True (([IO.File]::ReadAllText($claudeSettingsFile) | ConvertFrom-Json).hooks.Stop.matcher -contains "luthn.claude-agent-connector.v1") "failed Claude cleanup should restore the managed hook"
    $env:FAKE_CODEX_REMOVE_FAIL = "false"
    $claudeDisconnect = Invoke-LuthnProcess $installedCli @("disconnect", "claude")
    Assert-True ($claudeDisconnect.ExitCode -eq 0) "Claude Code disconnect should succeed: $($claudeDisconnect.Output)"
    Assert-True (-not [IO.File]::Exists($claudeOwnershipState)) "Claude disconnect should remove ownership state"
    Assert-True ([IO.File]::ReadAllText($claudeInstructionsFile) -ceq $originalInstructions) "Claude disconnect should preserve unrelated instructions"

    $unrelatedClaudeRegistration = '{"name":"luthn","transport":{"command":"unrelated-tool"}}'
    [IO.File]::WriteAllText($fakeCodexState, $unrelatedClaudeRegistration, [Text.UTF8Encoding]::new($false))
    $conflictingClaudeConnect = Invoke-LuthnProcess $installedCli @("connect", "claude")
    Assert-True ($conflictingClaudeConnect.ExitCode -ne 0) "Claude connection should reject an unrelated luthn registration"
    Assert-True ([IO.File]::ReadAllText($fakeCodexState) -ceq $unrelatedClaudeRegistration) "Claude connection should preserve an unrelated registration"
    [IO.File]::Delete($fakeCodexState)

    $firstHash = (Get-FileHash -LiteralPath $installedCli -Algorithm SHA256).Hash
    $configBeforeFullScopeInstall = [IO.File]::ReadAllText($configFile)
    $nonScopeConfig = @([IO.File]::ReadAllLines($configFile) | Where-Object {
        $_ -notmatch '^Luthn__Auth__Tokens__0__Scopes__\d+='
    })
    $fullCustomScopes = @(0..15 | ForEach-Object { "Luthn__Auth__Tokens__0__Scopes__$_=custom.scope.$_" })
    [IO.File]::WriteAllText(
        $configFile,
        ((@($nonScopeConfig) + $fullCustomScopes) -join "`n") + "`n",
        [Text.UTF8Encoding]::new($false))
    $secondInstall = Invoke-InstallerProcess $installerPath
    Assert-True ($secondInstall.ExitCode -eq 0) "repeated install should be idempotent: $($secondInstall.Output)"
    Assert-True ($secondInstall.Output -match "scope table is full") "install should warn instead of failing for a full custom scope table without connector ownership"
    Assert-True ([IO.File]::ReadAllText($configFile) -match "(?m)^Luthn__Auth__Tokens__0__Scopes__15=custom\.scope\.15$") "install should preserve a full custom scope table"
    [IO.File]::WriteAllText($configFile, $configBeforeFullScopeInstall, [Text.UTF8Encoding]::new($false))
    Assert-True ((Get-FileHash -LiteralPath $installedCli -Algorithm SHA256).Hash -eq $firstHash) "repeated install should preserve the validated CLI"

    [IO.File]::WriteAllText($failingCli, '$script:LuthnWindowsCliVersion = "1"' + "`nexit 23`n", [Text.UTF8Encoding]::new($false))
    $env:LUTHN_WINDOWS_CLI_SOURCE_FILE = $failingCli
    $failedCliInstall = Invoke-InstallerProcess $installerPath
    Assert-True ($failedCliInstall.ExitCode -ne 0) "a downloaded CLI runtime failure should return to the bootstrapper"
    Assert-True ((Get-FileHash -LiteralPath $installedCli -Algorithm SHA256).Hash -eq $firstHash) "a downloaded CLI runtime failure should restore the previous CLI"
    $env:LUTHN_WINDOWS_CLI_SOURCE_FILE = Join-Path $RepoRoot "scripts/luthn.ps1"

    [IO.File]::WriteAllText($invalidCli, "this is not PowerShell {", [Text.UTF8Encoding]::new($false))
    $env:LUTHN_WINDOWS_CLI_SOURCE_FILE = $invalidCli
    $invalidInstall = Invoke-InstallerProcess $installerPath
    Assert-True ($invalidInstall.ExitCode -ne 0) "invalid downloaded CLI should fail"
    Assert-True ((Get-FileHash -LiteralPath $installedCli -Algorithm SHA256).Hash -eq $firstHash) "invalid download should preserve the installed CLI"
    $env:LUTHN_WINDOWS_CLI_SOURCE_FILE = Join-Path $RepoRoot "scripts/luthn.ps1"

    $pwshDirectory = Split-Path -Parent (Get-Command pwsh -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
    Remove-Item Env:LUTHN_INSTALLER_DOCKER_COMMAND
    $env:Path = $pwshDirectory
    $missingDocker = Invoke-InstallerProcess $installerPath
    Assert-True ($missingDocker.ExitCode -ne 0) "missing Docker CLI should fail preflight"
    Assert-True ($missingDocker.Output -match "Docker Desktop with Docker Compose is required") "missing Docker should provide an actionable error"
    Assert-True ($missingDocker.Output -notmatch "Index was outside") "missing Docker should not expose an array bounds exception"
    $env:Path = $originalPath
    $env:LUTHN_INSTALLER_DOCKER_COMMAND = $fakeDocker

    $env:FAKE_DOCKER_COMPOSE_FAIL = "true"
    $composeUnavailable = Invoke-InstallerProcess $installerPath
    Assert-True ($composeUnavailable.ExitCode -ne 0) "missing Docker Compose should fail preflight"
    Assert-True ((Get-FileHash -LiteralPath $installedCli -Algorithm SHA256).Hash -eq $firstHash) "Compose preflight failure should preserve the installed CLI"
    $env:FAKE_DOCKER_COMPOSE_FAIL = "false"

    $env:FAKE_DOCKER_INFO_FAIL = "true"
    $env:LUTHN_DOCKER_DESKTOP_COMMAND = $fakeDockerDesktop
    $daemonAutoStart = Invoke-InstallerProcess $installerPath
    if ($env:LUTHN_TEST_TRACE -ceq "true" -and $daemonAutoStart.ExitCode -ne 0) { Write-Host $daemonAutoStart.Output }
    Assert-True ($daemonAutoStart.ExitCode -eq 0) "the bootstrap should start Docker Desktop and wait for its engine: $($daemonAutoStart.Output)"
    Assert-True ($daemonAutoStart.Output -match "Starting it and waiting") "automatic Docker Desktop startup should be reported"
    Remove-Item Env:LUTHN_DOCKER_DESKTOP_COMMAND
    if ([IO.File]::Exists($fakeDockerReadyMarker)) { [IO.File]::Delete($fakeDockerReadyMarker) }

    $daemonUnavailable = Invoke-InstallerProcess $installerPath
    if ($env:LUTHN_TEST_TRACE -ceq "true") { Write-Host $daemonUnavailable.Output }
    Assert-True ($daemonUnavailable.ExitCode -ne 0) "unreachable Docker daemon should fail preflight"
    Assert-True ($daemonUnavailable.Output -match "Start Docker Desktop" -and $daemonUnavailable.Output -match "engine, and retry") "daemon failure should explain the recovery action"
    Assert-True ((Get-FileHash -LiteralPath $installedCli -Algorithm SHA256).Hash -eq $firstHash) "daemon preflight failure should preserve the installed CLI"
    $env:FAKE_DOCKER_INFO_FAIL = "false"

    $env:FAKE_INSTALLER_DOCKER_OS = "windows"
    $wrongMode = Invoke-InstallerProcess $installerPath
    Assert-True ($wrongMode.ExitCode -ne 0) "Windows-container mode should fail preflight"
    Assert-True ($wrongMode.Output -match "Switch to Linux containers") "wrong container mode should explain the recovery action"
    Assert-True ((Get-FileHash -LiteralPath $installedCli -Algorithm SHA256).Hash -eq $firstHash) "preflight failure should preserve the installed CLI"
    $env:FAKE_INSTALLER_DOCKER_OS = "linux"

    $status = Invoke-LuthnProcess $installedCli @("status")
    Assert-True ($status.ExitCode -eq 0) "status should succeed: $($status.Output)"
    Assert-True ($status.Output -match "Health: ready") "status should report health"
    Assert-True ($status.Output -match "Readiness: ready") "status should report readiness"
    Assert-True ($status.Output -match "Image ID: sha256:fake") "status should report image identity"
    Assert-True ($status.Output -match "Running revision: a{40}") "status should report the running image revision"
    Assert-True ($status.Output -match "Selected revision: a{40}") "status should report the selected image revision"

    $versionResult = Invoke-LuthnProcess $installedCli @("version", "--json")
    Assert-True ($versionResult.ExitCode -eq 0) "version --json should succeed: $($versionResult.Output)"
    $version = $versionResult.Output | ConvertFrom-Json
    Assert-True ($version.installedImageReference -ceq "ghcr.io/jakobsung/luthn:main") "version should report the installed image reference"
    Assert-True ($version.cliTemplateVersion -ceq "3" -and $version.connectorTemplateVersion -ceq "3") "version should report CLI and connector template versions"
    Assert-True ($version.mcpSchemaVersion -ceq "0.1.0") "version should fall back to the legacy MCP server version when the image label and schemaVersion field are absent"
    Assert-True ($versionResult.Output -notmatch [regex]::Escape([IO.File]::ReadAllText($tokenFile))) "version JSON must not expose the service token"
    Assert-True ($versionResult.Output -notmatch [regex]::Escape([IO.File]::ReadAllText($operatorTokenFile))) "version JSON must not expose the operator token"

    $configBeforeUpdateCheck = [IO.File]::ReadAllText($configFile)
    $hooksBeforeUpdateCheck = [IO.File]::ReadAllText($codexHooksFile)
    $updateCheck = Invoke-LuthnProcess $installedCli @("update", "check", "--json")
    Assert-True ($updateCheck.ExitCode -eq 0) "update check should succeed: $($updateCheck.Output)"
    $updateCheckJson = $updateCheck.Output | ConvertFrom-Json
    Assert-True ($updateCheckJson.status -ceq "current") "matching remote identity should report current"
    Assert-True ([IO.File]::ReadAllText($configFile) -ceq $configBeforeUpdateCheck) "update check should not modify configuration"
    Assert-True ([IO.File]::ReadAllText($codexHooksFile) -ceq $hooksBeforeUpdateCheck) "update check should not modify Codex configuration"

    $pinnedImage = "ghcr.io/jakobsung/luthn@sha256:$('0' * 64)"
    [IO.File]::WriteAllText($configFile, $configBeforeUpdateCheck.Replace("LUTHN_IMAGE=ghcr.io/jakobsung/luthn:main", "LUTHN_IMAGE=$pinnedImage"), [Text.UTF8Encoding]::new($false))
    $pinnedConfig = [IO.File]::ReadAllText($configFile)
    $dockerLogCountBeforePinnedUpdate = [IO.File]::ReadAllLines($fakeDockerLog).Count
    $pinnedCheck = Invoke-LuthnProcess $installedCli @("update", "check", "--json")
    Assert-True ($pinnedCheck.ExitCode -eq 0 -and ($pinnedCheck.Output | ConvertFrom-Json).status -ceq "pinned") "immutable image references should report pinned"
    $pinnedUpdate = Invoke-LuthnProcess $installedCli @("update")
    Assert-True ($pinnedUpdate.ExitCode -ne 0 -and $pinnedUpdate.Output -match "configured image is immutable") "implicit update should stop for an immutable pin"
    Assert-True ([IO.File]::ReadAllText($configFile) -ceq $pinnedConfig) "pin checks should not modify configuration"
    $pinnedDockerLog = @([IO.File]::ReadAllLines($fakeDockerLog) | Select-Object -Skip $dockerLogCountBeforePinnedUpdate)
    Assert-True (-not ($pinnedDockerLog -match '^pull ')) "implicit pinned update must not pull"
    [IO.File]::WriteAllText($configFile, $configBeforeUpdateCheck, [Text.UTF8Encoding]::new($false))

    $env:FAKE_DOCKER_REMOTE_FAIL = "true"
    $remoteFailureConfig = [IO.File]::ReadAllText($configFile)
    $remoteFailure = Invoke-LuthnProcess $installedCli @("update", "check", "--json")
    Assert-True ($remoteFailure.ExitCode -ne 0 -and $remoteFailure.Output -match '"status":"error"') "remote update-check failure should emit the error contract"
    Assert-True ([IO.File]::ReadAllText($configFile) -ceq $remoteFailureConfig) "remote update-check failure should not modify configuration"
    $env:FAKE_DOCKER_REMOTE_FAIL = "false"

    $doctorResult = Invoke-LuthnProcess $installedCli @("doctor", "--json")
    Assert-True ($doctorResult.ExitCode -eq 0) "doctor --json should pass for the healthy fixture: $($doctorResult.Output)"
    $doctor = $doctorResult.Output | ConvertFrom-Json
    Assert-True ($doctor.status -ceq "ready") "doctor should report ready for a healthy fixture"
    $doctorNames = @($doctor.checks | ForEach-Object { $_.name })
    Assert-True ($doctorNames -contains "migrations" -and $doctorNames -contains "update-check" -and @($doctorNames -match '^codex-').Count -gt 0) "doctor should cover migration, update, and Codex state"
    $updatedCliContent = [IO.File]::ReadAllText((Join-Path $RepoRoot "scripts/luthn.ps1")) + "`n# windows-update-test-fixture`n"
    [IO.File]::WriteAllText($updatedCli, $updatedCliContent, [Text.UTF8Encoding]::new($false))
    $env:LUTHN_WINDOWS_CLI_SOURCE_FILE = $updatedCli
    $targetImage = "ghcr.io/jakobsung/luthn:sha-$('b' * 40)"
    $updateLogStart = [IO.File]::ReadAllLines($fakeDockerLog).Count
    $oversizedUnownedInstructions = "x" * (1024 * 1024 + 1)
    [IO.File]::WriteAllText($codexInstructionsFile, $oversizedUnownedInstructions, [Text.UTF8Encoding]::new($false))
    $operatorDigest = ([IO.File]::ReadAllLines($configFile) | Where-Object { $_ -cmatch '^Luthn__Auth__Tokens__1__Sha256Digest=' }).Split('=', 2)[1]
    $legacyConfig = @([IO.File]::ReadAllLines($configFile) | Where-Object {
        $_ -cne "Luthn__Auth__Tokens__0__Scopes__7=access.request" -and
        $_ -cne "Luthn__Auth__Tokens__0__Scopes__8=metrics.write" -and
        $_ -cnotmatch '^Luthn__Auth__Tokens__1__'
    }) + @(
        "Luthn__Auth__Tokens__1__Name=existing-integration",
        "Luthn__Auth__Tokens__1__Sha256Digest=sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc",
        "Luthn__Auth__Tokens__1__Scopes__0=memory.read",
        "Luthn__Auth__Tokens__1__Scopes__1=*",
        "Luthn__Auth__Tokens__1__ExpiresAt=2099-01-01T00:00:00Z",
        "Luthn__Auth__Tokens__2__Name=local-operator",
        "Luthn__Auth__Tokens__2__Sha256Digest=$operatorDigest",
        "Luthn__Auth__Tokens__2__Scopes__0=access.decide",
        "Luthn__Auth__Tokens__2__Scopes__1=*",
        "Luthn__Auth__Tokens__2__ExpiresAt=2099-01-01T00:00:00Z"
    )
    [IO.File]::WriteAllText($configFile, (($legacyConfig -join "`n") + "`n"), [Text.UTF8Encoding]::new($false))
    $collisionConfig = [IO.File]::ReadAllText($configFile)
    $collisionOperatorToken = [IO.File]::ReadAllText($operatorTokenFile)
    $collisionCliHash = (Get-FileHash -LiteralPath $installedCli -Algorithm SHA256).Hash
    $collisionUpdate = Invoke-LuthnProcess $installedCli @("update", $targetImage)
    Assert-True ($collisionUpdate.ExitCode -ne 0) "Windows update should stop when operator token slot 1 is occupied"
    Assert-True ($collisionUpdate.Output -match "token slot 1 is occupied") "operator slot collision should report the reason"
    Assert-True ([IO.File]::ReadAllText($configFile) -ceq $collisionConfig) "operator slot collision should preserve configuration"
    Assert-True ([IO.File]::ReadAllText($operatorTokenFile) -ceq $collisionOperatorToken) "operator slot collision should preserve the operator credential"
    Assert-True ((Get-FileHash -LiteralPath $installedCli -Algorithm SHA256).Hash -eq $collisionCliHash) "operator slot collision should preserve the installed CLI"

    $validLegacyConfig = @($legacyConfig | Where-Object { $_ -cnotmatch '^Luthn__Auth__Tokens__(?:1|2)__' }) + @(
        "Luthn__Auth__Tokens__1__Name=local-operator",
        "Luthn__Auth__Tokens__1__Sha256Digest=$operatorDigest",
        "Luthn__Auth__Tokens__1__Scopes__0=access.decide",
        "Luthn__Auth__Tokens__1__Scopes__1=*",
        "Luthn__Auth__Tokens__1__ExpiresAt=2099-01-01T00:00:00Z"
    )
    [IO.File]::WriteAllText($configFile, (($validLegacyConfig -join "`n") + "`n"), [Text.UTF8Encoding]::new($false))
    $update = Invoke-LuthnProcess $installedCli @("update", $targetImage)
    Assert-True ($update.ExitCode -eq 0) "Windows update should succeed: $($update.Output)"
    Assert-True ([IO.File]::ReadAllText($codexInstructionsFile) -ceq $oversizedUnownedInstructions) "an update without connector ownership should ignore unrelated oversized instructions"
    [IO.File]::WriteAllText($codexInstructionsFile, $originalInstructions, [Text.UTF8Encoding]::new($false))
    Assert-True ($update.Output -match "Luthn update completed") "successful update should report completion"
    Assert-True ($update.Output -match "Revision: a{40} -> a{40}") "successful update should report the revision transition"
    Assert-True ($update.Output -notmatch "Restart required:" -and $update.Output -notmatch "Agent notice:") "runtime-only update should not emit a compatibility restart notice"
    Assert-True ([IO.File]::ReadAllText($installedCli) -match "windows-update-test-fixture") "update should refresh the installed Windows CLI"
    Assert-True ([IO.File]::ReadAllText($configFile) -match "(?m)^LUTHN_IMAGE=$([regex]::Escape($targetImage))$") "update should select the target image"
    Assert-True ([IO.File]::ReadAllText($configFile) -cmatch "(?m)^Luthn__Auth__Tokens__0__Scopes__7=access\.request$") "update should provision the MCP sensitive-access request scope for legacy installs"
    Assert-True ([IO.File]::ReadAllText($configFile) -cmatch "(?m)^Luthn__Auth__Tokens__0__Scopes__8=metrics\.write$") "update should provision the MCP search telemetry write scope for legacy installs"
    Assert-True ([IO.File]::ReadAllText($configFile) -cmatch "(?m)^Luthn__Identity__Mode=SingleOwner$" -and [IO.File]::ReadAllText($configFile) -cmatch "(?m)^Luthn__Identity__SingleOwnerUserId=local-owner$") "update should provision the single-owner compatibility boundary for legacy installs"
    Assert-True ([IO.File]::ReadAllText($configFile) -cmatch "(?m)^Luthn__Auth__Tokens__0__UserId=local-owner$" -and [IO.File]::ReadAllText($configFile) -cmatch "(?m)^Luthn__Auth__Tokens__0__IsOperator=false$") "update should bind the legacy product token to the local owner"
    Assert-True ([IO.File]::ReadAllText($operatorTokenFile) -ceq $operatorToken) "update should preserve the local operator credential"
    $updatedConfig = [IO.File]::ReadAllText($configFile)
    Assert-True ($updatedConfig -cmatch "(?m)^LUTHN_OPERATOR_VOLUME=luthn-operator$") "update should preserve the separate Data Protection key volume selection"
    Assert-True ($updatedConfig -cmatch "(?m)^Luthn__Auth__Tokens__1__Name=local-operator$" -and $updatedConfig -cmatch "(?m)^Luthn__Auth__Tokens__1__IsOperator=true$" -and $updatedConfig -cmatch "(?m)^Luthn__Auth__Tokens__1__Scopes__0=access\.decide$" -and $updatedConfig -cmatch "(?m)^Luthn__Auth__Tokens__1__Scopes__1=config\.write$") "update should reuse the Compose-exposed operator slot with configuration access"
    Assert-True ($updatedConfig -cnotmatch "(?m)^Luthn__Auth__Tokens__1__ExpiresAt=") "update should normalize the managed operator slot without expiry metadata"
    $backupFiles = @(Get-ChildItem -LiteralPath (Join-Path $windowsRoot "state/backups") -Filter "*.dump")
    Assert-True ($backupFiles.Count -eq 1 -and $backupFiles[0].Length -gt 0) "update should create a non-empty PostgreSQL backup"
    $updateStateFile = Join-Path $windowsRoot "state/update-windows.json"
    $updateState = [IO.File]::ReadAllText($updateStateFile) | ConvertFrom-Json
    Assert-True ($updateState.status -ceq "ready") "successful update should record ready state"
    Assert-True ($updateState.targetImage -ceq $targetImage) "update state should record the target image"
    Assert-True ([IO.File]::Exists($updateState.backupPath)) "update state should record the backup path"

    $updateLog = @([IO.File]::ReadAllLines($fakeDockerLog) | Select-Object -Skip $updateLogStart)
    $stopIndex = -1
    $backupIndex = -1
    $migrationIndex = -1
    $apiStartIndex = -1
    for ($index = 0; $index -lt $updateLog.Count; $index++) {
        if ($stopIndex -lt 0 -and $updateLog[$index] -match " stop api$") { $stopIndex = $index }
        if ($backupIndex -lt 0 -and $updateLog[$index] -match " pg_dump ") { $backupIndex = $index }
        if ($migrationIndex -lt 0 -and $updateLog[$index] -match " run --rm --no-deps migrate$") { $migrationIndex = $index }
        if ($apiStartIndex -lt 0 -and $updateLog[$index] -match " up -d api$") { $apiStartIndex = $index }
    }
    Assert-True ($stopIndex -ge 0 -and $stopIndex -lt $backupIndex) "update should stop API writes before backup"
    Assert-True ($backupIndex -lt $migrationIndex -and $migrationIndex -lt $apiStartIndex) "update should back up before migration and start API afterward"

    $configBeforeFullScopeUpdate = [IO.File]::ReadAllText($configFile)
    $nonScopeConfig = @([IO.File]::ReadAllLines($configFile) | Where-Object {
        $_ -notmatch '^Luthn__Auth__Tokens__0__Scopes__\d+='
    })
    [IO.File]::WriteAllText(
        $configFile,
        ((@($nonScopeConfig) + $fullCustomScopes) -join "`n") + "`n",
        [Text.UTF8Encoding]::new($false))
    $fullScopeUpdate = Invoke-LuthnProcess $installedCli @("update", $targetImage)
    Assert-True ($fullScopeUpdate.ExitCode -eq 0) "update should not fail for a full custom scope table without connector ownership: $($fullScopeUpdate.Output)"
    Assert-True ($fullScopeUpdate.Output -match "scope table is full") "update should warn when it cannot add access.request to a full custom scope table"
    Assert-True ([IO.File]::ReadAllText($configFile) -match "(?m)^Luthn__Auth__Tokens__0__Scopes__15=custom\.scope\.15$") "update should preserve a full custom scope table"
    [IO.File]::WriteAllText($configFile, $configBeforeFullScopeUpdate, [Text.UTF8Encoding]::new($false))

    $updatedHash = (Get-FileHash -LiteralPath $installedCli -Algorithm SHA256).Hash
    $env:FAKE_DOCKER_PULL_FAIL = "true"
    $pullFailure = Invoke-LuthnProcess $installedCli @("update", "ghcr.io/jakobsung/luthn:pull-failure")
    Assert-True ($pullFailure.ExitCode -ne 0) "pull failure should stop update"
    Assert-True ($pullFailure.Output -match "running API and previous image were preserved") "pull failure should report preservation: $($pullFailure.Output)"
    Assert-True ((Get-FileHash -LiteralPath $installedCli -Algorithm SHA256).Hash -eq $updatedHash) "pull failure should preserve the installed CLI"
    Assert-True ([IO.File]::ReadAllText($configFile) -match "(?m)^LUTHN_IMAGE=$([regex]::Escape($targetImage))$") "pull failure should preserve the selected image"
    $env:FAKE_DOCKER_PULL_FAIL = "false"

    $backupCountBeforeFailure = @(Get-ChildItem -LiteralPath (Join-Path $windowsRoot "state/backups") -Filter "*.dump").Count
    $env:FAKE_DOCKER_BACKUP_FAIL = "true"
    $backupFailure = Invoke-LuthnProcess $installedCli @("update", "ghcr.io/jakobsung/luthn:backup-failure")
    Assert-True ($backupFailure.ExitCode -ne 0) "backup failure should stop update"
    Assert-True ($backupFailure.Output -match "previous API was restarted") "backup failure should restart the previous API"
    Assert-True (@(Get-ChildItem -LiteralPath (Join-Path $windowsRoot "state/backups") -Filter "*.dump").Count -eq $backupCountBeforeFailure) "failed backup should remove its partial file"
    Assert-True ([IO.File]::ReadAllText($configFile) -match "(?m)^LUTHN_IMAGE=$([regex]::Escape($targetImage))$") "backup failure should preserve the previous image reference"
    $env:FAKE_DOCKER_BACKUP_FAIL = "false"

    $env:FAKE_DOCKER_MIGRATE_FAIL = "true"
    $migrationFailure = Invoke-LuthnProcess $installedCli @("update", "ghcr.io/jakobsung/luthn:migration-failure")
    Assert-True ($migrationFailure.ExitCode -ne 0) "migration failure should stop update"
    Assert-True ($migrationFailure.Output -match "API remains stopped") "migration failure should leave API stopped for explicit recovery"
    $failedUpdateState = [IO.File]::ReadAllText($updateStateFile) | ConvertFrom-Json
    Assert-True ($failedUpdateState.status -ceq "failed" -and [IO.File]::Exists($failedUpdateState.backupPath)) "migration failure should preserve and record its backup"
    Assert-True ([IO.File]::ReadAllText($configFile) -match "(?m)^LUTHN_IMAGE=sha256:fake$") "migration failure should record the previous immutable image ID for recovery"
    $env:FAKE_DOCKER_MIGRATE_FAIL = "false"

    $env:FAKE_DOCKER_API_START_FAIL = "true"
    $apiStartFailure = Invoke-LuthnProcess $installedCli @("update", $targetImage)
    Assert-True ($apiStartFailure.ExitCode -ne 0) "API startup failure should stop update"
    Assert-True ($apiStartFailure.Output -match "API remains stopped") "API startup failure should fail closed"
    $env:FAKE_DOCKER_API_START_FAIL = "false"

    $recoveryUpdate = Invoke-LuthnProcess $installedCli @("update", $targetImage)
    Assert-True ($recoveryUpdate.ExitCode -eq 0) "a corrected update should recover after migration failure: $($recoveryUpdate.Output)"
    Assert-True ([IO.File]::ReadAllText($configFile) -match "(?m)^LUTHN_IMAGE=$([regex]::Escape($targetImage))$") "recovery update should restore the requested image"

    Remove-Item Env:LUTHN_CODEX_COMMAND
    Remove-Item Env:CODEX_CLI_PATH -ErrorAction SilentlyContinue
    $env:Path = $pwshDirectory
    $missingCodex = Invoke-LuthnProcess $installedCli @("connect", "codex")
    Assert-True ($missingCodex.ExitCode -ne 0) "missing Codex should fail without an array index error"
    Assert-True ($missingCodex.Output -match "No runnable Codex CLI was found") "missing Codex should provide an actionable error"
    Assert-True ($missingCodex.Output -notmatch "Index was outside") "missing Codex should not expose an array bounds exception"
    $env:Path = $originalPath
    $env:LUTHN_CODEX_COMMAND = $fakeCodex

    $unrelatedRegistration = [ordered]@{
        name = "luthn"
        enabled = $true
        transport = [ordered]@{ type = "stdio"; command = "unrelated-tool"; args = @("serve") }
    } | ConvertTo-Json -Depth 6
    [IO.File]::WriteAllText($fakeCodexState, ($unrelatedRegistration + "`n"), [Text.UTF8Encoding]::new($false))
    $unrelatedConnect = Invoke-LuthnProcess $installedCli @("connect", "codex")
    Assert-True ($unrelatedConnect.ExitCode -ne 0) "an unrelated luthn registration should stop setup"
    Assert-True (([IO.File]::ReadAllText($fakeCodexState) | ConvertFrom-Json).transport.command -ceq "unrelated-tool") "an unrelated registration should be preserved"
    [IO.File]::Delete($fakeCodexState)

    $validHooksBeforeMalformedTest = [IO.File]::ReadAllText($codexHooksFile)
    [IO.File]::WriteAllText($codexHooksFile, '{"hooks":{"Stop":{}}}', [Text.UTF8Encoding]::new($false))
    $malformedHooksBefore = [IO.File]::ReadAllText($codexHooksFile)
    $malformedHooksConnect = Invoke-LuthnProcess $installedCli @("connect", "codex")
    Assert-True ($malformedHooksConnect.ExitCode -ne 0) "malformed hooks configuration should stop setup"
    Assert-True ([IO.File]::ReadAllText($codexHooksFile) -ceq $malformedHooksBefore) "malformed hooks configuration should remain byte-for-byte unchanged"
    Assert-True (-not [IO.File]::Exists($fakeCodexState)) "malformed hooks configuration should not register MCP"
    [IO.File]::WriteAllText($codexHooksFile, $validHooksBeforeMalformedTest, [Text.UTF8Encoding]::new($false))

    $validInstructionsBeforeMalformedTest = [IO.File]::ReadAllText($codexInstructionsFile)
    $malformedInstructions = "$validInstructionsBeforeMalformedTest`r`n<!-- luthn:auto-recall:start -->`r`n"
    [IO.File]::WriteAllText($codexInstructionsFile, $malformedInstructions, [Text.UTF8Encoding]::new($false))
    $malformedRecallConnect = Invoke-LuthnProcess $installedCli @("connect", "codex", "--auto-recall")
    Assert-True ($malformedRecallConnect.ExitCode -ne 0) "malformed auto-recall markers should stop setup"
    Assert-True ([IO.File]::ReadAllText($codexInstructionsFile) -ceq $malformedInstructions) "malformed auto-recall instructions should remain unchanged"
    Assert-True (-not [IO.File]::Exists($fakeCodexState)) "malformed auto-recall instructions should not register MCP"
    [IO.File]::WriteAllText($codexInstructionsFile, $validInstructionsBeforeMalformedTest, [Text.UTF8Encoding]::new($false))

    $oversizedInstructions = "x" * (1024 * 1024 + 1)
    [IO.File]::WriteAllText($codexInstructionsFile, $oversizedInstructions, [Text.UTF8Encoding]::new($false))
    $oversizedInstructionsConnect = Invoke-LuthnProcess $installedCli @("connect", "codex")
    Assert-True ($oversizedInstructionsConnect.ExitCode -ne 0) "oversized Codex instructions should stop setup"
    Assert-True ($oversizedInstructionsConnect.Output -match "exceeds the supported size") "oversized Codex instructions should report the size limit"
    Assert-True (-not [IO.File]::Exists($fakeCodexState)) "oversized Codex instructions should not register MCP"
    [IO.File]::WriteAllText($codexInstructionsFile, $validInstructionsBeforeMalformedTest, [Text.UTF8Encoding]::new($false))

    $env:FAKE_MCP_PROBE_FAIL = "true"
    $hooksBeforeFailedProbe = [IO.File]::ReadAllText($codexHooksFile)
    $instructionsBeforeFailedProbe = [IO.File]::ReadAllText($codexInstructionsFile)
    $failedProbe = Invoke-LuthnProcess $installedCli @("connect", "codex")
    Assert-True ($failedProbe.ExitCode -ne 0) "MCP probe failure should fail setup"
    Assert-True (-not [IO.File]::Exists($fakeCodexState)) "MCP probe failure should roll back the Codex registration"
    Assert-True ([IO.File]::ReadAllText($codexHooksFile) -ceq $hooksBeforeFailedProbe) "MCP probe failure should restore hooks exactly"
    Assert-True ([IO.File]::ReadAllText($codexInstructionsFile) -ceq $instructionsBeforeFailedProbe) "MCP probe failure should preserve instructions"
    $env:FAKE_MCP_PROBE_FAIL = "false"

    if ($IsWindows) {
        $desktopCodexDir = Join-Path $env:LOCALAPPDATA "OpenAI/Codex/bin/test-runtime"
        [void][IO.Directory]::CreateDirectory($desktopCodexDir)
        $desktopCodexFixture = Join-Path $desktopCodexDir "codex-fixture.ps1"
        [IO.File]::Copy($fakeCodex, $desktopCodexFixture, $true)
        $desktopCodex = Join-Path $desktopCodexDir "codex.cmd"
        [IO.File]::WriteAllText($desktopCodex, "@echo off`r`npwsh -NoProfile -File `"%~dp0codex-fixture.ps1`" %*`r`n", [Text.Encoding]::ASCII)
        Remove-Item Env:LUTHN_CODEX_COMMAND
        $env:CODEX_CLI_PATH = Join-Path $testRoot "missing-codex.exe"
    } else {
        $env:LUTHN_CODEX_COMMAND = $fakeCodex
    }
    $connect = Invoke-LuthnProcess $installedCli @("connect", "codex")
    Assert-True ($connect.ExitCode -eq 0) "Codex connection should succeed: $($connect.Output)"
    if ($IsWindows) {
        Assert-True ([IO.File]::ReadAllText($fakeCodexLog) -match "(?m)^--version$") "Codex discovery should verify that a candidate is runnable"
        Remove-Item Env:CODEX_CLI_PATH
    }
    $env:LUTHN_CODEX_COMMAND = $fakeCodex
    Assert-True ([IO.File]::Exists($fakeCodexState)) "Codex MCP registration should exist"
    $registration = [IO.File]::ReadAllText($fakeCodexState) | ConvertFrom-Json
    Assert-True ($registration.transport.type -ceq "stdio") "Codex registration should be stdio"
    Assert-True (@($registration.transport.args) -ccontains "mcp") "Codex registration should invoke the mcp service"
    Assert-True (-not (([IO.File]::ReadAllText($fakeCodexState)).Contains($token))) "Codex registration should not contain the token"
    $matchingRegistration = [IO.File]::ReadAllText($fakeCodexState)
    $unrelatedDoctorRegistration = $matchingRegistration | ConvertFrom-Json
    $unrelatedDoctorRegistration.transport.command = "unrelated-tool"
    [IO.File]::WriteAllText($fakeCodexState, (($unrelatedDoctorRegistration | ConvertTo-Json -Depth 6) + "`n"), [Text.UTF8Encoding]::new($false))
    $changedRegistrationDoctor = Invoke-LuthnProcess $installedCli @("doctor", "--json")
    Assert-True ($changedRegistrationDoctor.ExitCode -ne 0) "doctor should fail when the luthn MCP registration points to an unrelated command"
    Assert-True ($changedRegistrationDoctor.Output -match '"name":"codex-mcp","status":"fail"') "doctor should identify a changed Codex MCP registration"
    [IO.File]::WriteAllText($fakeCodexState, $matchingRegistration, [Text.UTF8Encoding]::new($false))
    $installedHooks = [IO.File]::ReadAllText($codexHooksFile) | ConvertFrom-Json
    $installedLuthnHook = @($installedHooks.hooks.Stop | Where-Object { $_.matcher -ceq "luthn.agent-connector.v1" })
    Assert-True ($installedLuthnHook.Count -eq 1) "the Windows hook command check should find one Luthn hook"
    $installedHookCommand = [string]$installedLuthnHook[0].hooks[0].commandWindows
    Assert-True ($installedHookCommand.StartsWith('& "', [StringComparison]::Ordinal)) "the Windows hook command should invoke its quoted executable with PowerShell's call operator"
    $hookSyntaxCheck = [ScriptBlock]::Create($installedHookCommand)
    Assert-True ($null -ne $hookSyntaxCheck) "the registered Windows hook command should parse in PowerShell"

    $legacyHooks = [IO.File]::ReadAllText($codexHooksFile) | ConvertFrom-Json
    $legacyLuthnHook = @($legacyHooks.hooks.Stop | Where-Object { $_.matcher -ceq "luthn.agent-connector.v1" })
    $legacyLuthnHook[0].hooks[0].statusMessage = "Syncing Luthn memory"
    $legacyLuthnHook[0].hooks[0].timeout = 5
    [IO.File]::WriteAllText($codexHooksFile, (($legacyHooks | ConvertTo-Json -Depth 20) + "`n"), [Text.UTF8Encoding]::new($false))
    $legacyInstructions = [IO.File]::ReadAllText($codexInstructionsFile)
    $recallStartMarker = "<!-- luthn:auto-recall:start -->"
    $recallEndMarker = "<!-- luthn:auto-recall:end -->"
    $recallStart = $legacyInstructions.IndexOf($recallStartMarker, [StringComparison]::Ordinal)
    $recallEnd = $legacyInstructions.IndexOf($recallEndMarker, $recallStart, [StringComparison]::Ordinal) + $recallEndMarker.Length
    $legacyRecallBlock = "$recallStartMarker`r`n# Luthn lightweight recall`r`n`r`nLegacy managed instructions.`r`n$recallEndMarker"
    $legacyInstructions = $legacyInstructions.Substring(0, $recallStart) + $legacyRecallBlock + $legacyInstructions.Substring($recallEnd)
    [IO.File]::WriteAllText($codexInstructionsFile, $legacyInstructions, [Text.UTF8Encoding]::new($false))

    $upgradeConnect = Invoke-LuthnProcess $installedCli @("connect", "codex")
    Assert-True ($upgradeConnect.ExitCode -eq 0) "reconnect should upgrade legacy Luthn-managed configuration: $($upgradeConnect.Output)"
    $upgradedHooks = [IO.File]::ReadAllText($codexHooksFile) | ConvertFrom-Json
    $upgradedLuthnHook = @($upgradedHooks.hooks.Stop | Where-Object { $_.matcher -ceq "luthn.agent-connector.v1" })
    Assert-True ($upgradedLuthnHook.Count -eq 1) "upgrade should retain exactly one managed Stop hook"
    Assert-True ($upgradedLuthnHook[0].hooks[0].statusMessage -ceq "Luthn 메모리 저장 예약 중…") "upgrade should replace the legacy Stop status message"
    Assert-True ([int]$upgradedLuthnHook[0].hooks[0].timeout -eq 10) "upgrade should replace the legacy five-second Stop timeout"
    Assert-True (@($upgradedHooks.hooks.Stop | Where-Object { $_.matcher -ceq "other.owner" }).Count -eq 1) "upgrade should preserve unrelated hooks"
    $upgradedInstructions = [IO.File]::ReadAllText($codexInstructionsFile)
    Assert-True (-not $upgradedInstructions.Contains("Legacy managed instructions.")) "upgrade should replace the previous managed recall block"
    Assert-True ($upgradedInstructions.Contains("Preserve this text.")) "upgrade should preserve user instructions"

    $hookHashBeforeRepeat = (Get-FileHash -LiteralPath $codexHooksFile -Algorithm SHA256).Hash
    $instructionsHashBeforeRepeat = (Get-FileHash -LiteralPath $codexInstructionsFile -Algorithm SHA256).Hash
    $connectAgain = Invoke-LuthnProcess $installedCli @("connect", "codex")
    Assert-True ($connectAgain.ExitCode -eq 0) "Codex MCP connection should be idempotent: $($connectAgain.Output)"
    Assert-True ((Get-FileHash -LiteralPath $codexHooksFile -Algorithm SHA256).Hash -eq $hookHashBeforeRepeat) "repeated connect should not rewrite the trusted hook"
    Assert-True ((Get-FileHash -LiteralPath $codexInstructionsFile -Algorithm SHA256).Hash -eq $instructionsHashBeforeRepeat) "repeated connect should not rewrite managed instructions"

    $staleConnectorState = [IO.File]::ReadAllText($codexOwnershipState) | ConvertFrom-Json
    $staleConnectorState.connectorVersion = "2"
    $staleConnectorState.helperDigest = "0" * 64
    [IO.File]::WriteAllText($codexOwnershipState, (($staleConnectorState | ConvertTo-Json -Depth 20) + "`n"), [Text.UTF8Encoding]::new($false))
    $staleConnectorHooks = [IO.File]::ReadAllText($codexHooksFile) | ConvertFrom-Json
    $staleManagedHook = @($staleConnectorHooks.hooks.Stop | Where-Object { $_.matcher -ceq "luthn.agent-connector.v1" })
    $staleManagedHook[0].hooks[0].statusMessage = "Stale managed connector template"
    $staleManagedHook[0].hooks[0].timeout = 5
    [IO.File]::WriteAllText($codexHooksFile, (($staleConnectorHooks | ConvertTo-Json -Depth 20) + "`n"), [Text.UTF8Encoding]::new($false))
    $connectorUpdateContent = [IO.File]::ReadAllText((Join-Path $RepoRoot "scripts/luthn.ps1")) + "`n# connector-update-rollback-fixture`n"
    [IO.File]::WriteAllText($connectorUpdateCli, $connectorUpdateContent, [Text.UTF8Encoding]::new($false))
    $runtimeHashBeforeFailedReconcile = (Get-FileHash -LiteralPath $installedCli -Algorithm SHA256).Hash
    $composeHashBeforeFailedReconcile = (Get-FileHash -LiteralPath (Join-Path $windowsRoot "data/compose.yaml") -Algorithm SHA256).Hash
    $hooksBeforeFailedReconcile = [IO.File]::ReadAllText($codexHooksFile)
    $instructionsBeforeFailedReconcile = [IO.File]::ReadAllText($codexInstructionsFile)
    $stateBeforeFailedReconcile = [IO.File]::ReadAllText($codexOwnershipState)
    $env:LUTHN_WINDOWS_CLI_SOURCE_FILE = $connectorUpdateCli
    $env:FAKE_MCP_PROBE_FAIL = "true"
    $failedConnectorUpdate = Invoke-LuthnProcess $installedCli @("update", $targetImage)
    Assert-True ($failedConnectorUpdate.ExitCode -ne 0) "connector probe failure should fail the update"
    Assert-True ((Get-FileHash -LiteralPath $installedCli -Algorithm SHA256).Hash -eq $runtimeHashBeforeFailedReconcile) "failed connector reconciliation should restore the previous CLI bytes"
    Assert-True ((Get-FileHash -LiteralPath (Join-Path $windowsRoot "data/compose.yaml") -Algorithm SHA256).Hash -eq $composeHashBeforeFailedReconcile) "failed connector reconciliation should restore the previous Compose bytes"
    Assert-True ([IO.File]::ReadAllText($codexHooksFile) -ceq $hooksBeforeFailedReconcile) "failed connector reconciliation should restore hooks exactly"
    Assert-True ([IO.File]::ReadAllText($codexInstructionsFile) -ceq $instructionsBeforeFailedReconcile) "failed connector reconciliation should restore instructions exactly"
    Assert-True ([IO.File]::ReadAllText($codexOwnershipState) -ceq $stateBeforeFailedReconcile) "failed connector reconciliation should restore ownership state exactly"
    $env:FAKE_MCP_PROBE_FAIL = "false"
    $env:LUTHN_WINDOWS_CLI_SOURCE_FILE = $updatedCli

    $connectorUpdate = Invoke-LuthnProcess $installedCli @("update", $targetImage)
    Assert-True ($connectorUpdate.ExitCode -eq 0) "update should reconcile a stale connector template: $($connectorUpdate.Output)"
    Assert-True ($connectorUpdate.Output -match "Reconciling Codex connector template version 3") "update should report connector template reconciliation"
    Assert-True ($connectorUpdate.Output -match "Restart required: Luthn MCP compatibility changed") "connector template changes should require a Codex host restart"
    Assert-True ($connectorUpdate.Output -match "Agent notice: restart the current Codex host before invoking Luthn tools again\.") "connector template changes should emit the bounded agent notice"
    $reconciledConnectorState = [IO.File]::ReadAllText($codexOwnershipState) | ConvertFrom-Json
    Assert-True ($reconciledConnectorState.connectorVersion -ceq "3") "successful update should record the current connector template version"
    Assert-True ($reconciledConnectorState.helperDigest -cmatch "^[0-9a-f]{64}$" -and $reconciledConnectorState.helperDigest -cne ("0" * 64)) "successful update should replace a same-version stale helper digest"
    Assert-True ($reconciledConnectorState.templateDigest -cmatch "^[0-9a-f]{64}$") "successful update should record the current managed template digest"
    $reconciledHooks = [IO.File]::ReadAllText($codexHooksFile) | ConvertFrom-Json
    $reconciledManagedHook = @($reconciledHooks.hooks.Stop | Where-Object { $_.matcher -ceq "luthn.agent-connector.v1" })
    Assert-True ($reconciledManagedHook[0].hooks[0].statusMessage -ceq "Luthn 메모리 저장 예약 중…") "successful update should replace the stale managed hook template"
    Assert-True ([int]$reconciledManagedHook[0].hooks[0].timeout -eq 10) "successful update should replace a stale five-second Stop timeout"
    Assert-True (@($reconciledHooks.hooks.Stop | Where-Object { $_.matcher -ceq "other.owner" }).Count -eq 1) "connector reconciliation should preserve unrelated hooks"
    Assert-True ([IO.File]::ReadAllText($codexInstructionsFile).Contains("Preserve this text.")) "connector reconciliation should preserve unrelated instructions"

    $enableRecall = Invoke-LuthnProcess $installedCli @("connect", "codex", "--auto-recall")
    Assert-True ($enableRecall.ExitCode -eq 0) "explicit auto-recall compatibility should succeed: $($enableRecall.Output)"
    $recallHash = (Get-FileHash -LiteralPath $codexInstructionsFile -Algorithm SHA256).Hash
    $enableRecallAgain = Invoke-LuthnProcess $installedCli @("connect", "codex", "--auto-recall")
    Assert-True ($enableRecallAgain.ExitCode -eq 0) "explicit auto-recall compatibility should be idempotent: $($enableRecallAgain.Output)"
    Assert-True ((Get-FileHash -LiteralPath $codexInstructionsFile -Algorithm SHA256).Hash -eq $recallHash) "repeated auto-recall setup should not rewrite instructions"
    $instructionText = [IO.File]::ReadAllText($codexInstructionsFile)
    Assert-True (([regex]::Matches($instructionText, [regex]::Escape("<!-- luthn:auto-recall:start -->"))).Count -eq 1) "auto-recall should install one managed block"
    Assert-True ($instructionText.Contains("Preserve this text.")) "auto-recall should preserve user instructions"
    Assert-True ($instructionText.Contains("``maxItems``: 3") -and $instructionText.Contains("``maxTokens``: 600")) "auto-recall should install bounded recall instructions"
    Assert-True ($instructionText.Contains("``timeoutMs``: 200") -and $instructionText.Contains("``cacheTtlSeconds``: 600") -and $instructionText.Contains("``failOpen``: true")) "auto-recall should preserve timeout, cache, and fail-open bounds"
    Assert-True ($instructionText.Contains("exactly one commentary line") -and $instructionText.Contains("``Luthn 메모리 N개 참고``")) "auto-recall should describe the single positive recall commentary"
    Assert-True ($instructionText.Contains("zero actual memory") -and $instructionText.Contains("times out, returns an error, cannot be parsed") -and $instructionText.Contains("uses any fail-open path")) "auto-recall should suppress commentary for empty and failed recall"
    Assert-True ($instructionText.Contains("when ``get_context_pack`` was not called") -and $instructionText.Contains("at most once per user turn")) "auto-recall should suppress uncalled recall and duplicate commentary"
    Assert-True ($instructionText.Contains("``projectKey``, ``taskKey``, and ``topicTags``") -and $instructionText.Contains("Never send a raw workspace path") -and $instructionText.Contains("transcript path")) "auto-recall should limit optional recall metadata to non-sensitive normalized keys"
    Assert-True ($instructionText.Contains("memory titles, content, IDs, queries, scores, sources") -and $instructionText.Contains("normal assistant response or final response")) "auto-recall should protect memory details and response channels"

    $connectionStatus = Invoke-LuthnProcess $installedCli @("connection", "status", "codex")
    Assert-True ($connectionStatus.ExitCode -eq 0) "Windows Codex connection status should succeed: $($connectionStatus.Output)"
    [IO.File]::WriteAllText($codexPendingState, '{"version":2,"setupState":"cleanup-required"}', [Text.UTF8Encoding]::new($false))
    $pendingConnectionStatus = Invoke-LuthnProcess $installedCli @("connection", "status", "codex")
    Assert-True ($pendingConnectionStatus.Output -match "Local connector: cleanup-required") "connection status should expose pending cleanup state"
    [IO.File]::Delete($codexPendingState)
    Assert-True ($connectionStatus.Output -match "automatic-ingestion: configured") "connection status should report the hook"
    Assert-True ($connectionStatus.Output -match "lightweight-recall: enabled") "connection status should report auto-recall"

    $disableRecall = Invoke-LuthnProcess $installedCli @("connect", "codex", "--no-auto-recall")
    Assert-True ($disableRecall.ExitCode -eq 0) "explicit auto-recall opt-out should succeed: $($disableRecall.Output)"
    Assert-True (-not ([IO.File]::ReadAllText($codexInstructionsFile).Contains("luthn:auto-recall:start"))) "opt-out should remove only the managed recall block"
    Assert-True (-not ([IO.File]::ReadAllText($codexOwnershipState) | ConvertFrom-Json).autoRecall) "opt-out should update connector ownership state"
    $restoreDefaultRecall = Invoke-LuthnProcess $installedCli @("connect", "codex")
    Assert-True ($restoreDefaultRecall.ExitCode -eq 0) "default reconnect should restore auto-recall: $($restoreDefaultRecall.Output)"
    Assert-True ([IO.File]::ReadAllText($codexInstructionsFile).Contains("luthn:auto-recall:start")) "default reconnect should restore the managed recall block"

    $env:LUTHN_CODEX_HOOK_SYNCHRONOUS = "true"
    $env:LUTHN_CODEX_HOOK_CAPTURE_FILE = $codexHookCapture
    $validHookEvent = [ordered]@{
        hook_event_name = "Stop"
        session_id = "private-session-id"
        turn_id = "private-turn-id"
        last_assistant_message = "Implemented the Windows connector safely."
        transcript_path = "C:\private\transcript.jsonl"
        cwd = "C:\private\workspace"
    } | ConvertTo-Json -Compress
    $env:LUTHN_CODEX_HOOK_TEST_THROW = "true"
    $hookResult = Invoke-CodexHookProcess $installedCli $validHookEvent
    Remove-Item Env:LUTHN_CODEX_HOOK_TEST_THROW
    Assert-True ($hookResult.ExitCode -eq 0 -and [IO.File]::Exists($codexHookCapture)) "the Windows Stop hook should capture a bounded capsule: $($hookResult.Output)"
    $capturedCapsule = [IO.File]::ReadAllText($codexHookCapture) | ConvertFrom-Json
    Assert-True ($capturedCapsule.summary -ceq "Implemented the Windows connector safely.") "the hook should capture only the final assistant summary"
    $capturedText = [IO.File]::ReadAllText($codexHookCapture)
    Assert-True (-not $capturedText.Contains("private-session-id") -and -not $capturedText.Contains("private-turn-id")) "the hook should hash stable identifiers"
    Assert-True (-not $capturedText.Contains("transcript.jsonl") -and -not $capturedText.Contains("private\\workspace")) "the hook should ignore transcript and working-directory fields"
    $captureHash = (Get-FileHash -LiteralPath $codexHookCapture -Algorithm SHA256).Hash
    $secretHookEvent = [ordered]@{
        hook_event_name = "Stop"
        session_id = "secret-session"
        turn_id = "secret-turn"
        last_assistant_message = "api_key=sk-1234567890abcdefghijklmnop"
    } | ConvertTo-Json -Compress
    $secretHook = Invoke-CodexHookProcess $installedCli $secretHookEvent
    Assert-True ($secretHook.ExitCode -eq 0) "a secret-bearing hook payload should fail open"
    Assert-True ((Get-FileHash -LiteralPath $codexHookCapture -Algorithm SHA256).Hash -eq $captureHash) "a secret-bearing response should not be captured"
    $operatorTokenHookEvent = [ordered]@{
        hook_event_name = "Stop"
        session_id = "operator-secret-session"
        turn_id = "operator-secret-turn"
        last_assistant_message = $operatorToken
    } | ConvertTo-Json -Compress
    $operatorTokenHook = Invoke-CodexHookProcess $installedCli $operatorTokenHookEvent
    Assert-True ($operatorTokenHook.ExitCode -eq 0) "an operator-token hook payload should fail open"
    Assert-True ((Get-FileHash -LiteralPath $codexHookCapture -Algorithm SHA256).Hash -eq $captureHash) "a bare generated operator token should not be captured"
    $oversizedHook = "{`"hook_event_name`":`"Stop`",`"session_id`":`"s`",`"turn_id`":`"t`",`"last_assistant_message`":`"$(`"x`" * 270000)`"}"
    $oversizedResult = Invoke-CodexHookProcess $installedCli $oversizedHook
    Assert-True ($oversizedResult.ExitCode -eq 0) "oversized hook input should fail open"
    Assert-True ((Get-FileHash -LiteralPath $codexHookCapture -Algorithm SHA256).Hash -eq $captureHash) "oversized hook input should not be captured"

    [IO.File]::Delete($codexHookCapture)
    $defaultHook = Invoke-CodexHookProcess $installedCli $validHookEvent
    Assert-True ($defaultHook.ExitCode -eq 0 -and [IO.File]::Exists($codexHookCapture)) "the default Windows hook uploader should complete before the hook returns"
    Assert-True (([IO.File]::ReadAllText($codexHookCapture) | ConvertFrom-Json).summary -ceq "Implemented the Windows connector safely.") "the default Windows hook uploader should preserve the bounded capsule"
    Remove-Item Env:LUTHN_CODEX_HOOK_SYNCHRONOUS
    Remove-Item Env:LUTHN_CODEX_HOOK_CAPTURE_FILE

    [IO.File]::Delete($codexOwnershipState)
    $recoverOwnership = Invoke-LuthnProcess $installedCli @("connect", "codex")
    Assert-True ($recoverOwnership.ExitCode -eq 0) "matching Codex MCP registration should recover ownership state: $($recoverOwnership.Output)"
    Assert-True ([IO.File]::Exists($codexOwnershipState)) "matching registration should restore ownership state for uninstall"
    Assert-True (([IO.File]::ReadAllText($codexOwnershipState) | ConvertFrom-Json).autoRecall) "ownership recovery should retain the managed auto-recall state"

    $legacyCliContent = [IO.File]::ReadAllText((Join-Path $RepoRoot "scripts/luthn.ps1"))
    $legacyCliContent = $legacyCliContent -replace '(?m)^\s*"manifest"\s*\{[^\r\n]*\}\r?\n', ''
    $legacyCliContent = $legacyCliContent -replace '(?m)^\s*(helperDigest|templateDigest)\s*=.*\r?\n', ''
    [IO.File]::WriteAllText($legacyCli, $legacyCliContent, [Text.UTF8Encoding]::new($false))
    $env:LUTHN_WINDOWS_CLI_SOURCE_FILE = $legacyCli
    $legacyRollback = Invoke-LuthnProcess $installedCli @("update", "ghcr.io/jakobsung/luthn:legacy")
    Assert-True ($legacyRollback.ExitCode -eq 0) "update should roll back to a pre-manifest Windows runtime: $($legacyRollback.Output)"
    $legacyConnectorState = [IO.File]::ReadAllText($codexOwnershipState) | ConvertFrom-Json
    Assert-True ($legacyConnectorState.connectorVersion -ceq "3") "legacy rollback should retain version-only connector state"
    Assert-True (-not ($legacyConnectorState.PSObject.Properties.Name -contains "helperDigest")) "legacy rollback state should not require a helper digest"
    Assert-True (-not ($legacyConnectorState.PSObject.Properties.Name -contains "templateDigest")) "legacy rollback state should not require a template digest"

    if ($IsWindows) {
        Remove-Item Env:LUTHN_CODEX_COMMAND
        $env:CODEX_CLI_PATH = Join-Path $testRoot "missing-codex.exe"
    } else {
        $env:LUTHN_CODEX_COMMAND = $fakeCodex
    }
    $env:Path = $pwshDirectory
    $env:FAKE_CODEX_REMOVE_FAIL = "true"
    $blockedUninstall = Invoke-LuthnProcess $installedCli @("uninstall")
    Assert-True ($blockedUninstall.ExitCode -ne 0) "uninstall should stop when Codex cleanup fails"
    Assert-True ($blockedUninstall.Output -notmatch "Index was outside") "uninstall should not expose an array bounds exception"
    Assert-True ([IO.Directory]::Exists((Join-Path $windowsRoot "data"))) "blocked uninstall should preserve runtime"
    Assert-True ([IO.File]::ReadAllText($codexHooksFile).Contains("luthn.agent-connector.v1")) "blocked uninstall should restore the Luthn hook"
    Assert-True ([IO.File]::ReadAllText($codexInstructionsFile).Contains("luthn:auto-recall:start")) "blocked uninstall should restore auto-recall instructions"
    $env:FAKE_CODEX_REMOVE_FAIL = "false"

    $uninstall = Invoke-LuthnProcess $installedCli @("uninstall")
    Assert-True ($uninstall.ExitCode -eq 0) "default uninstall should clean up Codex and succeed: $($uninstall.Output)"
    Assert-True (-not [IO.Directory]::Exists((Join-Path $windowsRoot "data"))) "default uninstall should remove runtime data"
    Assert-True ([IO.File]::Exists($configFile)) "default uninstall should preserve config"
    Assert-True ([IO.File]::Exists($tokenFile)) "default uninstall should preserve token"
    Assert-True (-not [IO.File]::Exists($fakeCodexState)) "default uninstall should remove owned Codex registration"
    $finalHooks = [IO.File]::ReadAllText($codexHooksFile) | ConvertFrom-Json
    Assert-True (@($finalHooks.hooks.Stop | Where-Object { $_.matcher -ceq "luthn.agent-connector.v1" }).Count -eq 0) "uninstall should remove the Luthn hook"
    Assert-True (@($finalHooks.hooks.Stop | Where-Object { $_.matcher -ceq "other.owner" }).Count -eq 1) "uninstall should preserve unrelated hooks"
    Assert-True ([IO.File]::ReadAllText($codexInstructionsFile) -ceq $originalInstructions) "uninstall should remove only the managed auto-recall block"
    Assert-True (-not [IO.File]::Exists($installedCli) -and -not [IO.File]::Exists($installedShim)) "default uninstall should remove only Luthn CLI files"
    Assert-True ([IO.File]::Exists($sharedBinSentinel)) "default uninstall should preserve unrelated files in a shared bin directory"

    Write-Host "Windows lifecycle tests passed."
} finally {
    Remove-Item Env:FAKE_CODEX_REMOVE_FAIL -ErrorAction SilentlyContinue
    Remove-Item Env:FAKE_DOCKER_API_START_FAIL -ErrorAction SilentlyContinue
    Remove-Item Env:FAKE_DOCKER_BACKUP_FAIL -ErrorAction SilentlyContinue
    Remove-Item Env:FAKE_DOCKER_COMPOSE_FAIL -ErrorAction SilentlyContinue
    Remove-Item Env:FAKE_DOCKER_INFO_FAIL -ErrorAction SilentlyContinue
    Remove-Item Env:FAKE_DOCKER_MIGRATE_FAIL -ErrorAction SilentlyContinue
    Remove-Item Env:FAKE_DOCKER_PULL_FAIL -ErrorAction SilentlyContinue
    Remove-Item Env:FAKE_MCP_PROBE_FAIL -ErrorAction SilentlyContinue
    Remove-Item Env:CODEX_CLI_PATH -ErrorAction SilentlyContinue
    Remove-Item Env:LUTHN_BIN_DIR -ErrorAction SilentlyContinue
    Remove-Item Env:LUTHN_CODEX_HOOK_CAPTURE_FILE -ErrorAction SilentlyContinue
    Remove-Item Env:LUTHN_CODEX_HOOK_TEST_THROW -ErrorAction SilentlyContinue
    Remove-Item Env:LUTHN_CODEX_HOOK_INSTRUCTIONS_FILE -ErrorAction SilentlyContinue
    Remove-Item Env:LUTHN_CODEX_HOOK_SYNCHRONOUS -ErrorAction SilentlyContinue
    Remove-Item Env:CODEX_HOME -ErrorAction SilentlyContinue
    Remove-Item Env:LUTHN_CODEX_HOOKS_FILE -ErrorAction SilentlyContinue
    Remove-Item Env:LUTHN_CODEX_INSTRUCTIONS_FILE -ErrorAction SilentlyContinue
    Remove-Item Env:LUTHN_CODEX_SKIP_OBSERVATION -ErrorAction SilentlyContinue
    Remove-Item Env:CLAUDE_CONFIG_DIR -ErrorAction SilentlyContinue
    Remove-Item Env:LUTHN_CLAUDE_SETTINGS_FILE -ErrorAction SilentlyContinue
    Remove-Item Env:LUTHN_CLAUDE_INSTRUCTIONS_FILE -ErrorAction SilentlyContinue
    Remove-Item Env:LUTHN_DOCKER_DESKTOP_COMMAND -ErrorAction SilentlyContinue
    Remove-Item Env:LUTHN_INSTALLER_DOCKER_COMMAND -ErrorAction SilentlyContinue
    if ($originalPath) { $env:Path = $originalPath }
    if ($env:LUTHN_KEEP_TEST_ROOT -cne "true" -and [IO.Directory]::Exists($testRoot)) { [IO.Directory]::Delete($testRoot, $true) }
}
