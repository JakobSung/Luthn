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
  connect codex              Register the Docker-backed Codex MCP server.
  disconnect codex           Remove only a Luthn-owned Codex MCP registration.
  mcp [--list-tools]         Run the Docker-backed MCP stdio server.
  uninstall                  Remove services and runtime; preserve data/config.
  help                       Show this help.

Windows update, reset, purge uninstall, and the automatic Codex hook are not
available in this release.
"@
}

function Get-ToolSpec {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$OverrideVariable
    )

    $override = [Environment]::GetEnvironmentVariable($OverrideVariable)
    if ($override) {
        $resolvedOverride = (Resolve-Path -LiteralPath $override).Path
        if ([IO.Path]::GetExtension($resolvedOverride) -ieq ".ps1") {
            $pwsh = @(Get-Command pwsh -CommandType Application -ErrorAction Stop)[0].Source
            return [pscustomobject]@{ FilePath = $pwsh; PrefixArguments = @("-NoProfile", "-File", $resolvedOverride) }
        }
        return [pscustomobject]@{ FilePath = $resolvedOverride; PrefixArguments = @() }
    }

    $commandInfo = @(Get-Command $Name -CommandType Application -ErrorAction SilentlyContinue)[0]
    if (-not $commandInfo) {
        throw "missing required command: $Name"
    }
    return [pscustomobject]@{ FilePath = $commandInfo.Source; PrefixArguments = @() }
}

function Get-DockerTool { Get-ToolSpec -Name "docker" -OverrideVariable "LUTHN_DOCKER_COMMAND" }
function Get-CodexTool { Get-ToolSpec -Name "codex" -OverrideVariable "LUTHN_CODEX_COMMAND" }

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
    Assert-ToolSuccess $dockerInfo "Docker daemon check"
    if ($dockerInfo.StdOut.Trim() -cne "linux") {
        throw "Docker Desktop must be running in Linux-container mode."
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
    $acl = [Security.AccessControl.FileSecurity]::new()
    $acl.SetOwner($userSid)
    $acl.SetAccessRuleProtection($true, $false)
    $rule = [Security.AccessControl.FileSystemAccessRule]::new(
        $userSid,
        [Security.AccessControl.FileSystemRights]::FullControl,
        [Security.AccessControl.AccessControlType]::Allow)
    [void]$acl.AddAccessRule($rule)
    Set-Acl -LiteralPath $Path -AclObject $acl
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

function Install-ComposeRuntime {
    param([Parameter(Mandatory = $true)][string]$RuntimeSource)
    Ensure-Directories
    $temporaryCompose = Join-Path $script:DataDir "compose.$([Guid]::NewGuid().ToString('N')).tmp.yaml"
    $temporaryEnvironment = Join-Path $script:DataDir "validate.$([Guid]::NewGuid().ToString('N')).env"
    $temporaryToken = Join-Path $script:DataDir "validate.$([Guid]::NewGuid().ToString('N')).token"
    try {
        if ($env:LUTHN_COMPOSE_SOURCE_FILE) {
            [IO.File]::Copy($env:LUTHN_COMPOSE_SOURCE_FILE, $temporaryCompose, $true)
        } else {
            Invoke-WebRequest -Uri "$RuntimeSource/deploy/compose.yaml" -OutFile $temporaryCompose
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
    } finally {
        foreach ($path in @($temporaryCompose, $temporaryEnvironment, $temporaryToken)) {
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
    if ([IO.Directory]::Exists($script:BinDir)) { [IO.Directory]::Delete($script:BinDir, $true) }
    Write-Host "Luthn services and Windows runtime were removed. Data volumes, configuration, and backups were preserved."
}

try {
    switch -CaseSensitive ($Command.ToLowerInvariant()) {
        "install" { Install-Luthn $CommandArguments }
        "status" { Show-Status }
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
