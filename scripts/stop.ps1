[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')
$paths = Get-SaiContPaths
$operationLock = Enter-SaiContLifecycleLock
try {
    $runtimeState = Get-SaiContProcessState -Paths $paths
    if ($runtimeState.Disposition -eq 'UNVERIFIABLE') {
        throw "Refusing stop: lifecycle state is UNVERIFIABLE. $($runtimeState.Error)"
    }
    $running = $runtimeState.Process

    if ($null -eq $running) {
        if ($runtimeState.Disposition -eq 'POSITIVELY_STALE') {
            Remove-Item -LiteralPath $paths.PidFile,$paths.InstanceFile,$paths.StopFile -Force -ErrorAction SilentlyContinue
        }
        Write-Output 'STOPPED'
        exit 0
    }

    $originalId = $running.Id
    $originalPath = [IO.Path]::GetFullPath($running.Path)
    $originalStartUtc = $running.StartTime.ToUniversalTime()
    $originalToken = [string]$running.InstanceToken
    if ([String]::IsNullOrWhiteSpace($originalToken)) {
        throw 'Refusing stop: current instance has no verified token.'
    }
    if (-not (Test-Path -LiteralPath $paths.RunDirectory -PathType Container)) {
        New-Item -ItemType Directory -Path $paths.RunDirectory | Out-Null
    }
    $temporaryStop = $paths.StopFile + '.tmp.' + [Guid]::NewGuid().ToString('N')
    try {
        [IO.File]::WriteAllText($temporaryStop, $originalToken, [Text.Encoding]::ASCII)
        if (Test-Path -LiteralPath $paths.StopFile -PathType Leaf) {
            $backupStop = $paths.StopFile + '.replace-backup'
            [IO.File]::Replace($temporaryStop, $paths.StopFile, $backupStop, $true)
            Remove-Item -LiteralPath $backupStop -Force -ErrorAction SilentlyContinue
        }
        else {
            [IO.File]::Move($temporaryStop, $paths.StopFile)
        }
    }
    finally {
        Remove-Item -LiteralPath $temporaryStop -Force -ErrorAction SilentlyContinue
    }

    $deadlineUtc = [DateTime]::UtcNow.AddSeconds(10)
    do {
        Start-Sleep -Milliseconds 200
        $running.Refresh()
        if ($running.HasExited) {
            # W2L1-005: the owner is dead, so cleanup must verify the RECORDS
            # against the captured identity instead of asking a corpse to
            # identify itself. A retained/failed artifact is reported, never
            # swallowed behind an unconditional STOPPED.
            $cleanup = Remove-SaiContArtifactsForDeadOwner -Paths $paths -ProcessId $originalId -ProcessStartUtc $originalStartUtc -InstanceToken $originalToken
            if ($cleanup.Clean) {
                Write-Output "STOPPED pid=$originalId"
                exit 0
            }
            Write-Output "STOPPED_DEGRADED pid=$originalId retained=$([String]::Join('; ', $cleanup.Retained)) errors=$([String]::Join('; ', $cleanup.Errors))"
            exit 1
        }
    } while ([DateTime]::UtcNow -lt $deadlineUtc)

    $running.Refresh()
    if (-not $running.HasExited) {
        $verifiedPath = [IO.Path]::GetFullPath($running.Path)
        $verifiedStartUtc = $running.StartTime.ToUniversalTime()
        if ($running.Id -ne $originalId -or
            -not [String]::Equals($verifiedPath, $originalPath, [StringComparison]::OrdinalIgnoreCase) -or
            $verifiedStartUtc.Ticks -ne $originalStartUtc.Ticks) {
            throw "Refusing force stop: original process identity changed. PID=$originalId"
        }
        Stop-Process -Id $originalId -Force
        if (-not $running.WaitForExit(5000)) {
            throw "UNVERIFIABLE cleanup: original process did not exit after force stop: PID=$originalId"
        }
    }
    if (-not $running.HasExited) {
        throw "Original SAICONT process did not exit after force stop: PID=$originalId"
    }
    # W2L1-005: a force-killed process cannot run its own finally cleanup, so this
    # is exactly the path that previously left owned artifacts behind while
    # printing success.
    $cleanup = Remove-SaiContArtifactsForDeadOwner -Paths $paths -ProcessId $originalId -ProcessStartUtc $originalStartUtc -InstanceToken $originalToken
    if ($cleanup.Clean) {
        Write-Output "STOPPED forcefully pid=$originalId"
        exit 0
    }
    Write-Output "STOPPED_DEGRADED forcefully pid=$originalId retained=$([String]::Join('; ', $cleanup.Retained)) errors=$([String]::Join('; ', $cleanup.Errors))"
    exit 1
}
finally {
    Exit-SaiContLifecycleLock -Mutex $operationLock
}
