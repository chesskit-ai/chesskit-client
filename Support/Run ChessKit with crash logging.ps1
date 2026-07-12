param(
    [switch]$ForceGdi
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$exe = Join-Path $root 'ChessKit.exe'
$localLogs = Join-Path $env:LOCALAPPDATA 'ChessKit\Logs'
$bundleCache = Join-Path $env:LOCALAPPDATA 'ChessKit\BundleCache'
$rootLauncherLog = Join-Path $root 'diagnostic-launcher.log'
$localLauncherLog = Join-Path $localLogs 'diagnostic-launcher.log'
$launcherLog = $null

function Ensure-DirectoryBestEffort {
    param([string]$Path)

    try {
        New-Item -ItemType Directory -Path $Path -Force -ErrorAction Stop | Out-Null
        return $true
    }
    catch {
        return $false
    }
}

function Append-LauncherLogBestEffort {
    param(
        [string]$Path,
        [string]$Value
    )

    if ([string]::IsNullOrWhiteSpace($Path)) {
        return $false
    }

    try {
        Add-Content -LiteralPath $Path -Value $Value -Encoding UTF8 -ErrorAction Stop
        return $true
    }
    catch {
        return $false
    }
}

function Write-LauncherLogBestEffort {
    param([string]$Value)

    if ($script:launcherLog -and
        (Append-LauncherLogBestEffort -Path $script:launcherLog -Value $Value)) {
        return
    }

    # Prefer a log beside the EXE because that is the folder users normally
    # copy back. If it is read-only, controlled-folder protected, or otherwise
    # unavailable, fall back to the same LocalAppData directory used by the
    # application's always-on lifecycle diagnostics.
    if ($script:launcherLog -ne $script:rootLauncherLog -and
        (Append-LauncherLogBestEffort -Path $script:rootLauncherLog -Value $Value)) {
        $script:launcherLog = $script:rootLauncherLog
        return
    }

    if ($script:launcherLog -ne $script:localLauncherLog) {
        [void](Ensure-DirectoryBestEffort -Path $script:localLogs)
        if (Append-LauncherLogBestEffort -Path $script:localLauncherLog -Value $Value) {
            $script:launcherLog = $script:localLauncherLog
            return
        }
    }

    # Logging must never prevent Chess Kit from launching.
    $script:launcherLog = $null
}

if (-not (Test-Path -LiteralPath $exe)) {
    throw "ChessKit.exe was not found beside this launcher: $exe"
}

[void](Ensure-DirectoryBestEffort -Path $localLogs)
$bundleCacheReady = Ensure-DirectoryBestEffort -Path $bundleCache

$env:CHESSKIT_LOG = '1'
$env:CHESSKIT_DIAG_LOG = '1'
$env:CHESSKIT_ARROW_TIMELINE = '1'
$env:CHESSKIT_BOARD_TRACE_LOG = '0'
if ($bundleCacheReady) {
    $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR = $bundleCache
}
$env:CHESSKIT_FORCE_GDI = if ($ForceGdi) { '1' } else { '0' }

$mode = if ($ForceGdi) { 'GDI safe mode' } else { 'normal GPU capture' }
$started = Get-Date
$startLine = '{0:yyyy-MM-dd HH:mm:ss.fff} START mode={1} exe={2}' -f $started, $mode, $exe
Write-LauncherLogBestEffort -Value $startLine

Write-Host "Starting Chess Kit with diagnostics ($mode)..."
Write-Host "Keep this window open. It will record the process exit code."
$initialLauncherLogDisplay = if ($launcherLog) { $launcherLog } else { 'unavailable (Chess Kit will still launch)' }
Write-Host "Launcher record: $initialLauncherLogDisplay"

$exitCode = -1
try {
    $process = Start-Process -FilePath $exe -WorkingDirectory $root -PassThru
    $process.WaitForExit()
    $process.Refresh()
    $exitCode = $process.ExitCode
}
catch {
    Write-LauncherLogBestEffort -Value ("{0:yyyy-MM-dd HH:mm:ss.fff} LAUNCH_ERROR {1}" -f (Get-Date), $_)
    throw
}
finally {
    $ended = Get-Date
    $elapsed = [long]($ended - $started).TotalMilliseconds
    $unsignedExit = [BitConverter]::ToUInt32([BitConverter]::GetBytes([int]$exitCode), 0)
    $exitHex = '0x{0:X8}' -f $unsignedExit
    $exitLine = '{0:yyyy-MM-dd HH:mm:ss.fff} EXIT mode={1} code={2} hex={3} elapsedMs={4}' -f $ended, $mode, $exitCode, $exitHex, $elapsed
    Write-LauncherLogBestEffort -Value $exitLine
}

Write-Host ''
Write-Host "Chess Kit exited with code $exitCode ($exitHex)."
Write-Host "Logs beside the EXE: $root"
Write-Host "Fallback lifecycle/crash logs: $localLogs"
$launcherLogDisplay = if ($launcherLog) { $launcherLog } else { 'unavailable (Chess Kit still launched)' }
Write-Host "Launcher record: $launcherLogDisplay"
Write-Host ''
Read-Host 'Press Enter to close this diagnostics window'
exit $exitCode
