[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')
$paths = Get-SaiContPaths
$running = Get-SaiContProcess -Paths $paths

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

$deadlineUtc = [DateTime]::UtcNow.AddSeconds(10)
do {
    Start-Sleep -Milliseconds 200
    $current = Get-SaiContProcess -Paths $paths
    if ($null -eq $current) {
        Remove-Item -LiteralPath $paths.PidFile -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $paths.InstanceFile -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $paths.StopFile -Force -ErrorAction SilentlyContinue
        Write-Output "STOPPED pid=$($running.Id)"
        exit 0
    }
} while ([DateTime]::UtcNow -lt $deadlineUtc)

$current = Get-SaiContProcess -Paths $paths
if ($null -ne $current) {
    Stop-Process -Id $current.Id -Force
    $current.WaitForExit(5000) | Out-Null
}
Remove-Item -LiteralPath $paths.PidFile -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $paths.InstanceFile -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $paths.StopFile -Force -ErrorAction SilentlyContinue
Write-Output "STOPPED forcefully pid=$($running.Id)"
