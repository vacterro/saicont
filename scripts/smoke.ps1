[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$executablePath = Join-Path $projectRoot 'bin\SAICONT.exe'
$configurationPath = Join-Path $projectRoot 'SAICONT.config.xml'
. (Join-Path $PSScriptRoot 'common.ps1')
$paths = Get-SaiContPaths
$stateExistedBefore = Test-Path -LiteralPath $paths.StateFile -PathType Leaf
$stateHashBefore = if ($stateExistedBefore) { (Get-FileHash -Algorithm SHA256 -LiteralPath $paths.StateFile).Hash } else { $null }

$runtimeState = Get-SaiContProcessState -Paths $paths
if ($runtimeState.Disposition -eq 'RUNNING_VERIFIED') {
    throw "Refusing smoke test while SAICONT PID $($runtimeState.Process.Id) is already running."
}
if ($runtimeState.Disposition -eq 'UNVERIFIABLE') {
    throw "Refusing smoke test: live runtime identity is unverifiable. $($runtimeState.Error)"
}
if ($runtimeState.Disposition -eq 'POSITIVELY_STALE') {
    & (Join-Path $PSScriptRoot 'status.ps1') -Repair | Out-Host
    if ((Get-SaiContProcessState -Paths $paths).Disposition -ne 'STOPPED_VERIFIED') {
        throw 'Refusing smoke test: stale runtime repair did not produce verified stopped state.'
    }
}

Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.ps1' -File | ForEach-Object {
    [void][scriptblock]::Create((Get-Content -Raw -LiteralPath $_.FullName))
}
Write-Output 'PASS: PowerShell parser checks'

& (Join-Path $projectRoot 'build.ps1') | Out-Host
& $executablePath --self-test
if ($LASTEXITCODE -ne 0) {
    throw "Self-test failed with exit code $LASTEXITCODE"
}

Write-Output 'Configuration validation preflight:'
& $executablePath --validate-config --config $configurationPath
if ($LASTEXITCODE -ne 0) {
    throw "Configuration validation failed with exit code $LASTEXITCODE"
}

Write-Output 'Controlled input injection harness test:'
$harnessFile = [System.IO.Path]::GetTempFileName()
try {
    $harnessProcess = Start-Process -FilePath $executablePath -ArgumentList @('--input-harness', $harnessFile) -PassThru
    Start-Sleep -Milliseconds 400
    $startTicks = $harnessProcess.StartTime.ToUniversalTime().Ticks
    & $executablePath --verified-harness-inject $harnessProcess.Id $startTicks 'must_not_arrive' 1 'wrong-start' | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw 'Verified harness wrong-start negative control failed.'
    }
    & $executablePath --verified-harness-inject $harnessProcess.Id $startTicks 'must_not_arrive' 1 'wrong-membership' | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw 'Verified harness wrong-membership negative control failed.'
    }
    $injectOutput = & $executablePath --verified-harness-inject $harnessProcess.Id $startTicks 'smoke_harness_token' 25 'normal'
    $injectExit = $LASTEXITCODE
    $null = $harnessProcess.WaitForExit(5000)
    $receivedText = if (Test-Path -LiteralPath $harnessFile) { (Get-Content -Raw -LiteralPath $harnessFile).Trim() } else { '' }
    if ($injectExit -ne 0 -or $harnessProcess.ExitCode -ne 0 -or $receivedText -ne 'smoke_harness_token') {
        throw "Input injection harness failed: exit=$injectExit out=$injectOutput received=$receivedText"
    }
    $injectOutput | Out-Host
    Write-Output "PASS: Controlled verified-write harness received exactly one line (PID $($harnessProcess.Id))"

    $vanishedFile = $harnessFile + '.vanished'
    $vanished = Start-Process -FilePath $executablePath -ArgumentList @('--input-harness', $vanishedFile) -PassThru
    Start-Sleep -Milliseconds 300
    $vanishedTicks = $vanished.StartTime.ToUniversalTime().Ticks
    Stop-Process -Id $vanished.Id -Force
    $null = $vanished.WaitForExit(5000)
    $savedErrorActionPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        & $executablePath --verified-harness-inject $vanished.Id $vanishedTicks 'must_not_arrive' 1 'normal' 2>$null | Out-Null
        $vanishedInjectExit = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $savedErrorActionPreference
    }
    if ($vanishedInjectExit -eq 0) {
        throw 'Verified harness process-disappeared negative control unexpectedly wrote input.'
    }
    Remove-Item -LiteralPath $vanishedFile -Force -ErrorAction SilentlyContinue
    Write-Output 'PASS: Controlled verified-write process-disappeared race refused'
}
finally {
    if (Test-Path -LiteralPath $harnessFile) {
        Remove-Item -LiteralPath $harnessFile -Force -ErrorAction SilentlyContinue
    }
}

Write-Output 'Cline-first live probe (read-only):'
$probeOutput = & $executablePath --probe --config $configurationPath
$probeExit = $LASTEXITCODE
$probeOutput | Where-Object { $_ -match 'PROBE|TOTAL|RESULT' } | Out-Host
if ($probeExit -ge 2) {
    throw "Probe FAILED (exit $probeExit): a discovered Cline/Codex console could not be read. Investigate or remove the stale target process, then rerun."
}
if ($probeExit -eq 1) {
    Write-Warning 'No matching live Cline console was available; deterministic checks still passed.'
}
if (@($probeOutput | Where-Object { $_ -match '^ERROR ' }).Count -gt 0) {
    throw 'Probe reported console attachment errors for a discovered target; refusing a PASS.'
}

$launchAttempted = $false
try {
    $launchAttempted = $true
    & (Join-Path $PSScriptRoot 'start.ps1') -DryRun
    & (Join-Path $PSScriptRoot 'status.ps1')
    if ($LASTEXITCODE -ne 0) {
        throw 'Dry-run lifecycle status check failed.'
    }
    Start-Sleep -Seconds 3
    [IO.File]::WriteAllText($paths.StopFile, 'FOREIGN-STALE-TOKEN', [Text.Encoding]::ASCII)
    Start-Sleep -Milliseconds 400
    & (Join-Path $PSScriptRoot 'status.ps1')
    if ($LASTEXITCODE -ne 0) {
        throw 'Foreign stale stop token terminated the current instance.'
    }
    Write-Output 'PASS: Foreign stale stop token did not terminate current instance'

    $duplicateRejected = $false
    try {
        & (Join-Path $PSScriptRoot 'start.ps1') -DryRun
    }
    catch {
        $duplicateRejected = $true
    }
    if (-not $duplicateRejected) {
        throw 'Duplicate dry-run start was not rejected.'
    }
    Write-Output 'PASS: Duplicate continuous start rejected'

    & (Join-Path $PSScriptRoot 'status.ps1')
    if ($LASTEXITCODE -ne 0) {
        throw 'Dry-run lifecycle died during multi-poll cycle.'
    }
}
finally {
    if ($launchAttempted) {
        & (Join-Path $PSScriptRoot 'stop.ps1')
    }
}

if (Test-Path -LiteralPath $paths.PidFile -PathType Leaf) {
    throw "PID file was not cleaned up after stop: $($paths.PidFile)"
}
if (Test-Path -LiteralPath $paths.StopFile -PathType Leaf) {
    throw "Stop file was not cleaned up after stop: $($paths.StopFile)"
}

$stateExistsAfter = Test-Path -LiteralPath $paths.StateFile -PathType Leaf
$stateHashAfter = if ($stateExistsAfter) { (Get-FileHash -Algorithm SHA256 -LiteralPath $paths.StateFile).Hash } else { $null }
if ($stateExistedBefore -ne $stateExistsAfter -or $stateHashBefore -ne $stateHashAfter) {
    throw 'Probe/dry-run smoke mutated the production durable retry state.'
}
Write-Output 'PASS: Probe and dry-run left production retry state unchanged'

$shellPath = (Get-Process -Id $PID).Path
if ((Get-SaiContProcessState -Paths $paths).Disposition -ne 'STOPPED_VERIFIED') {
    throw 'Refusing synthetic lifecycle fixtures: runtime is not verified stopped.'
}
[IO.File]::WriteAllText($paths.PidFile, '999999', [Text.Encoding]::ASCII)
[IO.File]::WriteAllText($paths.InstanceFile, '<broken>', [Text.Encoding]::UTF8)
[IO.File]::WriteAllText($paths.StopFile, 'STALE', [Text.Encoding]::ASCII)
& $shellPath -NoProfile -File (Join-Path $PSScriptRoot 'status.ps1') | Out-Host
if ($LASTEXITCODE -ne 1) {
    throw "Stale runtime status expected exit 1, got $LASTEXITCODE"
}
& $shellPath -NoProfile -File (Join-Path $PSScriptRoot 'status.ps1') -Repair | Out-Host
if ($LASTEXITCODE -ne 1 -or (Test-Path -LiteralPath $paths.PidFile) -or (Test-Path -LiteralPath $paths.InstanceFile) -or (Test-Path -LiteralPath $paths.StopFile)) {
    throw 'Stale runtime artifact repair failed.'
}
Write-Output 'PASS: Stale PID/instance/stop artifacts detected and repaired without touching durable state'

$taskBefore = Get-ScheduledTask -TaskName 'SAICONT' -ErrorAction SilentlyContinue
& $shellPath -NoProfile -File (Join-Path $PSScriptRoot 'install.ps1') -WhatIf | Out-Host
if ($LASTEXITCODE -ne 0) {
    throw "Install -WhatIf failed with exit code $LASTEXITCODE"
}
$taskAfter = Get-ScheduledTask -TaskName 'SAICONT' -ErrorAction SilentlyContinue
if (($null -eq $taskBefore) -ne ($null -eq $taskAfter)) {
    throw 'Install -WhatIf changed scheduled-task presence.'
}
Write-Output 'PASS: Install -WhatIf made no scheduled-task change'

Write-Output 'PASS: Cline-first smoke suite'
