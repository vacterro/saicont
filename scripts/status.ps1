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
        [AllowNull()][psobject]$Running,
        [AllowNull()][psobject]$HealthSnapshot,
        [AllowNull()][psobject]$Verified
    )
    $task = Get-ScheduledTask -TaskName 'SAICONT' -ErrorAction SilentlyContinue
    $health = if ($null -ne $HealthSnapshot) { $HealthSnapshot.Health } else { $null }
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
        # CORE-006: the health join. Wave-3's contract requires this command to
        # answer liveness, health and next-due without another process scraping
        # internal files. Read-only: -Json never mutates, with or without -Repair.
        health_disposition = if ($null -ne $HealthSnapshot) { [string]$HealthSnapshot.Disposition } else { 'MISSING' }
        health_age_seconds = if ($null -ne $HealthSnapshot) { $HealthSnapshot.AgeSeconds } else { $null }
        health_fresh = if ($null -ne $HealthSnapshot) { [bool]$HealthSnapshot.Fresh } else { $false }
        health_matches_instance = if ($null -ne $Verified) { [bool]$Verified.HealthMatchesInstance } else { $false }
        health_updated_utc = if ($null -ne $health) { Format-SaiContUtc $health.health_updated_utc } else { $null }
        health_instance_short_id = if ($null -ne $health -and $null -ne $health.instance_short_id) { [string]$health.instance_short_id } else { $null }
        health_mode = if ($null -ne $health -and $null -ne $health.mode) { [string]$health.mode } else { $null }
        health_config_fingerprint = if ($null -ne $health -and $null -ne $health.config_fingerprint) { [string]$health.config_fingerprint } else { $null }
        state_store_healthy = if ($null -ne $health) { [bool]$health.state_store_healthy } else { $null }
        state_store_disposition = if ($null -ne $health -and $null -ne $health.state_store_disposition) { [string]$health.state_store_disposition } else { $null }
        last_poll_utc = if ($null -ne $health) { Format-SaiContUtc $health.last_poll_utc } else { $null }
        last_successful_poll_utc = if ($null -ne $health) { Format-SaiContUtc $health.last_successful_poll_utc } else { $null }
        last_error_utc = if ($null -ne $health) { Format-SaiContUtc $health.last_error_utc } else { $null }
        last_error_code = if ($null -ne $health -and $null -ne $health.last_error_code) { [string]$health.last_error_code } else { $null }
        next_due_utc = if ($null -ne $health) { Format-SaiContUtc $health.next_due_utc } else { $null }
        last_write_utc = if ($null -ne $health) { Format-SaiContUtc $health.last_write_utc } else { $null }
        active_sessions = if ($null -ne $health) { [int]$health.active_sessions } else { $null }
        detail = if ($null -ne $Verified -and $null -ne $Verified.Detail) { [string]$Verified.Detail } else { $null }
    } | ConvertTo-Json -Compress
}

$operationLock = if ($Repair) { Enter-SaiContLifecycleLock } else { $null }
try {
    $runtimeState = Get-SaiContProcessState -Paths $paths
    $running = $runtimeState.Process
    # CORE-006: read health for every disposition, then classify by joining it
    # with the already verified lifecycle identity.
    $healthSnapshot = Get-SaiContHealthSnapshot -Paths $paths
    $verified = Get-SaiContVerifiedStatus -Paths $paths -RuntimeState $runtimeState -HealthSnapshot $healthSnapshot

    if ($runtimeState.Disposition -eq 'UNVERIFIABLE') {
        if ($Json) { Write-SaiContJsonStatus -Status 'UNVERIFIABLE' -ExitCode 2 -RuntimeState $runtimeState -Running $running -HealthSnapshot $healthSnapshot -Verified $verified }
        else { Write-Output "UNVERIFIABLE_RUNTIME ($($runtimeState.Error))" }
        exit 2
    }
    if ($null -eq $running) {
        if ($runtimeState.Disposition -eq 'POSITIVELY_STALE' -or (Test-Path -LiteralPath $paths.StopFile)) {
            if ($Repair) {
                if ($runtimeState.Disposition -eq 'POSITIVELY_STALE') {
                    Remove-Item -LiteralPath $paths.PidFile,$paths.InstanceFile,$paths.StopFile -Force -ErrorAction SilentlyContinue
                }
                if ($Json) { Write-SaiContJsonStatus -Status 'STOPPED_REPAIRED' -ExitCode 1 -RuntimeState $runtimeState -Running $null -HealthSnapshot $healthSnapshot -Verified $verified }
                else { Write-Output 'STOPPED (repaired stale runtime artifacts)' }
            }
            else {
                if ($Json) { Write-SaiContJsonStatus -Status 'POSITIVELY_STALE' -ExitCode 1 -RuntimeState $runtimeState -Running $null -HealthSnapshot $healthSnapshot -Verified $verified }
                else { Write-Output 'STALE_RUNTIME_ARTIFACT (process not running but PID/instance file present)' }
            }
            exit 1
        }
        if ($Json) { Write-SaiContJsonStatus -Status 'STOPPED_VERIFIED' -ExitCode 1 -RuntimeState $runtimeState -Running $null -HealthSnapshot $healthSnapshot -Verified $verified }
        else { Write-Output 'STOPPED' }
        exit 1
    }

    $modeStr = if ($running.Mode) { " mode=$($running.Mode)" } else { '' }
    $tokenStr = if ($running.InstanceToken) { " token=$($running.InstanceToken)" } else { '' }
    $startStr = if ($running.InstanceStartUtc) { " start=$($running.InstanceStartUtc.ToString('o'))" } else { '' }
    if ($Json) { Write-SaiContJsonStatus -Status $verified.Status -ExitCode $verified.ExitCode -RuntimeState $runtimeState -Running $running -HealthSnapshot $healthSnapshot -Verified $verified }
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
        Write-Output "DIAGNOSTIC config=$configStatus state=$stateStatus task=$taskStatus last_log_utc=$lastLogUtc health=$($healthSnapshot.Disposition) health_age_s=$($healthSnapshot.AgeSeconds)"
    }
    # CORE-006: a live verified process whose health snapshot cannot be joined is
    # UNVERIFIABLE, not RUNNING. Exit code follows the classification.
    exit $verified.ExitCode
}
finally {
    if ($null -ne $operationLock) { Exit-SaiContLifecycleLock -Mutex $operationLock }
}
