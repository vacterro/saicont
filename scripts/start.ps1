[CmdletBinding()]
param(
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')
$paths = Get-SaiContPaths

if (-not (Test-Path -LiteralPath $paths.Executable -PathType Leaf)) {
    throw "Executable not found. Run .\build.ps1 first: $($paths.Executable)"
}
if (-not (Test-Path -LiteralPath $paths.Configuration -PathType Leaf)) {
    throw "Configuration not found: $($paths.Configuration)"
}

$operationLock = Enter-SaiContLifecycleLock
try {
    $requestedMode = if ($DryRun) { '--dry-run' } else { '--watch' }
    $runtimeState = Get-SaiContProcessState -Paths $paths
    if ($runtimeState.Disposition -eq 'UNVERIFIABLE') {
        throw "SAICONT lifecycle state is UNVERIFIABLE: $($runtimeState.Error)"
    }
    $running = $runtimeState.Process
    if ($null -ne $running) {
        if ($requestedMode -eq '--watch' -and [String]::Equals([string]$running.Mode, $requestedMode, [StringComparison]::OrdinalIgnoreCase)) {
            Write-Output "RUNNING pid=$($running.Id) mode=$requestedMode"
            exit 0
        }
        throw "SAICONT already running in mode $($running.Mode); requested $requestedMode. Stop it before changing mode."
    }

    if (-not (Test-Path -LiteralPath $paths.RunDirectory -PathType Container)) {
        New-Item -ItemType Directory -Path $paths.RunDirectory | Out-Null
    }

    $mode = $requestedMode
    $launcherArguments = @(
        $mode,
        '--config', ('"' + $paths.Configuration + '"'),
        '--pid-file', ('"' + $paths.PidFile + '"'),
        '--stop-file', ('"' + $paths.StopFile + '"'),
        '--state-file', ('"' + $paths.StateFile + '"'),
        '--instance-file', ('"' + $paths.InstanceFile + '"'))
    $launchId = [Guid]::NewGuid().ToString('N')
    $stdoutPath = Join-Path $paths.RunDirectory ("start.$launchId.out")
    $stderrPath = Join-Path $paths.RunDirectory ("start.$launchId.err")
    $launcher = $null
    $startupVerified = $false
    try {
        $launcher = Start-Process -FilePath $paths.Executable -ArgumentList ($launcherArguments -join ' ') -WindowStyle Hidden -PassThru -RedirectStandardOutput $stdoutPath -RedirectStandardError $stderrPath
        $deadlineUtc = [DateTime]::UtcNow.AddSeconds(10)
        do {
            Start-Sleep -Milliseconds 200
            $running = Get-SaiContProcess -Paths $paths
            if ($null -ne $running) {
                $startupVerified = $true
                Write-Output "STARTED mode=$mode pid=$($running.Id)"
                break
            }
            $launcher.Refresh()
            if ($launcher.HasExited) {
                $childOutput = if (Test-Path -LiteralPath $stdoutPath) { (Get-Content -Raw -LiteralPath $stdoutPath).Trim() } else { '' }
                $childError = if (Test-Path -LiteralPath $stderrPath) { (Get-Content -Raw -LiteralPath $stderrPath).Trim() } else { '' }
                throw "SAICONT failed with exit code $($launcher.ExitCode); stdout=$childOutput stderr=$childError"
            }
        } while ([DateTime]::UtcNow -lt $deadlineUtc)
        if (-not $startupVerified) {
            throw "SAICONT did not create a valid PID file before timeout."
        }
    }
    finally {
        if (-not $startupVerified -and $null -ne $launcher) {
            $launcher.Refresh()
            if (-not $launcher.HasExited) {
                Stop-Process -Id $launcher.Id -Force -ErrorAction SilentlyContinue
                if (-not $launcher.WaitForExit(5000)) {
                    throw "UNVERIFIABLE cleanup: launch process PID $($launcher.Id) did not exit."
                }
            }
            $current = Get-SaiContProcess -Paths $paths
            if ($null -ne $current -and $current.Id -eq $launcher.Id) {
                Remove-SaiContArtifactsIfOwned -Paths $paths -ProcessId $current.Id -ProcessStartUtc $current.InstanceStartUtc -InstanceToken ([string]$current.InstanceToken) | Out-Null
            }
        }
        Remove-Item -LiteralPath $stdoutPath,$stderrPath -Force -ErrorAction SilentlyContinue
    }
}
finally {
    Exit-SaiContLifecycleLock -Mutex $operationLock
}
