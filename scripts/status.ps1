[CmdletBinding()]
param(
    [switch]$Repair,
    [switch]$Diagnostic
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')
$paths = Get-SaiContPaths
$running = Get-SaiContProcess -Paths $paths

if ($null -eq $running) {
    if ((Test-Path -LiteralPath $paths.PidFile) -or (Test-Path -LiteralPath $paths.InstanceFile) -or (Test-Path -LiteralPath $paths.StopFile)) {
        if ($Repair) {
            Remove-Item -LiteralPath $paths.PidFile -Force -ErrorAction SilentlyContinue
            Remove-Item -LiteralPath $paths.InstanceFile -Force -ErrorAction SilentlyContinue
            Remove-Item -LiteralPath $paths.StopFile -Force -ErrorAction SilentlyContinue
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
    $stateStatus = 'MISSING'
    if (Test-Path -LiteralPath $paths.StateFile -PathType Leaf) {
        try {
            [xml]$stateXml = Get-Content -Raw -Encoding UTF8 -LiteralPath $paths.StateFile
            $stateStatus = if ($stateXml.saicontState.version -eq '1') { 'VALID_V1' } else { 'UNSUPPORTED' }
        }
        catch {
            $stateStatus = 'CORRUPT'
        }
    }
    $taskStatus = if (Get-ScheduledTask -TaskName 'SAICONT' -ErrorAction SilentlyContinue) { 'INSTALLED' } else { 'NOT_INSTALLED' }
    $logPath = Join-Path $paths.ProjectRoot 'logs\SAICONT.log'
    $lastLogUtc = if (Test-Path -LiteralPath $logPath -PathType Leaf) { (Get-Item -LiteralPath $logPath).LastWriteTimeUtc.ToString('o') } else { 'none' }
    Write-Output "DIAGNOSTIC config=$configStatus state=$stateStatus task=$taskStatus last_log_utc=$lastLogUtc"
}
exit 0
