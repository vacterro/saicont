[CmdletBinding(SupportsShouldProcess = $true)]
param()

$ErrorActionPreference = 'Stop'
$taskName = 'SAICONT'

if (-not $PSCmdlet.ShouldProcess($taskName, 'Stop SAICONT and remove its scheduled task')) {
    exit 0
}

& (Join-Path $PSScriptRoot 'stop.ps1')
$stopExit = $LASTEXITCODE
if ($stopExit -ne 0) {
    throw "stop.ps1 failed (LASTEXITCODE=$stopExit), NOT removing task"
}
$task = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
if ($null -ne $task) {
    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false
}
Write-Output "UNINSTALLED task=$taskName; configuration, logs, and durable state preserved"
