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

function Get-InstallerToolSpec {
    $override = $env:LUTHN_INSTALLER_DOCKER_COMMAND
    if ($override) {
        $resolvedOverride = (Resolve-Path -LiteralPath $override).Path
        if ([IO.Path]::GetExtension($resolvedOverride) -ieq ".ps1") {
            $pwsh = @(Get-Command pwsh -CommandType Application -ErrorAction Stop)[0].Source
            return [pscustomobject]@{ FilePath = $pwsh; PrefixArguments = @("-NoProfile", "-File", $resolvedOverride) }
        }
        return [pscustomobject]@{ FilePath = $resolvedOverride; PrefixArguments = @() }
    }
    $docker = @(Get-Command docker -CommandType Application -ErrorAction SilentlyContinue)[0]
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

function Assert-WindowsPreflight {
    if ($PSVersionTable.PSVersion -lt [Version]"7.4") { throw "PowerShell 7.4 or later is required." }
    $docker = Get-InstallerToolSpec
    $compose = Invoke-CapturedCommand $docker @("compose", "version")
    if ($compose.ExitCode -ne 0) { throw "Docker Compose is unavailable: $($compose.StdErr.Trim())" }
    $info = Invoke-CapturedCommand $docker @("info", "--format", "{{.OSType}}")
    if ($info.ExitCode -ne 0) { throw "Docker Desktop is not reachable: $($info.StdErr.Trim())" }
    if ($info.StdOut.Trim() -cne "linux") { throw "Docker Desktop must be running in Linux-container mode." }
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
    $pwshPath = @(Get-Command pwsh -CommandType Application -ErrorAction Stop)[0].Source
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
