#requires -Version 7.4

[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [string]$Command = "help",

    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$CommandArguments = @()
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$script:LuthnWindowsCliVersion = "1"
$script:ProjectName = if ($env:LUTHN_PROJECT_NAME) { $env:LUTHN_PROJECT_NAME } else { "luthn" }
$script:RootDir = if ($env:LUTHN_WINDOWS_ROOT) {
    $env:LUTHN_WINDOWS_ROOT
} elseif ($env:LOCALAPPDATA) {
    Join-Path $env:LOCALAPPDATA "Luthn"
} else {
    throw "LOCALAPPDATA is required."
}
$script:DataDir = if ($env:LUTHN_DATA_DIR) { $env:LUTHN_DATA_DIR } else { Join-Path $script:RootDir "data" }
$script:ConfigDir = if ($env:LUTHN_CONFIG_DIR) { $env:LUTHN_CONFIG_DIR } else { Join-Path $script:RootDir "config" }
$script:StateDir = if ($env:LUTHN_STATE_DIR) { $env:LUTHN_STATE_DIR } else { Join-Path $script:RootDir "state" }
$script:BinDir = if ($env:LUTHN_BIN_DIR) { $env:LUTHN_BIN_DIR } else { Join-Path $script:RootDir "bin" }
$script:ComposeFile = if ($env:LUTHN_COMPOSE_FILE) { $env:LUTHN_COMPOSE_FILE } else { Join-Path $script:DataDir "compose.yaml" }
$script:ConfigFile = if ($env:LUTHN_CONFIG_FILE) { $env:LUTHN_CONFIG_FILE } else { Join-Path $script:ConfigDir "luthn.env" }
$script:TokenFile = if ($env:LUTHN_SERVICE_TOKEN_FILE) { $env:LUTHN_SERVICE_TOKEN_FILE } else { Join-Path $script:ConfigDir "service-token" }
$script:ConnectorStateDir = if ($env:LUTHN_CONNECTOR_STATE_DIR) { $env:LUTHN_CONNECTOR_STATE_DIR } else { Join-Path $script:StateDir "connectors" }
$script:CodexStateFile = if ($env:LUTHN_CODEX_STATE_FILE) { $env:LUTHN_CODEX_STATE_FILE } else { Join-Path $script:ConnectorStateDir "codex-windows.json" }
$script:CodexPendingStateFile = if ($env:LUTHN_CODEX_PENDING_STATE_FILE) { $env:LUTHN_CODEX_PENDING_STATE_FILE } else { Join-Path $script:ConnectorStateDir "codex-windows.pending.json" }
$script:CodexHome = if ($env:CODEX_HOME) { $env:CODEX_HOME } elseif ($env:USERPROFILE) { Join-Path $env:USERPROFILE ".codex" } else { throw "USERPROFILE or CODEX_HOME is required." }
$script:CodexHooksFile = if ($env:LUTHN_CODEX_HOOKS_FILE) { $env:LUTHN_CODEX_HOOKS_FILE } else { Join-Path $script:CodexHome "hooks.json" }
$script:CodexInstructionsFile = if ($env:LUTHN_CODEX_INSTRUCTIONS_FILE) { $env:LUTHN_CODEX_INSTRUCTIONS_FILE } else { Join-Path $script:CodexHome "AGENTS.md" }
$script:UpdateStateFile = if ($env:LUTHN_UPDATE_STATE_FILE) { $env:LUTHN_UPDATE_STATE_FILE } else { Join-Path $script:StateDir "update-windows.json" }
$script:DistributionRef = if ($env:LUTHN_DISTRIBUTION_REF) { $env:LUTHN_DISTRIBUTION_REF } else { "main" }
$script:SourceBaseUrl = if ($env:LUTHN_SOURCE_BASE_URL) { $env:LUTHN_SOURCE_BASE_URL.TrimEnd("/") } else { "https://raw.githubusercontent.com/JakobSung/Luthn/$($script:DistributionRef)" }
$script:DefaultImage = "ghcr.io/jakobsung/luthn:main"
$script:Utf8NoBom = [System.Text.UTF8Encoding]::new($false)
$script:StrictUtf8 = [System.Text.UTF8Encoding]::new($false, $true)
$script:CodexHookMarker = "luthn.agent-connector.v1"
$script:AutoRecallStartMarker = "<!-- luthn:auto-recall:start -->"
$script:AutoRecallEndMarker = "<!-- luthn:auto-recall:end -->"
$script:MaxCodexHookInputBytes = 256 * 1024
$script:MaxCodexInstructionsBytes = 1024 * 1024
$script:MaxTurnCapsuleCharacters = 3900
$script:CodexHttpTimeoutSeconds = 4
$script:AutoRecallInstruction = @"
<!-- luthn:auto-recall:start -->
# Luthn lightweight recall

For a new task or a material topic change, call the Luthn MCP
``get_context_pack`` tool once before substantial work. Use a short task query
and non-sensitive project/task cache key with these bounds:

- ``maxItems``: 3
- ``maxTokens``: 600
- ``timeoutMs``: 200
- ``cacheKey``: a stable non-sensitive project/task key
- ``cacheTtlSeconds``: 600
- ``failOpen``: true

For continued work on the same task, reuse the context already returned in the
conversation instead of calling the tool again. Refresh only after a material
topic change or cache expiry. If lightweight recall returns no context, times
out, or fails, continue without memory. Use deeper Luthn MCP search tools only
when the bounded context pack is insufficient.
<!-- luthn:auto-recall:end -->
"@

function Show-Usage {
    @"
usage: luthn <command> [options]

commands:
  install [--connect-codex]  Install Luthn and optionally connect Codex.
  status                     Show services, readiness, console, and image.
  update [image]             Back up, pull, migrate, restart, and verify.
  connect codex [--no-auto-recall]
                             Configure Codex; recall is enabled by default.
  connection status codex    Show local and server Codex connection state.
  disconnect codex           Remove only Luthn-owned Codex configuration.
  mcp [--list-tools]         Run the Docker-backed MCP stdio server.
  uninstall                  Remove services and runtime; preserve data/config.
  help                       Show this help.

Windows reset and purge uninstall are not available in this release.
"@
}

function Get-PwshPath {
    $pwsh = Get-Command pwsh -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $pwsh) { throw "PowerShell 7.4 or later is required. Install it and retry from pwsh." }
    return $pwsh.Source
}

function New-ToolSpecFromPath {
    param([Parameter(Mandatory = $true)][string]$Path)

    $resolved = Resolve-Path -LiteralPath $Path -ErrorAction SilentlyContinue
    if (-not $resolved) { throw "required command path does not exist: $Path" }
    $resolvedPath = $resolved.Path
    if ([IO.Path]::GetExtension($resolvedPath) -ieq ".ps1") {
        return [pscustomobject]@{ FilePath = Get-PwshPath; PrefixArguments = @("-NoProfile", "-File", $resolvedPath) }
    }
    if ([IO.Path]::GetExtension($resolvedPath) -in @(".cmd", ".bat")) {
        $commandProcessor = if ($env:ComSpec) { $env:ComSpec } else { Join-Path $env:SystemRoot "System32\cmd.exe" }
        return [pscustomobject]@{ FilePath = $commandProcessor; PrefixArguments = @("/d", "/s", "/c", "call", $resolvedPath) }
    }
    return [pscustomobject]@{ FilePath = $resolvedPath; PrefixArguments = @() }
}

function Get-ToolSpec {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$OverrideVariable
    )

    $override = [Environment]::GetEnvironmentVariable($OverrideVariable)
    if ($override) {
        return New-ToolSpecFromPath -Path $override
    }

    $commandInfo = Get-Command $Name -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $commandInfo) {
        throw "missing required command: $Name"
    }
    return [pscustomobject]@{ FilePath = $commandInfo.Source; PrefixArguments = @() }
}

function Get-DockerTool { Get-ToolSpec -Name "docker" -OverrideVariable "LUTHN_DOCKER_COMMAND" }

function Invoke-ToolCapture {
    param(
        [Parameter(Mandatory = $true)]$Tool,
        [string[]]$Arguments = @(),
        [AllowNull()][string]$StandardInput = $null
    )

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $Tool.FilePath
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.RedirectStandardInput = $null -ne $StandardInput
    foreach ($argument in @($Tool.PrefixArguments) + $Arguments) {
        [void]$startInfo.ArgumentList.Add([string]$argument)
    }

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    if (-not $process.Start()) {
        throw "failed to start command: $($Tool.FilePath)"
    }
    if ($null -ne $StandardInput) {
        $process.StandardInput.Write($StandardInput)
        $process.StandardInput.Close()
    }
    $stdout = $process.StandardOutput.ReadToEnd()
    $stderr = $process.StandardError.ReadToEnd()
    $process.WaitForExit()
    return [pscustomobject]@{
        ExitCode = $process.ExitCode
        StdOut = $stdout
        StdErr = $stderr
    }
}

function Get-CodexTool {
    if ($env:LUTHN_CODEX_COMMAND) {
        return Get-ToolSpec -Name "codex" -OverrideVariable "LUTHN_CODEX_COMMAND"
    }

    $candidatePaths = [Collections.Generic.List[string]]::new()
    if ($env:CODEX_CLI_PATH) { $candidatePaths.Add($env:CODEX_CLI_PATH) }
    if ($env:LOCALAPPDATA) {
        $desktopBinRoot = Join-Path $env:LOCALAPPDATA "OpenAI\Codex\bin"
        if ([IO.Directory]::Exists($desktopBinRoot)) {
            Get-ChildItem -Path (Join-Path $desktopBinRoot "*\codex.*") -File -ErrorAction SilentlyContinue |
                Where-Object { $_.Extension -in @(".exe", ".cmd", ".bat", ".ps1") } |
                Sort-Object LastWriteTime -Descending |
                ForEach-Object { $candidatePaths.Add($_.FullName) }
        }
    }
    Get-Command codex -CommandType Application -All -ErrorAction SilentlyContinue |
        ForEach-Object { $candidatePaths.Add($_.Source) }

    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($candidatePath in $candidatePaths) {
        if (-not $candidatePath -or -not $seen.Add($candidatePath)) { continue }
        try {
            $candidate = New-ToolSpecFromPath -Path $candidatePath
            $version = Invoke-ToolCapture -Tool $candidate -Arguments @("--version")
            if ($version.ExitCode -eq 0 -and ($version.StdOut + $version.StdErr).Trim() -match '^codex-cli\s+') {
                return $candidate
            }
        } catch {
            continue
        }
    }

    throw "No runnable Codex CLI was found. Install the Codex CLI, restart the terminal, or set LUTHN_CODEX_COMMAND to a runnable codex executable."
}

function Assert-ToolSuccess {
    param(
        [Parameter(Mandatory = $true)]$Result,
        [Parameter(Mandatory = $true)][string]$Description
    )
    if ($Result.ExitCode -ne 0) {
        $detail = $Result.StdErr.Trim()
        if (-not $detail) { $detail = $Result.StdOut.Trim() }
        throw "$Description failed with exit code $($Result.ExitCode): $detail"
    }
}

function Invoke-ToolVisible {
    param(
        [Parameter(Mandatory = $true)]$Tool,
        [string[]]$Arguments = @()
    )
    $allArguments = @($Tool.PrefixArguments) + $Arguments
    & $Tool.FilePath @allArguments
    if ($LASTEXITCODE -ne 0) {
        throw "command failed with exit code $LASTEXITCODE`: $($Tool.FilePath) $($Arguments -join ' ')"
    }
}

function Get-ComposeArguments {
    param([string[]]$Arguments = @())
    return @(
        "compose",
        "--project-name", $script:ProjectName,
        "--env-file", $script:ConfigFile,
        "-f", $script:ComposeFile
    ) + $Arguments
}

function Invoke-ComposeCapture {
    param([string[]]$Arguments = @())
    Invoke-ToolCapture -Tool (Get-DockerTool) -Arguments (Get-ComposeArguments $Arguments)
}

function Invoke-ComposeVisible {
    param([string[]]$Arguments = @())
    Invoke-ToolVisible -Tool (Get-DockerTool) -Arguments (Get-ComposeArguments $Arguments)
}

function Test-DockerPreflight {
    $docker = Get-DockerTool
    $composeVersion = Invoke-ToolCapture -Tool $docker -Arguments @("compose", "version")
    Assert-ToolSuccess $composeVersion "Docker Compose check"

    $dockerInfo = Invoke-ToolCapture -Tool $docker -Arguments @("info", "--format", "{{.OSType}}")
    if ($dockerInfo.ExitCode -ne 0) {
        $detail = $dockerInfo.StdErr.Trim()
        if (-not $detail) { $detail = $dockerInfo.StdOut.Trim() }
        throw "Docker Desktop is not reachable. Start Docker Desktop, wait for the Linux engine, and retry. $detail"
    }
    if ($dockerInfo.StdOut.Trim() -cne "linux") {
        throw "Docker Desktop is running Windows containers. Switch to Linux containers from the Docker Desktop menu and retry."
    }
}

function Ensure-Directories {
    foreach ($path in @($script:DataDir, $script:ConfigDir, $script:StateDir, (Join-Path $script:StateDir "backups"), $script:ConnectorStateDir, $script:BinDir)) {
        [void][IO.Directory]::CreateDirectory($path)
    }
}

function Write-Utf8File {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [AllowEmptyString()][Parameter(Mandatory = $true)][string]$Content
    )
    [void][IO.Directory]::CreateDirectory((Split-Path -Parent $Path))
    $temporaryPath = "$Path.$([Guid]::NewGuid().ToString('N')).tmp"
    [IO.File]::WriteAllText($temporaryPath, $Content, $script:Utf8NoBom)
    try {
        if ([IO.File]::Exists($Path)) {
            $backupPath = "$Path.$([Guid]::NewGuid().ToString('N')).bak"
            [IO.File]::Replace($temporaryPath, $Path, $backupPath, $true)
            [IO.File]::Delete($backupPath)
        } else {
            [IO.File]::Move($temporaryPath, $Path)
        }
    } finally {
        if ([IO.File]::Exists($temporaryPath)) { [IO.File]::Delete($temporaryPath) }
    }
}

function Protect-SecretFile {
    param([Parameter(Mandatory = $true)][string]$Path)
    if (-not $IsWindows) { return }

    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $userSid = $identity.User
    $fileInfo = [IO.FileInfo]::new($Path)
    $acl = [IO.FileSystemAclExtensions]::GetAccessControl(
        $fileInfo,
        [Security.AccessControl.AccessControlSections]::Access)
    $acl.SetAccessRuleProtection($true, $false)
    $existingRules = $acl.GetAccessRules(
        $true,
        $true,
        [Security.Principal.SecurityIdentifier])
    foreach ($existingRule in $existingRules) {
        [void]$acl.RemoveAccessRuleSpecific($existingRule)
    }
    $rule = [Security.AccessControl.FileSystemAccessRule]::new(
        $userSid,
        [Security.AccessControl.FileSystemRights]::FullControl,
        [Security.AccessControl.AccessControlType]::Allow)
    [void]$acl.AddAccessRule($rule)
    [IO.FileSystemAclExtensions]::SetAccessControl($fileInfo, $acl)
}

function Read-ConfigValue {
    param(
        [Parameter(Mandatory = $true)][string]$Key,
        [string]$Fallback = ""
    )
    if (-not [IO.File]::Exists($script:ConfigFile)) { return $Fallback }
    foreach ($line in [IO.File]::ReadAllLines($script:ConfigFile)) {
        $separator = $line.IndexOf("=")
        if ($separator -ge 0 -and $line.Substring(0, $separator) -ceq $Key) {
            $value = $line.Substring($separator + 1)
            if ($value) { return $value }
            return $Fallback
        }
    }
    return $Fallback
}

function Set-ConfigValue {
    param(
        [Parameter(Mandatory = $true)][string]$Key,
        [AllowEmptyString()][Parameter(Mandatory = $true)][string]$Value
    )
    $lines = [Collections.Generic.List[string]]::new()
    $updated = $false
    if ([IO.File]::Exists($script:ConfigFile)) {
        foreach ($line in [IO.File]::ReadAllLines($script:ConfigFile)) {
            $separator = $line.IndexOf("=")
            if ($separator -ge 0 -and $line.Substring(0, $separator) -ceq $Key) {
                $lines.Add("$Key=$Value")
                $updated = $true
            } else {
                $lines.Add($line)
            }
        }
    }
    if (-not $updated) { $lines.Add("$Key=$Value") }
    Write-Utf8File -Path $script:ConfigFile -Content (($lines -join "`n") + "`n")
    Protect-SecretFile $script:ConfigFile
}

function Ensure-ConfigValue {
    param([string]$Key, [string]$Value)
    if (-not (Read-ConfigValue -Key $Key)) { Set-ConfigValue -Key $Key -Value $Value }
}

function New-ServiceToken {
    $bytes = [Security.Cryptography.RandomNumberGenerator]::GetBytes(24)
    return [Convert]::ToHexString($bytes).ToLowerInvariant()
}

function Get-RuntimeSource {
    param([string]$Image, [string]$Revision)
    if ($Image -like "ghcr.io/jakobsung/luthn:*" -and $Revision -cmatch "^[0-9a-f]{40}$") {
        return "https://raw.githubusercontent.com/JakobSung/Luthn/$Revision"
    }
    if ($Image -cmatch "^ghcr\.io/jakobsung/luthn:sha-([0-9a-f]{40})$") {
        return "https://raw.githubusercontent.com/JakobSung/Luthn/$($Matches[1])"
    }
    return $script:SourceBaseUrl
}

function Test-WindowsCliFile {
    param([Parameter(Mandatory = $true)][string]$Path)

    $tokens = $null
    $parseErrors = $null
    [void][Management.Automation.Language.Parser]::ParseFile($Path, [ref]$tokens, [ref]$parseErrors)
    if ($parseErrors.Count -gt 0) { throw "downloaded Windows CLI did not pass PowerShell syntax validation" }
    if ([IO.File]::ReadAllText($Path) -notmatch '\$script:LuthnWindowsCliVersion\s*=\s*"1"') {
        throw "downloaded Windows CLI did not match the Luthn distribution contract"
    }
}

function Invoke-ToolToFile {
    param(
        [Parameter(Mandatory = $true)]$Tool,
        [string[]]$Arguments = @(),
        [Parameter(Mandatory = $true)][string]$OutputPath
    )

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $Tool.FilePath
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    foreach ($argument in @($Tool.PrefixArguments) + $Arguments) {
        [void]$startInfo.ArgumentList.Add([string]$argument)
    }

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    if (-not $process.Start()) { throw "failed to start command: $($Tool.FilePath)" }
    $stderrTask = $process.StandardError.ReadToEndAsync()
    try {
        $output = [IO.File]::Open($OutputPath, [IO.FileMode]::Create, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try {
            $process.StandardOutput.BaseStream.CopyTo($output)
        } finally {
            $output.Dispose()
        }
        $process.WaitForExit()
        return [pscustomobject]@{
            ExitCode = $process.ExitCode
            StdErr = $stderrTask.GetAwaiter().GetResult()
        }
    } finally {
        if (-not $process.HasExited) { $process.Kill($true) }
        $process.Dispose()
    }
}

function Install-ComposeRuntime {
    param(
        [Parameter(Mandatory = $true)][string]$RuntimeSource,
        [switch]$IncludeCli
    )
    Ensure-Directories
    $temporaryCompose = Join-Path $script:DataDir "compose.$([Guid]::NewGuid().ToString('N')).tmp.yaml"
    $temporaryEnvironment = Join-Path $script:DataDir "validate.$([Guid]::NewGuid().ToString('N')).env"
    $temporaryToken = Join-Path $script:DataDir "validate.$([Guid]::NewGuid().ToString('N')).token"
    $temporaryCli = Join-Path $script:BinDir "luthn.$([Guid]::NewGuid().ToString('N')).tmp.ps1"
    $installedCli = Join-Path $script:BinDir "luthn.ps1"
    $previousCompose = if ([IO.File]::Exists($script:ComposeFile)) { [IO.File]::ReadAllBytes($script:ComposeFile) } else { $null }
    $previousCli = if ($IncludeCli -and [IO.File]::Exists($installedCli)) { [IO.File]::ReadAllBytes($installedCli) } else { $null }
    $composeReplaced = $false
    $cliReplaced = $false
    try {
        if ($env:LUTHN_COMPOSE_SOURCE_FILE) {
            [IO.File]::Copy($env:LUTHN_COMPOSE_SOURCE_FILE, $temporaryCompose, $true)
        } else {
            Invoke-WebRequest -Uri "$RuntimeSource/deploy/compose.yaml" -OutFile $temporaryCompose
        }
        if ($IncludeCli) {
            if ($env:LUTHN_WINDOWS_CLI_SOURCE_FILE) {
                [IO.File]::Copy($env:LUTHN_WINDOWS_CLI_SOURCE_FILE, $temporaryCli, $true)
            } else {
                Invoke-WebRequest -Uri "$RuntimeSource/scripts/luthn.ps1" -OutFile $temporaryCli
            }
            Test-WindowsCliFile -Path $temporaryCli
        }
        $content = [IO.File]::ReadAllText($temporaryCompose)
        if ($content -notmatch "(?m)^name:\s*luthn\s*$" -or $content -notmatch "(?m)^\s{2}mcp:\s*$") {
            throw "downloaded Compose runtime did not match the Luthn distribution contract"
        }
        Write-Utf8File -Path $temporaryToken -Content "validation-token"
        Write-Utf8File -Path $temporaryEnvironment -Content @"
LUTHN_IMAGE=$script:DefaultImage
LUTHN_SERVICE_TOKEN_FILE=$temporaryToken
Luthn__Auth__Tokens__0__Sha256Digest=validation
"@
        $validationArguments = @(
            "compose", "--project-name", $script:ProjectName,
            "--env-file", $temporaryEnvironment,
            "-f", $temporaryCompose,
            "config", "--quiet"
        )
        $validation = Invoke-ToolCapture -Tool (Get-DockerTool) -Arguments $validationArguments
        Assert-ToolSuccess $validation "Compose runtime validation"

        $runtimeContent = [IO.File]::ReadAllText($temporaryCompose)
        Write-Utf8File -Path $script:ComposeFile -Content $runtimeContent
        $composeReplaced = $true
        if ($IncludeCli) {
            Write-Utf8File -Path $installedCli -Content ([IO.File]::ReadAllText($temporaryCli))
            $cliReplaced = $true
        }
    } catch {
        if ($cliReplaced) {
            if ($null -ne $previousCli) { [IO.File]::WriteAllBytes($installedCli, $previousCli) }
            elseif ([IO.File]::Exists($installedCli)) { [IO.File]::Delete($installedCli) }
        }
        if ($composeReplaced) {
            if ($null -ne $previousCompose) { [IO.File]::WriteAllBytes($script:ComposeFile, $previousCompose) }
            elseif ([IO.File]::Exists($script:ComposeFile)) { [IO.File]::Delete($script:ComposeFile) }
        }
        throw
    } finally {
        foreach ($path in @($temporaryCompose, $temporaryEnvironment, $temporaryToken, $temporaryCli)) {
            if ([IO.File]::Exists($path)) { [IO.File]::Delete($path) }
        }
    }
}

function Write-InitialConfig {
    param([string]$Image, [string]$Digest)
    $port = if ($env:LUTHN_PORT) { $env:LUTHN_PORT } else { "8080" }
    $postgresVolume = if ($env:LUTHN_POSTGRES_VOLUME) { $env:LUTHN_POSTGRES_VOLUME } else { "luthn-postgres" }
    $operatorVolume = if ($env:LUTHN_OPERATOR_VOLUME) { $env:LUTHN_OPERATOR_VOLUME } else { "luthn-operator" }
    $content = @(
        "LUTHN_IMAGE=$Image",
        "LUTHN_PORT=$port",
        "LUTHN_ENVIRONMENT=Development",
        "LUTHN_BASE_URL=http://127.0.0.1:$port",
        "LUTHN_POSTGRES_VOLUME=$postgresVolume",
        "LUTHN_OPERATOR_VOLUME=$operatorVolume",
        "LUTHN_SERVICE_TOKEN_FILE=$script:TokenFile",
        "LUTHN_DOCKER_CONNECTION_STRING=Host=postgres;Port=5432;Database=luthn;Username=luthn",
        "POSTGRES_DB=luthn",
        "POSTGRES_USER=luthn",
        "POSTGRES_HOST_AUTH_METHOD=trust",
        "Luthn__Classification__Provider=mock",
        "Luthn__Auth__RequireServiceToken=true",
        "Luthn__Auth__Tokens__0__Name=local-agent",
        "Luthn__Auth__Tokens__0__Sha256Digest=$Digest",
        "Luthn__Auth__Tokens__0__Scopes__0=agent.read",
        "Luthn__Auth__Tokens__0__Scopes__1=agent.write.summary",
        "Luthn__Auth__Tokens__0__Scopes__2=memory.write",
        "Luthn__Auth__Tokens__0__Scopes__3=memory.read",
        "Luthn__Auth__Tokens__0__Scopes__4=classification.preview",
        "Luthn__Auth__Tokens__0__Scopes__5=agent.connection.read",
        "Luthn__Auth__Tokens__0__Scopes__6=agent.connection.write"
    ) -join "`n"
    Write-Utf8File -Path $script:ConfigFile -Content ($content + "`n")
    Protect-SecretFile $script:ConfigFile
}

function Get-TokenDigest {
    param([string]$Image, [string]$Token)
    $result = Invoke-ToolCapture -Tool (Get-DockerTool) -Arguments @("run", "--rm", "-i", $Image, "token-digest", "--stdin") -StandardInput $Token
    Assert-ToolSuccess $result "service token digest"
    $digest = $result.StdOut.Trim()
    if ($digest -cnotmatch "^sha256:[0-9a-f]{64}$") { throw "service token digest output was invalid" }
    return $digest
}

function Wait-ForPostgres {
    for ($attempt = 0; $attempt -lt 60; $attempt++) {
        $result = Invoke-ComposeCapture @("exec", "-T", "postgres", "pg_isready", "-U", (Read-ConfigValue "POSTGRES_USER" "luthn"), "-d", (Read-ConfigValue "POSTGRES_DB" "luthn"))
        if ($result.ExitCode -eq 0) { return }
        Start-Sleep -Seconds 2
    }
    throw "PostgreSQL did not become ready."
}

function Test-HttpReady {
    param([string]$Url)
    if ($env:LUTHN_HTTP_CHECK_COMMAND) {
        $healthTool = Get-ToolSpec -Name "health-check" -OverrideVariable "LUTHN_HTTP_CHECK_COMMAND"
        $healthResult = Invoke-ToolCapture -Tool $healthTool -Arguments @($Url)
        return $healthResult.ExitCode -eq 0
    }
    try {
        $response = Invoke-WebRequest -Uri $Url -TimeoutSec 5
        return $response.StatusCode -ge 200 -and $response.StatusCode -lt 300
    } catch {
        return $false
    }
}

function Wait-ForApi {
    $baseUrl = Read-ConfigValue "LUTHN_BASE_URL" "http://127.0.0.1:8080"
    for ($attempt = 0; $attempt -lt 60; $attempt++) {
        if ((Test-HttpReady "$baseUrl/healthz") -and (Test-HttpReady "$baseUrl/readyz")) { return }
        Start-Sleep -Seconds 2
    }
    throw "Luthn did not pass health and readiness checks."
}

function Require-Installation {
    if (-not [IO.File]::Exists($script:ComposeFile) -or -not [IO.File]::Exists($script:ConfigFile)) {
        throw "Luthn is not installed. Run: luthn install"
    }
}

function Install-Luthn {
    param([string[]]$Arguments)
    $connectCodex = $false
    foreach ($argument in $Arguments) {
        if ($argument -ceq "--connect-codex") { $connectCodex = $true } else { throw "usage: luthn install [--connect-codex]" }
    }

    Test-DockerPreflight
    $docker = Get-DockerTool
    $image = Read-ConfigValue "LUTHN_IMAGE" $(if ($env:LUTHN_IMAGE) { $env:LUTHN_IMAGE } else { $script:DefaultImage })
    Write-Host "Pulling $image..."
    Invoke-ToolVisible -Tool $docker -Arguments @("pull", $image)

    $revisionResult = Invoke-ToolCapture -Tool $docker -Arguments @("image", "inspect", "--format", "{{ index .Config.Labels `"org.opencontainers.image.revision`" }}", $image)
    $revision = if ($revisionResult.ExitCode -eq 0) { $revisionResult.StdOut.Trim() } else { "" }
    Install-ComposeRuntime -RuntimeSource (Get-RuntimeSource -Image $image -Revision $revision)

    Ensure-Directories
    if (-not [IO.File]::Exists($script:ConfigFile)) {
        $token = New-ServiceToken
        $digest = Get-TokenDigest -Image $image -Token $token
        Write-Utf8File -Path $script:TokenFile -Content $token
        Protect-SecretFile $script:TokenFile
        Write-InitialConfig -Image $image -Digest $digest
    } else {
        Ensure-ConfigValue "LUTHN_IMAGE" $image
        Ensure-ConfigValue "LUTHN_PORT" $(if ($env:LUTHN_PORT) { $env:LUTHN_PORT } else { "8080" })
        Ensure-ConfigValue "LUTHN_ENVIRONMENT" "Development"
        Ensure-ConfigValue "LUTHN_BASE_URL" "http://127.0.0.1:$(Read-ConfigValue 'LUTHN_PORT' '8080')"
        Ensure-ConfigValue "LUTHN_POSTGRES_VOLUME" $(if ($env:LUTHN_POSTGRES_VOLUME) { $env:LUTHN_POSTGRES_VOLUME } else { "luthn-postgres" })
        Ensure-ConfigValue "LUTHN_OPERATOR_VOLUME" $(if ($env:LUTHN_OPERATOR_VOLUME) { $env:LUTHN_OPERATOR_VOLUME } else { "luthn-operator" })
        $configuredTokenFile = Read-ConfigValue "LUTHN_SERVICE_TOKEN_FILE" $script:TokenFile
        $script:TokenFile = $configuredTokenFile
        if ([IO.File]::Exists($script:TokenFile)) {
            $token = [IO.File]::ReadAllText($script:TokenFile).Trim()
        } else {
            $token = New-ServiceToken
            Write-Utf8File -Path $script:TokenFile -Content $token
        }
        Protect-SecretFile $script:TokenFile
        Set-ConfigValue "LUTHN_SERVICE_TOKEN_FILE" $script:TokenFile
        Set-ConfigValue "Luthn__Auth__Tokens__0__Sha256Digest" (Get-TokenDigest -Image $image -Token $token)
        Ensure-ConfigValue "LUTHN_DOCKER_CONNECTION_STRING" "Host=postgres;Port=5432;Database=luthn;Username=luthn"
        Ensure-ConfigValue "POSTGRES_DB" "luthn"
        Ensure-ConfigValue "POSTGRES_USER" "luthn"
        Ensure-ConfigValue "POSTGRES_HOST_AUTH_METHOD" "trust"
        Ensure-ConfigValue "Luthn__Classification__Provider" "mock"
        Ensure-ConfigValue "Luthn__Auth__RequireServiceToken" "true"
        Ensure-ConfigValue "Luthn__Auth__Tokens__0__Name" "local-agent"
        $scopes = @("agent.read", "agent.write.summary", "memory.write", "memory.read", "classification.preview", "agent.connection.read", "agent.connection.write")
        for ($index = 0; $index -lt $scopes.Count; $index++) {
            Ensure-ConfigValue "Luthn__Auth__Tokens__0__Scopes__$index" $scopes[$index]
        }
    }

    Write-Host "Starting Luthn..."
    Invoke-ComposeVisible @("pull", "postgres")
    Invoke-ComposeVisible @("up", "-d", "postgres")
    Wait-ForPostgres
    Invoke-ComposeVisible @("run", "--rm", "--no-deps", "migrate")
    Invoke-ComposeVisible @("up", "-d", "api")
    Wait-ForApi
    Invoke-ComposeVisible @("--profile", "tools", "run", "--rm", "--no-deps", "seed")

    $baseUrl = Read-ConfigValue "LUTHN_BASE_URL" "http://127.0.0.1:8080"
    Write-Host ""
    Write-Host "Luthn is ready."
    Write-Host "Console: $baseUrl/"
    Write-Host "Status:  luthn status"
    Write-Host "Config:  $script:ConfigFile"
    Write-Host "Agent:   luthn connect codex"

    if ($connectCodex) { Connect-Codex }
}

function Show-Status {
    Require-Installation
    Test-DockerPreflight
    $docker = Get-DockerTool
    $baseUrl = Read-ConfigValue "LUTHN_BASE_URL" "http://127.0.0.1:8080"
    $imageRef = Read-ConfigValue "LUTHN_IMAGE" $script:DefaultImage
    $containerResult = Invoke-ComposeCapture @("ps", "-q", "api")
    $containerId = if ($containerResult.ExitCode -eq 0) { $containerResult.StdOut.Trim() } else { "" }
    $imageId = ""
    $digest = ""
    if ($containerId) {
        $imageResult = Invoke-ToolCapture -Tool $docker -Arguments @("inspect", "--format", "{{.Image}}", $containerId)
        if ($imageResult.ExitCode -eq 0) { $imageId = $imageResult.StdOut.Trim() }
        if ($imageId) {
            $digestResult = Invoke-ToolCapture -Tool $docker -Arguments @("image", "inspect", "--format", "{{join .RepoDigests `", `"}}", $imageId)
            if ($digestResult.ExitCode -eq 0) { $digest = $digestResult.StdOut.Trim() }
        }
    }

    Write-Host "Luthn services:"
    Invoke-ComposeVisible @("ps")
    Write-Host ""
    Write-Host "Health: $(if (Test-HttpReady "$baseUrl/healthz") { 'ready' } else { 'unavailable' })"
    Write-Host "Readiness: $(if (Test-HttpReady "$baseUrl/readyz") { 'ready' } else { 'not ready' })"
    Write-Host "Console: $baseUrl/"
    Write-Host "Image: $imageRef"
    Write-Host "Image ID: $(if ($imageId) { $imageId } else { 'unavailable' })"
    Write-Host "Digest: $(if ($digest) { $digest } else { 'unavailable' })"
}

function Get-ApiImageId {
    $docker = Get-DockerTool
    $container = Invoke-ComposeCapture @("ps", "-q", "api")
    if ($container.ExitCode -ne 0 -or -not $container.StdOut.Trim()) { return "" }
    $image = Invoke-ToolCapture -Tool $docker -Arguments @("inspect", "--format", "{{.Image}}", $container.StdOut.Trim())
    if ($image.ExitCode -ne 0) { return "" }
    return $image.StdOut.Trim()
}

function Write-UpdateState {
    param(
        [Parameter(Mandatory = $true)][string]$Status,
        [Parameter(Mandatory = $true)][string]$TargetImage,
        [string]$TargetImageId = "",
        [string]$PreviousImageRef = "",
        [string]$PreviousImageId = "",
        [string]$BackupPath = ""
    )

    Ensure-Directories
    $state = [ordered]@{
        version = 1
        status = $Status
        targetImage = $TargetImage
        targetImageId = $TargetImageId
        previousImageRef = $PreviousImageRef
        previousImageId = $PreviousImageId
        backupPath = $BackupPath
        updatedAt = [DateTimeOffset]::UtcNow.ToString("O")
    } | ConvertTo-Json -Depth 4
    Write-Utf8File -Path $script:UpdateStateFile -Content ($state + "`n")
    Protect-SecretFile $script:UpdateStateFile
}

function Stop-WritePaths {
    $docker = Get-DockerTool
    $apiStop = Invoke-ComposeCapture @("stop", "api")
    if ($apiStop.ExitCode -ne 0) {
        $apiContainer = Invoke-ComposeCapture @("ps", "-q", "api")
        if ($apiContainer.ExitCode -ne 0) { throw "API stop failed and its container could not be inspected." }
        $apiContainerId = $apiContainer.StdOut.Trim()
        if ($apiContainerId) {
            $stop = Invoke-ToolCapture -Tool $docker -Arguments @("stop", $apiContainerId)
            if ($stop.ExitCode -ne 0) {
                $kill = Invoke-ToolCapture -Tool $docker -Arguments @("kill", $apiContainerId)
                Assert-ToolSuccess $kill "API fail-closed stop"
            }
        }
    }

    foreach ($service in @("mcp", "adapter")) {
        $running = Invoke-ToolCapture -Tool $docker -Arguments @(
            "ps", "-q",
            "--filter", "label=com.docker.compose.project=$($script:ProjectName)",
            "--filter", "label=com.docker.compose.service=$service")
        Assert-ToolSuccess $running "$service write-path inspection"
        $containerIds = @($running.StdOut -split "`r?`n" | ForEach-Object { $_.Trim() } | Where-Object { $_ })
        if ($containerIds.Count -gt 0) {
            $stopped = Invoke-ToolCapture -Tool $docker -Arguments (@("stop") + $containerIds)
            Assert-ToolSuccess $stopped "$service write-path stop"
        }
    }
}

function Write-PostgresBackup {
    param([Parameter(Mandatory = $true)][string]$Path)

    $arguments = Get-ComposeArguments @(
        "exec", "-T", "postgres", "pg_dump",
        "-U", (Read-ConfigValue "POSTGRES_USER" "luthn"),
        "-d", (Read-ConfigValue "POSTGRES_DB" "luthn"),
        "-Fc")
    $result = Invoke-ToolToFile -Tool (Get-DockerTool) -Arguments $arguments -OutputPath $Path
    if ($result.ExitCode -ne 0) {
        if ([IO.File]::Exists($Path)) { [IO.File]::Delete($Path) }
        $detail = $result.StdErr.Trim()
        throw "PostgreSQL backup failed with exit code $($result.ExitCode): $detail"
    }
    Protect-SecretFile $Path
}

function Update-Luthn {
    param([string[]]$Arguments)
    if ($Arguments.Count -gt 1) { throw "usage: luthn update [image]" }

    Require-Installation
    Test-DockerPreflight
    $docker = Get-DockerTool
    $targetImage = if ($Arguments.Count -eq 1) { $Arguments[0] } else { Read-ConfigValue "LUTHN_IMAGE" $script:DefaultImage }
    if (-not $targetImage) { throw "usage: luthn update [image]" }
    $previousImageRef = Read-ConfigValue "LUTHN_IMAGE" $script:DefaultImage
    $previousImageId = Get-ApiImageId

    Ensure-Directories
    Write-Host "Pulling $targetImage..."
    $pull = if ($env:LUTHN_SKIP_PULL -ceq "true") {
        Invoke-ToolCapture -Tool $docker -Arguments @("image", "inspect", $targetImage)
    } else {
        Invoke-ToolCapture -Tool $docker -Arguments @("pull", $targetImage)
    }
    if ($pull.ExitCode -ne 0) {
        Write-UpdateState -Status "failed" -TargetImage $targetImage -PreviousImageRef $previousImageRef -PreviousImageId $previousImageId
        throw "Update failed while pulling the target image. The running API and previous image were preserved."
    }

    $targetIdResult = Invoke-ToolCapture -Tool $docker -Arguments @("image", "inspect", "--format", "{{.Id}}", $targetImage)
    Assert-ToolSuccess $targetIdResult "target image inspection"
    $targetImageId = $targetIdResult.StdOut.Trim()
    $revisionResult = Invoke-ToolCapture -Tool $docker -Arguments @("image", "inspect", "--format", "{{ index .Config.Labels `"org.opencontainers.image.revision`" }}", $targetImage)
    $targetRevision = if ($revisionResult.ExitCode -eq 0) { $revisionResult.StdOut.Trim() } else { "" }

    Write-Host "Refreshing Windows CLI and Compose runtime..."
    try {
        Install-ComposeRuntime -RuntimeSource (Get-RuntimeSource -Image $targetImage -Revision $targetRevision) -IncludeCli
    } catch {
        Write-UpdateState -Status "failed" -TargetImage $targetImage -TargetImageId $targetImageId -PreviousImageRef $previousImageRef -PreviousImageId $previousImageId
        throw "Update failed while refreshing the Windows lifecycle runtime. The running API and previous image were preserved. $($_.Exception.Message)"
    }

    $postgres = Invoke-ComposeCapture @("up", "-d", "postgres")
    Assert-ToolSuccess $postgres "PostgreSQL startup"
    Wait-ForPostgres

    Write-Host "Stopping API and MCP write paths..."
    try {
        Stop-WritePaths
        if ($env:LUTHN_UPDATE_AFTER_STOP_HOOK) {
            Invoke-ToolVisible -Tool (New-ToolSpecFromPath -Path $env:LUTHN_UPDATE_AFTER_STOP_HOOK)
        }
    } catch {
        Write-UpdateState -Status "failed" -TargetImage $targetImage -TargetImageId $targetImageId -PreviousImageRef $previousImageRef -PreviousImageId $previousImageId
        $restart = Invoke-ComposeCapture @("up", "-d", "api")
        if ($restart.ExitCode -ne 0) { throw "Update stopped before backup and the previous API could not be restarted. $($_.Exception.Message)" }
        throw "Update stopped before backup. The previous API was restarted. $($_.Exception.Message)"
    }

    $backupPath = Join-Path (Join-Path $script:StateDir "backups") "luthn-$([DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ')).dump"
    Write-Host "Backing up PostgreSQL to $backupPath..."
    try {
        Write-PostgresBackup -Path $backupPath
    } catch {
        Set-ConfigValue -Key "LUTHN_IMAGE" -Value $previousImageRef
        Write-UpdateState -Status "failed" -TargetImage $targetImage -TargetImageId $targetImageId -PreviousImageRef $previousImageRef -PreviousImageId $previousImageId
        $restart = Invoke-ComposeCapture @("up", "-d", "api")
        if ($restart.ExitCode -ne 0) { throw "Update failed while backing up PostgreSQL, and the previous API could not be restarted. $($_.Exception.Message)" }
        throw "Update failed while backing up PostgreSQL. The previous API was restarted. $($_.Exception.Message)"
    }

    Write-UpdateState -Status "updating" -TargetImage $targetImage -TargetImageId $targetImageId -PreviousImageRef $previousImageRef -PreviousImageId $previousImageId -BackupPath $backupPath
    Set-ConfigValue -Key "LUTHN_IMAGE" -Value $targetImage
    Write-Host "Applying target-image migrations..."
    $migration = Invoke-ComposeCapture @("run", "--rm", "--no-deps", "migrate")
    if ($migration.ExitCode -ne 0) {
        $recoveryImage = if ($previousImageId) { $previousImageId } else { $previousImageRef }
        Set-ConfigValue -Key "LUTHN_IMAGE" -Value $recoveryImage
        Write-UpdateState -Status "failed" -TargetImage $targetImage -TargetImageId $targetImageId -PreviousImageRef $previousImageRef -PreviousImageId $previousImageId -BackupPath $backupPath
        throw "Update failed during migration. API remains stopped; use the recorded backup and previous image for recovery."
    }

    $apiStart = Invoke-ComposeCapture @("up", "-d", "api")
    if ($apiStart.ExitCode -ne 0) {
        $stopError = ""
        try { Stop-WritePaths } catch { $stopError = $_.Exception.Message }
        $recoveryImage = if ($previousImageId) { $previousImageId } else { $previousImageRef }
        Set-ConfigValue -Key "LUTHN_IMAGE" -Value $recoveryImage
        Write-UpdateState -Status "failed" -TargetImage $targetImage -TargetImageId $targetImageId -PreviousImageRef $previousImageRef -PreviousImageId $previousImageId -BackupPath $backupPath
        if ($stopError) { throw "Update failed while starting the target API and its write paths could not be confirmed stopped: $stopError" }
        throw "Update failed while starting the target API. API remains stopped; the backup and previous image are recorded."
    }
    try {
        Wait-ForApi
    } catch {
        $stopError = ""
        try { Stop-WritePaths } catch { $stopError = $_.Exception.Message }
        $recoveryImage = if ($previousImageId) { $previousImageId } else { $previousImageRef }
        Set-ConfigValue -Key "LUTHN_IMAGE" -Value $recoveryImage
        Write-UpdateState -Status "failed" -TargetImage $targetImage -TargetImageId $targetImageId -PreviousImageRef $previousImageRef -PreviousImageId $previousImageId -BackupPath $backupPath
        if ($stopError) { throw "Update failed readiness and its write paths could not be confirmed stopped: $stopError" }
        throw "Update failed readiness. API is stopped; the backup and previous image are recorded."
    }

    $currentImageId = Get-ApiImageId
    Write-UpdateState -Status "ready" -TargetImage $targetImage -TargetImageId $currentImageId -PreviousImageRef $previousImageRef -PreviousImageId $previousImageId -BackupPath $backupPath
    Write-Host "Luthn update completed: $targetImage"
}

function Get-McpDockerArguments {
    return Get-ComposeArguments @("--profile", "tools", "run", "--rm", "--no-deps", "-T", "mcp")
}

function Get-CodexManagedTarget {
    param([Parameter(Mandatory = $true)][string]$Path)

    $item = Get-Item -LiteralPath $Path -Force -ErrorAction SilentlyContinue
    if ($item) {
        if ($item.LinkType) {
            $target = $item.ResolveLinkTarget($true)
            if (-not $target) { throw "Codex configuration link could not be resolved: $Path" }
            return $target.FullName
        }
        return $item.FullName
    }
    return [IO.Path]::GetFullPath($Path)
}

function Write-CodexManagedText {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [AllowEmptyString()][Parameter(Mandatory = $true)][string]$Content
    )
    Write-Utf8File -Path (Get-CodexManagedTarget $Path) -Content $Content
}

function Get-CodexFileSnapshot {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [int]$MaximumBytes = $script:MaxCodexInstructionsBytes
    )
    $target = Get-CodexManagedTarget $Path
    $fileInfo = if ([IO.File]::Exists($target)) { [IO.FileInfo]::new($target) } else { $null }
    if ($fileInfo -and $fileInfo.Length -gt $MaximumBytes) {
        throw "Codex configuration exceeds the supported size: $Path"
    }
    return [pscustomobject]@{
        Path = $Path
        Target = $target
        Existed = $null -ne $fileInfo
        Bytes = if ($fileInfo) { [IO.File]::ReadAllBytes($target) } else { $null }
    }
}

function Restore-CodexFileSnapshot {
    param([Parameter(Mandatory = $true)]$Snapshot)
    if ($Snapshot.Existed) {
        [void][IO.Directory]::CreateDirectory((Split-Path -Parent $Snapshot.Target))
        [IO.File]::WriteAllBytes($Snapshot.Target, $Snapshot.Bytes)
    } elseif ([IO.File]::Exists($Snapshot.Target)) {
        [IO.File]::Delete($Snapshot.Target)
    }
}

function Read-CodexManagedText {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [int]$MaximumBytes = $script:MaxCodexInstructionsBytes
    )
    $target = Get-CodexManagedTarget $Path
    if (-not [IO.File]::Exists($target)) { return "" }
    if ([IO.FileInfo]::new($target).Length -gt $MaximumBytes) {
        throw "Codex configuration exceeds the supported size: $Path"
    }
    return [IO.File]::ReadAllText($target)
}

function Get-CodexHooksDocument {
    param([Parameter(Mandatory = $true)][string]$Path)
    $content = Read-CodexManagedText -Path $Path
    if (-not $content) { return [ordered]@{} }
    try {
        $document = $content | ConvertFrom-Json -AsHashtable
    } catch {
        throw "Codex hooks configuration is not valid JSON: $Path"
    }
    if ($document -isnot [Collections.IDictionary]) {
        throw "Codex hooks configuration must be a JSON object: $Path"
    }
    if ($document.Contains("hooks") -and $document["hooks"] -isnot [Collections.IDictionary]) {
        throw "Codex hooks configuration 'hooks' must be an object: $Path"
    }
    return $document
}

function Get-CodexStopGroups {
    param(
        [Parameter(Mandatory = $true)][Collections.IDictionary]$Document,
        [switch]$Create
    )
    if (-not $Document.Contains("hooks")) {
        if (-not $Create) { return $null }
        $Document["hooks"] = [ordered]@{}
    }
    $hooks = $Document["hooks"]
    if (-not $hooks.Contains("Stop")) {
        if (-not $Create) { return $null }
        $hooks["Stop"] = @()
    }
    if ($hooks["Stop"] -isnot [Collections.IList]) {
        throw "Codex hooks configuration 'hooks.Stop' must be an array."
    }
    return @($hooks["Stop"])
}

function Test-CodexHookInstalled {
    param([string]$Path = $script:CodexHooksFile, [string]$Command = (Get-CodexHookCommand))
    if (-not [IO.File]::Exists((Get-CodexManagedTarget $Path))) { return $false }
    $document = Get-CodexHooksDocument $Path
    $groups = @(Get-CodexStopGroups -Document $document)
    $matches = @($groups | Where-Object { $_ -is [Collections.IDictionary] -and $_["matcher"] -ceq $script:CodexHookMarker })
    if ($matches.Count -ne 1) { return $false }
    $handlers = $matches[0]["hooks"]
    return $handlers -is [Collections.IList] -and $handlers.Count -eq 1 -and
        $handlers[0] -is [Collections.IDictionary] -and $handlers[0]["type"] -ceq "command" -and
        $handlers[0]["command"] -ceq $Command -and
        (-not $handlers[0].Contains("commandWindows") -or $handlers[0]["commandWindows"] -ceq $Command) -and
        (-not $handlers[0].Contains("command_windows") -or $handlers[0]["command_windows"] -ceq $Command) -and
        -not $handlers[0].Contains("async")
}

function Install-CodexHook {
    param([string]$Path = $script:CodexHooksFile, [string]$Command = (Get-CodexHookCommand))
    if (Test-CodexHookInstalled -Path $Path -Command $Command) { return $false }
    $document = Get-CodexHooksDocument $Path
    $groups = @(Get-CodexStopGroups -Document $document -Create)
    $remaining = @($groups | Where-Object { $_ -isnot [Collections.IDictionary] -or $_["matcher"] -cne $script:CodexHookMarker })
    $managed = [ordered]@{
        matcher = $script:CodexHookMarker
        hooks = @([ordered]@{
            type = "command"
            command = $Command
            commandWindows = $Command
            timeout = $script:CodexHttpTimeoutSeconds + 1
            statusMessage = "Syncing Luthn memory"
        })
    }
    $document["hooks"]["Stop"] = @($remaining) + @($managed)
    Write-CodexManagedText -Path $Path -Content (($document | ConvertTo-Json -Depth 20) + "`n")
    return $true
}

function Remove-CodexHook {
    param([string]$Path = $script:CodexHooksFile, [switch]$DeleteIfEmpty)
    if (-not [IO.File]::Exists((Get-CodexManagedTarget $Path))) { return $false }
    $document = Get-CodexHooksDocument $Path
    $groups = @(Get-CodexStopGroups -Document $document)
    if ($null -eq $groups) { return $false }
    $remaining = @($groups | Where-Object { $_ -isnot [Collections.IDictionary] -or $_["matcher"] -cne $script:CodexHookMarker })
    if ($remaining.Count -eq $groups.Count) { return $false }
    $document["hooks"]["Stop"] = $remaining
    if ($DeleteIfEmpty -and $document.Count -eq 1 -and $document["hooks"].Count -eq 1 -and $remaining.Count -eq 0) {
        $target = Get-CodexManagedTarget $Path
        if ([IO.File]::Exists($target)) { [IO.File]::Delete($target) }
        return $true
    }
    Write-CodexManagedText -Path $Path -Content (($document | ConvertTo-Json -Depth 20) + "`n")
    return $true
}

function Get-ContentWithoutAutoRecall {
    param([Parameter(Mandatory = $true)][AllowEmptyString()][string]$Content)
    $startCount = ([regex]::Matches($Content, [regex]::Escape($script:AutoRecallStartMarker))).Count
    $endCount = ([regex]::Matches($Content, [regex]::Escape($script:AutoRecallEndMarker))).Count
    if ($startCount -eq 0 -and $endCount -eq 0) { return $Content }
    if ($startCount -ne 1 -or $endCount -ne 1) {
        throw "Codex instructions contain malformed Luthn auto-recall markers."
    }
    $start = $Content.IndexOf($script:AutoRecallStartMarker, [StringComparison]::Ordinal)
    $end = $Content.IndexOf($script:AutoRecallEndMarker, $start, [StringComparison]::Ordinal)
    if ($end -lt $start) { throw "Codex instructions contain malformed Luthn auto-recall markers." }
    $before = $Content.Substring(0, $start).TrimEnd([char[]]"`r`n")
    $afterStart = $end + $script:AutoRecallEndMarker.Length
    $after = $Content.Substring($afterStart).TrimStart([char[]]"`r`n")
    $parts = @(@($before, $after) | Where-Object { $_ })
    if ($parts.Count -eq 0) { return "" }
    return (($parts -join "`r`n`r`n").TrimEnd([char[]]"`r`n") + "`r`n")
}

function Test-CodexAutoRecallInstalled {
    param([string]$Path = $script:CodexInstructionsFile)
    $content = Read-CodexManagedText -Path $Path
    $startCount = ([regex]::Matches($content, [regex]::Escape($script:AutoRecallStartMarker))).Count
    $endCount = ([regex]::Matches($content, [regex]::Escape($script:AutoRecallEndMarker))).Count
    if ($startCount -eq 0 -and $endCount -eq 0) { return $false }
    if ($startCount -ne 1 -or $endCount -ne 1) {
        throw "Codex instructions contain malformed Luthn auto-recall markers."
    }
    return $content.Contains($script:AutoRecallInstruction.TrimEnd([char[]]"`r`n"), [StringComparison]::Ordinal)
}

function Install-CodexAutoRecall {
    param([string]$Path = $script:CodexInstructionsFile)
    if (Test-CodexAutoRecallInstalled -Path $Path) { return $false }
    $content = Get-ContentWithoutAutoRecall (Read-CodexManagedText -Path $Path)
    $prefix = $content.TrimEnd([char[]]"`r`n")
    $instruction = $script:AutoRecallInstruction.TrimEnd([char[]]"`r`n")
    $updated = if ($prefix) { "$prefix`r`n`r`n$instruction`r`n" } else { "$instruction`r`n" }
    Write-CodexManagedText -Path $Path -Content $updated
    return $true
}

function Remove-CodexAutoRecall {
    param([string]$Path = $script:CodexInstructionsFile, [switch]$PreserveEmpty)
    $content = Read-CodexManagedText -Path $Path
    $updated = Get-ContentWithoutAutoRecall $content
    if ($updated -ceq $content) { return $false }
    if ($updated -or $PreserveEmpty) {
        Write-CodexManagedText -Path $Path -Content $updated
    } else {
        $target = Get-CodexManagedTarget $Path
        if ([IO.File]::Exists($target)) { [IO.File]::Delete($target) }
    }
    return $true
}

function Get-CodexHookCommand {
    if ($env:LUTHN_CODEX_HOOK_COMMAND) { return $env:LUTHN_CODEX_HOOK_COMMAND }
    $pwsh = Get-PwshPath
    $cli = Join-Path $script:BinDir "luthn.ps1"
    if (-not [IO.File]::Exists($cli)) { $cli = $PSCommandPath }
    if ($pwsh.Contains('"') -or $cli.Contains('"')) { throw "Codex hook command paths cannot contain a quote." }
    return "`"$pwsh`" -NoProfile -NonInteractive -File `"$cli`" codex-hook"
}

function Read-BoundedStandardInput {
    param([Parameter(Mandatory = $true)][int]$MaximumBytes)
    $stream = [Console]::OpenStandardInput()
    $memory = [IO.MemoryStream]::new()
    try {
        $buffer = [byte[]]::new(8192)
        while (($read = $stream.Read($buffer, 0, $buffer.Length)) -gt 0) {
            if ($memory.Length + $read -gt $MaximumBytes) { throw "Codex hook input exceeded the bounded payload limit." }
            $memory.Write($buffer, 0, $read)
        }
        return $script:StrictUtf8.GetString($memory.ToArray()).TrimStart([char]0xFEFF)
    } finally {
        $memory.Dispose()
    }
}

function Get-StableCodexId {
    param([Parameter(Mandatory = $true)][string]$Prefix, [Parameter(Mandatory = $true)][string]$Value)
    $hash = [Security.Cryptography.SHA256]::HashData($script:Utf8NoBom.GetBytes($Value))
    return "$Prefix-$(([Convert]::ToHexString($hash).ToLowerInvariant()).Substring(0, 32))"
}

function Test-CodexMessageContainsCredentials {
    param([Parameter(Mandatory = $true)][string]$Value)
    $patterns = @(
        '(?s)-----BEGIN [A-Z0-9 ]*PRIVATE KEY-----.*?-----END [A-Z0-9 ]*PRIVATE KEY-----',
        '\b(?:sk-[A-Za-z0-9_-]{16,}|ghp_[A-Za-z0-9]{16,}|github_pat_[A-Za-z0-9_]{16,}|AKIA[A-Z0-9]{16})\b',
        '(?i)\bBearer\s+[A-Za-z0-9._~+/=-]{16,}',
        '(?i)\b(?:Authorization\s*[:=]\s*)?Basic\s+[A-Za-z0-9+/]{8,}={0,2}',
        '\b[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\b',
        '(?i)\b[A-Za-z][A-Za-z0-9+.-]*://[^\s/@:]+:[^\s/@]+@',
        '(?i)(?:api[_-]?key|access[_-]?token|refresh[_-]?token|client[_-]?secret|token|password|passwd|secret|private[_-]?key|database[_-]?url|connection[_-]?string)\s*[:=]\s*\S'
    )
    foreach ($pattern in $patterns) {
        if ([regex]::IsMatch($Value, $pattern)) { return $true }
    }
    return $false
}

function New-CodexTurnCapsule {
    param([Parameter(Mandatory = $true)][string]$HookJson)
    $inputObject = $HookJson | ConvertFrom-Json
    if ($inputObject.hook_event_name -cne "Stop") { throw "expected Codex Stop hook input" }
    $sessionId = [string]$inputObject.session_id
    $turnId = [string]$inputObject.turn_id
    if (-not $sessionId.Trim()) { throw "Codex Stop hook input is missing session_id" }
    if (-not $turnId.Trim()) { throw "Codex Stop hook input is missing turn_id" }
    if ($null -eq $inputObject.last_assistant_message) { return $null }
    if ($inputObject.last_assistant_message -isnot [string]) { throw "Codex Stop hook last_assistant_message must be text" }
    $summary = $inputObject.last_assistant_message.Trim()
    if (-not $summary -or (Test-CodexMessageContainsCredentials $summary)) { return $null }
    $serviceTokenFile = Read-ConfigValue "LUTHN_SERVICE_TOKEN_FILE" $script:TokenFile
    if ([IO.File]::Exists($serviceTokenFile)) {
        $serviceToken = [IO.File]::ReadAllText($serviceTokenFile).Trim()
        if ($serviceToken -and $summary.Contains($serviceToken, [StringComparison]::Ordinal)) { return $null }
    }
    if ($summary.Length -gt $script:MaxTurnCapsuleCharacters) {
        $summary = $summary.Substring(0, $script:MaxTurnCapsuleCharacters).TrimEnd()
    }
    $summaryHash = [Security.Cryptography.SHA256]::HashData($script:Utf8NoBom.GetBytes($summary))
    return [ordered]@{
        sessionId = Get-StableCodexId "codex-session" $sessionId.Trim()
        turnId = Get-StableCodexId "codex-turn" $turnId.Trim()
        sourceAgent = "codex"
        summary = $summary
        coreTags = @("codex", "conversation")
        contentDigest = "sha256:$([Convert]::ToHexString($summaryHash).ToLowerInvariant())"
        idempotencyKey = Get-StableCodexId "codex-capsule" "$($sessionId.Trim()):$($turnId.Trim())"
        title = "Codex turn capsule"
    }
}

function Invoke-LuthnApiRequest {
    param(
        [Parameter(Mandatory = $true)][string]$Method,
        [Parameter(Mandatory = $true)][string]$Url,
        [Parameter(Mandatory = $true)][string]$Token,
        [AllowNull()]$Payload = $null
    )
    $client = [Net.Http.HttpClient]::new()
    $client.Timeout = [TimeSpan]::FromSeconds($script:CodexHttpTimeoutSeconds)
    $request = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::new($Method), $Url)
    $response = $null
    $request.Headers.Authorization = [Net.Http.Headers.AuthenticationHeaderValue]::new("Bearer", $Token)
    if ($null -ne $Payload) {
        $json = $Payload | ConvertTo-Json -Depth 12 -Compress
        $request.Content = [Net.Http.StringContent]::new($json, $script:Utf8NoBom, "application/json")
    }
    try {
        $response = $client.SendAsync($request).GetAwaiter().GetResult()
        $content = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        if (-not $response.IsSuccessStatusCode) { throw "Luthn API returned HTTP $([int]$response.StatusCode)." }
        if (-not $content) { return $null }
        return $content | ConvertFrom-Json -AsHashtable
    } finally {
        $request.Dispose()
        if ($null -ne $response) { $response.Dispose() }
        $client.Dispose()
    }
}

function Get-CodexApiCredentials {
    $tokenFile = Read-ConfigValue "LUTHN_SERVICE_TOKEN_FILE" $script:TokenFile
    if (-not [IO.File]::Exists($tokenFile)) { throw "Luthn service token is missing." }
    $token = [IO.File]::ReadAllText($tokenFile).Trim()
    if (-not $token -or $token.Length -gt 4096) { throw "Luthn service token is invalid." }
    return [pscustomobject]@{
        BaseUrl = (Read-ConfigValue "LUTHN_BASE_URL" "http://127.0.0.1:8080").TrimEnd("/")
        Token = $token
    }
}

function Send-CodexObservation {
    param([Parameter(Mandatory = $true)][object[]]$Channels)
    if ($env:LUTHN_CODEX_SKIP_OBSERVATION -ceq "true") { return }
    $credentials = Get-CodexApiCredentials
    $payload = [ordered]@{
        agentName = "Codex"
        integrationKind = "host-hook-mcp"
        connectorVersion = $script:LuthnWindowsCliVersion
        channels = $Channels
    }
    [void](Invoke-LuthnApiRequest -Method "POST" -Url "$($credentials.BaseUrl)/api/agent-connections/codex/observations" -Token $credentials.Token -Payload $payload)
}

function Invoke-CodexHookUploadPayload {
    param([Parameter(Mandatory = $true)][string]$PayloadJson)
    try {
        if ($env:LUTHN_CODEX_HOOK_CAPTURE_FILE) {
            Write-Utf8File -Path $env:LUTHN_CODEX_HOOK_CAPTURE_FILE -Content ($PayloadJson.TrimEnd() + "`n")
            return
        }
        $capsule = $PayloadJson | ConvertFrom-Json -AsHashtable
        $credentials = Get-CodexApiCredentials
        [void](Invoke-LuthnApiRequest -Method "POST" -Url "$($credentials.BaseUrl)/api/agent/turn-summaries" -Token $credentials.Token -Payload $capsule)
        try {
            Send-CodexObservation @([ordered]@{ channel = "automatic-ingestion"; configured = $true; verificationState = "Verified"; activityState = "Succeeded"; failureCode = $null })
        } catch {}
    } catch {
        try {
            Send-CodexObservation @([ordered]@{ channel = "automatic-ingestion"; configured = $true; verificationState = "Verified"; activityState = "Failed"; failureCode = "delivery.failed" })
        } catch {}
    }
}

function Start-CodexHookUpload {
    param([Parameter(Mandatory = $true)][string]$PayloadJson)
    if ($env:LUTHN_CODEX_HOOK_SYNCHRONOUS -ceq "true") {
        Invoke-CodexHookUploadPayload $PayloadJson
        return
    }
    $cli = Join-Path $script:BinDir "luthn.ps1"
    if (-not [IO.File]::Exists($cli)) { $cli = $PSCommandPath }
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = Get-PwshPath
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardInput = $true
    foreach ($argument in @("-NoProfile", "-NonInteractive", "-File", $cli, "codex-hook-upload")) {
        [void]$startInfo.ArgumentList.Add($argument)
    }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    try {
        if (-not $process.Start()) { return }
        $process.StandardInput.Write($PayloadJson)
        $process.StandardInput.Close()
    } finally {
        $process.Dispose()
    }
}

function Run-CodexHook {
    try {
        $hookJson = Read-BoundedStandardInput $script:MaxCodexHookInputBytes
        $capsule = New-CodexTurnCapsule $hookJson
        if ($null -ne $capsule) { Start-CodexHookUpload ($capsule | ConvertTo-Json -Depth 8 -Compress) }
    } catch {
        if ($env:LUTHN_CODEX_HOOK_TEST_THROW -ceq "true") { throw }
    }
}

function Run-CodexHookUpload {
    try {
        Invoke-CodexHookUploadPayload (Read-BoundedStandardInput 32768)
    } catch {
        if ($env:LUTHN_CODEX_HOOK_TEST_THROW -ceq "true") { throw }
    }
}

function Test-StringArrayEqual {
    param([object[]]$Left, [object[]]$Right)
    if ($Left.Count -ne $Right.Count) { return $false }
    for ($index = 0; $index -lt $Left.Count; $index++) {
        if ([string]$Left[$index] -cne [string]$Right[$index]) { return $false }
    }
    return $true
}

function Get-CodexRegistration {
    param($Codex)
    $result = Invoke-ToolCapture -Tool $Codex -Arguments @("mcp", "get", "luthn", "--json")
    if ($result.ExitCode -ne 0) {
        if (($result.StdErr + $result.StdOut) -match "No MCP server named") { return $null }
        throw "Codex MCP inspection failed: $($result.StdErr.Trim())"
    }
    return $result.StdOut | ConvertFrom-Json
}

function Write-ConnectorState {
    param(
        [string]$Path,
        [string]$State,
        [string]$CommandPath,
        [string[]]$Arguments,
        [string]$HookCommand,
        [bool]$HookInstalled,
        [bool]$AutoRecall,
        [bool]$HooksExisted,
        [bool]$InstructionsExisted
    )
    Ensure-Directories
    $content = [ordered]@{
        version = 2
        integration = "host-hook-mcp"
        setupState = $State
        mcpName = "luthn"
        command = $CommandPath
        arguments = $Arguments
        hooksFile = $script:CodexHooksFile
        hookCommand = $HookCommand
        hookInstalled = $HookInstalled
        hooksExistedBeforeConnect = $HooksExisted
        instructionsFile = $script:CodexInstructionsFile
        autoRecall = $AutoRecall
        instructionsExistedBeforeConnect = $InstructionsExisted
        updatedAt = [DateTimeOffset]::UtcNow.ToString("O")
    } | ConvertTo-Json -Depth 5
    Write-Utf8File -Path $Path -Content ($content + "`n")
    Protect-SecretFile $Path
}

function Test-RegistrationMatches {
    param($Registration, [string]$CommandPath, [string[]]$Arguments)
    if (-not $Registration -or $Registration.transport.type -cne "stdio") { return $false }
    return $Registration.transport.command -ceq $CommandPath -and (Test-StringArrayEqual @($Registration.transport.args) $Arguments)
}

function Test-McpProbe {
    $probe = Invoke-ComposeCapture @("--profile", "tools", "run", "--rm", "--no-deps", "-T", "mcp", "mcp", "--list-tools")
    if ($probe.ExitCode -ne 0) { return $false }
    return @($probe.StdOut -split "`r?`n") -ccontains "get_context_pack"
}

function Connect-Codex {
    param([string[]]$Arguments = @())
    $autoRecallRequested = $true
    $recallOptionSeen = $false
    foreach ($argument in $Arguments) {
        if ($recallOptionSeen) { throw "usage: luthn connect codex [--no-auto-recall]" }
        if ($argument -ceq "--auto-recall") { $autoRecallRequested = $true; $recallOptionSeen = $true }
        elseif ($argument -ceq "--no-auto-recall") { $autoRecallRequested = $false; $recallOptionSeen = $true }
        else { throw "usage: luthn connect codex [--no-auto-recall]" }
    }
    Require-Installation
    Test-DockerPreflight
    $docker = Get-DockerTool
    $codex = Get-CodexTool
    $mcpArguments = @($docker.PrefixArguments) + @(Get-McpDockerArguments)
    $existing = Get-CodexRegistration $codex
    if ($existing -and -not (Test-RegistrationMatches $existing $docker.FilePath $mcpArguments)) {
        throw "Codex already has an unrelated MCP registration named 'luthn'; no configuration was changed."
    }
    $hookCommand = Get-CodexHookCommand
    $hookSnapshot = Get-CodexFileSnapshot $script:CodexHooksFile
    $instructionsSnapshot = Get-CodexFileSnapshot $script:CodexInstructionsFile
    $hooksExistedBeforeConnect = $hookSnapshot.Existed
    $instructionsExistedBeforeConnect = $instructionsSnapshot.Existed
    if ([IO.File]::Exists($script:CodexStateFile)) {
        try {
            $previousState = [IO.File]::ReadAllText($script:CodexStateFile) | ConvertFrom-Json -AsHashtable
            if ([int]$previousState["version"] -ge 2) {
                if ($previousState.Contains("hooksExistedBeforeConnect")) { $hooksExistedBeforeConnect = [bool]$previousState["hooksExistedBeforeConnect"] }
                if ($previousState.Contains("instructionsExistedBeforeConnect")) { $instructionsExistedBeforeConnect = [bool]$previousState["instructionsExistedBeforeConnect"] }
            }
        } catch {}
    }
    $preexistingRecall = Test-CodexAutoRecallInstalled
    Write-ConnectorState -Path $script:CodexPendingStateFile -State "pending" -CommandPath $docker.FilePath -Arguments $mcpArguments -HookCommand $hookCommand -HookInstalled $false -AutoRecall $preexistingRecall -HooksExisted $hooksExistedBeforeConnect -InstructionsExisted $instructionsExistedBeforeConnect
    $added = $false
    try {
        [void](Install-CodexHook -Command $hookCommand)
        if (-not $existing) {
            $addResult = Invoke-ToolCapture -Tool $codex -Arguments (@("mcp", "add", "luthn", "--", $docker.FilePath) + $mcpArguments)
            Assert-ToolSuccess $addResult "Codex MCP registration"
            $added = $true
        }
        if (-not (Test-McpProbe)) { throw "Codex MCP probe failed." }
        if ($autoRecallRequested) {
            [void](Install-CodexAutoRecall)
        } else {
            [void](Remove-CodexAutoRecall -PreserveEmpty:$instructionsExistedBeforeConnect)
        }
        $autoRecallEnabled = Test-CodexAutoRecallInstalled
        Write-ConnectorState -Path $script:CodexStateFile -State "configured" -CommandPath $docker.FilePath -Arguments $mcpArguments -HookCommand $hookCommand -HookInstalled $true -AutoRecall $autoRecallEnabled -HooksExisted $hooksExistedBeforeConnect -InstructionsExisted $instructionsExistedBeforeConnect
        [IO.File]::Delete($script:CodexPendingStateFile)
    } catch {
        $originalError = $_.Exception.Message
        $rollbackError = ""
        try {
            Restore-CodexFileSnapshot $hookSnapshot
            Restore-CodexFileSnapshot $instructionsSnapshot
        } catch { $rollbackError = $_.Exception.Message }
        $mcpCleanupError = ""
        if ($added) {
            $removeResult = Invoke-ToolCapture -Tool $codex -Arguments @("mcp", "remove", "luthn")
            if ($removeResult.ExitCode -ne 0) { $mcpCleanupError = "Codex MCP cleanup also failed" }
        }
        if (-not $rollbackError -and -not $mcpCleanupError) {
            [IO.File]::Delete($script:CodexPendingStateFile)
        } else {
            $hookOwned = $true
            $recallOwned = $preexistingRecall
            try { $hookOwned = Test-CodexHookInstalled -Command $hookCommand } catch {}
            try { $recallOwned = Test-CodexAutoRecallInstalled } catch {}
            Write-ConnectorState -Path $script:CodexPendingStateFile -State "cleanup-required" -CommandPath $docker.FilePath -Arguments $mcpArguments -HookCommand $hookCommand -HookInstalled $hookOwned -AutoRecall $recallOwned -HooksExisted $hooksExistedBeforeConnect -InstructionsExisted $instructionsExistedBeforeConnect
            $details = @($mcpCleanupError, $(if ($rollbackError) { "Codex configuration rollback also failed: $rollbackError" })) | Where-Object { $_ }
            throw "$originalError $($details -join '. '); ownership state was preserved."
        }
        throw $originalError
    }
    try {
        Send-CodexObservation @(
            [ordered]@{ channel = "automatic-ingestion"; configured = $true; verificationState = "Unknown"; activityState = "Unknown"; failureCode = $null },
            [ordered]@{ channel = "mcp"; configured = $true; verificationState = "Verified"; activityState = "Succeeded"; failureCode = $null }
        )
    } catch { Write-Warning "Codex is configured, but the initial server observation could not be recorded." }
    $recallStatus = if (Test-CodexAutoRecallInstalled) { "enabled" } else { "disabled" }
    Write-Host "Codex connector files are configured, but automatic memory capture is not ready yet."
    Write-Host "  automatic ingestion: waiting for Codex hook trust"
    Write-Host "  MCP: configured and verified"
    Write-Host "  lightweight recall: $recallStatus"
    Write-Host ""
    Write-Host "Required one-time Codex security steps:"
    Write-Host "  1. Quit and reopen Codex."
    Write-Host "  2. In a Codex message box, enter /hooks."
    Write-Host "  3. Open Stop > $($script:CodexHookMarker) and choose Trust."
    Write-Host "  4. Complete one Codex turn, then verify with:"
    Write-Host "     luthn connection status codex"
}

function Show-CodexConnectionStatus {
    Require-Installation
    $hookCommand = Get-CodexHookCommand
    $hookConfigured = $false
    $recallConfigured = $false
    try { $hookConfigured = Test-CodexHookInstalled -Command $hookCommand } catch {}
    try { $recallConfigured = Test-CodexAutoRecallInstalled } catch {}
    $mcpConfigured = $false
    try {
        $docker = Get-DockerTool
        $mcpArguments = @($docker.PrefixArguments) + @(Get-McpDockerArguments)
        $mcpConfigured = Test-RegistrationMatches (Get-CodexRegistration (Get-CodexTool)) $docker.FilePath $mcpArguments
    } catch {}
    $localStatePath = if ([IO.File]::Exists($script:CodexPendingStateFile)) {
        $script:CodexPendingStateFile
    } elseif ([IO.File]::Exists($script:CodexStateFile)) {
        $script:CodexStateFile
    } else {
        $null
    }
    $localStatus = "not configured"
    if ($localStatePath) {
        try {
            $localState = [IO.File]::ReadAllText($localStatePath) | ConvertFrom-Json -AsHashtable
            $localStatus = switch ([string]$localState["setupState"]) {
                "configured" { "configured"; break }
                "pending" { "pending"; break }
                "cleanup-required" { "cleanup-required"; break }
                default { "state-invalid"; break }
            }
        } catch {
            $localStatus = "state-invalid"
        }
    }
    Write-Host "Local connector: $localStatus"
    Write-Host "  automatic-ingestion: $(if ($hookConfigured) { 'configured' } else { 'missing' })"
    Write-Host "  mcp: $(if ($mcpConfigured) { 'configured' } else { 'missing or changed' })"
    Write-Host "  lightweight-recall: $(if ($recallConfigured) { 'enabled' } else { 'disabled' })"
    try {
        if ($env:LUTHN_CODEX_SKIP_OBSERVATION -ceq "true") { throw "observation disabled" }
        $credentials = Get-CodexApiCredentials
        $response = Invoke-LuthnApiRequest -Method "GET" -Url "$($credentials.BaseUrl)/api/agent-connections" -Token $credentials.Token
        $connection = @($response["connections"] | Where-Object { $_["agentId"] -ceq "codex" }) | Select-Object -First 1
        if (-not $connection) { Write-Host "Server observation: unknown"; return }
        Write-Host "Server observation: $($connection['state'])"
        foreach ($channel in @($connection["channels"])) {
            $detail = [string]$channel["state"]
            if ($channel["failureCode"]) { $detail += " ($($channel['failureCode']))" }
            Write-Host "  $($channel['channel']): $detail"
        }
    } catch { Write-Host "Server observation: unavailable" }
}

function Disconnect-Codex {
    $statePath = if ([IO.File]::Exists($script:CodexStateFile)) { $script:CodexStateFile } elseif ([IO.File]::Exists($script:CodexPendingStateFile)) { $script:CodexPendingStateFile } else { $null }
    if (-not $statePath) {
        Write-Host "No Luthn-owned Windows Codex configuration was recorded."
        return
    }

    $state = [IO.File]::ReadAllText($statePath) | ConvertFrom-Json -AsHashtable
    $stateVersion = if ($state.Contains("version")) { [int]$state["version"] } else { 1 }
    $hooksFile = if ($stateVersion -ge 2 -and $state["hooksFile"]) { [string]$state["hooksFile"] } else { $script:CodexHooksFile }
    $instructionsFile = if ($stateVersion -ge 2 -and $state["instructionsFile"]) { [string]$state["instructionsFile"] } else { $script:CodexInstructionsFile }
    $hookSnapshot = Get-CodexFileSnapshot $hooksFile
    $instructionsSnapshot = Get-CodexFileSnapshot $instructionsFile
    $codex = Get-CodexTool
    $existing = Get-CodexRegistration $codex
    try {
        $deleteEmptyHooks = $stateVersion -ge 2 -and $state.Contains("hooksExistedBeforeConnect") -and -not $state["hooksExistedBeforeConnect"]
        $preserveEmptyInstructions = $stateVersion -ge 2 -and $state.Contains("instructionsExistedBeforeConnect") -and $state["instructionsExistedBeforeConnect"]
        if ($stateVersion -ge 2 -and $state["hookInstalled"]) { [void](Remove-CodexHook -Path $hooksFile -DeleteIfEmpty:$deleteEmptyHooks) }
        if ($stateVersion -ge 2 -and $state["autoRecall"]) { [void](Remove-CodexAutoRecall -Path $instructionsFile -PreserveEmpty:$preserveEmptyInstructions) }
        if ($existing) {
            if (-not (Test-RegistrationMatches $existing ([string]$state["command"]) @($state["arguments"]))) {
                throw "The 'luthn' MCP registration changed after setup and was preserved."
            }
            $removeResult = Invoke-ToolCapture -Tool $codex -Arguments @("mcp", "remove", "luthn")
            Assert-ToolSuccess $removeResult "Codex MCP cleanup"
        }
    } catch {
        $originalError = $_.Exception.Message
        try {
            Restore-CodexFileSnapshot $hookSnapshot
            Restore-CodexFileSnapshot $instructionsSnapshot
        } catch { throw "$originalError Codex configuration rollback also failed: $($_.Exception.Message)" }
        throw $originalError
    }
    foreach ($path in @($script:CodexStateFile, $script:CodexPendingStateFile)) {
        try { if ([IO.File]::Exists($path)) { [IO.File]::Delete($path) } } catch {}
    }
    try {
        Send-CodexObservation @(
            [ordered]@{ channel = "automatic-ingestion"; configured = $false; verificationState = "Unknown"; activityState = "Unknown"; failureCode = $null },
            [ordered]@{ channel = "mcp"; configured = $false; verificationState = "Unknown"; activityState = "Unknown"; failureCode = $null }
        )
    } catch {}
    Write-Host "Luthn-owned Codex connector configuration was removed."
}

function Run-Mcp {
    param([string[]]$Arguments)
    Require-Installation
    Test-DockerPreflight
    $docker = Get-DockerTool
    $dockerArguments = @($docker.PrefixArguments) + @(Get-McpDockerArguments)
    if ($Arguments.Count -gt 0) { $dockerArguments += @("mcp") + $Arguments }
    & $docker.FilePath @dockerArguments
    if ($LASTEXITCODE -ne 0) { throw "Luthn MCP exited with code $LASTEXITCODE" }
}

function Uninstall-Luthn {
    param([string[]]$Arguments)
    if ($Arguments.Count -gt 0) { throw "usage: luthn uninstall" }
    Test-DockerPreflight
    if ([IO.File]::Exists($script:CodexStateFile) -or [IO.File]::Exists($script:CodexPendingStateFile)) {
        try {
            Disconnect-Codex
        } catch {
            throw "Uninstall stopped because Codex connector cleanup did not complete. $($_.Exception.Message)"
        }
    }
    if ([IO.File]::Exists($script:ComposeFile) -and [IO.File]::Exists($script:ConfigFile)) {
        Invoke-ComposeVisible @("down", "--remove-orphans")
    }
    if ([IO.Directory]::Exists($script:DataDir)) { [IO.Directory]::Delete($script:DataDir, $true) }
    foreach ($ownedCliPath in @((Join-Path $script:BinDir "luthn.ps1"), (Join-Path $script:BinDir "luthn.cmd"))) {
        if ([IO.File]::Exists($ownedCliPath)) { [IO.File]::Delete($ownedCliPath) }
    }
    $defaultBinDir = [IO.Path]::GetFullPath((Join-Path $script:RootDir "bin")).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    $actualBinDir = [IO.Path]::GetFullPath($script:BinDir).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    if ($actualBinDir -ieq $defaultBinDir -and [IO.Directory]::Exists($script:BinDir) -and [IO.Directory]::GetFileSystemEntries($script:BinDir).Count -eq 0) {
        [IO.Directory]::Delete($script:BinDir)
    }
    Write-Host "Luthn services and Windows runtime were removed. Data volumes, configuration, and backups were preserved."
}

try {
    switch -CaseSensitive ($Command.ToLowerInvariant()) {
        "install" { Install-Luthn $CommandArguments }
        "status" { Show-Status }
        "update" { Update-Luthn $CommandArguments }
        "connect" {
            if ($CommandArguments.Count -lt 1 -or $CommandArguments[0] -cne "codex") { throw "usage: luthn connect codex [--no-auto-recall]" }
            Connect-Codex @($CommandArguments | Select-Object -Skip 1)
        }
        "connection" {
            if ($CommandArguments.Count -ne 2 -or $CommandArguments[0] -cne "status" -or $CommandArguments[1] -cne "codex") { throw "usage: luthn connection status codex" }
            Show-CodexConnectionStatus
        }
        "disconnect" {
            if ($CommandArguments.Count -ne 1 -or $CommandArguments[0] -cne "codex") { throw "usage: luthn disconnect codex" }
            Disconnect-Codex
        }
        "mcp" { Run-Mcp $CommandArguments }
        "codex-hook" {
            if ($CommandArguments.Count -ne 0) { throw "usage: luthn codex-hook" }
            Run-CodexHook
        }
        "codex-hook-upload" {
            if ($CommandArguments.Count -ne 0) { throw "usage: luthn codex-hook-upload" }
            Run-CodexHookUpload
        }
        "uninstall" { Uninstall-Luthn $CommandArguments }
        "help" { Show-Usage }
        "-h" { Show-Usage }
        "--help" { Show-Usage }
        default { throw "unknown command: $Command" }
    }
} catch {
    [Console]::Error.WriteLine($_.Exception.Message)
    if ($env:LUTHN_TEST_NO_EXIT -ceq "true") { throw }
    exit 1
}
