[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')
$paths = Get-SaiContPaths
$runtimeState = Get-SaiContProcessState -Paths $paths
if ($runtimeState.Disposition -eq 'UNVERIFIABLE') {
    throw "Refusing stop: lifecycle state is UNVERIFIABLE. $($runtimeState.Error)"
}
$running = $runtimeState.Process

if ($null -eq $running) {
    Remove-Item -LiteralPath $paths.PidFile -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $paths.InstanceFile -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $paths.StopFile -Force -ErrorAction SilentlyContinue
    Write-Output 'STOPPED'
    exit 0
}

if (-not (Test-Path -LiteralPath $paths.RunDirectory -PathType Container)) {
    New-Item -ItemType Directory -Path $paths.RunDirectory | Out-Null
}

$stopToken = [string]$running.InstanceToken
if ([String]::IsNullOrWhiteSpace($stopToken)) {
    throw 'Refusing stop: current instance has no verified token.'
}
$temporaryStop = $paths.StopFile + '.tmp.' + [Guid]::NewGuid().ToString('N')
try {
    [IO.File]::WriteAllText($temporaryStop, $stopToken, [Text.Encoding]::ASCII)
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

$originalId = $running.Id
$originalPath = [IO.Path]::GetFullPath($running.Path)
$originalStartUtc = $running.StartTime.ToUniversalTime()
$originalToken = [string]$running.InstanceToken
$deadlineUtc = [DateTime]::UtcNow.AddSeconds(10)
do {
    Start-Sleep -Milliseconds 200
    $running.Refresh()
    if ($running.HasExited) {
        Remove-Item -LiteralPath $paths.PidFile -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $paths.InstanceFile -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $paths.StopFile -Force -ErrorAction SilentlyContinue
        Write-Output "STOPPED pid=$originalId"
        exit 0
    }
} while ([DateTime]::UtcNow -lt $deadlineUtc)

$running.Refresh()
if (-not $running.HasExited) {
    $verifiedPath = [IO.Path]::GetFullPath($running.Path)
    $verifiedStartUtc = $running.StartTime.ToUniversalTime()
    if ($running.Id -ne $originalId -or
        -not [String]::Equals($verifiedPath, $originalPath, [StringComparison]::OrdinalIgnoreCase) -or
        $verifiedStartUtc -ne $originalStartUtc) {
        throw "Refusing force stop: original process identity changed. PID=$originalId"
    }
    Stop-Process -Id $originalId -Force
    $running.WaitForExit(5000) | Out-Null
}
if (-not $running.HasExited) {
    throw "Original SAICONT process did not exit after force stop: PID=$originalId"
}
Remove-Item -LiteralPath $paths.PidFile -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $paths.InstanceFile -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $paths.StopFile -Force -ErrorAction SilentlyContinue
Write-Output "STOPPED forcefully pid=$originalId"
