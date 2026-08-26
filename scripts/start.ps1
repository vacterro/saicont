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

$running = Get-SaiContProcess -Paths $paths
if ($null -ne $running) {
    if ($DryRun) {
        throw "SAICONT is already running as PID $($running.Id); stop it before a dry-run smoke test."
    }
    Write-Output "RUNNING pid=$($running.Id)"
    exit 0
}

if (-not (Test-Path -LiteralPath $paths.RunDirectory -PathType Container)) {
    New-Item -ItemType Directory -Path $paths.RunDirectory | Out-Null
}
Remove-Item -LiteralPath $paths.PidFile -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $paths.InstanceFile -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $paths.StopFile -Force -ErrorAction SilentlyContinue

$wscriptPath = Join-Path $env:WINDIR 'System32\wscript.exe'
$launcherArguments = @('//B', '//Nologo', $paths.Launcher)
if ($DryRun) {
    $launcherArguments += '--dry-run'
}
& $wscriptPath @launcherArguments
$launcherExitCode = Get-Variable -Name LASTEXITCODE -ValueOnly -ErrorAction SilentlyContinue
if ($null -ne $launcherExitCode -and $launcherExitCode -ne 0) {
    throw "Hidden launcher failed with exit code $launcherExitCode"
}

$deadlineUtc = [DateTime]::UtcNow.AddSeconds(10)
do {
    Start-Sleep -Milliseconds 200
    $running = Get-SaiContProcess -Paths $paths
    if ($null -ne $running) {
        $mode = if ($DryRun) { 'dry-run' } else { 'watch' }
        Write-Output "STARTED mode=$mode pid=$($running.Id)"
        exit 0
    }
} while ([DateTime]::UtcNow -lt $deadlineUtc)

throw "SAICONT did not create a valid PID file. Check logs\SAICONT.log and the configuration."
