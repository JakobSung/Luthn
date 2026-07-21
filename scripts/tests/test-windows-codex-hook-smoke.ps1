#requires -Version 7.4

[CmdletBinding()]
param(
    [string]$RepoRoot = (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)),
    [switch]$RequireCodexEvent
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if ($env:LUTHN_RUN_CODEX_HOOK_SMOKE -cne "1") {
    Write-Host "Windows Codex hook smoke skipped; set LUTHN_RUN_CODEX_HOOK_SMOKE=1 to run it."
    return
}

function Find-CodexExecutable {
    if ($env:LUTHN_CODEX_COMMAND -and [IO.File]::Exists($env:LUTHN_CODEX_COMMAND)) {
        return $env:LUTHN_CODEX_COMMAND
    }
    $candidates = [Collections.Generic.List[string]]::new()
    if ($env:LOCALAPPDATA) {
        $desktopRoot = Join-Path $env:LOCALAPPDATA "OpenAI\Codex\bin"
        if ([IO.Directory]::Exists($desktopRoot)) {
            Get-ChildItem -Path (Join-Path $desktopRoot "*\codex.exe") -File -ErrorAction SilentlyContinue |
                Sort-Object LastWriteTime -Descending |
                ForEach-Object { [void]$candidates.Add($_.FullName) }
        }
    }
    $command = Get-Command codex -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($command) { [void]$candidates.Add($command.Source) }
    foreach ($candidate in $candidates) {
        if ([IO.File]::Exists($candidate)) { return $candidate }
    }
    throw "A runnable Codex CLI is required for the authenticated Windows hook smoke test."
}

function Set-EnvironmentValue {
    param([string]$Name, [AllowNull()][string]$Value)
    [Environment]::SetEnvironmentVariable($Name, $Value, [EnvironmentVariableTarget]::Process)
}

$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) "luthn-windows-codex-smoke-$([Guid]::NewGuid().ToString('N'))"
$originalCodexHome = if ($env:CODEX_HOME) { $env:CODEX_HOME } else { Join-Path $env:USERPROFILE ".codex" }
$authFile = Join-Path $originalCodexHome "auth.json"
if (-not [IO.File]::Exists($authFile)) { throw "Codex authentication is required before running the hook smoke test." }

$managedEnvironment = @(
    "CODEX_HOME",
    "LUTHN_WINDOWS_ROOT",
    "LUTHN_CONFIG_FILE",
    "LUTHN_SERVICE_TOKEN_FILE",
    "LUTHN_BIN_DIR",
    "LUTHN_CODEX_HOOK_CAPTURE_FILE",
    "LUTHN_CODEX_HOOK_SYNCHRONOUS",
    "LUTHN_RAW_HOOK_CAPTURE_FILE",
    "LUTHN_TEST_NO_EXIT"
)
$previousEnvironment = @{}
foreach ($name in $managedEnvironment) { $previousEnvironment[$name] = [Environment]::GetEnvironmentVariable($name) }

try {
    $codexHome = Join-Path $temporaryRoot "codex"
    $configDirectory = Join-Path $temporaryRoot "luthn\config"
    $binDirectory = Join-Path $temporaryRoot "luthn\bin"
    [void][IO.Directory]::CreateDirectory($codexHome)
    [void][IO.Directory]::CreateDirectory($configDirectory)
    [void][IO.Directory]::CreateDirectory($binDirectory)

    $authLink = Join-Path $codexHome "auth.json"
    [void](New-Item -ItemType HardLink -Path $authLink -Target $authFile)

    $tokenFile = Join-Path $configDirectory "service-token"
    $configFile = Join-Path $configDirectory "luthn.env"
    $captureFile = Join-Path $temporaryRoot "captured-capsule.json"
    $rawCaptureFile = Join-Path $temporaryRoot "raw-hook-event.json"
    $token = "smoke-$([Guid]::NewGuid().ToString('N'))"
    [IO.File]::WriteAllText($tokenFile, $token, [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText(
        $configFile,
        "LUTHN_BASE_URL=http://127.0.0.1:1`nLUTHN_SERVICE_TOKEN_FILE=$tokenFile`n",
        [Text.UTF8Encoding]::new($false))

    $pwsh = (Get-Command pwsh -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
    $cli = (Resolve-Path (Join-Path $RepoRoot "scripts\luthn.ps1")).Path
    $forwarder = Join-Path $temporaryRoot "forward-hook.ps1"
    $escapedPwsh = $pwsh.Replace("'", "''")
    $escapedCli = $cli.Replace("'", "''")
    $forwarderContent = @"
`$raw = [Console]::OpenStandardInput()
`$memory = [IO.MemoryStream]::new()
try {
    `$raw.CopyTo(`$memory)
    `$bytes = `$memory.ToArray()
    [IO.File]::WriteAllBytes(`$env:LUTHN_RAW_HOOK_CAPTURE_FILE, `$bytes)
    `$startInfo = [Diagnostics.ProcessStartInfo]::new()
    `$startInfo.FileName = '$escapedPwsh'
    `$startInfo.UseShellExecute = `$false
    `$startInfo.CreateNoWindow = `$true
    `$startInfo.RedirectStandardInput = `$true
    foreach (`$argument in @('-NoProfile', '-NonInteractive', '-File', '$escapedCli', 'codex-hook')) {
        [void]`$startInfo.ArgumentList.Add(`$argument)
    }
    `$process = [Diagnostics.Process]::new()
    `$process.StartInfo = `$startInfo
    try {
        [void]`$process.Start()
        `$process.StandardInput.BaseStream.Write(`$bytes, 0, `$bytes.Length)
        `$process.StandardInput.Close()
        `$process.WaitForExit()
        exit `$process.ExitCode
    } finally { `$process.Dispose() }
} finally { `$memory.Dispose() }
"@
    [IO.File]::WriteAllText($forwarder, $forwarderContent, [Text.UTF8Encoding]::new($false))
    $hookCommand = "& `"$pwsh`" -NoProfile -NonInteractive -File `"$forwarder`""
    $hooks = [ordered]@{
        hooks = [ordered]@{
            Stop = @([ordered]@{
                matcher = "luthn.agent-connector.v1"
                hooks = @([ordered]@{
                    type = "command"
                    command = $hookCommand
                    timeout = 5
                    statusMessage = "Luthn 메모리 저장 예약 중…"
                })
            })
        }
    }
    [IO.File]::WriteAllText(
        (Join-Path $codexHome "hooks.json"),
        (($hooks | ConvertTo-Json -Depth 10) + "`n"),
        [Text.UTF8Encoding]::new($false))
    $tomlCommand = $hookCommand.Replace("\", "\\").Replace('"', '\"')
    $configToml = @"
[hooks]
stop = [{ matcher = "luthn.agent-connector.v1", hooks = [{ type = "command", command = "$tomlCommand", command_windows = "$tomlCommand", timeout = 5, status_message = "Luthn 메모리 저장 예약 중…" }] }]
"@
    [IO.File]::WriteAllText((Join-Path $codexHome "config.toml"), $configToml, [Text.UTF8Encoding]::new($false))

    Set-EnvironmentValue "CODEX_HOME" $codexHome
    Set-EnvironmentValue "LUTHN_WINDOWS_ROOT" (Join-Path $temporaryRoot "luthn")
    Set-EnvironmentValue "LUTHN_CONFIG_FILE" $configFile
    Set-EnvironmentValue "LUTHN_SERVICE_TOKEN_FILE" $tokenFile
    Set-EnvironmentValue "LUTHN_BIN_DIR" $binDirectory
    Set-EnvironmentValue "LUTHN_CODEX_HOOK_CAPTURE_FILE" $captureFile
    Set-EnvironmentValue "LUTHN_CODEX_HOOK_SYNCHRONOUS" "true"
    Set-EnvironmentValue "LUTHN_RAW_HOOK_CAPTURE_FILE" $rawCaptureFile
    Set-EnvironmentValue "LUTHN_TEST_NO_EXIT" "true"

    $codex = Find-CodexExecutable
    $result = & $codex exec --ephemeral --strict-config --enable hooks --disable plugins --dangerously-bypass-hook-trust --skip-git-repo-check -C $temporaryRoot "Reply with OK." 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) { throw "Codex hook smoke failed: $result" }
    if (-not [IO.File]::Exists($rawCaptureFile)) {
        $message = "Codex exec completed without invoking the Windows Stop hook. This isolated CODEX_HOME smoke is advisory because Codex exec may not load user hooks in an ephemeral home. Run the interactive /hooks Trust smoke manually."
        if ($RequireCodexEvent) { throw "$message Codex output: $result" }
        Write-Warning $message
        return
    }
    if (-not [IO.File]::Exists($captureFile)) {
        $rawEvent = [IO.File]::ReadAllText($rawCaptureFile) | ConvertFrom-Json
        $fieldNames = @($rawEvent.PSObject.Properties.Name) -join ", "
        throw "Codex invoked the Windows Stop hook, but Luthn rejected its event fields: $fieldNames"
    }
    $capsule = [IO.File]::ReadAllText($captureFile) | ConvertFrom-Json
    if ($capsule.sourceAgent -cne "codex" -or -not ([string]$capsule.summary).Trim()) {
        throw "Codex invoked the hook, but the captured capsule was invalid."
    }
    Write-Host "Windows Codex hook smoke passed."
} finally {
    foreach ($name in $managedEnvironment) { Set-EnvironmentValue $name $previousEnvironment[$name] }
    if ([IO.Directory]::Exists($temporaryRoot)) { [IO.Directory]::Delete($temporaryRoot, $true) }
}
