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

$script:LuthnWindowsCliVersion = "3"
$script:CodexConnectorTemplateVersion = "3"
$script:McpSchemaVersion = "3"
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
$script:OperatorTokenFile = if ($env:LUTHN_OPERATOR_TOKEN_FILE) { $env:LUTHN_OPERATOR_TOKEN_FILE } else { Join-Path $script:ConfigDir "operator-token" }
$script:ConnectorStateDir = if ($env:LUTHN_CONNECTOR_STATE_DIR) { $env:LUTHN_CONNECTOR_STATE_DIR } else { Join-Path $script:StateDir "connectors" }
$script:CodexStateFile = if ($env:LUTHN_CODEX_STATE_FILE) { $env:LUTHN_CODEX_STATE_FILE } else { Join-Path $script:ConnectorStateDir "codex-windows.json" }
$script:CodexPendingStateFile = if ($env:LUTHN_CODEX_PENDING_STATE_FILE) { $env:LUTHN_CODEX_PENDING_STATE_FILE } else { Join-Path $script:ConnectorStateDir "codex-windows.pending.json" }
$script:CodexHome = if ($env:CODEX_HOME) { $env:CODEX_HOME } elseif ($env:USERPROFILE) { Join-Path $env:USERPROFILE ".codex" } else { throw "USERPROFILE or CODEX_HOME is required." }
$script:CodexHooksFile = if ($env:LUTHN_CODEX_HOOKS_FILE) { $env:LUTHN_CODEX_HOOKS_FILE } else { Join-Path $script:CodexHome "hooks.json" }
$script:CodexInstructionsFile = if ($env:LUTHN_CODEX_INSTRUCTIONS_FILE) { $env:LUTHN_CODEX_INSTRUCTIONS_FILE } else { Join-Path $script:CodexHome "AGENTS.md" }
$script:ClaudeStateFile = if ($env:LUTHN_CLAUDE_STATE_FILE) { $env:LUTHN_CLAUDE_STATE_FILE } else { Join-Path $script:ConnectorStateDir "claude-code-windows.json" }
$script:ClaudePendingStateFile = if ($env:LUTHN_CLAUDE_PENDING_STATE_FILE) { $env:LUTHN_CLAUDE_PENDING_STATE_FILE } else { Join-Path $script:ConnectorStateDir "claude-code-windows.pending.json" }
$script:ClaudeHome = if ($env:CLAUDE_CONFIG_DIR) { $env:CLAUDE_CONFIG_DIR } elseif ($env:USERPROFILE) { Join-Path $env:USERPROFILE ".claude" } else { throw "USERPROFILE or CLAUDE_CONFIG_DIR is required." }
$script:ClaudeSettingsFile = if ($env:LUTHN_CLAUDE_SETTINGS_FILE) { $env:LUTHN_CLAUDE_SETTINGS_FILE } else { Join-Path $script:ClaudeHome "settings.json" }
$script:ClaudeInstructionsFile = if ($env:LUTHN_CLAUDE_INSTRUCTIONS_FILE) { $env:LUTHN_CLAUDE_INSTRUCTIONS_FILE } else { Join-Path $script:ClaudeHome "CLAUDE.md" }
$script:UpdateStateFile = if ($env:LUTHN_UPDATE_STATE_FILE) { $env:LUTHN_UPDATE_STATE_FILE } else { Join-Path $script:StateDir "update-windows.json" }
$script:DistributionRef = if ($env:LUTHN_DISTRIBUTION_REF) { $env:LUTHN_DISTRIBUTION_REF } else { "main" }
$script:SourceBaseUrl = if ($env:LUTHN_SOURCE_BASE_URL) { $env:LUTHN_SOURCE_BASE_URL.TrimEnd("/") } else { "https://raw.githubusercontent.com/JakobSung/Luthn/$($script:DistributionRef)" }
$script:DefaultImage = "ghcr.io/jakobsung/luthn:main"
$script:Utf8NoBom = [System.Text.UTF8Encoding]::new($false)
$script:StrictUtf8 = [System.Text.UTF8Encoding]::new($false, $true)
$script:CodexHookMarker = "luthn.agent-connector.v1"
$script:ClaudeHookMarker = "luthn.claude-agent-connector.v1"
$script:CodexHookStatusMessage = "Luthn 메모리 저장 예약 중…"
$script:ClaudeHookStatusMessage = "Saving Luthn memory…"
$script:AutoRecallStartMarker = "<!-- luthn:auto-recall:start -->"
$script:AutoRecallEndMarker = "<!-- luthn:auto-recall:end -->"
$script:MaxCodexHookInputBytes = 256 * 1024
$script:MaxCodexInstructionsBytes = 1024 * 1024
$script:MaxTurnCapsuleCharacters = 3900
$script:CodexHttpTimeoutSeconds = 4
$script:CodexHookTimeoutSeconds = ($script:CodexHttpTimeoutSeconds * 2) + 2
$script:AutoRecallInstruction = @"
<!-- luthn:auto-recall:start -->
# Luthn lightweight recall

For a new task or a material topic change, call the Luthn MCP
``get_context_pack`` tool once before substantial work. Use a short task query
and non-sensitive project/task cache key with these bounds. When known, also
send only normalized non-sensitive ``projectKey``, ``taskKey``, and ``topicTags``.
Never send a raw workspace path, transcript path, transcript content, or other
sensitive data as recall metadata:

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

After calling ``get_context_pack``, use Codex commentary for recall status only
under these rules:

- If the response succeeds, parses correctly, and contains one or more actual
  memory items, emit exactly one commentary line for the current user turn:
  ``Luthn 메모리 N개 참고``. Replace ``N`` with the number of actual memory
  items returned.
- Do not emit recall commentary when the response contains zero actual memory
  items, times out, returns an error, cannot be parsed, or uses any fail-open path.
- Do not emit recall commentary when ``get_context_pack`` was not called.
- Emit the recall commentary at most once per user turn, even if recall is
  refreshed or retried.
- Never include memory titles, content, IDs, queries, scores, sources, or any
  sensitive information in the commentary.
- Do not put the recall status in a normal assistant response or final response.
<!-- luthn:auto-recall:end -->
"@
$script:ClaudeAutoRecallInstruction = @"
<!-- luthn:auto-recall:start -->
# Luthn lightweight recall

For a new task or material topic change, call the Luthn MCP `get_context_pack`
tool once before substantial work. Send only short, non-sensitive normalized
metadata; never send a workspace path, transcript path, transcript content, or
credential. Use maxItems 3, maxTokens 600, timeoutMs 200, a stable non-sensitive
cacheKey, cacheTtlSeconds 600, and failOpen true. Reuse the result during the
same task and continue without memory when the call is empty, times out, or fails.
<!-- luthn:auto-recall:end -->
"@

function Show-Usage {
    @"
usage: luthn <command> [options]

commands:
  version [--json]           Show installed runtime and compatibility versions.
  manifest                   Show versioned connector template digests as JSON.
  install [--connect-codex|--connect-claude]
                             Install Luthn and optionally connect one agent.
  status                     Show services, readiness, console, and image.
  update check [--json]      Check the configured update channel without pulling.
  update [image]             Back up, pull, migrate, restart, and verify.
  doctor [--json]            Diagnose runtime, update, and Codex integration state.
  connect codex|claude [--no-auto-recall]
                             Configure Codex or Claude Code; recall is enabled by default.
  connection status codex|claude
                             Show local and server connector state.
  disconnect codex|claude    Remove only Luthn-owned connector configuration.
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

function Remove-ConfigPrefix {
    param([Parameter(Mandatory = $true)][string]$Prefix)
    if (-not [IO.File]::Exists($script:ConfigFile)) { return }
    $lines = @([IO.File]::ReadAllLines($script:ConfigFile) | Where-Object {
        $separator = $_.IndexOf("=")
        $separator -lt 0 -or -not $_.Substring(0, $separator).StartsWith($Prefix, [StringComparison]::Ordinal)
    })
    Write-Utf8File -Path $script:ConfigFile -Content $(if ($lines.Count -gt 0) { ($lines -join "`n") + "`n" } else { "" })
    Protect-SecretFile $script:ConfigFile
}

function Ensure-ConfigValue {
    param([string]$Key, [string]$Value)
    if (-not (Read-ConfigValue -Key $Key)) { Set-ConfigValue -Key $Key -Value $Value }
}

function Upgrade-LegacyClassificationDefault {
    $provider = Read-ConfigValue "Luthn__Classification__Provider" ""
    $allowMock = Read-ConfigValue "Luthn__Classification__AllowMock" ""
    if ($provider -ceq "unconfigured" -and $allowMock -ceq "false") {
        Set-ConfigValue "Luthn__Classification__Provider" "mock"
        Set-ConfigValue "Luthn__Classification__AllowMock" "true"
    }
}

function Ensure-ServiceTokenScope {
    param(
        [Parameter(Mandatory = $true)][string]$Scope,
        [switch]$Required
    )
    for ($index = 0; $index -lt 16; $index++) {
        $value = Read-ConfigValue "Luthn__Auth__Tokens__0__Scopes__$index"
        if ($value -ceq "*" -or $value -ceq $Scope) { return }
    }
    for ($index = 0; $index -lt 16; $index++) {
        $key = "Luthn__Auth__Tokens__0__Scopes__$index"
        if (-not (Read-ConfigValue $key)) {
            Set-ConfigValue $key $Scope
            return
        }
    }
    if ($Required) {
        throw "No free service-token scope slot is available for $Scope."
    }
    Write-Warning "The custom service-token scope table is full; $Scope was not added."
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
    $content = [IO.File]::ReadAllText($Path)
    if ($content -notmatch '\$script:LuthnWindowsCliVersion\s*=\s*"[1-9][0-9]*"' -or
        $content -notmatch '(?m)^function Connect-Codex\s*\{') {
        throw "downloaded Windows CLI did not match the Luthn distribution contract"
    }
}

function Get-WindowsCliVersionFromFile {
    param([Parameter(Mandatory = $true)][string]$Path)

    $content = [IO.File]::ReadAllText($Path)
    if ($content -notmatch '\$script:LuthnWindowsCliVersion\s*=\s*"([1-9][0-9]*)"') {
        throw "Windows CLI template version is missing or invalid: $Path"
    }
    return $Matches[1]
}

function Get-Sha256Hex {
    param([Parameter(Mandatory = $true)][byte[]]$Bytes)
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($Bytes)).ToLowerInvariant()
}

function Get-WindowsManagedTemplateDigest {
    $template = [ordered]@{
        hookMarker = $script:CodexHookMarker
        hookStatusMessage = $script:CodexHookStatusMessage
        hookTimeoutSeconds = $script:CodexHookTimeoutSeconds
        autoRecallInstruction = $script:AutoRecallInstruction
    } | ConvertTo-Json -Compress
    return Get-Sha256Hex -Bytes ([Text.Encoding]::UTF8.GetBytes($template))
}

function Get-CurrentWindowsCliManifest {
    $cliPath = [IO.Path]::GetFullPath($PSCommandPath)
    return [ordered]@{
        version = $script:CodexConnectorTemplateVersion
        cliVersion = $script:LuthnWindowsCliVersion
        helperDigest = Get-Sha256Hex -Bytes ([IO.File]::ReadAllBytes($cliPath))
        templateDigest = Get-WindowsManagedTemplateDigest
    }
}

function Test-ImmutableImageReference {
    param([Parameter(Mandatory = $true)][string]$Image)
    return $Image -cmatch '@sha256:[0-9a-f]{64}$' -or $Image -cmatch ':sha-[0-9a-f]{40}$'
}

function Test-OfficialImageReference {
    param([Parameter(Mandatory = $true)][string]$Image)
    return $Image -clike 'ghcr.io/jakobsung/luthn:*' -or $Image -clike 'ghcr.io/jakobsung/luthn@*'
}

function Get-ImageLabel {
    param(
        [Parameter(Mandatory = $true)][string]$Image,
        [Parameter(Mandatory = $true)][string]$Label
    )
    $result = Invoke-ToolCapture -Tool (Get-DockerTool) -Arguments @(
        "image", "inspect", "--format", "{{ index .Config.Labels `"$Label`" }}", $Image)
    if ($result.ExitCode -ne 0) { return "" }
    return $result.StdOut.Trim()
}

function Get-ImageDigest {
    param([Parameter(Mandatory = $true)][string]$Image)
    $result = Invoke-ToolCapture -Tool (Get-DockerTool) -Arguments @(
        "image", "inspect", "--format", "{{join .RepoDigests `",`"}}", $Image)
    if ($result.ExitCode -ne 0 -or -not $result.StdOut.Trim()) { return "" }
    $last = @($result.StdOut.Trim() -split ',')[-1]
    return @($last -split '@')[-1]
}

function Get-StableReleaseVersion {
    param([string]$Version)
    if ($Version -cmatch '^v?[0-9]+\.[0-9]+\.[0-9]+([.-][0-9A-Za-z.-]+)?$') { return $Version }
    return ""
}

function Get-McpSchemaVersionFromOutput {
    param([AllowEmptyString()][string]$Output)
    foreach ($line in @($Output -split "`r?`n")) {
        try {
            $payload = $line | ConvertFrom-Json
            $serverInfo = $payload.result.serverInfo
            $schemaProperty = $serverInfo.PSObject.Properties["schemaVersion"]
            $versionProperty = $serverInfo.PSObject.Properties["version"]
            $version = if ($schemaProperty -and [string]$schemaProperty.Value) {
                [string]$schemaProperty.Value
            } elseif ($versionProperty) {
                [string]$versionProperty.Value
            } else {
                ""
            }
            if ($version) { return $version }
        } catch {}
    }
    return ""
}

function Get-McpSchemaVersionFromRuntime {
    if (-not [IO.File]::Exists($script:ComposeFile) -or -not [IO.File]::Exists($script:ConfigFile)) { return "" }
    $request = '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}' + "`n"
    $result = Invoke-ToolCapture -Tool (Get-DockerTool) -Arguments (Get-ComposeArguments @(
        "--profile", "tools", "run", "--rm", "--no-deps", "-T", "mcp")) -StandardInput $request
    if ($result.ExitCode -ne 0) { return "" }
    return (Get-McpSchemaVersionFromOutput -Output $result.StdOut)
}

function Get-McpSchemaVersionFromImage {
    param([Parameter(Mandatory = $true)][string]$Image)
    $request = '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}' + "`n"
    $result = Invoke-ToolCapture -Tool (Get-DockerTool) -Arguments @(
        "run", "--rm", "-i", $Image, "mcp") -StandardInput $request
    if ($result.ExitCode -ne 0) { return "" }
    return (Get-McpSchemaVersionFromOutput -Output $result.StdOut)
}

function Get-VersionInformation {
    $imageReference = Read-ConfigValue "LUTHN_IMAGE" $script:DefaultImage
    $imageId = ""
    $digest = ""
    $revision = ""
    $releaseVersion = ""
    $mcpSchemaVersion = ""
    if ([IO.File]::Exists($script:ComposeFile) -and [IO.File]::Exists($script:ConfigFile)) {
        try { $imageId = Get-ApiImageId } catch { $imageId = "" }
        if ($imageId) {
            $digest = Get-ImageDigest -Image $imageId
            $revision = Get-ImageLabel -Image $imageId -Label "org.opencontainers.image.revision"
            $releaseVersion = Get-StableReleaseVersion (Get-ImageLabel -Image $imageId -Label "org.opencontainers.image.version")
            $reportedMcpSchema = Get-ImageLabel -Image $imageId -Label "io.luthn.mcp-schema.version"
            if ($reportedMcpSchema) { $mcpSchemaVersion = $reportedMcpSchema }
            else { $mcpSchemaVersion = Get-McpSchemaVersionFromRuntime }
        }
    }
    return [ordered]@{
        installedImageReference = $imageReference
        imageDigest = $(if ($digest) { $digest } else { $null })
        sourceRevision = $(if ($revision) { $revision } else { $null })
        cliTemplateVersion = $script:LuthnWindowsCliVersion
        connectorTemplateVersion = $script:CodexConnectorTemplateVersion
        mcpSchemaVersion = $(if ($mcpSchemaVersion) { $mcpSchemaVersion } else { $null })
        stableReleaseVersion = $(if ($releaseVersion) { $releaseVersion } else { $null })
    }
}

function Show-VersionInformation {
    param([string[]]$Arguments)
    if ($Arguments.Count -gt 1 -or ($Arguments.Count -eq 1 -and $Arguments[0] -cne "--json")) {
        throw "usage: luthn version [--json]"
    }
    $information = Get-VersionInformation
    if ($Arguments.Count -eq 1) {
        $information | ConvertTo-Json -Compress
        return
    }
    Write-Host "Installed image: $($information.installedImageReference)"
    Write-Host "Image digest: $(if ($information.imageDigest) { $information.imageDigest } else { 'unavailable' })"
    Write-Host "Source revision: $(if ($information.sourceRevision) { $information.sourceRevision } else { 'unavailable' })"
    Write-Host "CLI template: $($information.cliTemplateVersion)"
    Write-Host "Connector template: $($information.connectorTemplateVersion)"
    Write-Host "MCP schema: $(if ($information.mcpSchemaVersion) { $information.mcpSchemaVersion } else { 'unavailable' })"
    Write-Host "Stable release: $(if ($information.stableReleaseVersion) { $information.stableReleaseVersion } else { 'unavailable' })"
}

function Get-RemoteImageMetadata {
    param([Parameter(Mandatory = $true)][string]$Image)
    $docker = Get-DockerTool
    $manifestResult = Invoke-ToolCapture -Tool $docker -Arguments @(
        "buildx", "imagetools", "inspect", $Image, "--format", "{{json .Manifest}}")
    Assert-ToolSuccess $manifestResult "remote image manifest inspection"
    $imageResult = Invoke-ToolCapture -Tool $docker -Arguments @(
        "buildx", "imagetools", "inspect", $Image, "--format", "{{json .Image}}")
    Assert-ToolSuccess $imageResult "remote image configuration inspection"
    $manifest = $manifestResult.StdOut | ConvertFrom-Json -AsHashtable
    $images = $imageResult.StdOut | ConvertFrom-Json -AsHashtable
    $firstPlatform = @($images.Keys | Sort-Object | Select-Object -First 1)[0]
    $labels = if ($firstPlatform) { $images[$firstPlatform]["config"]["Labels"] } else { @{} }
    return [ordered]@{
        digest = [string]$manifest["digest"]
        revision = [string]$labels["org.opencontainers.image.revision"]
        releaseVersion = [string]$labels["org.opencontainers.image.version"]
        cliTemplateVersion = [string]$labels["io.luthn.cli-template.version"]
        connectorTemplateVersion = [string]$labels["io.luthn.connector-template.version"]
        mcpSchemaVersion = [string]$labels["io.luthn.mcp-schema.version"]
    }
}

function Invoke-UpdateCheck {
    param([string[]]$Arguments)
    if ($Arguments.Count -gt 1 -or ($Arguments.Count -eq 1 -and $Arguments[0] -cne "--json")) {
        throw "usage: luthn update check [--json]"
    }
    Require-Installation
    Test-DockerPreflight
    $asJson = $Arguments.Count -eq 1
    $channel = Read-ConfigValue "LUTHN_IMAGE" $script:DefaultImage
    $currentId = Get-ApiImageId
    $currentDigest = if ($currentId) { Get-ImageDigest -Image $currentId } else { "" }
    $currentRevision = if ($currentId) { Get-ImageLabel -Image $currentId -Label "org.opencontainers.image.revision" } else { "" }
    $candidateDigest = ""
    $candidateRevision = ""
    $exitCode = 0
    if (Test-ImmutableImageReference $channel) {
        $status = "pinned"
        $message = "The configured image is immutable. Select an explicit mutable channel or target to update."
    } elseif (-not (Test-OfficialImageReference $channel)) {
        $status = "unavailable"
        $message = "Automatic checks are available only for the official Luthn image channel."
    } else {
        try {
            $candidate = Get-RemoteImageMetadata -Image $channel
            $candidateDigest = $candidate.digest
            $candidateRevision = $candidate.revision
            $identitiesMatch = if ($currentDigest -and $candidateDigest) {
                $currentDigest -ceq $candidateDigest
            } else {
                $currentRevision -and $currentRevision -ceq $candidateRevision
            }
            if ($identitiesMatch) {
                $status = "current"
                $message = "The installed runtime matches the configured update channel."
            } else {
                $status = "update-available"
                $message = "An update is available. Run 'luthn update' when you are ready."
            }
        } catch {
            $status = "error"
            $message = "The configured update channel could not be inspected. No local state was changed."
            $exitCode = 1
        }
    }
    $result = [ordered]@{
        status = $status
        channel = $channel
        currentDigest = $(if ($currentDigest) { $currentDigest } else { $null })
        candidateDigest = $(if ($candidateDigest) { $candidateDigest } else { $null })
        currentRevision = $(if ($currentRevision) { $currentRevision } else { $null })
        candidateRevision = $(if ($candidateRevision) { $candidateRevision } else { $null })
        message = $message
    }
    if ($asJson) {
        $result | ConvertTo-Json -Compress
    } else {
        Write-Host "Update status: $status"
        Write-Host "Channel: $channel"
        Write-Host "Current digest: $(if ($currentDigest) { $currentDigest } else { 'unavailable' })"
        Write-Host "Candidate digest: $(if ($candidateDigest) { $candidateDigest } else { 'unavailable' })"
        Write-Host "Current revision: $(if ($currentRevision) { $currentRevision } else { 'unavailable' })"
        Write-Host "Candidate revision: $(if ($candidateRevision) { $candidateRevision } else { 'unavailable' })"
        Write-Host $message
    }
    if ($exitCode -ne 0) { throw $message }
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
    param([string]$Image, [string]$Digest, [string]$OperatorDigest)
    $port = if ($env:LUTHN_PORT) { $env:LUTHN_PORT } else { "8080" }
    $postgresVolume = if ($env:LUTHN_POSTGRES_VOLUME) { $env:LUTHN_POSTGRES_VOLUME } else { "luthn-postgres" }
    $operatorVolume = if ($env:LUTHN_OPERATOR_VOLUME) { $env:LUTHN_OPERATOR_VOLUME } else { "luthn-operator" }
    $content = @(
        "LUTHN_IMAGE=$Image",
        "LUTHN_PORT=$port",
        "LUTHN_ENVIRONMENT=Production",
        "LUTHN_BASE_URL=http://127.0.0.1:$port",
        "LUTHN_POSTGRES_VOLUME=$postgresVolume",
        "LUTHN_OPERATOR_VOLUME=$operatorVolume",
        "LUTHN_SERVICE_TOKEN_FILE=$script:TokenFile",
        "LUTHN_OPERATOR_TOKEN_FILE=$script:OperatorTokenFile",
        "LUTHN_DOCKER_CONNECTION_STRING=Host=postgres;Port=5432;Database=luthn;Username=luthn",
        "POSTGRES_DB=luthn",
        "POSTGRES_USER=luthn",
        "POSTGRES_HOST_AUTH_METHOD=trust",
        "Luthn__Classification__Provider=mock",
        "Luthn__Classification__AllowMock=true",
        "Luthn__Auth__RequireServiceToken=true",
        "Luthn__Identity__Mode=SingleOwner",
        "Luthn__Identity__SingleOwnerUserId=local-owner",
        "Luthn__Auth__Tokens__0__Name=local-agent",
        "Luthn__Auth__Tokens__0__Sha256Digest=$Digest",
        "Luthn__Auth__Tokens__0__UserId=local-owner",
        "Luthn__Auth__Tokens__0__IsOperator=false",
        "Luthn__Auth__Tokens__0__Scopes__0=agent.read",
        "Luthn__Auth__Tokens__0__Scopes__1=agent.write.summary",
        "Luthn__Auth__Tokens__0__Scopes__2=memory.write",
        "Luthn__Auth__Tokens__0__Scopes__3=memory.read",
        "Luthn__Auth__Tokens__0__Scopes__4=classification.preview",
        "Luthn__Auth__Tokens__0__Scopes__5=agent.connection.read",
        "Luthn__Auth__Tokens__0__Scopes__6=agent.connection.write"
        "Luthn__Auth__Tokens__0__Scopes__7=access.request",
        "Luthn__Auth__Tokens__0__Scopes__8=metrics.write",
        "Luthn__Auth__Tokens__1__Name=local-operator",
        "Luthn__Auth__Tokens__1__Sha256Digest=$OperatorDigest",
        "Luthn__Auth__Tokens__1__IsOperator=true",
        "Luthn__Auth__Tokens__1__Scopes__0=access.decide",
        "Luthn__Auth__Tokens__1__Scopes__1=config.write"
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

function Assert-OperatorCredentialSlotAvailable {
    param([string]$Image)
    if (-not [IO.File]::Exists($script:ConfigFile)) { return }
    $slotOccupied = [IO.File]::ReadAllLines($script:ConfigFile) |
        Where-Object { $_ -cmatch '^Luthn__Auth__Tokens__1__' } |
        Select-Object -First 1
    if (-not $slotOccupied) { return }

    $configuredOperatorTokenFile = Read-ConfigValue "LUTHN_OPERATOR_TOKEN_FILE" $script:OperatorTokenFile
    if ((Read-ConfigValue "Luthn__Auth__Tokens__1__Name") -cne "local-operator" -or
        -not [IO.File]::Exists($configuredOperatorTokenFile)) {
        throw "token slot 1 is occupied and cannot be used for the local operator credential"
    }
    $operatorToken = [IO.File]::ReadAllText($configuredOperatorTokenFile).Trim()
    $operatorDigest = Get-TokenDigest -Image $Image -Token $operatorToken
    if ((Read-ConfigValue "Luthn__Auth__Tokens__1__Sha256Digest") -cne $operatorDigest) {
        throw "token slot 1 is occupied and cannot be used for the local operator credential"
    }
}

function Ensure-OperatorCredential {
    param([string]$Image)
    Assert-OperatorCredentialSlotAvailable -Image $Image
    $script:OperatorTokenFile = Read-ConfigValue "LUTHN_OPERATOR_TOKEN_FILE" $script:OperatorTokenFile
    if ([IO.File]::Exists($script:OperatorTokenFile)) {
        $operatorToken = [IO.File]::ReadAllText($script:OperatorTokenFile).Trim()
    } else {
        $operatorToken = New-ServiceToken
        Write-Utf8File -Path $script:OperatorTokenFile -Content $operatorToken
    }
    Protect-SecretFile $script:OperatorTokenFile
    Set-ConfigValue "LUTHN_OPERATOR_TOKEN_FILE" $script:OperatorTokenFile
    $operatorDigest = Get-TokenDigest -Image $Image -Token $operatorToken
    $operatorPrefix = "Luthn__Auth__Tokens__1__"
    Remove-ConfigPrefix $operatorPrefix
    Set-ConfigValue "${operatorPrefix}Name" "local-operator"
    Set-ConfigValue "${operatorPrefix}Sha256Digest" $operatorDigest
    Set-ConfigValue "${operatorPrefix}IsOperator" "true"
    Set-ConfigValue "${operatorPrefix}Scopes__0" "access.decide"
    Set-ConfigValue "${operatorPrefix}Scopes__1" "config.write"
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

function Get-HttpDiagnostic {
    param([string]$Url)
    if ($env:LUTHN_HTTP_CHECK_COMMAND) {
        $healthTool = Get-ToolSpec -Name "health-check" -OverrideVariable "LUTHN_HTTP_CHECK_COMMAND"
        $healthResult = Invoke-ToolCapture -Tool $healthTool -Arguments @($Url)
        return [pscustomobject]@{
            StatusCode = $(if ($healthResult.ExitCode -eq 0) { 200 } else { 503 })
            Body = $healthResult.StdOut
        }
    }
    try {
        $response = Invoke-WebRequest -Uri $Url -TimeoutSec 5 -SkipHttpErrorCheck
        return [pscustomobject]@{ StatusCode = [int]$response.StatusCode; Body = [string]$response.Content }
    } catch {
        return [pscustomobject]@{ StatusCode = 0; Body = "" }
    }
}

function Test-ClassificationSetupPending {
    param([int]$StatusCode, [string]$Body)
    return $StatusCode -eq 503 -and
        $Body -match '"dependency"\s*:\s*"classification-provider"' -and
        ($Body -match "No classification provider is configured" -or
         $Body -match "The mock classification provider is disabled" -or
         $Body -match "Production classification requires an operator-configured non-mock provider")
}

function Test-ClassificationSetupRequiredByConfig {
    $provider = Read-ConfigValue "Luthn__Classification__Provider" "mock"
    $allowMock = Read-ConfigValue "Luthn__Classification__AllowMock" "true"
    return $provider -ceq "unconfigured" -or
        ($provider -ceq "mock" -and $allowMock -ine "true")
}

function Wait-ForApi {
    $baseUrl = Read-ConfigValue "LUTHN_BASE_URL" "http://127.0.0.1:8080"
    for ($attempt = 0; $attempt -lt 60; $attempt++) {
        if (Test-HttpReady "$baseUrl/healthz") {
            if (Test-HttpReady "$baseUrl/readyz") { return }
            $readiness = Get-HttpDiagnostic "$baseUrl/readyz"
            if (($readiness.StatusCode -ge 200 -and $readiness.StatusCode -lt 300) -or
                (Test-ClassificationSetupPending -StatusCode $readiness.StatusCode -Body $readiness.Body)) { return }
        }
        Start-Sleep -Seconds 2
    }
    throw "Luthn did not reach a healthy or safely unconfigured state."
}

function Require-Installation {
    if (-not [IO.File]::Exists($script:ComposeFile) -or -not [IO.File]::Exists($script:ConfigFile)) {
        throw "Luthn is not installed. Run: luthn install"
    }
}

function Install-Luthn {
    param([string[]]$Arguments)
    $connectCodex = $false
    $connectClaude = $false
    foreach ($argument in $Arguments) {
        if ($argument -ceq "--connect-codex") { $connectCodex = $true }
        elseif ($argument -ceq "--connect-claude") { $connectClaude = $true }
        else { throw "usage: luthn install [--connect-codex|--connect-claude]" }
    }

    Test-DockerPreflight
    $docker = Get-DockerTool
    $image = Read-ConfigValue "LUTHN_IMAGE" $(if ($env:LUTHN_IMAGE) { $env:LUTHN_IMAGE } else { $script:DefaultImage })
    Write-Host "Pulling $image..."
    Invoke-ToolVisible -Tool $docker -Arguments @("pull", $image)
    Assert-OperatorCredentialSlotAvailable -Image $image

    $revisionResult = Invoke-ToolCapture -Tool $docker -Arguments @("image", "inspect", "--format", "{{ index .Config.Labels `"org.opencontainers.image.revision`" }}", $image)
    $revision = if ($revisionResult.ExitCode -eq 0) { $revisionResult.StdOut.Trim() } else { "" }
    Install-ComposeRuntime -RuntimeSource (Get-RuntimeSource -Image $image -Revision $revision)

    Ensure-Directories
    if (-not [IO.File]::Exists($script:ConfigFile)) {
        $token = New-ServiceToken
        $digest = Get-TokenDigest -Image $image -Token $token
        $operatorToken = New-ServiceToken
        $operatorDigest = Get-TokenDigest -Image $image -Token $operatorToken
        Write-Utf8File -Path $script:TokenFile -Content $token
        Protect-SecretFile $script:TokenFile
        Write-Utf8File -Path $script:OperatorTokenFile -Content $operatorToken
        Protect-SecretFile $script:OperatorTokenFile
        Write-InitialConfig -Image $image -Digest $digest -OperatorDigest $operatorDigest
    } else {
        Ensure-ConfigValue "LUTHN_IMAGE" $image
        Ensure-ConfigValue "LUTHN_PORT" $(if ($env:LUTHN_PORT) { $env:LUTHN_PORT } else { "8080" })
        Ensure-ConfigValue "LUTHN_ENVIRONMENT" "Production"
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
        Ensure-ConfigValue "Luthn__Classification__AllowMock" "true"
        Upgrade-LegacyClassificationDefault
        Ensure-ConfigValue "Luthn__Auth__RequireServiceToken" "true"
        Ensure-ConfigValue "Luthn__Identity__Mode" "SingleOwner"
        Ensure-ConfigValue "Luthn__Identity__SingleOwnerUserId" "local-owner"
        Ensure-ConfigValue "Luthn__Auth__Tokens__0__Name" "local-agent"
        Ensure-ConfigValue "Luthn__Auth__Tokens__0__UserId" "local-owner"
        Ensure-ConfigValue "Luthn__Auth__Tokens__0__IsOperator" "false"
        $scopes = @("agent.read", "agent.write.summary", "memory.write", "memory.read", "classification.preview", "agent.connection.read", "agent.connection.write")
        for ($index = 0; $index -lt $scopes.Count; $index++) {
            Ensure-ConfigValue "Luthn__Auth__Tokens__0__Scopes__$index" $scopes[$index]
        }
        $accessScopeRequired = $connectCodex -or $connectClaude -or
            [IO.File]::Exists($script:CodexStateFile) -or
            [IO.File]::Exists($script:CodexPendingStateFile) -or
            [IO.File]::Exists($script:ClaudeStateFile) -or
            [IO.File]::Exists($script:ClaudePendingStateFile)
        Ensure-ServiceTokenScope "access.request" -Required:$accessScopeRequired
        Ensure-ServiceTokenScope "metrics.write" -Required:$accessScopeRequired
    }
    Ensure-OperatorCredential -Image $image

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
    if (Test-ClassificationSetupRequiredByConfig) {
        Write-Host "Luthn is running."
        Write-Host "Classification: setup required in the operator console."
    } else {
        Write-Host "Luthn is ready."
    }
    Write-Host "Console: $baseUrl/"
    Write-Host "Status:  luthn status"
    Write-Host "Config:  $script:ConfigFile"
    Write-Host "Operator token: $script:OperatorTokenFile"
    Write-Host "Agent:   luthn connect codex"

    if ($connectCodex) { Connect-Codex }
    if ($connectClaude) { Connect-Claude }
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
    $runningRevision = ""
    $selectedImageId = ""
    $selectedRevision = ""
    if ($containerId) {
        $imageResult = Invoke-ToolCapture -Tool $docker -Arguments @("inspect", "--format", "{{.Image}}", $containerId)
        if ($imageResult.ExitCode -eq 0) { $imageId = $imageResult.StdOut.Trim() }
        if ($imageId) {
            $digestResult = Invoke-ToolCapture -Tool $docker -Arguments @("image", "inspect", "--format", "{{join .RepoDigests `", `"}}", $imageId)
            if ($digestResult.ExitCode -eq 0) { $digest = $digestResult.StdOut.Trim() }
            $revisionResult = Invoke-ToolCapture -Tool $docker -Arguments @("image", "inspect", "--format", "{{ index .Config.Labels `"org.opencontainers.image.revision`" }}", $imageId)
            if ($revisionResult.ExitCode -eq 0) { $runningRevision = $revisionResult.StdOut.Trim() }
        }
    }
    $selectedIdResult = Invoke-ToolCapture -Tool $docker -Arguments @("image", "inspect", "--format", "{{.Id}}", $imageRef)
    if ($selectedIdResult.ExitCode -eq 0) { $selectedImageId = $selectedIdResult.StdOut.Trim() }
    $selectedRevisionResult = Invoke-ToolCapture -Tool $docker -Arguments @("image", "inspect", "--format", "{{ index .Config.Labels `"org.opencontainers.image.revision`" }}", $imageRef)
    if ($selectedRevisionResult.ExitCode -eq 0) { $selectedRevision = $selectedRevisionResult.StdOut.Trim() }

    Write-Host "Luthn services:"
    Invoke-ComposeVisible @("ps")
    Write-Host ""
    Write-Host "Health: $(if (Test-HttpReady "$baseUrl/healthz") { 'ready' } else { 'unavailable' })"
    Write-Host "Readiness: $(if (Test-HttpReady "$baseUrl/readyz") { 'ready' } else { 'not ready' })"
    if (Test-ClassificationSetupRequiredByConfig) {
        Write-Host "Classification: setup required in the operator console"
    }
    Write-Host "Console: $baseUrl/"
    Write-Host "Image: $imageRef"
    Write-Host "Image ID: $(if ($imageId) { $imageId } else { 'unavailable' })"
    Write-Host "Digest: $(if ($digest) { $digest } else { 'unavailable' })"
    Write-Host "Running revision: $(if ($runningRevision) { $runningRevision } else { 'unavailable' })"
    Write-Host "Selected revision: $(if ($selectedRevision) { $selectedRevision } else { 'unavailable' })"
    if ($imageId -and $selectedImageId -and $imageId -cne $selectedImageId) {
        Write-Host "Runtime drift: a locally selected image is not running; run 'luthn update'."
    }
}

function Add-DoctorCheck {
    param(
        [AllowEmptyCollection()][Parameter(Mandatory = $true)][Collections.Generic.List[object]]$Checks,
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][ValidateSet("pass", "warn", "fail")][string]$Status,
        [Parameter(Mandatory = $true)][bool]$Required,
        [Parameter(Mandatory = $true)][string]$Message,
        [string]$Remediation = ""
    )
    $Checks.Add([ordered]@{
        name = $Name
        status = $Status
        required = $Required
        message = $Message
        remediation = $(if ($Remediation) { $Remediation } else { $null })
    })
}

function Invoke-Doctor {
    param([string[]]$Arguments)
    if ($Arguments.Count -gt 1 -or ($Arguments.Count -eq 1 -and $Arguments[0] -cne "--json")) {
        throw "usage: luthn doctor [--json]"
    }
    $asJson = $Arguments.Count -eq 1
    $checks = [Collections.Generic.List[object]]::new()
    try {
        $docker = Get-DockerTool
        Add-DoctorCheck $checks "docker" "pass" $true "Docker CLI is available."
        $compose = Invoke-ToolCapture -Tool $docker -Arguments @("compose", "version")
        if ($compose.ExitCode -eq 0) {
            Add-DoctorCheck $checks "compose" "pass" $true "Docker Compose is available."
        } else {
            Add-DoctorCheck $checks "compose" "fail" $true "Docker Compose is unavailable." "Install the Docker Compose plugin."
        }
        $daemon = Invoke-ToolCapture -Tool $docker -Arguments @("info", "--format", "{{.OSType}}")
        if ($daemon.ExitCode -eq 0 -and $daemon.StdOut.Trim() -ceq "linux") {
            Add-DoctorCheck $checks "docker-daemon" "pass" $true "Docker Linux engine is reachable."
        } else {
            Add-DoctorCheck $checks "docker-daemon" "fail" $true "Docker Linux engine is not reachable." "Start Docker Desktop and switch to Linux containers."
        }
    } catch {
        Add-DoctorCheck $checks "docker" "fail" $true "Docker CLI is unavailable." "Install Docker Desktop."
        Add-DoctorCheck $checks "compose" "fail" $true "Docker Compose could not be checked." "Install Docker with Compose."
        Add-DoctorCheck $checks "docker-daemon" "fail" $true "Docker daemon could not be checked." "Install and start Docker Desktop."
    }

    if ([IO.File]::Exists($script:ComposeFile) -and [IO.File]::Exists($script:ConfigFile) -and
        [IO.File]::Exists((Join-Path $script:BinDir "luthn.ps1"))) {
        Add-DoctorCheck $checks "installation" "pass" $true "Installed runtime files are present."
    } else {
        Add-DoctorCheck $checks "installation" "fail" $true "Installed runtime files are incomplete." "Run 'luthn install' to repair the installation."
    }

    $baseUrl = Read-ConfigValue "LUTHN_BASE_URL" "http://127.0.0.1:8080"
    if (Test-HttpReady "$baseUrl/healthz") {
        Add-DoctorCheck $checks "api-health" "pass" $true "API health check passed."
    } else {
        Add-DoctorCheck $checks "api-health" "fail" $true "API health check failed." "Run 'luthn status' and inspect API logs."
    }
    $readiness = Get-HttpDiagnostic "$baseUrl/readyz"
    if ($readiness.StatusCode -ge 200 -and $readiness.StatusCode -lt 300) {
        Add-DoctorCheck $checks "api-readiness" "pass" $true "API readiness check passed."
        Add-DoctorCheck $checks "migrations" "pass" $true "Database migrations are current."
    } elseif (Test-ClassificationSetupPending -StatusCode $readiness.StatusCode -Body $readiness.Body) {
        Add-DoctorCheck $checks "api-readiness" "fail" $true "Classification provider setup is required." "Open the operator console and configure a production provider."
        Add-DoctorCheck $checks "migrations" "pass" $true "Database migrations are current; classification setup is pending."
    } elseif ($readiness.Body -match "pending migrations") {
        Add-DoctorCheck $checks "api-readiness" "fail" $true "API is not ready because migrations are pending." "Run 'luthn update' to apply the target migrations."
        Add-DoctorCheck $checks "migrations" "fail" $true "Database has pending migrations." "Run 'luthn update'."
    } else {
        Add-DoctorCheck $checks "api-readiness" "fail" $true "API readiness check failed." "Run 'luthn status' and inspect API and migration logs."
        Add-DoctorCheck $checks "migrations" "warn" $false "Migration state could not be confirmed while readiness is unavailable." "Restore API readiness, then run 'luthn doctor' again."
    }

    $runningId = ""
    $selectedId = ""
    try {
        $runningId = Get-ApiImageId
        $selectedRef = Read-ConfigValue "LUTHN_IMAGE" $script:DefaultImage
        $selected = Invoke-ToolCapture -Tool (Get-DockerTool) -Arguments @("image", "inspect", "--format", "{{.Id}}", $selectedRef)
        if ($selected.ExitCode -eq 0) { $selectedId = $selected.StdOut.Trim() }
    } catch {}
    if ($runningId -and $selectedId -and $runningId -ceq $selectedId) {
        Add-DoctorCheck $checks "runtime-drift" "pass" $true "The selected image is running."
    } elseif ($runningId -and $selectedId) {
        Add-DoctorCheck $checks "runtime-drift" "fail" $true "The selected image is not running." "Run 'luthn update' to reconcile runtime drift."
    } else {
        Add-DoctorCheck $checks "runtime-drift" "warn" $false "Runtime image identity could not be confirmed." "Start Luthn and run the doctor again."
    }

    try {
        $updateResult = (Invoke-UpdateCheck @("--json")) | ConvertFrom-Json
        switch -CaseSensitive ([string]$updateResult.status) {
            "current" { Add-DoctorCheck $checks "update-check" "pass" $false "The configured channel is current." }
            "update-available" { Add-DoctorCheck $checks "update-check" "warn" $false "An update is available." "Run 'luthn update' when ready." }
            "pinned" { Add-DoctorCheck $checks "update-check" "warn" $false "The installation is pinned to an immutable image." "Choose an explicit target to update." }
            "unavailable" { Add-DoctorCheck $checks "update-check" "warn" $false "Automatic update checks are unavailable for this image channel." "Check the custom registry manually." }
            default { Add-DoctorCheck $checks "update-check" "warn" $false "Update status could not be determined." "Run 'luthn update check' for details." }
        }
    } catch {
        Add-DoctorCheck $checks "update-check" "warn" $false "The configured update channel could not be checked." "Run 'luthn update check' after network access is restored."
    }

    $connectorStatePath = if ([IO.File]::Exists($script:CodexStateFile)) { $script:CodexStateFile } elseif ([IO.File]::Exists($script:CodexPendingStateFile)) { $script:CodexPendingStateFile } else { "" }
    if ($connectorStatePath) {
        try {
            $state = [IO.File]::ReadAllText($connectorStatePath) | ConvertFrom-Json -AsHashtable
            if (Test-CodexHookInstalled -Path ([string]$state["hooksFile"]) -Command ([string]$state["hookCommand"])) {
                Add-DoctorCheck $checks "codex-hook" "pass" $true "Codex Stop hook is configured."
            } else {
                Add-DoctorCheck $checks "codex-hook" "fail" $true "Codex Stop hook is missing or changed." "Run 'luthn connect codex'."
            }
            if ([bool]$state["autoRecall"]) {
                if (Test-CodexAutoRecallInstalled -Path ([string]$state["instructionsFile"])) {
                    Add-DoctorCheck $checks "auto-recall" "pass" $true "Codex auto-recall instructions are configured."
                } else {
                    Add-DoctorCheck $checks "auto-recall" "fail" $true "Codex auto-recall instructions are missing or changed." "Run 'luthn connect codex'."
                }
            } else {
                Add-DoctorCheck $checks "auto-recall" "pass" $false "Codex auto-recall is intentionally disabled."
            }
            $doctorDocker = Get-DockerTool
            $doctorMcpArguments = @($doctorDocker.PrefixArguments) + @(Get-McpDockerArguments)
            $registration = Get-CodexRegistration (Get-CodexTool)
            if (Test-RegistrationMatches $registration $doctorDocker.FilePath $doctorMcpArguments) {
                Add-DoctorCheck $checks "codex-mcp" "pass" $true "Codex MCP registration matches Luthn."
            } else {
                Add-DoctorCheck $checks "codex-mcp" "fail" $true "Codex MCP registration is missing or changed." "Run 'luthn connect codex'."
            }
        } catch {
            Add-DoctorCheck $checks "codex-integration" "fail" $true "Codex integration state could not be verified." "Run 'luthn connect codex'."
        }
    } else {
        Add-DoctorCheck $checks "codex-integration" "warn" $false "Codex is not connected to Luthn." "Run 'luthn connect codex' when this host should use Luthn."
    }

    $failed = @($checks | Where-Object { $_["required"] -and $_["status"] -ceq "fail" }).Count -gt 0
    $result = [ordered]@{ status = $(if ($failed) { "failed" } else { "ready" }); checks = @($checks) }
    if ($asJson) {
        $result | ConvertTo-Json -Compress -Depth 5
    } else {
        Write-Host "Luthn doctor: $($result.status)"
        foreach ($check in $checks) {
            Write-Host ("{0,-18} {1,-5} {2}" -f $check["name"], $check["status"], $check["message"])
            if ($check["remediation"]) { Write-Host "  remediation: $($check['remediation'])" }
        }
    }
    if ($failed) { throw "One or more required Luthn doctor checks failed." }
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
    if ($Arguments.Count -eq 0 -and (Test-ImmutableImageReference $targetImage)) {
        throw "Update stopped because the configured image is immutable: $targetImage. Choose an explicit mutable channel or target: luthn update <image>"
    }
    $previousImageRef = Read-ConfigValue "LUTHN_IMAGE" $script:DefaultImage
    $previousImageId = Get-ApiImageId
    $previousRevision = ""
    $previousMcpSchema = ""
    if ($previousImageId) {
        $previousRevisionResult = Invoke-ToolCapture -Tool $docker -Arguments @("image", "inspect", "--format", "{{ index .Config.Labels `"org.opencontainers.image.revision`" }}", $previousImageId)
        if ($previousRevisionResult.ExitCode -eq 0) { $previousRevision = $previousRevisionResult.StdOut.Trim() }
        $previousMcpSchema = Get-ImageLabel -Image $previousImageId -Label "io.luthn.mcp-schema.version"
        if (-not $previousMcpSchema) { $previousMcpSchema = Get-McpSchemaVersionFromImage -Image $previousImageId }
    }

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
    $targetMcpSchema = Get-ImageLabel -Image $targetImageId -Label "io.luthn.mcp-schema.version"
    if (-not $targetMcpSchema) { $targetMcpSchema = Get-McpSchemaVersionFromImage -Image $targetImageId }
    Assert-OperatorCredentialSlotAvailable -Image $targetImage

    $installedCli = Join-Path $script:BinDir "luthn.ps1"
    $configuredOperatorTokenFile = Read-ConfigValue "LUTHN_OPERATOR_TOKEN_FILE" $script:OperatorTokenFile
    $runtimeSnapshots = @(
        Get-CodexFileSnapshot -Path $script:ComposeFile
        Get-CodexFileSnapshot -Path $installedCli
        Get-CodexFileSnapshot -Path $script:ConfigFile
        Get-CodexFileSnapshot -Path $configuredOperatorTokenFile
    )
    $connectorSnapshots = @()
    $connectorWasConfigured = $false
    $connectorAutoRecall = $true
    $previousConnectorVersion = ""
    $previousHelperDigest = ""
    $previousTemplateDigest = ""
    if ([IO.File]::Exists($script:CodexStateFile)) {
        try {
            $connectorState = [IO.File]::ReadAllText($script:CodexStateFile) | ConvertFrom-Json -AsHashtable
            $connectorWasConfigured = $connectorState.Contains("setupState") -and $connectorState["setupState"] -ceq "configured"
            if ($connectorState.Contains("autoRecall")) { $connectorAutoRecall = [bool]$connectorState["autoRecall"] }
            if ($connectorState.Contains("connectorVersion")) { $previousConnectorVersion = [string]$connectorState["connectorVersion"] }
            if ($connectorState.Contains("helperDigest")) { $previousHelperDigest = [string]$connectorState["helperDigest"] }
            if ($connectorState.Contains("templateDigest")) { $previousTemplateDigest = [string]$connectorState["templateDigest"] }
        } catch {
            $connectorWasConfigured = $false
        }
    }
    if ($connectorWasConfigured) {
        $connectorSnapshots = @(
            Get-CodexFileSnapshot -Path $script:CodexHooksFile
            Get-CodexFileSnapshot -Path $script:CodexInstructionsFile
            Get-CodexFileSnapshot -Path $script:CodexStateFile
            Get-CodexFileSnapshot -Path $script:CodexPendingStateFile
        )
    }

    $compatibilityChanged = $false
    Write-Host "Refreshing Windows CLI and Compose runtime..."
    try {
        Ensure-ServiceTokenScope "access.request" -Required:$connectorWasConfigured
        Ensure-ServiceTokenScope "metrics.write" -Required:$connectorWasConfigured
        Ensure-OperatorCredential -Image $targetImage
        Install-ComposeRuntime -RuntimeSource (Get-RuntimeSource -Image $targetImage -Revision $targetRevision) -IncludeCli
        $targetCliContent = [IO.File]::ReadAllText($installedCli)
        $targetHasManifest = $targetCliContent -match '"manifest"\s*\{'
        if ($targetHasManifest) {
            $manifestResult = Invoke-ToolCapture -Tool (New-ToolSpecFromPath -Path $installedCli) -Arguments @("manifest")
            if ($manifestResult.ExitCode -ne 0) { throw "downloaded Windows CLI manifest could not be read" }
            $targetManifest = $manifestResult.StdOut | ConvertFrom-Json -AsHashtable
            $targetConnectorVersion = [string]$targetManifest["version"]
            $targetHelperDigest = [string]$targetManifest["helperDigest"]
            $targetTemplateDigest = [string]$targetManifest["templateDigest"]
            if ($targetConnectorVersion -notmatch '^[1-9][0-9]*$' -or
                $targetHelperDigest -notmatch '^[0-9a-f]{64}$' -or
                $targetTemplateDigest -notmatch '^[0-9a-f]{64}$') {
                throw "downloaded Windows CLI manifest did not match the Luthn distribution contract"
            }
        } else {
            $targetConnectorVersion = Get-WindowsCliVersionFromFile -Path $installedCli
            $targetHelperDigest = ""
            $targetTemplateDigest = ""
        }
        if ($connectorWasConfigured) {
            $connectorChanged = $previousConnectorVersion -cne $targetConnectorVersion
            if ($targetHasManifest) {
                $connectorChanged = $connectorChanged -or
                    $previousHelperDigest -cne $targetHelperDigest -or
                    $previousTemplateDigest -cne $targetTemplateDigest
            }
            if ($previousMcpSchema -cne $targetMcpSchema) {
                $connectorChanged = $true
            }
            $compatibilityChanged = $connectorChanged
            $connectorAction = if ($connectorChanged) { "Reconciling" } else { "Validating" }
            Write-Host "$connectorAction Codex connector template version $targetConnectorVersion..."
            $connectArguments = @("connect", "codex")
            if (-not $connectorAutoRecall) { $connectArguments += "--no-auto-recall" }
            $reconcile = Invoke-ToolCapture -Tool (New-ToolSpecFromPath -Path $installedCli) -Arguments $connectArguments
            if ($reconcile.ExitCode -ne 0) {
                $detail = ($reconcile.StdErr + "`n" + $reconcile.StdOut).Trim()
                throw "Codex connector reconciliation failed: $detail"
            }
        }
    } catch {
        $restoreErrors = @()
        foreach ($snapshot in @($runtimeSnapshots) + @($connectorSnapshots)) {
            try { Restore-CodexFileSnapshot $snapshot } catch { $restoreErrors += $_.Exception.Message }
        }
        Write-UpdateState -Status "failed" -TargetImage $targetImage -TargetImageId $targetImageId -PreviousImageRef $previousImageRef -PreviousImageId $previousImageId
        $restoreDetail = if ($restoreErrors.Count -gt 0) { " Restore also failed: $($restoreErrors -join '; ')" } else { "" }
        throw "Update failed while refreshing the Windows lifecycle runtime. The running API and previous image were preserved. $($_.Exception.Message)$restoreDetail"
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
    Write-Host "Revision: $(if ($previousRevision) { $previousRevision } else { 'unavailable' }) -> $(if ($targetRevision) { $targetRevision } else { 'unavailable' })"
    if ($compatibilityChanged) {
        Write-Host "Restart required: Luthn MCP compatibility changed ($(if ($previousMcpSchema) { $previousMcpSchema } else { 'unknown' }) -> $(if ($targetMcpSchema) { $targetMcpSchema } else { $script:McpSchemaVersion }))."
        Write-Host "Agent notice: restart the current Codex host before invoking Luthn tools again."
    }
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
        $handlers[0]["statusMessage"] -ceq $script:CodexHookStatusMessage -and
        $handlers[0].Contains("timeout") -and
        [int]$handlers[0]["timeout"] -eq $script:CodexHookTimeoutSeconds -and
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
            timeout = $script:CodexHookTimeoutSeconds
            statusMessage = $script:CodexHookStatusMessage
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
    # Codex runs Windows hook command strings through PowerShell. A quoted
    # executable path is only a string expression there unless it is invoked
    # with the call operator.
    return "& `"$pwsh`" -NoProfile -NonInteractive -File `"$cli`" codex-hook"
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
    $managedTokenFiles = @(
        (Read-ConfigValue "LUTHN_SERVICE_TOKEN_FILE" $script:TokenFile),
        (Read-ConfigValue "LUTHN_OPERATOR_TOKEN_FILE" $script:OperatorTokenFile)
    )
    foreach ($managedTokenFile in $managedTokenFiles) {
        if ([IO.File]::Exists($managedTokenFile)) {
            $managedToken = [IO.File]::ReadAllText($managedTokenFile).Trim()
            if ($managedToken -and $summary.Contains($managedToken, [StringComparison]::Ordinal)) { return $null }
        }
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
        connectorVersion = $script:CodexConnectorTemplateVersion
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
    # Keep the upload in the Stop hook process. Codex may tear down detached
    # descendants after the hook command exits, which made the previous
    # fire-and-forget PowerShell uploader lose otherwise valid capsules on
    # Windows. Delivery remains bounded by the Windows hook's timeout.
    Invoke-CodexHookUploadPayload $PayloadJson
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
    $manifest = Get-CurrentWindowsCliManifest
    $content = [ordered]@{
        version = 2
        connectorVersion = $script:CodexConnectorTemplateVersion
        helperDigest = $manifest.helperDigest
        templateDigest = $manifest.templateDigest
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

function Get-ClaudeTool {
    return Get-ToolSpec -Name "claude" -OverrideVariable "LUTHN_CLAUDE_COMMAND"
}

function Get-ClaudeSettingsDocument {
    if (-not [IO.File]::Exists($script:ClaudeSettingsFile)) { return [ordered]@{} }
    $content = Read-CodexManagedText -Path $script:ClaudeSettingsFile
    try { $document = $content | ConvertFrom-Json -AsHashtable } catch { throw "Claude settings are not valid JSON: $($script:ClaudeSettingsFile)" }
    if ($null -eq $document -or $document -isnot [Collections.IDictionary]) { throw "Claude settings must be a JSON object." }
    if ($document.Contains("hooks") -and $document["hooks"] -isnot [Collections.IDictionary]) { throw "Claude settings hooks must be an object." }
    return $document
}

function Get-ClaudeStopGroups {
    param([Collections.IDictionary]$Document, [switch]$Create)
    if (-not $Document.Contains("hooks")) { if (-not $Create) { return $null }; $Document["hooks"] = [ordered]@{} }
    $hooks = $Document["hooks"]
    if (-not $hooks.Contains("Stop")) { if (-not $Create) { return $null }; $hooks["Stop"] = @() }
    if ($hooks["Stop"] -isnot [System.Collections.IEnumerable]) { throw "Claude settings hooks.Stop must be an array." }
    return @($hooks["Stop"])
}

function Get-ClaudeHookArguments { return @("-NoProfile", "-File", $script:BinDir + "\\luthn.ps1", "claude-hook") }

function Test-ClaudeHookInstalled {
    param([string]$Path = $script:ClaudeSettingsFile, [string]$Command = (Get-PwshPath), [string[]]$Arguments = (Get-ClaudeHookArguments))
    if (-not [IO.File]::Exists((Get-CodexManagedTarget $Path))) { return $false }
    $originalPath = $script:ClaudeSettingsFile
    $script:ClaudeSettingsFile = $Path
    try { $groups = @(Get-ClaudeStopGroups (Get-ClaudeSettingsDocument)) } finally { $script:ClaudeSettingsFile = $originalPath }
    $matches = @($groups | Where-Object { $_ -is [Collections.IDictionary] -and $_["matcher"] -ceq $script:ClaudeHookMarker })
    if ($matches.Count -ne 1) { return $false }
    $handlers = @($matches[0]["hooks"])
    return $handlers.Count -eq 1 -and $handlers[0]["type"] -ceq "command" -and $handlers[0]["command"] -ceq $Command -and (Test-StringArrayEqual @($handlers[0]["args"]) $Arguments)
}

function Install-ClaudeHook {
    param([string]$Path = $script:ClaudeSettingsFile)
    $originalPath = $script:ClaudeSettingsFile
    $script:ClaudeSettingsFile = $Path
    try { $document = Get-ClaudeSettingsDocument } finally { $script:ClaudeSettingsFile = $originalPath }
    $groups = @(Get-ClaudeStopGroups $document -Create)
    $remaining = @($groups | Where-Object { $_ -isnot [Collections.IDictionary] -or $_["matcher"] -cne $script:ClaudeHookMarker })
    $remaining += [ordered]@{ matcher = $script:ClaudeHookMarker; hooks = @([ordered]@{ type = "command"; command = Get-PwshPath; args = Get-ClaudeHookArguments; timeout = 10; statusMessage = $script:ClaudeHookStatusMessage }) }
    $document["hooks"]["Stop"] = $remaining
    Write-CodexManagedText -Path $Path -Content (($document | ConvertTo-Json -Depth 20) + "`n")
}

function Remove-ClaudeHook {
    param([string]$Path = $script:ClaudeSettingsFile)
    if (-not [IO.File]::Exists((Get-CodexManagedTarget $Path))) { return }
    $originalPath = $script:ClaudeSettingsFile
    $script:ClaudeSettingsFile = $Path
    try { $document = Get-ClaudeSettingsDocument } finally { $script:ClaudeSettingsFile = $originalPath }
    $groups = Get-ClaudeStopGroups $document
    if ($null -eq $groups) { return }
    $document["hooks"]["Stop"] = @($groups | Where-Object { $_ -isnot [Collections.IDictionary] -or $_["matcher"] -cne $script:ClaudeHookMarker })
    Write-CodexManagedText -Path $Path -Content (($document | ConvertTo-Json -Depth 20) + "`n")
}

function Test-ClaudeAutoRecallInstalled {
    param([string]$Path = $script:ClaudeInstructionsFile)
    if (-not [IO.File]::Exists((Get-CodexManagedTarget $Path))) { return $false }
    return (Read-CodexManagedText $Path).Contains($script:ClaudeAutoRecallInstruction)
}

function Set-ClaudeAutoRecall([bool]$Enabled, [string]$Path = $script:ClaudeInstructionsFile) {
    $target = Get-CodexManagedTarget $Path
    if (-not $Enabled -and -not [IO.File]::Exists($target)) { return }
    $content = Read-CodexManagedText $Path
    $clean = Get-ContentWithoutAutoRecall $content
    if ($Enabled) { $clean = (($clean.TrimEnd()) + $(if ($clean.Trim()) { "`n`n" } else { "" }) + $script:ClaudeAutoRecallInstruction + "`n") }
    if (-not $Enabled -and -not $clean) { [IO.File]::Delete($target) } else { Write-CodexManagedText -Path $Path -Content $clean }
}

function New-ClaudeTurnCapsule([string]$HookJson) {
    $input = $HookJson | ConvertFrom-Json -AsHashtable
    if ($input["hook_event_name"] -cne "Stop") { throw "expected Claude Stop hook input" }
    $session = [string]$input["session_id"]; $message = $input["last_assistant_message"]
    if (-not $session.Trim() -or $message -isnot [string] -or -not $message.Trim() -or (Test-CodexMessageContainsCredentials $message)) { return $null }
    $summary = $message.Trim(); if ($summary.Length -gt $script:MaxTurnCapsuleCharacters) { $summary = $summary.Substring(0, $script:MaxTurnCapsuleCharacters).TrimEnd() }
    foreach ($managedTokenFile in @((Read-ConfigValue "LUTHN_SERVICE_TOKEN_FILE" $script:TokenFile), (Read-ConfigValue "LUTHN_OPERATOR_TOKEN_FILE" $script:OperatorTokenFile))) {
        if ([IO.File]::Exists($managedTokenFile)) {
            $managedToken = [IO.File]::ReadAllText($managedTokenFile).Trim()
            if ($managedToken -and $summary.Contains($managedToken, [StringComparison]::Ordinal)) { return $null }
        }
    }
    $turn = [string]$input["prompt_id"]; if (-not $turn.Trim()) { $turn = Get-StableCodexId "claude-message" $summary }
    $summaryHash = [Security.Cryptography.SHA256]::HashData($script:Utf8NoBom.GetBytes($summary))
    return [ordered]@{ sessionId = Get-StableCodexId "claude-session" $session; turnId = Get-StableCodexId "claude-turn" $turn; sourceAgent = "claude-code"; summary = $summary; coreTags = @("claude-code", "conversation"); contentDigest = "sha256:$([Convert]::ToHexString($summaryHash).ToLowerInvariant())"; idempotencyKey = Get-StableCodexId "claude-capsule" "$session`:$turn"; title = "Claude Code turn capsule"; provenance = [ordered]@{ agentId = "claude-code"; applicationId = "claude-code"; connectorId = "luthn-claude-code-connector"; connectorVersion = $script:CodexConnectorTemplateVersion } }
}

function Send-ClaudeObservation {
    param([Parameter(Mandatory = $true)][object[]]$Channels)
    try {
        $credentials = Get-CodexApiCredentials
        [void](Invoke-LuthnApiRequest "POST" "$($credentials.BaseUrl)/api/agent-connections/claude-code/observations" $credentials.Token ([ordered]@{ agentName = "Claude Code"; integrationKind = "host-hook-mcp"; connectorVersion = $script:CodexConnectorTemplateVersion; channels = $Channels }))
    } catch {}
}

function Run-ClaudeHook {
    try { $capsule = New-ClaudeTurnCapsule (Read-BoundedStandardInput $script:MaxCodexHookInputBytes); if ($null -ne $capsule) { $credentials = Get-CodexApiCredentials; [void](Invoke-LuthnApiRequest "POST" "$($credentials.BaseUrl)/api/agent/turn-summaries" $credentials.Token $capsule); Send-ClaudeObservation @([ordered]@{ channel = "automatic-ingestion"; configured = $true; verificationState = "Verified"; activityState = "Succeeded"; failureCode = $null }) } } catch { Send-ClaudeObservation @([ordered]@{ channel = "automatic-ingestion"; configured = $true; verificationState = "Verified"; activityState = "Failed"; failureCode = "delivery.failed" }) }
}

function Get-ClaudeRegistrationSnapshot($Claude) {
    $result = Invoke-ToolCapture -Tool $Claude -Arguments @("mcp", "get", "luthn")
    if ($result.ExitCode -ne 0) { return $null }
    $normalized = ($result.StdOut + $result.StdErr).Trim()
    return [ordered]@{ output = $normalized; digest = Get-Sha256Hex ($script:Utf8NoBom.GetBytes($normalized)) }
}

function Write-ClaudeConnectorState {
    param([string]$Path, [string]$SetupState, [string]$SettingsFile, [string]$InstructionsFile, [bool]$AutoRecall, [bool]$McpOwned, [string]$McpDigest, [bool]$SettingsExisted, [bool]$InstructionsExisted)
    Ensure-Directories
    $state = [ordered]@{ version = 2; agentId = "claude-code"; setupState = $SetupState; settingsFile = $SettingsFile; instructionsFile = $InstructionsFile; hookInstalled = $true; autoRecall = $AutoRecall; mcpOwned = $McpOwned; mcpDigest = $McpDigest; settingsExistedBeforeConnect = $SettingsExisted; instructionsExistedBeforeConnect = $InstructionsExisted; updatedAt = [DateTimeOffset]::UtcNow.ToString("O") }
    Write-Utf8File -Path $Path -Content (($state | ConvertTo-Json -Depth 5 -Compress) + "`n")
    Protect-SecretFile $Path
}

function Ensure-ClaudeConnectorScopes {
    $before = [IO.File]::ReadAllText($script:ConfigFile)
    try {
        foreach ($scope in @("agent.connection.read", "agent.connection.write", "access.request", "metrics.write")) { Ensure-ServiceTokenScope $scope -Required }
        $after = [IO.File]::ReadAllText($script:ConfigFile)
        if ($after -ceq $before) { return [pscustomobject]@{ Changed = $false; Content = $before } }
        Invoke-ComposeVisible @("up", "-d", "--force-recreate", "api")
        Wait-ForApi
    } catch {
        if ([IO.File]::ReadAllText($script:ConfigFile) -cne $before) {
            Write-Utf8File -Path $script:ConfigFile -Content $before
            try { Invoke-ComposeVisible @("up", "-d", "--force-recreate", "api"); Wait-ForApi } catch {}
        }
        throw
    }
    return [pscustomobject]@{ Changed = $true; Content = $before }
}

function Restore-ClaudeConnectorScopes($Snapshot) {
    if (-not $Snapshot -or -not $Snapshot.Changed) { return }
    Write-Utf8File -Path $script:ConfigFile -Content $Snapshot.Content
    Invoke-ComposeVisible @("up", "-d", "--force-recreate", "api")
    Wait-ForApi
}

function Connect-Claude {
    param([string[]]$Arguments = @())
    $autoRecall = $true
    if ($Arguments.Count -gt 1 -or ($Arguments.Count -eq 1 -and $Arguments[0] -notin @("--auto-recall", "--no-auto-recall"))) { throw "usage: luthn connect claude [--no-auto-recall]" }
    if ($Arguments -contains "--no-auto-recall") { $autoRecall = $false }
    Require-Installation; Test-DockerPreflight
    $claude = Get-ClaudeTool; $docker = Get-DockerTool; $mcpArgs = @($docker.PrefixArguments) + @(Get-McpDockerArguments)
    $previousState = $null
    $previousStatePath = if ([IO.File]::Exists($script:ClaudeStateFile)) { $script:ClaudeStateFile } elseif ([IO.File]::Exists($script:ClaudePendingStateFile)) { $script:ClaudePendingStateFile } else { $null }
    if ($previousStatePath) { try { $previousState = [IO.File]::ReadAllText($previousStatePath) | ConvertFrom-Json -AsHashtable } catch {} }
    $existing = Get-ClaudeRegistrationSnapshot $claude
    if ($existing -and -not $previousState) { throw "Claude Code already has an unrelated MCP registration named 'luthn'; no configuration was changed." }
    if ($existing -and $previousState -and $previousState["mcpDigest"] -and $existing.digest -cne $previousState["mcpDigest"]) { throw "Claude Code's 'luthn' MCP registration changed after setup and was preserved." }
    $settingsFile = if ($previousState -and $previousState["settingsFile"]) { [string]$previousState["settingsFile"] } else { $script:ClaudeSettingsFile }
    $instructionsFile = if ($previousState -and $previousState["instructionsFile"]) { [string]$previousState["instructionsFile"] } else { $script:ClaudeInstructionsFile }
    $settingsSnapshot = Get-CodexFileSnapshot $settingsFile
    $instructionsSnapshot = Get-CodexFileSnapshot $instructionsFile
    $scopeSnapshot = $null
    $added = $false; $rollbackErrors = [Collections.Generic.List[string]]::new()
    try {
        Write-ClaudeConnectorState $script:ClaudePendingStateFile "pending" $settingsFile $instructionsFile $autoRecall $true $(if ($existing) { $existing.digest } else { "" }) $settingsSnapshot.Existed $instructionsSnapshot.Existed
        $scopeSnapshot = Ensure-ClaudeConnectorScopes
        if (-not $existing) {
            Assert-ToolSuccess (Invoke-ToolCapture -Tool $claude -Arguments (@("mcp", "add", "--scope", "user", "luthn", "--", $docker.FilePath) + $mcpArgs)) "Claude MCP registration"
            $added = $true; $existing = Get-ClaudeRegistrationSnapshot $claude
            if (-not $existing) { throw "Claude MCP registration could not be verified." }
        }
        if (-not (Test-McpProbe)) { throw "Claude MCP probe failed." }
        Install-ClaudeHook -Path $settingsFile
        Set-ClaudeAutoRecall $autoRecall $instructionsFile
        Write-ClaudeConnectorState $script:ClaudeStateFile "configured" $settingsFile $instructionsFile $autoRecall $true $existing.digest $settingsSnapshot.Existed $instructionsSnapshot.Existed
        [IO.File]::Delete($script:ClaudePendingStateFile)
    } catch {
        $originalError = $_.Exception.Message
        try { Restore-CodexFileSnapshot $settingsSnapshot; Restore-CodexFileSnapshot $instructionsSnapshot } catch { $rollbackErrors.Add("Claude file rollback failed: $($_.Exception.Message)") }
        if ($added) { try { Assert-ToolSuccess (Invoke-ToolCapture -Tool $claude -Arguments @("mcp", "remove", "luthn")) "Claude MCP rollback" } catch { $rollbackErrors.Add($_.Exception.Message) } }
        try { Restore-ClaudeConnectorScopes $scopeSnapshot } catch { $rollbackErrors.Add("scope rollback failed: $($_.Exception.Message)") }
        if ($rollbackErrors.Count -eq 0) { [IO.File]::Delete($script:ClaudePendingStateFile) }
        throw "$originalError$($(if ($rollbackErrors.Count) { ' ' + ($rollbackErrors -join '; ') + '; ownership state was preserved.' } else { '' }))"
    }
    Send-ClaudeObservation @([ordered]@{ channel = "automatic-ingestion"; configured = $true; verificationState = "Unknown"; activityState = "Unknown"; failureCode = $null }, [ordered]@{ channel = "mcp"; configured = $true; verificationState = "Verified"; activityState = "Succeeded"; failureCode = $null })
    Write-Host "Claude Code connector is configured. Restart Claude Code, complete a turn, then run: luthn connection status claude"
}

function Show-ClaudeConnectionStatus {
    $statePath = if ([IO.File]::Exists($script:ClaudeStateFile)) { $script:ClaudeStateFile } elseif ([IO.File]::Exists($script:ClaudePendingStateFile)) { $script:ClaudePendingStateFile } else { $null }
    $state = if ($statePath) { try { [IO.File]::ReadAllText($statePath) | ConvertFrom-Json -AsHashtable } catch { $null } } else { $null }
    $settingsFile = if ($state -and $state["settingsFile"]) { [string]$state["settingsFile"] } else { $script:ClaudeSettingsFile }
    $instructionsFile = if ($state -and $state["instructionsFile"]) { [string]$state["instructionsFile"] } else { $script:ClaudeInstructionsFile }
    Write-Host "Local connector: $(if ($state) { [string]$state['setupState'] } else { 'not configured' })"
    Write-Host "  automatic-ingestion: $(if (Test-ClaudeHookInstalled -Path $settingsFile) { 'configured' } else { 'missing' })"
    $mcpState = "missing"
    try { $registration = Get-ClaudeRegistrationSnapshot (Get-ClaudeTool); if ($registration -and $state -and (-not $state["mcpDigest"] -or $registration.digest -ceq $state["mcpDigest"])) { $mcpState = "configured" } elseif ($registration) { $mcpState = "changed" } } catch {}
    Write-Host "  mcp: $mcpState"
    Write-Host "  lightweight-recall: $(if (Test-ClaudeAutoRecallInstalled -Path $instructionsFile) { 'enabled' } else { 'disabled' })"
    try { $credentials = Get-CodexApiCredentials; $response = Invoke-LuthnApiRequest "GET" "$($credentials.BaseUrl)/api/agent-connections" $credentials.Token; $connection = @($response["connections"] | Where-Object { $_["agentId"] -ceq "claude-code" }) | Select-Object -First 1; if (-not $connection) { Write-Host "Server observation: unknown"; return }; Write-Host "Server observation: $($connection['state'])"; foreach ($channel in @($connection["channels"])) { Write-Host "  $($channel['channel']): $($channel['state'])" } } catch { Write-Host "Server observation: unavailable" }
}

function Disconnect-Claude {
    $statePath = if ([IO.File]::Exists($script:ClaudeStateFile)) { $script:ClaudeStateFile } elseif ([IO.File]::Exists($script:ClaudePendingStateFile)) { $script:ClaudePendingStateFile } else { $null }
    if (-not $statePath) { Write-Host "No Luthn-owned Claude Code configuration was recorded."; return }
    $state = [IO.File]::ReadAllText($statePath) | ConvertFrom-Json -AsHashtable
    $settingsFile = if ($state["settingsFile"]) { [string]$state["settingsFile"] } else { $script:ClaudeSettingsFile }
    $instructionsFile = if ($state["instructionsFile"]) { [string]$state["instructionsFile"] } else { $script:ClaudeInstructionsFile }
    $settingsSnapshot = Get-CodexFileSnapshot $settingsFile; $instructionsSnapshot = Get-CodexFileSnapshot $instructionsFile
    $claude = Get-ClaudeTool; $registration = Get-ClaudeRegistrationSnapshot $claude
    if ($registration -and $state["mcpDigest"] -and $registration.digest -cne $state["mcpDigest"]) { throw "Claude Code's 'luthn' MCP registration changed after setup and was preserved." }
    try {
        Remove-ClaudeHook -Path $settingsFile
        Set-ClaudeAutoRecall $false $instructionsFile
        if ($registration -and $state["mcpOwned"] -ne $false) { Assert-ToolSuccess (Invoke-ToolCapture -Tool $claude -Arguments @("mcp", "remove", "luthn")) "Claude MCP cleanup" }
    } catch {
        $originalError = $_.Exception.Message
        try { Restore-CodexFileSnapshot $settingsSnapshot; Restore-CodexFileSnapshot $instructionsSnapshot } catch { throw "$originalError Claude configuration rollback also failed: $($_.Exception.Message)" }
        throw $originalError
    }
    foreach ($path in @($script:ClaudeStateFile, $script:ClaudePendingStateFile)) { if ([IO.File]::Exists($path)) { [IO.File]::Delete($path) } }
    Send-ClaudeObservation @([ordered]@{ channel = "automatic-ingestion"; configured = $false; verificationState = "Unknown"; activityState = "Unknown"; failureCode = $null }, [ordered]@{ channel = "mcp"; configured = $false; verificationState = "Unknown"; activityState = "Unknown"; failureCode = $null })
    Write-Host "Luthn-owned Claude Code connector configuration was removed."
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
    if ([IO.File]::Exists($script:ClaudeStateFile) -or [IO.File]::Exists($script:ClaudePendingStateFile)) {
        try {
            Disconnect-Claude
        } catch {
            throw "Uninstall stopped because Claude Code connector cleanup did not complete. $($_.Exception.Message)"
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
        "version" { Show-VersionInformation $CommandArguments }
        "manifest" { Get-CurrentWindowsCliManifest | ConvertTo-Json -Compress }
        "install" { Install-Luthn $CommandArguments }
        "status" { Show-Status }
        "update" {
            if ($CommandArguments.Count -ge 1 -and $CommandArguments[0] -ceq "check") {
                Invoke-UpdateCheck @($CommandArguments | Select-Object -Skip 1)
            } else {
                Update-Luthn $CommandArguments
            }
        }
        "doctor" { Invoke-Doctor $CommandArguments }
        "connect" {
            if ($CommandArguments.Count -lt 1) { throw "usage: luthn connect codex|claude [--no-auto-recall]" }
            if ($CommandArguments[0] -ceq "codex") { Connect-Codex @($CommandArguments | Select-Object -Skip 1) }
            elseif ($CommandArguments[0] -ceq "claude") { Connect-Claude @($CommandArguments | Select-Object -Skip 1) }
            else { throw "usage: luthn connect codex|claude [--no-auto-recall]" }
        }
        "connection" {
            if ($CommandArguments.Count -ne 2 -or $CommandArguments[0] -cne "status") { throw "usage: luthn connection status codex|claude" }
            if ($CommandArguments[1] -ceq "codex") { Show-CodexConnectionStatus }
            elseif ($CommandArguments[1] -ceq "claude") { Show-ClaudeConnectionStatus }
            else { throw "usage: luthn connection status codex|claude" }
        }
        "disconnect" {
            if ($CommandArguments.Count -ne 1) { throw "usage: luthn disconnect codex|claude" }
            if ($CommandArguments[0] -ceq "codex") { Disconnect-Codex }
            elseif ($CommandArguments[0] -ceq "claude") { Disconnect-Claude }
            else { throw "usage: luthn disconnect codex|claude" }
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
        "claude-hook" {
            if ($CommandArguments.Count -ne 0) { throw "usage: luthn claude-hook" }
            Run-ClaudeHook
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
