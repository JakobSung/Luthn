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
    try {
        $output = & $CliPath @Arguments *>&1 | Out-String
        return [pscustomobject]@{ ExitCode = 0; Output = $output }
    } catch {
        return [pscustomobject]@{ ExitCode = 1; Output = ($_ | Out-String) }
    }
}

function Invoke-InstallerProcess {
    param([string]$InstallerPath, [switch]$ConnectCodex)
    try {
        $output = if ($ConnectCodex) {
            & $InstallerPath -ConnectCodex *>&1 | Out-String
        } else {
            & $InstallerPath *>&1 | Out-String
        }
        return [pscustomobject]@{ ExitCode = 0; Output = $output }
    } catch {
        return [pscustomobject]@{ ExitCode = 1; Output = ($_ | Out-String) }
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
$sharedBinDir = Join-Path $testRoot "shared 도구 bin"
$sharedBinSentinel = Join-Path $sharedBinDir "unrelated-tool.txt"
$installedCli = Join-Path $sharedBinDir "luthn.ps1"
$codexOwnershipState = Join-Path $windowsRoot "state/connectors/codex-windows.json"
$codexPendingState = Join-Path $windowsRoot "state/connectors/codex-windows.pending.json"
$codexHome = Join-Path $testRoot "codex home"
$codexHooksFile = Join-Path $codexHome "hooks.json"
$codexInstructionsFile = Join-Path $codexHome "AGENTS.md"
$codexHookCapture = Join-Path $testRoot "hook-capsule.json"
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
if ($args.Count -ge 1 -and $args[0] -ceq "pull") { if ($env:FAKE_DOCKER_PULL_FAIL -ceq "true") { exit 16 }; "pulled"; exit 0 }
if ($args.Count -ge 2 -and $args[0] -ceq "image" -and $args[1] -ceq "inspect") {
    if ($joined -match "org.opencontainers.image.revision") { "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" }
    elseif ($joined -match "RepoDigests") { "ghcr.io/jakobsung/luthn@sha256:fake" }
    else { "sha256:fake" }
    exit 0
}
if ($args.Count -ge 1 -and $args[0] -ceq "inspect") { "sha256:fake"; exit 0 }
if ($args.Count -ge 1 -and $args[0] -ceq "run") { "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"; exit 0 }
if ($args.Count -ge 1 -and $args[0] -ceq "ps") { exit 0 }
if ($args.Count -ge 1 -and $args[0] -in @("stop", "kill")) { exit 0 }
if ($args.Count -ge 1 -and $args[0] -ceq "compose") {
    if ($args -ccontains "--list-tools") { if ($env:FAKE_MCP_PROBE_FAIL -ceq "true") { exit 14 }; "get_context_pack"; "search_safe_context"; exit 0 }
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
if [ "$1" = "pull" ]; then [ "${FAKE_DOCKER_PULL_FAIL:-false}" = "true" ] && exit 16; echo "pulled"; exit 0; fi
if [ "$1" = "image" ] && [ "$2" = "inspect" ]; then
  case "$joined" in
    *org.opencontainers.image.revision*) echo "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" ;;
    *RepoDigests*) echo "ghcr.io/jakobsung/luthn@sha256:fake" ;;
    *) echo "sha256:fake" ;;
  esac
  exit 0
fi
if [ "$1" = "inspect" ]; then echo "sha256:fake"; exit 0; fi
if [ "$1" = "run" ]; then echo "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"; exit 0; fi
if [ "$1" = "ps" ]; then exit 0; fi
if [ "$1" = "stop" ] || [ "$1" = "kill" ]; then exit 0; fi
if [ "$1" = "compose" ]; then
  case "$joined" in
    *--list-tools*) [ "${FAKE_MCP_PROBE_FAIL:-false}" = "true" ] && exit 14; printf 'get_context_pack\nsearch_safe_context\n'; exit 0 ;;
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
    $env:LUTHN_CODEX_HOOKS_FILE = $codexHooksFile
    $env:LUTHN_CODEX_INSTRUCTIONS_FILE = $codexInstructionsFile
    $env:LUTHN_CODEX_SKIP_OBSERVATION = "true"
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
    Assert-True ($install.Output -match "Luthn is ready") "install should report readiness"

    $configFile = Join-Path $windowsRoot "config/luthn.env"
    $tokenFile = Join-Path $windowsRoot "config/service-token"
    Assert-True ([IO.File]::Exists($configFile)) "config should exist"
    Assert-True ([IO.File]::Exists($tokenFile)) "token should exist"
    $token = [IO.File]::ReadAllText($tokenFile)
    Assert-True ($token -cmatch "^[0-9a-f]{48}$") "token should be a 24-byte hex value"
    $configBytes = [IO.File]::ReadAllBytes($configFile)
    Assert-True (-not ($configBytes.Length -ge 3 -and $configBytes[0] -eq 0xEF -and $configBytes[1] -eq 0xBB -and $configBytes[2] -eq 0xBF)) "config should be UTF-8 without BOM"
    Assert-True ([IO.File]::ReadAllText($configFile) -cmatch "Luthn__Auth__Tokens__0__Sha256Digest=sha256:[0-9a-f]{64}") "config should preserve the token-digest sha256 prefix"
    Assert-True (-not ([IO.File]::ReadAllText($fakeDockerLog).Contains($token))) "Docker arguments and logs should not contain the token"
    Assert-True (-not (([IO.File]::ReadAllText($fakeCodexState)).Contains($token))) "one-step Codex registration should not contain the token"
    $installedHooks = [IO.File]::ReadAllText($codexHooksFile) | ConvertFrom-Json
    $luthnHook = @($installedHooks.hooks.Stop | Where-Object { $_.matcher -ceq "luthn.agent-connector.v1" })
    Assert-True ($luthnHook.Count -eq 1) "one-step setup should install one Luthn Stop hook"
    Assert-True ($luthnHook[0].hooks[0].commandWindows -ceq $luthnHook[0].hooks[0].command) "Windows hook should include a matching commandWindows entry"
    Assert-True (@($installedHooks.hooks.Stop | Where-Object { $_.matcher -ceq "other.owner" }).Count -eq 1) "one-step setup should preserve unrelated hooks"
    Assert-True (-not ([IO.File]::ReadAllText($codexHooksFile).Contains($token))) "Codex hooks should not contain the token"
    Assert-True ([IO.File]::ReadAllText($codexInstructionsFile).Contains("luthn:auto-recall:start")) "one-step setup should enable auto-recall by default"
    $connectorState = [IO.File]::ReadAllText($codexOwnershipState) | ConvertFrom-Json
    Assert-True ($connectorState.version -eq 2 -and $connectorState.integration -ceq "host-hook-mcp") "Windows connector state should record the hook and MCP integration"
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

    $firstHash = (Get-FileHash -LiteralPath $installedCli -Algorithm SHA256).Hash
    $secondInstall = Invoke-InstallerProcess $installerPath
    Assert-True ($secondInstall.ExitCode -eq 0) "repeated install should be idempotent: $($secondInstall.Output)"
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
    Assert-True ($daemonAutoStart.ExitCode -eq 0) "the bootstrap should start Docker Desktop and wait for its engine: $($daemonAutoStart.Output)"
    Assert-True ($daemonAutoStart.Output -match "Starting it and waiting") "automatic Docker Desktop startup should be reported"
    Remove-Item Env:LUTHN_DOCKER_DESKTOP_COMMAND
    if ([IO.File]::Exists($fakeDockerReadyMarker)) { [IO.File]::Delete($fakeDockerReadyMarker) }

    $daemonUnavailable = Invoke-InstallerProcess $installerPath
    Assert-True ($daemonUnavailable.ExitCode -ne 0) "unreachable Docker daemon should fail preflight"
    Assert-True ($daemonUnavailable.Output -match "Start Docker Desktop, wait for the engine, and retry") "daemon failure should explain the recovery action"
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

    $updatedCliContent = [IO.File]::ReadAllText((Join-Path $RepoRoot "scripts/luthn.ps1")) + "`n# windows-update-test-fixture`n"
    [IO.File]::WriteAllText($updatedCli, $updatedCliContent, [Text.UTF8Encoding]::new($false))
    $env:LUTHN_WINDOWS_CLI_SOURCE_FILE = $updatedCli
    $targetImage = "ghcr.io/jakobsung/luthn:sha-$('b' * 40)"
    $updateLogStart = [IO.File]::ReadAllLines($fakeDockerLog).Count
    $update = Invoke-LuthnProcess $installedCli @("update", $targetImage)
    Assert-True ($update.ExitCode -eq 0) "Windows update should succeed: $($update.Output)"
    Assert-True ($update.Output -match "Luthn update completed") "successful update should report completion"
    Assert-True ($update.Output -match "Revision: a{40} -> a{40}") "successful update should report the revision transition"
    Assert-True ([IO.File]::ReadAllText($installedCli) -match "windows-update-test-fixture") "update should refresh the installed Windows CLI"
    Assert-True ([IO.File]::ReadAllText($configFile) -match "(?m)^LUTHN_IMAGE=$([regex]::Escape($targetImage))$") "update should select the target image"
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

    $updatedHash = (Get-FileHash -LiteralPath $installedCli -Algorithm SHA256).Hash
    $env:FAKE_DOCKER_PULL_FAIL = "true"
    $pullFailure = Invoke-LuthnProcess $installedCli @("update", "ghcr.io/jakobsung/luthn:pull-failure")
    Assert-True ($pullFailure.ExitCode -ne 0) "pull failure should stop update"
    Assert-True ($pullFailure.Output -match "running API and previous image were preserved") "pull failure should report preservation"
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

    $desktopCodexDir = Join-Path $env:LOCALAPPDATA "OpenAI/Codex/bin/test-runtime"
    [void][IO.Directory]::CreateDirectory($desktopCodexDir)
    $desktopCodexFixture = Join-Path $desktopCodexDir "codex-fixture.ps1"
    [IO.File]::Copy($fakeCodex, $desktopCodexFixture, $true)
    $desktopCodex = Join-Path $desktopCodexDir "codex.cmd"
    [IO.File]::WriteAllText($desktopCodex, "@echo off`r`npwsh -NoProfile -File `"%~dp0codex-fixture.ps1`" %*`r`n", [Text.Encoding]::ASCII)
    Remove-Item Env:LUTHN_CODEX_COMMAND
    $env:CODEX_CLI_PATH = Join-Path $testRoot "missing-codex.exe"
    $connect = Invoke-LuthnProcess $installedCli @("connect", "codex")
    Assert-True ($connect.ExitCode -eq 0) "Codex Desktop CLI discovery should succeed: $($connect.Output)"
    Assert-True ([IO.File]::ReadAllText($fakeCodexLog) -match "(?m)^--version$") "Codex discovery should verify that a candidate is runnable"
    Remove-Item Env:CODEX_CLI_PATH
    $env:LUTHN_CODEX_COMMAND = $fakeCodex
    Assert-True ([IO.File]::Exists($fakeCodexState)) "Codex MCP registration should exist"
    $registration = [IO.File]::ReadAllText($fakeCodexState) | ConvertFrom-Json
    Assert-True ($registration.transport.type -ceq "stdio") "Codex registration should be stdio"
    Assert-True (@($registration.transport.args) -ccontains "mcp") "Codex registration should invoke the mcp service"
    Assert-True (-not (([IO.File]::ReadAllText($fakeCodexState)).Contains($token))) "Codex registration should not contain the token"
    $installedHooks = [IO.File]::ReadAllText($codexHooksFile) | ConvertFrom-Json
    $installedLuthnHook = @($installedHooks.hooks.Stop | Where-Object { $_.matcher -ceq "luthn.agent-connector.v1" })
    Assert-True ($installedLuthnHook.Count -eq 1) "the Windows hook command check should find one Luthn hook"
    $installedHookCommand = [string]$installedLuthnHook[0].hooks[0].commandWindows
    Assert-True ($installedHookCommand.StartsWith('& "', [StringComparison]::Ordinal)) "the Windows hook command should invoke its quoted executable with PowerShell's call operator"
    $hookSyntaxCheck = [ScriptBlock]::Create($installedHookCommand)
    Assert-True ($null -ne $hookSyntaxCheck) "the registered Windows hook command should parse in PowerShell"

    $hookHashBeforeRepeat = (Get-FileHash -LiteralPath $codexHooksFile -Algorithm SHA256).Hash
    $connectAgain = Invoke-LuthnProcess $installedCli @("connect", "codex")
    Assert-True ($connectAgain.ExitCode -eq 0) "Codex MCP connection should be idempotent: $($connectAgain.Output)"
    Assert-True ((Get-FileHash -LiteralPath $codexHooksFile -Algorithm SHA256).Hash -eq $hookHashBeforeRepeat) "repeated connect should not rewrite the trusted hook"

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
    $oversizedHook = "{`"hook_event_name`":`"Stop`",`"session_id`":`"s`",`"turn_id`":`"t`",`"last_assistant_message`":`"$(`"x`" * 270000)`"}"
    $oversizedResult = Invoke-CodexHookProcess $installedCli $oversizedHook
    Assert-True ($oversizedResult.ExitCode -eq 0) "oversized hook input should fail open"
    Assert-True ((Get-FileHash -LiteralPath $codexHookCapture -Algorithm SHA256).Hash -eq $captureHash) "oversized hook input should not be captured"

    Remove-Item Env:LUTHN_CODEX_HOOK_SYNCHRONOUS
    [IO.File]::Delete($codexHookCapture)
    $asyncHook = Invoke-CodexHookProcess $installedCli $validHookEvent
    for ($attempt = 0; $attempt -lt 50 -and -not [IO.File]::Exists($codexHookCapture); $attempt++) { Start-Sleep -Milliseconds 100 }
    Assert-True ($asyncHook.ExitCode -eq 0 -and [IO.File]::Exists($codexHookCapture)) "the detached Windows hook uploader should complete after the hook returns"
    Remove-Item Env:LUTHN_CODEX_HOOK_CAPTURE_FILE

    [IO.File]::Delete($codexOwnershipState)
    $recoverOwnership = Invoke-LuthnProcess $installedCli @("connect", "codex")
    Assert-True ($recoverOwnership.ExitCode -eq 0) "matching Codex MCP registration should recover ownership state: $($recoverOwnership.Output)"
    Assert-True ([IO.File]::Exists($codexOwnershipState)) "matching registration should restore ownership state for uninstall"
    Assert-True (([IO.File]::ReadAllText($codexOwnershipState) | ConvertFrom-Json).autoRecall) "ownership recovery should retain the managed auto-recall state"

    Remove-Item Env:LUTHN_CODEX_COMMAND
    $env:CODEX_CLI_PATH = Join-Path $testRoot "missing-codex.exe"
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
    Assert-True ($uninstall.ExitCode -eq 0) "default uninstall should discover Codex Desktop and succeed: $($uninstall.Output)"
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
    Remove-Item Env:LUTHN_CODEX_HOOKS_FILE -ErrorAction SilentlyContinue
    Remove-Item Env:LUTHN_CODEX_INSTRUCTIONS_FILE -ErrorAction SilentlyContinue
    Remove-Item Env:LUTHN_CODEX_SKIP_OBSERVATION -ErrorAction SilentlyContinue
    Remove-Item Env:LUTHN_DOCKER_DESKTOP_COMMAND -ErrorAction SilentlyContinue
    Remove-Item Env:LUTHN_INSTALLER_DOCKER_COMMAND -ErrorAction SilentlyContinue
    if ($originalPath) { $env:Path = $originalPath }
    if ($env:LUTHN_KEEP_TEST_ROOT -cne "true" -and [IO.Directory]::Exists($testRoot)) { [IO.Directory]::Delete($testRoot, $true) }
}
