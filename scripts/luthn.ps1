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
$script:UpdateStateFile = if ($env:LUTHN_UPDATE_STATE_FILE) { $env:LUTHN_UPDATE_STATE_FILE } else { Join-Path $script:StateDir "update-windows.json" }
$script:DistributionRef = if ($env:LUTHN_DISTRIBUTION_REF) { $env:LUTHN_DISTRIBUTION_REF } else { "main" }
$script:SourceBaseUrl = if ($env:LUTHN_SOURCE_BASE_URL) { $env:LUTHN_SOURCE_BASE_URL.TrimEnd("/") } else { "https://raw.githubusercontent.com/JakobSung/Luthn/$($script:DistributionRef)" }
$script:DefaultImage = "ghcr.io/jakobsung/luthn:main"
$script:Utf8NoBom = [System.Text.UTF8Encoding]::new($false)

function Show-Usage {
    @"
usage: luthn <command> [options]

commands:
  install [--connect-codex]  Install Luthn and optionally register Codex MCP.
  status                     Show services, readiness, console, and image.
  update [image]             Back up, pull, migrate, restart, and verify.
  connect codex              Register the Docker-backed Codex MCP server.
  disconnect codex           Remove only a Luthn-owned Codex MCP registration.
  mcp [--list-tools]         Run the Docker-backed MCP stdio server.
  uninstall                  Remove services and runtime; preserve data/config.
  help                       Show this help.

Windows reset, purge uninstall, and the automatic Codex hook are not available
in this release.
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
    param([string]$Path, [string]$State, [string]$CommandPath, [string[]]$Arguments)
    Ensure-Directories
    $content = [ordered]@{
        version = 1
        integration = "windows-docker-mcp"
        setupState = $State
        mcpName = "luthn"
        command = $CommandPath
        arguments = $Arguments
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
    Require-Installation
    Test-DockerPreflight
    $docker = Get-DockerTool
    $codex = Get-CodexTool
    $mcpArguments = @($docker.PrefixArguments) + @(Get-McpDockerArguments)
    $existing = Get-CodexRegistration $codex

    if ($existing) {
        if (-not (Test-RegistrationMatches $existing $docker.FilePath $mcpArguments)) {
            throw "Codex already has an unrelated MCP registration named 'luthn'; no configuration was changed."
        }
        if (-not (Test-McpProbe)) { throw "Codex MCP probe failed; the existing registration was preserved." }
        Write-ConnectorState -Path $script:CodexStateFile -State "configured" -CommandPath $docker.FilePath -Arguments $mcpArguments
        if ([IO.File]::Exists($script:CodexPendingStateFile)) { [IO.File]::Delete($script:CodexPendingStateFile) }
        Write-Host "Codex MCP is already configured for the Windows Docker runtime."
        return
    }

    Write-ConnectorState -Path $script:CodexPendingStateFile -State "pending" -CommandPath $docker.FilePath -Arguments $mcpArguments
    $added = $false
    try {
        $addResult = Invoke-ToolCapture -Tool $codex -Arguments (@("mcp", "add", "luthn", "--", $docker.FilePath) + $mcpArguments)
        Assert-ToolSuccess $addResult "Codex MCP registration"
        $added = $true
        if (-not (Test-McpProbe)) { throw "Codex MCP probe failed." }
        Write-ConnectorState -Path $script:CodexStateFile -State "configured" -CommandPath $docker.FilePath -Arguments $mcpArguments
        [IO.File]::Delete($script:CodexPendingStateFile)
    } catch {
        if ($added) {
            $removeResult = Invoke-ToolCapture -Tool $codex -Arguments @("mcp", "remove", "luthn")
            if ($removeResult.ExitCode -eq 0) {
                [IO.File]::Delete($script:CodexPendingStateFile)
            } else {
                Write-ConnectorState -Path $script:CodexPendingStateFile -State "cleanup-required" -CommandPath $docker.FilePath -Arguments $mcpArguments
                throw "$($_.Exception.Message) Codex cleanup also failed; ownership state was preserved."
            }
        } else {
            [IO.File]::Delete($script:CodexPendingStateFile)
        }
        throw
    }

    Write-Host "Codex MCP is configured for Luthn."
    Write-Host "Restart Codex, then use /mcp to verify the luthn server."
    Write-Host "The Windows automatic turn-capsule hook is not installed in this release."
}

function Disconnect-Codex {
    $statePath = if ([IO.File]::Exists($script:CodexStateFile)) { $script:CodexStateFile } elseif ([IO.File]::Exists($script:CodexPendingStateFile)) { $script:CodexPendingStateFile } else { $null }
    if (-not $statePath) {
        Write-Host "No Luthn-owned Windows Codex MCP registration was recorded."
        return
    }

    $state = [IO.File]::ReadAllText($statePath) | ConvertFrom-Json
    $codex = Get-CodexTool
    $existing = Get-CodexRegistration $codex
    if ($existing) {
        if (-not (Test-RegistrationMatches $existing ([string]$state.command) @($state.arguments))) {
            throw "The 'luthn' MCP registration changed after setup and was preserved."
        }
        $removeResult = Invoke-ToolCapture -Tool $codex -Arguments @("mcp", "remove", "luthn")
        Assert-ToolSuccess $removeResult "Codex MCP cleanup"
    }
    foreach ($path in @($script:CodexStateFile, $script:CodexPendingStateFile)) {
        if ([IO.File]::Exists($path)) { [IO.File]::Delete($path) }
    }
    Write-Host "Luthn-owned Codex MCP configuration was removed."
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
            throw "Uninstall stopped because Codex MCP cleanup did not complete. $($_.Exception.Message)"
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
            if ($CommandArguments.Count -ne 1 -or $CommandArguments[0] -cne "codex") { throw "usage: luthn connect codex" }
            Connect-Codex
        }
        "disconnect" {
            if ($CommandArguments.Count -ne 1 -or $CommandArguments[0] -cne "codex") { throw "usage: luthn disconnect codex" }
            Disconnect-Codex
        }
        "mcp" { Run-Mcp $CommandArguments }
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
