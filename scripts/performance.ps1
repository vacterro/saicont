[CmdletBinding()]
param(
    [ValidateRange(4, 60)]
    [int]$DurationSeconds = 6
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$executable = Join-Path $projectRoot 'bin\SAICONT.exe'
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
    & (Join-Path $projectRoot 'build.ps1') | Out-Host
}

# PERF-005: exercise the embedded self-test path so the production
# engine actually runs deterministic active-console scenarios (multi-rule
# send, memory soak, etc.) and emits MEASURE lines. The PowerShell layer
# captures the structural metrics and adds a one-core-equivalent CPU
# measurement that the multi-core normalization in the prior script hid.

$temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$testRoot = [IO.Path]::GetFullPath((Join-Path $temporaryRoot ('SAICONT-perf-' + [Guid]::NewGuid().ToString('N'))))
if (-not $testRoot.StartsWith($temporaryRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing performance staging outside temporary root: $testRoot"
}

$selfTestOutput = & $executable --self-test 2>&1
$selfTestExit = $LASTEXITCODE
if ($selfTestExit -ne 0) {
    $selfTestOutput | ForEach-Object { Write-Output $_ }
    throw "Embedded self-test returned non-zero exit code: $selfTestExit"
}
$selfTestOutput | ForEach-Object { Write-Output $_ }

$measureLines = @($selfTestOutput | Where-Object { $_ -like 'MEASURE:*' })

function Get-MeasureValue {
    param([string]$Line, [string]$Key)
    $pattern = '(?<=^|\s)' + [regex]::Escape($Key) + '=([0-9.+\-eE]+)'
    $match = [regex]::Match($Line, $pattern)
    if ($match.Success) { return [double]$match.Groups[1].Value }
    return $null
}

$perfPoll = $measureLines | Where-Object { $_ -like 'MEASURE: perf_poll*' } | Select-Object -First 1
$perfMulti = $measureLines | Where-Object { $_ -like 'MEASURE: perf_multi*' } | Select-Object -First 1
$perfMem = $measureLines | Where-Object { $_ -like 'MEASURE: perf_mem*' } | Select-Object -First 1
$soak = $measureLines | Where-Object { $_ -like 'MEASURE: soak*' } | Select-Object -First 1

if (-not $perfPoll) { throw 'Embedded self-test did not emit perf_poll MEASURE line.' }
if (-not $perfMulti) { throw 'Embedded self-test did not emit perf_multi MEASURE line.' }
if (-not $perfMem) { throw 'Embedded self-test did not emit perf_mem MEASURE line.' }

$pollAvg = Get-MeasureValue $perfPoll 'avg_ms'
$pollMedian = Get-MeasureValue $perfPoll 'median_ms'
$pollP95 = Get-MeasureValue $perfPoll 'p95_ms'
$pollMax = Get-MeasureValue $perfPoll 'max_ms'
$oneCorePct = Get-MeasureValue $perfPoll 'one_core_cpu_pct'

$multiWrites = Get-MeasureValue $perfMulti 'writes'
$multiStates = Get-MeasureValue $perfMulti 'states'

$memDelta = Get-MeasureValue $perfMem 'managed_delta_bytes'

Write-Output ('MEASURE: idle_scenario poll_avg_ms={0:F2} poll_median_ms={1:F2} poll_p95_ms={2:F2} poll_max_ms={3:F2} one_core_cpu_pct={4:F2}' -f $pollAvg, $pollMedian, $pollP95, $pollMax, $oneCorePct)
Write-Output ('MEASURE: active_scenario multi_writes={0} multi_states={1}' -f $multiWrites, $multiStates)
Write-Output ('MEASURE: memory_scenario managed_delta_bytes={0}' -f $memDelta)
if ($soak) {
    Write-Output ('MEASURE: soak_scenario ' + $soak.Substring('MEASURE: '.Length))
}

# Process-level idle measurement against the same executable running
# against an idle target, so we can also report working set / handles
# without conflating it with the deterministic self-test path.
try {
    New-Item -ItemType Directory -Path $testRoot | Out-Null
    $configPath = Join-Path $testRoot 'perf.config.xml'
    $logPath = Join-Path $testRoot 'perf.log'
    $pidPath = Join-Path $testRoot 'perf.pid'
    $stopPath = Join-Path $testRoot 'perf.stop'
    $statePath = Join-Path $testRoot 'perf.state.xml'
    $instancePath = Join-Path $testRoot 'perf.instance.xml'
    $config = @"
<?xml version="1.0" encoding="utf-8"?>
<saicont pollIntervalMilliseconds="2000">
  <logging path="$logPath" maxBytes="1048576" retainedFiles="2" duplicateWindowSeconds="60" />
  <targets>
    <target name="idle-budget" enabled="true" command="cc" scanLines="180" maximumTriggerDistanceLines="150" initialDelaySeconds="60" retryIntervalSeconds="60" parseRetryTime="false">
      <processNames><process>saicont-no-such-target</process></processNames>
      <triggerPatterns><pattern>(?i)never-match</pattern></triggerPatterns>
      <readyPatterns><pattern>^ready$</pattern></readyPatterns>
      <busyPatterns />
    </target>
  </targets>
</saicont>
"@
    [IO.File]::WriteAllText($configPath, $config, [Text.UTF8Encoding]::new($false))

    $arguments = @(
        '--dry-run', '--config', $configPath,
        '--pid-file', $pidPath, '--stop-file', $stopPath,
        '--state-file', $statePath, '--instance-file', $instancePath)
    $watcher = Start-Process -FilePath $executable -ArgumentList $arguments -PassThru -WindowStyle Hidden
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    while (-not (Test-Path -LiteralPath $instancePath -PathType Leaf) -and [DateTime]::UtcNow -lt $deadline) {
        Start-Sleep -Milliseconds 100
    }
    if (-not (Test-Path -LiteralPath $instancePath -PathType Leaf)) {
        throw 'Performance watcher did not create an instance record.'
    }

    Start-Sleep -Milliseconds 500
    $watcher.Refresh()
    $cpuBefore = $watcher.TotalProcessorTime
    $workingSetBefore = $watcher.WorkingSet64
    $handlesBefore = $watcher.HandleCount
    $measurement = [Diagnostics.Stopwatch]::StartNew()
    Start-Sleep -Seconds $DurationSeconds
    $measurement.Stop()
    $watcher.Refresh()
    $cpuAfter = $watcher.TotalProcessorTime
    $workingSetAfter = $watcher.WorkingSet64
    $handlesAfter = $watcher.HandleCount

    [xml]$instance = Get-Content -Raw -Encoding UTF8 -LiteralPath $instancePath
    $token = [string]$instance.saicontInstance.instanceToken
    [IO.File]::WriteAllText($stopPath, $token, [Text.Encoding]::ASCII)
    $stopTimer = [Diagnostics.Stopwatch]::StartNew()
    if (-not $watcher.WaitForExit(5000)) {
        throw 'Performance watcher did not stop gracefully within five seconds.'
    }
    $stopTimer.Stop()

    $cpuMilliseconds = ($cpuAfter - $cpuBefore).TotalMilliseconds
    $oneCoreCpuPctIdle = if ($measurement.ElapsedMilliseconds -gt 0) {
        100.0 * $cpuMilliseconds / $measurement.ElapsedMilliseconds
    } else { 0.0 }
    $multiCoreCpuPct = if ($measurement.ElapsedMilliseconds -gt 0) {
        100.0 * $cpuMilliseconds / ($measurement.ElapsedMilliseconds * [Environment]::ProcessorCount)
    } else { 0.0 }
    $workingSetDelta = $workingSetAfter - $workingSetBefore
    $handleDelta = $handlesAfter - $handlesBefore

    Write-Output ('MEASURE: idle_process duration_ms={0} cpu_ms={1:F3} one_core_cpu_pct={2:F4} multi_core_cpu_pct={3:F4} working_set_start={4} working_set_end={5} working_set_delta={6} handles_start={7} handles_end={8} handle_delta={9} stop_latency_ms={10}' -f
        $measurement.ElapsedMilliseconds, $cpuMilliseconds, $oneCoreCpuPctIdle, $multiCoreCpuPct, $workingSetBefore, $workingSetAfter, $workingSetDelta, $handlesBefore, $handlesAfter, $handleDelta, $stopTimer.ElapsedMilliseconds)

    if ($oneCoreCpuPctIdle -ge 25.0) { throw "Idle one-core CPU budget exceeded: $oneCoreCpuPctIdle%" }
    if ($workingSetDelta -ge 16MB) { throw "Working-set growth budget exceeded: $workingSetDelta bytes" }
    if ($handleDelta -gt 4) { throw "Handle growth budget exceeded: $handleDelta" }
    if ($stopTimer.ElapsedMilliseconds -ge 1500) { throw "Stop latency budget exceeded: $($stopTimer.ElapsedMilliseconds) ms" }
    if ($pollAvg -ge 50.0) { throw "Average poll budget exceeded: $pollAvg ms" }
    if ($pollP95 -ge 150.0) { throw "P95 poll budget exceeded: $pollP95 ms" }
    if ($multiWrites -ne 5) { throw "Active scenario must produce exactly five writes, got $multiWrites" }
    if ($memDelta -ge 8MB) { throw "Memory-scenario budget exceeded: $memDelta bytes" }
    Write-Output 'STATUS: PERFORMANCE_BUDGET PASS'
}
finally {
    if ($null -ne $watcher -and -not $watcher.HasExited) {
        Stop-Process -Id $watcher.Id -Force -ErrorAction SilentlyContinue
        $null = $watcher.WaitForExit(5000)
    }
    $resolvedTestRoot = [IO.Path]::GetFullPath($testRoot)
    if ($resolvedTestRoot.StartsWith($temporaryRoot, [StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $resolvedTestRoot)) {
        Remove-Item -LiteralPath $resolvedTestRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
