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

$temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$testRoot = [IO.Path]::GetFullPath((Join-Path $temporaryRoot ('SAICONT-perf-' + [Guid]::NewGuid().ToString('N'))))
if (-not $testRoot.StartsWith($temporaryRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing performance staging outside temporary root: $testRoot"
}

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
    $cpuPercent = if ($measurement.ElapsedMilliseconds -gt 0) {
        100.0 * $cpuMilliseconds / ($measurement.ElapsedMilliseconds * [Environment]::ProcessorCount)
    }
    else { 0.0 }
    $workingSetDelta = $workingSetAfter - $workingSetBefore
    $handleDelta = $handlesAfter - $handlesBefore

    Write-Output ('MEASURE: idle duration_ms={0} cpu_ms={1:F3} cpu_percent={2:F4} working_set_start={3} working_set_end={4} working_set_delta={5} handles_start={6} handles_end={7} handle_delta={8} stop_latency_ms={9}' -f
        $measurement.ElapsedMilliseconds, $cpuMilliseconds, $cpuPercent, $workingSetBefore, $workingSetAfter, $workingSetDelta, $handlesBefore, $handlesAfter, $handleDelta, $stopTimer.ElapsedMilliseconds)

    if ($cpuPercent -ge 5.0) { throw "Idle CPU budget exceeded: $cpuPercent%" }
    if ($workingSetDelta -ge 16MB) { throw "Working-set growth budget exceeded: $workingSetDelta bytes" }
    if ($handleDelta -gt 4) { throw "Handle growth budget exceeded: $handleDelta" }
    if ($stopTimer.ElapsedMilliseconds -ge 1500) { throw "Stop latency budget exceeded: $($stopTimer.ElapsedMilliseconds) ms" }
    Write-Output 'STATUS: PERFORMANCE_BUDGET PASS'
}
finally {
    if ($null -ne $watcher -and -not $watcher.HasExited) {
        Stop-Process -Id $watcher.Id -Force -ErrorAction SilentlyContinue
        $null = $watcher.WaitForExit(5000)
    }
    $resolvedTestRoot = [IO.Path]::GetFullPath($testRoot)
    if ($resolvedTestRoot.StartsWith($temporaryRoot, [StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $resolvedTestRoot)) {
        Remove-Item -LiteralPath $resolvedTestRoot -Recurse -Force
    }
}
