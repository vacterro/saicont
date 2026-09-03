[CmdletBinding()]
param(
    [switch]$Repair,
    [switch]$Diagnostic,
    [switch]$Json
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')
$paths = Get-SaiContPaths

function Write-SaiContJsonStatus {
    param(
        [Parameter(Mandatory = $true)][string]$Status,
        [Parameter(Mandatory = $true)][int]$ExitCode,
        [Parameter(Mandatory = $true)][psobject]$RuntimeState,
        [AllowNull()][psobject]$Running
    )
    $task = Get-ScheduledTask -TaskName 'SAICONT' -ErrorAction SilentlyContinue
    [ordered]@{
        schema_version = 1
        status = $Status
        exit_code = $ExitCode
        runtime_disposition = [string]$RuntimeState.Disposition
        process_id = if ($null -ne $Running) { [int]$Running.Id } else { $null }
        process_start_utc = if ($null -ne $Running -and $Running.InstanceStartUtc) { $Running.InstanceStartUtc.ToUniversalTime().ToString('o') } else { $null }
        mode = if ($null -ne $Running) { [string]$Running.Mode } else { $null }
        executable = [string]$paths.Executable
        task_installed = ($null -ne $task)
        task_enabled = if ($null -ne $task) { [bool]$task.Settings.Enabled } else { $false }
        task_state = if ($null -ne $task) { [string]$task.State } else { $null }
        repair_requested = [bool]$Repair
    } | ConvertTo-Json -Compress
}

$operationLock = if ($Repair) { Enter-SaiContLifecycleLock } else { $null }
try {
    $runtimeState = Get-SaiContProcessState -Paths $paths
    $running = $runtimeState.Process

    if ($runtimeState.Disposition -eq 'UNVERIFIABLE') {
        if ($Json) { Write-SaiContJsonStatus -Status 'UNVERIFIABLE_RUNTIME' -ExitCode 2 -RuntimeState $runtimeState -Running $running }
        else { Write-Output "UNVERIFIABLE_RUNTIME ($($runtimeState.Error))" }
        exit 2
    }
    if ($null -eq $running) {
        if ($runtimeState.Disposition -eq 'POSITIVELY_STALE' -or (Test-Path -LiteralPath $paths.StopFile)) {
            if ($Repair) {
                if ($runtimeState.Disposition -eq 'POSITIVELY_STALE') {
                    Remove-Item -LiteralPath $paths.PidFile,$paths.InstanceFile,$paths.StopFile -Force -ErrorAction SilentlyContinue
                }
                if ($Json) { Write-SaiContJsonStatus -Status 'STOPPED_REPAIRED' -ExitCode 1 -RuntimeState $runtimeState -Running $null }
                else { Write-Output 'STOPPED (repaired stale runtime artifacts)' }
            }
            else {
                if ($Json) { Write-SaiContJsonStatus -Status 'STALE_RUNTIME_ARTIFACT' -ExitCode 1 -RuntimeState $runtimeState -Running $null }
                else { Write-Output 'STALE_RUNTIME_ARTIFACT (process not running but PID/instance file present)' }
            }
            exit 1
        }
        if ($Json) { Write-SaiContJsonStatus -Status 'STOPPED' -ExitCode 1 -RuntimeState $runtimeState -Running $null }
        else { Write-Output 'STOPPED' }
        exit 1
    }

    $modeStr = if ($running.Mode) { " mode=$($running.Mode)" } else { '' }
    $tokenStr = if ($running.InstanceToken) { " token=$($running.InstanceToken)" } else { '' }
    $startStr = if ($running.InstanceStartUtc) { " start=$($running.InstanceStartUtc.ToString('o'))" } else { '' }
    if ($Json) { Write-SaiContJsonStatus -Status 'RUNNING' -ExitCode 0 -RuntimeState $runtimeState -Running $running }
    else { Write-Output "RUNNING pid=$($running.Id)$startStr$modeStr$tokenStr executable=$($paths.Executable)" }
    if ($Diagnostic -and -not $Json) {
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
