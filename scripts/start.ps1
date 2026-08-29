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
Remove-Item -LiteralPath $paths.PidFile -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $paths.InstanceFile -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $paths.StopFile -Force -ErrorAction SilentlyContinue

$mode = if ($DryRun) { '--dry-run' } else { '--watch' }
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
try {
    $launcher = Start-Process -FilePath $paths.Executable -ArgumentList ($launcherArguments -join ' ') -WindowStyle Hidden -PassThru -RedirectStandardOutput $stdoutPath -RedirectStandardError $stderrPath
    $deadlineUtc = [DateTime]::UtcNow.AddSeconds(10)
    do {
        Start-Sleep -Milliseconds 200
        $running = Get-SaiContProcess -Paths $paths
        if ($null -ne $running) {
            Write-Output "STARTED mode=$mode pid=$($running.Id)"
            exit 0
        }
        $launcher.Refresh()
        if ($launcher.HasExited) {
            $running = Get-SaiContProcess -Paths $paths
            if ($null -ne $running) {
                Write-Output "STARTED mode=$mode pid=$($running.Id)"
                exit 0
            }
            $childOutput = if (Test-Path -LiteralPath $stdoutPath) { (Get-Content -Raw -LiteralPath $stdoutPath).Trim() } else { '' }
            $childError = if (Test-Path -LiteralPath $stderrPath) { (Get-Content -Raw -LiteralPath $stderrPath).Trim() } else { '' }
            throw "SAICONT failed with exit code $($launcher.ExitCode); stdout=$childOutput stderr=$childError"
        }
    } while ([DateTime]::UtcNow -lt $deadlineUtc)
    throw "SAICONT did not create a valid PID file. Check logs\SAICONT.log and the configuration."
}
finally {
    Remove-Item -LiteralPath $stdoutPath,$stderrPath -Force -ErrorAction SilentlyContinue
}
