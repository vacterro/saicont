[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')
$paths = Get-SaiContPaths
$taskName = 'SAICONT'
$identity = [Security.Principal.WindowsIdentity]::GetCurrent().Name

if (-not $PSCmdlet.ShouldProcess($taskName, 'Build, register a hidden logon task, and start SAICONT')) {
    exit 0
}

& (Join-Path $paths.ProjectRoot 'build.ps1') | Out-Host
& $paths.Executable --validate-config --config $paths.Configuration | Out-Host
if ($LASTEXITCODE -ne 0) {
    throw "Configuration preflight failed with exit code $LASTEXITCODE"
}
& $paths.Executable --self-test | Out-Host
if ($LASTEXITCODE -ne 0) {
    throw "Self-test preflight failed with exit code $LASTEXITCODE"
}

$wscriptPath = Join-Path $env:WINDIR 'System32\wscript.exe'
$taskArguments = "//B //Nologo `"$($paths.Launcher)`""
$action = New-ScheduledTaskAction -Execute $wscriptPath -Argument $taskArguments
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $identity
$principal = New-ScheduledTaskPrincipal -UserId $identity -LogonType Interactive -RunLevel Limited
$settings = New-ScheduledTaskSettingsSet -MultipleInstances IgnoreNew -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1)
$previousTask = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
$previousTaskXml = if ($null -ne $previousTask) { Export-ScheduledTask -TaskName $taskName } else { $null }

try {
    & (Join-Path $PSScriptRoot 'stop.ps1') | Out-Host
    Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Force | Out-Null
    if ($DryRun) {
        & (Join-Path $PSScriptRoot 'start.ps1') -DryRun | Out-Host
    }
    else {
        & (Join-Path $PSScriptRoot 'start.ps1') | Out-Host
    }
    & (Join-Path $PSScriptRoot 'status.ps1') | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw 'Installed task did not produce a verified running instance.'
    }
}
catch {
    if ($null -ne $previousTaskXml) {
        Register-ScheduledTask -TaskName $taskName -Xml $previousTaskXml -Force | Out-Null
    }
    else {
        Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
    }
    throw
}
Write-Output "INSTALLED task=$taskName user=$identity"
