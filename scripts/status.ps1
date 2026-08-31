[CmdletBinding()]
param(
    [switch]$Repair,
    [switch]$Diagnostic
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')
$paths = Get-SaiContPaths
$operationLock = if ($Repair) { Enter-SaiContLifecycleLock } else { $null }
try {
    $runtimeState = Get-SaiContProcessState -Paths $paths
    $running = $runtimeState.Process

    if ($runtimeState.Disposition -eq 'UNVERIFIABLE') {
        Write-Output "UNVERIFIABLE_RUNTIME ($($runtimeState.Error))"
        exit 2
    }
    if ($null -eq $running) {
        if ($runtimeState.Disposition -eq 'POSITIVELY_STALE' -or (Test-Path -LiteralPath $paths.StopFile)) {
            if ($Repair) {
                if ($runtimeState.Disposition -eq 'POSITIVELY_STALE') {
                    Remove-Item -LiteralPath $paths.PidFile,$paths.InstanceFile,$paths.StopFile -Force -ErrorAction SilentlyContinue
                }
                Write-Output 'STOPPED (repaired stale runtime artifacts)'
            }
            else {
                Write-Output 'STALE_RUNTIME_ARTIFACT (process not running but PID/instance file present)'
            }
            exit 1
        }
        Write-Output 'STOPPED'
        exit 1
    }

    $modeStr = if ($running.Mode) { " mode=$($running.Mode)" } else { '' }
    $tokenStr = if ($running.InstanceToken) { " token=$($running.InstanceToken)" } else { '' }
    $startStr = if ($running.InstanceStartUtc) { " start=$($running.InstanceStartUtc.ToString('o'))" } else { '' }
    Write-Output "RUNNING pid=$($running.Id)$startStr$modeStr$tokenStr executable=$($paths.Executable)"
    if ($Diagnostic) {
        & $paths.Executable --validate-config --config $paths.Configuration | Out-Null
        $configStatus = if ($LASTEXITCODE -eq 0) { 'VALID' } else { 'INVALID' }
        $stateOutput = & $paths.Executable --validate-state --state-file $paths.StateFile 2>&1
        $stateExit = $LASTEXITCODE
        $stateStatus = if ($stateOutput -match '^STATE: (.+?)(?:\s|$)') { $Matches[1] } else { 'I/O_UNAVAILABLE' }
        if ($stateExit -ne 0 -and $stateStatus -eq 'VALID_V1') {
            $stateStatus = 'I/O_UNAVAILABLE'
        }
        $taskStatus = if (Get-ScheduledTask -TaskName 'SAICONT' -ErrorAction SilentlyContinue) { 'INSTALLED' } else { 'NOT_INSTALLED' }
        $logPath = Join-Path $paths.ProjectRoot 'run\SAICONT.log'
        $lastLogUtc = if (Test-Path -LiteralPath $logPath -PathType Leaf) { (Get-Item -LiteralPath $logPath).LastWriteTimeUtc.ToString('o') } else { 'none' }
        Write-Output "DIAGNOSTIC config=$configStatus state=$stateStatus task=$taskStatus last_log_utc=$lastLogUtc"
    }
    exit 0
}
finally {
    if ($null -ne $operationLock) { Exit-SaiContLifecycleLock -Mutex $operationLock }
}
