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
    param([string]$InstallerPath, [string[]]$Arguments = @())
    try {
        $output = & $InstallerPath @Arguments *>&1 | Out-String
        return [pscustomobject]@{ ExitCode = 0; Output = $output }
    } catch {
        return [pscustomobject]@{ ExitCode = 1; Output = ($_ | Out-String) }
    }
}

$testRoot = Join-Path ([IO.Path]::GetTempPath()) "Luthn Windows 한글 $([Guid]::NewGuid().ToString('N'))"
$windowsRoot = Join-Path $testRoot "installed root"
$fakeDocker = Join-Path $testRoot $(if ($IsWindows) { "fake-docker.ps1" } else { "fake-docker" })
$fakeCodex = Join-Path $testRoot $(if ($IsWindows) { "fake-codex.ps1" } else { "fake-codex" })
$fakeHealth = Join-Path $testRoot $(if ($IsWindows) { "fake-health.ps1" } else { "fake-health" })
$fakeDockerLog = Join-Path $testRoot "docker.log"
$fakeCodexLog = Join-Path $testRoot "codex.log"
$fakeCodexState = Join-Path $testRoot "codex-state.json"
$fakeCodexTemplate = Join-Path $testRoot "codex-template.json"
$invalidCli = Join-Path $testRoot "invalid.ps1"
$installedCli = Join-Path $windowsRoot "bin/luthn.ps1"

try {
    [void][IO.Directory]::CreateDirectory($testRoot)
    if ($env:LUTHN_TEST_TRACE -ceq "true") { Write-Host "test root: $testRoot" }
    if ($IsWindows) {
        [IO.File]::WriteAllText($fakeDocker, @'
$ErrorActionPreference = "Stop"
[IO.File]::AppendAllText($env:FAKE_DOCKER_LOG, (($args -join " ") + "`n"))
$joined = $args -join " "
if ($args.Count -ge 2 -and $args[0] -ceq "compose" -and $args[1] -ceq "version") { if ($env:FAKE_DOCKER_COMPOSE_FAIL -ceq "true") { exit 12 }; "Docker Compose version v2.fake"; exit 0 }
if ($args.Count -ge 1 -and $args[0] -ceq "info") { if ($env:FAKE_DOCKER_INFO_FAIL -ceq "true") { exit 13 }; if ($env:FAKE_INSTALLER_DOCKER_OS) { $env:FAKE_INSTALLER_DOCKER_OS } else { "linux" }; exit 0 }
if ($args.Count -ge 1 -and $args[0] -ceq "pull") { "pulled"; exit 0 }
if ($args.Count -ge 2 -and $args[0] -ceq "image" -and $args[1] -ceq "inspect") {
    if ($joined -match "org.opencontainers.image.revision") { "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" }
    elseif ($joined -match "RepoDigests") { "ghcr.io/jakobsung/luthn@sha256:fake" }
    else { "sha256:fake" }
    exit 0
}
if ($args.Count -ge 1 -and $args[0] -ceq "inspect") { "sha256:fake"; exit 0 }
if ($args.Count -ge 1 -and $args[0] -ceq "run") { "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"; exit 0 }
if ($args.Count -ge 1 -and $args[0] -ceq "compose") {
    if ($args -ccontains "--list-tools") { if ($env:FAKE_MCP_PROBE_FAIL -ceq "true") { exit 14 }; "get_context_pack"; "search_safe_context"; exit 0 }
    if ($args -ccontains "pg_isready") { exit 0 }
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
if [ "$1" = "info" ]; then [ "${FAKE_DOCKER_INFO_FAIL:-false}" = "true" ] && exit 13; echo "${FAKE_INSTALLER_DOCKER_OS:-linux}"; exit 0; fi
if [ "$1" = "pull" ]; then echo "pulled"; exit 0; fi
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
if [ "$1" = "compose" ]; then
  case "$joined" in
    *--list-tools*) [ "${FAKE_MCP_PROBE_FAIL:-false}" = "true" ] && exit 14; printf 'get_context_pack\nsearch_safe_context\n'; exit 0 ;;
    *pg_isready*) exit 0 ;;
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
    } else {
        [IO.File]::WriteAllText($fakeHealth, @'
#!/bin/sh
case "$1" in
  */healthz|*/readyz) exit 0 ;;
  *) exit 1 ;;
esac
'@, [Text.UTF8Encoding]::new($false))
    }
    if (-not $IsWindows) {
        & chmod +x $fakeDocker $fakeCodex $fakeHealth
        if ($LASTEXITCODE -ne 0) { throw "failed to make fake tools executable" }
    }

    $env:LOCALAPPDATA = Join-Path $testRoot "local app data"
    $env:LUTHN_WINDOWS_ROOT = $windowsRoot
    $env:LUTHN_TEST_NO_EXIT = "true"
    $env:LUTHN_DOCKER_COMMAND = $fakeDocker
    $env:LUTHN_INSTALLER_DOCKER_COMMAND = $fakeDocker
    $env:LUTHN_CODEX_COMMAND = $fakeCodex
    $env:LUTHN_HTTP_CHECK_COMMAND = $fakeHealth
    $env:LUTHN_COMPOSE_SOURCE_FILE = Join-Path $RepoRoot "deploy/compose.yaml"
    $env:LUTHN_WINDOWS_CLI_SOURCE_FILE = Join-Path $RepoRoot "scripts/luthn.ps1"
    $env:LUTHN_PORT = "18080"
    $env:FAKE_DOCKER_LOG = $fakeDockerLog
    $env:FAKE_CODEX_LOG = $fakeCodexLog
    $env:FAKE_CODEX_STATE = $fakeCodexState
    $env:FAKE_CODEX_TEMPLATE = $fakeCodexTemplate

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
    $install = Invoke-InstallerProcess $installerPath
    Assert-True ($install.ExitCode -eq 0) "bootstrap install should succeed: $($install.Output)"
    Assert-True ([IO.File]::Exists($installedCli)) "bootstrap should install luthn.ps1"
    Assert-True ([IO.File]::Exists((Join-Path $windowsRoot "bin/luthn.cmd"))) "bootstrap should install luthn.cmd"
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

    $firstHash = (Get-FileHash -LiteralPath $installedCli -Algorithm SHA256).Hash
    $secondInstall = Invoke-InstallerProcess $installerPath
    Assert-True ($secondInstall.ExitCode -eq 0) "repeated install should be idempotent: $($secondInstall.Output)"
    Assert-True ((Get-FileHash -LiteralPath $installedCli -Algorithm SHA256).Hash -eq $firstHash) "repeated install should preserve the validated CLI"

    [IO.File]::WriteAllText($invalidCli, "this is not PowerShell {", [Text.UTF8Encoding]::new($false))
    $env:LUTHN_WINDOWS_CLI_SOURCE_FILE = $invalidCli
    $invalidInstall = Invoke-InstallerProcess $installerPath
    Assert-True ($invalidInstall.ExitCode -ne 0) "invalid downloaded CLI should fail"
    Assert-True ((Get-FileHash -LiteralPath $installedCli -Algorithm SHA256).Hash -eq $firstHash) "invalid download should preserve the installed CLI"
    $env:LUTHN_WINDOWS_CLI_SOURCE_FILE = Join-Path $RepoRoot "scripts/luthn.ps1"

    $env:FAKE_DOCKER_COMPOSE_FAIL = "true"
    $composeUnavailable = Invoke-InstallerProcess $installerPath
    Assert-True ($composeUnavailable.ExitCode -ne 0) "missing Docker Compose should fail preflight"
    Assert-True ((Get-FileHash -LiteralPath $installedCli -Algorithm SHA256).Hash -eq $firstHash) "Compose preflight failure should preserve the installed CLI"
    $env:FAKE_DOCKER_COMPOSE_FAIL = "false"

    $env:FAKE_DOCKER_INFO_FAIL = "true"
    $daemonUnavailable = Invoke-InstallerProcess $installerPath
    Assert-True ($daemonUnavailable.ExitCode -ne 0) "unreachable Docker daemon should fail preflight"
    Assert-True ((Get-FileHash -LiteralPath $installedCli -Algorithm SHA256).Hash -eq $firstHash) "daemon preflight failure should preserve the installed CLI"
    $env:FAKE_DOCKER_INFO_FAIL = "false"

    $env:FAKE_INSTALLER_DOCKER_OS = "windows"
    $wrongMode = Invoke-InstallerProcess $installerPath
    Assert-True ($wrongMode.ExitCode -ne 0) "Windows-container mode should fail preflight"
    Assert-True ((Get-FileHash -LiteralPath $installedCli -Algorithm SHA256).Hash -eq $firstHash) "preflight failure should preserve the installed CLI"
    $env:FAKE_INSTALLER_DOCKER_OS = "linux"

    $status = Invoke-LuthnProcess $installedCli @("status")
    Assert-True ($status.ExitCode -eq 0) "status should succeed: $($status.Output)"
    Assert-True ($status.Output -match "Health: ready") "status should report health"
    Assert-True ($status.Output -match "Readiness: ready") "status should report readiness"
    Assert-True ($status.Output -match "Image ID: sha256:fake") "status should report image identity"

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

    $env:FAKE_MCP_PROBE_FAIL = "true"
    $failedProbe = Invoke-LuthnProcess $installedCli @("connect", "codex")
    Assert-True ($failedProbe.ExitCode -ne 0) "MCP probe failure should fail setup"
    Assert-True (-not [IO.File]::Exists($fakeCodexState)) "MCP probe failure should roll back the Codex registration"
    $env:FAKE_MCP_PROBE_FAIL = "false"

    $connect = Invoke-LuthnProcess $installedCli @("connect", "codex")
    Assert-True ($connect.ExitCode -eq 0) "Codex MCP connection should succeed: $($connect.Output)"
    Assert-True ([IO.File]::Exists($fakeCodexState)) "Codex MCP registration should exist"
    $registration = [IO.File]::ReadAllText($fakeCodexState) | ConvertFrom-Json
    Assert-True ($registration.transport.type -ceq "stdio") "Codex registration should be stdio"
    Assert-True (@($registration.transport.args) -ccontains "mcp") "Codex registration should invoke the mcp service"
    Assert-True (-not (([IO.File]::ReadAllText($fakeCodexState)).Contains($token))) "Codex registration should not contain the token"

    $connectAgain = Invoke-LuthnProcess $installedCli @("connect", "codex")
    Assert-True ($connectAgain.ExitCode -eq 0) "Codex MCP connection should be idempotent: $($connectAgain.Output)"

    $env:FAKE_CODEX_REMOVE_FAIL = "true"
    $blockedUninstall = Invoke-LuthnProcess $installedCli @("uninstall")
    Assert-True ($blockedUninstall.ExitCode -ne 0) "uninstall should stop when Codex cleanup fails"
    Assert-True ([IO.Directory]::Exists((Join-Path $windowsRoot "data"))) "blocked uninstall should preserve runtime"
    $env:FAKE_CODEX_REMOVE_FAIL = "false"

    $uninstall = Invoke-LuthnProcess $installedCli @("uninstall")
    Assert-True ($uninstall.ExitCode -eq 0) "default uninstall should succeed: $($uninstall.Output)"
    Assert-True (-not [IO.Directory]::Exists((Join-Path $windowsRoot "data"))) "default uninstall should remove runtime data"
    Assert-True ([IO.File]::Exists($configFile)) "default uninstall should preserve config"
    Assert-True ([IO.File]::Exists($tokenFile)) "default uninstall should preserve token"
    Assert-True (-not [IO.File]::Exists($fakeCodexState)) "default uninstall should remove owned Codex registration"

    Write-Host "Windows lifecycle tests passed."
} finally {
    Remove-Item Env:FAKE_CODEX_REMOVE_FAIL -ErrorAction SilentlyContinue
    Remove-Item Env:FAKE_DOCKER_COMPOSE_FAIL -ErrorAction SilentlyContinue
    Remove-Item Env:FAKE_DOCKER_INFO_FAIL -ErrorAction SilentlyContinue
    Remove-Item Env:FAKE_MCP_PROBE_FAIL -ErrorAction SilentlyContinue
    Remove-Item Env:LUTHN_INSTALLER_DOCKER_COMMAND -ErrorAction SilentlyContinue
    if ($env:LUTHN_KEEP_TEST_ROOT -cne "true" -and [IO.Directory]::Exists($testRoot)) { [IO.Directory]::Delete($testRoot, $true) }
}
