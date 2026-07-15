#requires -Version 7.4

[CmdletBinding()]
param(
    [switch]$ConnectCodex
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$distributionRef = if ($env:LUTHN_DISTRIBUTION_REF) { $env:LUTHN_DISTRIBUTION_REF } else { "main" }
$sourceBaseUrl = if ($env:LUTHN_SOURCE_BASE_URL) { $env:LUTHN_SOURCE_BASE_URL.TrimEnd("/") } else { "https://raw.githubusercontent.com/JakobSung/Luthn/$distributionRef" }
$rootDir = if ($env:LUTHN_WINDOWS_ROOT) {
    $env:LUTHN_WINDOWS_ROOT
} elseif ($env:LOCALAPPDATA) {
    Join-Path $env:LOCALAPPDATA "Luthn"
} else {
    throw "LOCALAPPDATA is required."
}
$binDir = if ($env:LUTHN_BIN_DIR) { $env:LUTHN_BIN_DIR } else { Join-Path $rootDir "bin" }
$cliPath = Join-Path $binDir "luthn.ps1"
$shimPath = Join-Path $binDir "luthn.cmd"
$utf8NoBom = [Text.UTF8Encoding]::new($false)

function Get-PwshPath {
    $pwsh = Get-Command pwsh -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $pwsh) { throw "PowerShell 7.4 or later is required. Install it, then run this installer with pwsh." }
    return $pwsh.Source
}

function New-InstallerToolSpec {
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

function Get-InstallerToolSpec {
    $override = $env:LUTHN_INSTALLER_DOCKER_COMMAND
    if ($override) {
        return New-InstallerToolSpec -Path $override
    }
    $docker = Get-Command docker -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $docker) { throw "Docker Desktop with Docker Compose is required." }
    return [pscustomobject]@{ FilePath = $docker.Source; PrefixArguments = @() }
}

function Invoke-CapturedCommand {
    param($Tool, [string[]]$Arguments)
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $Tool.FilePath
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    foreach ($argument in @($Tool.PrefixArguments) + $Arguments) { [void]$startInfo.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    [void]$process.Start()
    $stdout = $process.StandardOutput.ReadToEnd()
    $stderr = $process.StandardError.ReadToEnd()
    $process.WaitForExit()
    return [pscustomobject]@{ ExitCode = $process.ExitCode; StdOut = $stdout; StdErr = $stderr }
}

function Get-DockerDesktopToolSpec {
    if ($env:LUTHN_DOCKER_DESKTOP_COMMAND) {
        return New-InstallerToolSpec -Path $env:LUTHN_DOCKER_DESKTOP_COMMAND
    }
    if ($env:LUTHN_INSTALLER_DOCKER_COMMAND) { return $null }
    if (-not $env:ProgramFiles) { return $null }
    $desktopPath = Join-Path $env:ProgramFiles "Docker\Docker\Docker Desktop.exe"
    if (-not [IO.File]::Exists($desktopPath)) { return $null }
    return [pscustomobject]@{ FilePath = $desktopPath; PrefixArguments = @() }
}

function Start-DetachedTool {
    param([Parameter(Mandatory = $true)]$Tool)

    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $Tool.FilePath
    $startInfo.UseShellExecute = $true
    $startInfo.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    foreach ($argument in $Tool.PrefixArguments) { [void]$startInfo.ArgumentList.Add([string]$argument) }
    $process = [Diagnostics.Process]::Start($startInfo)
    if (-not $process) { throw "failed to start Docker Desktop: $($Tool.FilePath)" }
}

function Start-DockerDesktopAndWait {
    param([Parameter(Mandatory = $true)]$Docker)

    $desktop = Get-DockerDesktopToolSpec
    if (-not $desktop) { return $null }

    Write-Host "Docker Desktop is not reachable. Starting it and waiting for the Linux engine..."
    Start-DetachedTool -Tool $desktop
    $lastResult = $null
    for ($attempt = 0; $attempt -lt 60; $attempt++) {
        $lastResult = Invoke-CapturedCommand $Docker @("info", "--format", "{{.OSType}}")
        if ($lastResult.ExitCode -eq 0) { return $lastResult }
        Start-Sleep -Seconds 2
    }
    return $lastResult
}

function Assert-WindowsPreflight {
    if ($PSVersionTable.PSVersion -lt [Version]"7.4") { throw "PowerShell 7.4 or later is required." }
    $docker = Get-InstallerToolSpec
    $compose = Invoke-CapturedCommand $docker @("compose", "version")
    if ($compose.ExitCode -ne 0) { throw "Docker Compose is unavailable: $($compose.StdErr.Trim())" }
    $info = Invoke-CapturedCommand $docker @("info", "--format", "{{.OSType}}")
    if ($info.ExitCode -ne 0) { $info = Start-DockerDesktopAndWait -Docker $docker }
    if (-not $info -or $info.ExitCode -ne 0) {
        $detail = if ($info) { $info.StdErr.Trim() } else { "Docker Desktop could not be started automatically." }
        throw "Docker Desktop is not reachable. Start Docker Desktop, wait for the engine, and retry. $detail"
    }
    if ($info.StdOut.Trim() -cne "linux") {
        throw "Docker Desktop is running Windows containers. Switch to Linux containers from the Docker Desktop menu and retry."
    }
}

Assert-WindowsPreflight
[void][IO.Directory]::CreateDirectory($binDir)
$temporaryCli = Join-Path $binDir "luthn.$([Guid]::NewGuid().ToString('N')).tmp.ps1"
$cliExistedBefore = [IO.File]::Exists($cliPath)
$backupCli = $null
$previousShim = if ([IO.File]::Exists($shimPath)) { [IO.File]::ReadAllBytes($shimPath) } else { $null }
$pathChanged = $false

try {
    if ($env:LUTHN_WINDOWS_CLI_SOURCE_FILE) {
        [IO.File]::Copy($env:LUTHN_WINDOWS_CLI_SOURCE_FILE, $temporaryCli, $true)
    } else {
        Invoke-WebRequest -Uri "$sourceBaseUrl/scripts/luthn.ps1" -OutFile $temporaryCli
    }
    $tokens = $null
    $parseErrors = $null
    [void][Management.Automation.Language.Parser]::ParseFile($temporaryCli, [ref]$tokens, [ref]$parseErrors)
    if ($parseErrors.Count -gt 0) { throw "downloaded Windows CLI did not pass PowerShell syntax validation" }
    $downloadedContent = [IO.File]::ReadAllText($temporaryCli)
    if ($downloadedContent -notmatch '\$script:LuthnWindowsCliVersion\s*=\s*"1"') {
        throw "downloaded Windows CLI did not match the Luthn distribution contract"
    }

    if ([IO.File]::Exists($cliPath)) {
        $backupCli = Join-Path $binDir "luthn.$([Guid]::NewGuid().ToString('N')).bak.ps1"
        [IO.File]::Replace($temporaryCli, $cliPath, $backupCli, $true)
    } else {
        [IO.File]::Move($temporaryCli, $cliPath)
    }

    $installArguments = @("install")
    if ($ConnectCodex) { $installArguments += "--connect-codex" }
    $pwshPath = Get-PwshPath
    & $pwshPath -NoProfile -File $cliPath @installArguments
    if ($LASTEXITCODE -ne 0) { throw "Luthn Windows installation failed with exit code $LASTEXITCODE" }

    $shimContent = "@echo off`r`npwsh -NoProfile -File `"%~dp0luthn.ps1`" %*`r`n"
    [IO.File]::WriteAllText($shimPath, $shimContent, [Text.Encoding]::ASCII)

    if ($IsWindows) {
        $userPath = [Environment]::GetEnvironmentVariable("Path", "User")
        $pathEntries = @($userPath -split ";" | Where-Object { $_ })
        if ($pathEntries -notcontains $binDir) {
            $newUserPath = (@($pathEntries) + $binDir) -join ";"
            [Environment]::SetEnvironmentVariable("Path", $newUserPath, "User")
            $env:Path = "$binDir;$env:Path"
            $pathChanged = $true
        }
    }

    if ($backupCli -and [IO.File]::Exists($backupCli)) { [IO.File]::Delete($backupCli) }
    Write-Host "Windows CLI: $cliPath"
    if ($pathChanged) { Write-Host "Open a new terminal before using 'luthn' from PATH." }
} catch {
    if ($backupCli -and [IO.File]::Exists($backupCli)) {
        if ([IO.File]::Exists($cliPath)) { [IO.File]::Delete($cliPath) }
        [IO.File]::Move($backupCli, $cliPath)
    } elseif (-not $cliExistedBefore -and [IO.File]::Exists($cliPath) -and -not $backupCli) {
        [IO.File]::Delete($cliPath)
    }
    if ($null -ne $previousShim) {
        [IO.File]::WriteAllBytes($shimPath, $previousShim)
    } elseif ([IO.File]::Exists($shimPath)) {
        [IO.File]::Delete($shimPath)
    }
    throw
} finally {
    if ([IO.File]::Exists($temporaryCli)) { [IO.File]::Delete($temporaryCli) }
}
