Set-StrictMode -Version 2.0

function Get-SaiContPaths {
    $projectRoot = Split-Path -Parent $PSScriptRoot
    [pscustomobject]@{
        ProjectRoot = $projectRoot
        Executable = Join-Path $projectRoot 'bin\SAICONT.exe'
        Configuration = Join-Path $projectRoot 'SAICONT.config.xml'
        Launcher = Join-Path $PSScriptRoot 'SAICONT.vbs'
        RunDirectory = Join-Path $projectRoot 'run'
        PidFile = Join-Path $projectRoot 'run\SAICONT.pid'
        InstanceFile = Join-Path $projectRoot 'run\SAICONT.instance.xml'
        StopFile = Join-Path $projectRoot 'run\SAICONT.stop'
        StateFile = Join-Path $projectRoot 'run\SAICONT.state.xml'
        HealthFile = Join-Path $projectRoot 'run\SAICONT.health.json'
    }
}

# CORE-006: ConvertFrom-Json silently rehydrates ISO-8601 strings into DateTime
# objects, and casting those to [string] yields the current culture's format. The
# integration seam must publish round-trip UTC, so normalize explicitly.
function Format-SaiContUtc {
    param([AllowNull()][object]$Value)
    if ($null -eq $Value) { return $null }
    if ($Value -is [DateTime]) { return ([DateTime]$Value).ToUniversalTime().ToString('o', [Globalization.CultureInfo]::InvariantCulture) }
    $text = [string]$Value
    if ([String]::IsNullOrWhiteSpace($text)) { return $null }
    try {
        return [DateTime]::Parse(
            $text,
            [Globalization.CultureInfo]::InvariantCulture,
            [Globalization.DateTimeStyles]::AdjustToUniversal -bor [Globalization.DateTimeStyles]::AssumeUniversal).ToString('o', [Globalization.CultureInfo]::InvariantCulture)
    }
    catch { return $text }
}

# CORE-006: read the machine health contract read-only and defensively. The
# documented integration seam has to answer "is this exact instance alive, is it
# healthy, and when is the next action due?", and it can only do that if a
# missing/truncated/foreign-schema snapshot is a NAMED disposition instead of an
# exception. Never mutates anything.
function Get-SaiContHealthSnapshot {
    param(
        [Parameter(Mandatory = $true)][psobject]$Paths,
        [int]$FreshnessSeconds = 120
    )

    if (-not (Test-Path -LiteralPath $Paths.HealthFile -PathType Leaf)) {
        return [pscustomobject]@{ Disposition = 'MISSING'; Health = $null; AgeSeconds = $null; Fresh = $false; Error = 'Health snapshot is absent.' }
    }

    try {
        $raw = Get-Content -Raw -Encoding UTF8 -LiteralPath $Paths.HealthFile
    }
    catch {
        return [pscustomobject]@{ Disposition = 'UNREADABLE'; Health = $null; AgeSeconds = $null; Fresh = $false; Error = $_.Exception.Message }
    }

    if ([String]::IsNullOrWhiteSpace($raw)) {
        return [pscustomobject]@{ Disposition = 'MALFORMED'; Health = $null; AgeSeconds = $null; Fresh = $false; Error = 'Health snapshot is empty.' }
    }

    try {
        $health = $raw | ConvertFrom-Json
    }
    catch {
        return [pscustomobject]@{ Disposition = 'MALFORMED'; Health = $null; AgeSeconds = $null; Fresh = $false; Error = 'Health snapshot is not valid JSON.' }
    }

    $schema = 0
    if (-not [int]::TryParse([string]$health.schema_version, [ref]$schema) -or $schema -ne 1) {
        return [pscustomobject]@{ Disposition = 'UNSUPPORTED_SCHEMA'; Health = $health; AgeSeconds = $null; Fresh = $false; Error = "Unsupported health schema_version '$([string]$health.schema_version)'." }
    }

    $ageSeconds = $null
    $fresh = $false
    if ($null -ne $health.health_updated_utc -and -not [String]::IsNullOrWhiteSpace([string]$health.health_updated_utc)) {
        try {
            $updated = if ($health.health_updated_utc -is [DateTime]) {
                ([DateTime]$health.health_updated_utc).ToUniversalTime()
            }
            else {
                [DateTime]::Parse(
                    [string]$health.health_updated_utc,
                    [Globalization.CultureInfo]::InvariantCulture,
                    [Globalization.DateTimeStyles]::AdjustToUniversal -bor [Globalization.DateTimeStyles]::AssumeUniversal)
            }
            $ageSeconds = [int][Math]::Round(([DateTime]::UtcNow - $updated).TotalSeconds)
            $fresh = ($ageSeconds -ge -5 -and $ageSeconds -le $FreshnessSeconds)
        }
        catch {
            return [pscustomobject]@{ Disposition = 'MALFORMED'; Health = $health; AgeSeconds = $null; Fresh = $false; Error = 'health_updated_utc is not a parsable timestamp.' }
        }
    }
    else {
        return [pscustomobject]@{ Disposition = 'MALFORMED'; Health = $health; AgeSeconds = $null; Fresh = $false; Error = 'health_updated_utc is absent.' }
    }

    $disposition = if ($fresh) { 'FRESH' } else { 'STALE' }
    return [pscustomobject]@{ Disposition = $disposition; Health = $health; AgeSeconds = $ageSeconds; Fresh = $fresh; Error = $null }
}

# CORE-006: join the verified lifecycle identity with the health snapshot. This
# is the cross-check docs/OPERATIONS.md promises: health PID/start must match the
# instance the runtime state already verified, otherwise the snapshot describes a
# different (possibly reused) process and must never be reported as verified.
function Get-SaiContVerifiedStatus {
    param(
        [Parameter(Mandatory = $true)][psobject]$Paths,
        [Parameter(Mandatory = $true)][psobject]$RuntimeState,
        [AllowNull()][psobject]$HealthSnapshot
    )

    $running = $RuntimeState.Process
    if ($RuntimeState.Disposition -eq 'UNVERIFIABLE') {
        return [pscustomobject]@{ Status = 'UNVERIFIABLE'; ExitCode = 2; HealthMatchesInstance = $false; Detail = $RuntimeState.Error }
    }
    if ($null -eq $running) {
        if ($RuntimeState.Disposition -eq 'POSITIVELY_STALE') {
            return [pscustomobject]@{ Status = 'POSITIVELY_STALE'; ExitCode = 1; HealthMatchesInstance = $false; Detail = $RuntimeState.Error }
        }
        return [pscustomobject]@{ Status = 'STOPPED_VERIFIED'; ExitCode = 1; HealthMatchesInstance = $false; Detail = $null }
    }

    if ($null -eq $HealthSnapshot -or $HealthSnapshot.Disposition -ne 'FRESH') {
        $reason = if ($null -eq $HealthSnapshot) { 'no health snapshot supplied' } else { "health snapshot is $($HealthSnapshot.Disposition)" }
        return [pscustomobject]@{ Status = 'UNVERIFIABLE'; ExitCode = 2; HealthMatchesInstance = $false; Detail = "Live verified instance but $reason." }
    }

    $health = $HealthSnapshot.Health
    $healthPid = 0
    [void][int]::TryParse([string]$health.process_id, [ref]$healthPid)
    $identityMatch = ($healthPid -eq [int]$running.Id)
    if ($identityMatch -and $null -ne $health.process_start_utc -and -not [String]::IsNullOrWhiteSpace([string]$health.process_start_utc)) {
        try {
            $healthStart = if ($health.process_start_utc -is [DateTime]) {
                ([DateTime]$health.process_start_utc).ToUniversalTime()
            }
            else {
                [DateTime]::Parse(
                    [string]$health.process_start_utc,
                    [Globalization.CultureInfo]::InvariantCulture,
                    [Globalization.DateTimeStyles]::AdjustToUniversal -bor [Globalization.DateTimeStyles]::AssumeUniversal)
            }
            # health_updated_utc is emitted at second resolution, so compare the
            # process start at the same resolution instead of on exact ticks.
            $identityMatch = ([Math]::Abs(($healthStart - $running.InstanceStartUtc).TotalSeconds) -le 1)
        }
        catch { $identityMatch = $false }
    }
    elseif ($identityMatch) {
        $identityMatch = $false
    }

    if (-not $identityMatch) {
        return [pscustomobject]@{ Status = 'UNVERIFIABLE'; ExitCode = 2; HealthMatchesInstance = $false; Detail = 'Health snapshot identity does not match the verified live instance.' }
    }
    return [pscustomobject]@{ Status = 'RUNNING_VERIFIED'; ExitCode = 0; HealthMatchesInstance = $true; Detail = $null }
}

function Get-SaiContProcessState {
    param(
        [Parameter(Mandatory = $true)]
        [psobject]$Paths
    )

    $hasMetadata = (Test-Path -LiteralPath $Paths.PidFile -PathType Leaf) -or (Test-Path -LiteralPath $Paths.InstanceFile -PathType Leaf)
    if (-not $hasMetadata) {
        return [pscustomobject]@{ Disposition = 'STOPPED_VERIFIED'; Process = $null; Error = $null }
    }

    $pidValue = 0
    $pidText = if (Test-Path -LiteralPath $Paths.PidFile -PathType Leaf) { (Get-Content -Raw -LiteralPath $Paths.PidFile).Trim() } else { '' }
    if (-not [int]::TryParse($pidText, [ref]$pidValue)) {
        return [pscustomobject]@{ Disposition = 'UNVERIFIABLE'; Process = $null; Error = 'PID metadata is malformed.' }
    }
    $candidate = Get-Process -Id $pidValue -ErrorAction SilentlyContinue
    if ($null -eq $candidate -or $candidate.HasExited) {
        return [pscustomobject]@{ Disposition = 'POSITIVELY_STALE'; Process = $null; Error = 'Recorded process is absent.' }
    }
    $verified = Get-SaiContProcess -Paths $Paths
    if ($null -ne $verified) {
        return [pscustomobject]@{ Disposition = 'RUNNING_VERIFIED'; Process = $verified; Error = $null }
    }
    return [pscustomobject]@{ Disposition = 'UNVERIFIABLE'; Process = $candidate; Error = 'Live process exists but identity metadata cannot be verified.' }
}

function Enter-SaiContLifecycleLock {
    $mutex = New-Object System.Threading.Mutex($false, 'Global\SAICONT_LifecycleOperations')
    if (-not $mutex.WaitOne(10000)) {
        $mutex.Dispose()
        throw 'Could not acquire SAICONT lifecycle operation lock.'
    }
    return $mutex
}

function Exit-SaiContLifecycleLock {
    param([Parameter(Mandatory = $true)][System.Threading.Mutex]$Mutex)
    try { $Mutex.ReleaseMutex() } catch { }
    $Mutex.Dispose()
}

function Remove-SaiContArtifactsIfOwned {
    param(
        [Parameter(Mandatory = $true)][psobject]$Paths,
        [Parameter(Mandatory = $true)][int]$ProcessId,
        [Parameter(Mandatory = $true)][DateTime]$ProcessStartUtc,
        [Parameter(Mandatory = $true)][string]$InstanceToken
    )
    $current = Get-SaiContProcess -Paths $Paths
    if ($null -eq $current -or $current.Id -ne $ProcessId -or
        $current.InstanceStartUtc -ne $ProcessStartUtc -or
        -not [String]::Equals([string]$current.InstanceToken, $InstanceToken, [StringComparison]::Ordinal)) {
        return $false
    }
    Remove-Item -LiteralPath $Paths.PidFile,$Paths.InstanceFile,$Paths.StopFile -Force -ErrorAction SilentlyContinue
    return $true
}

# W2L1-005: read the on-disk instance record WITHOUT requiring the process it
# describes to still be alive. Get-SaiContProcess deliberately couples identity
# verification to liveness, which is correct for "is it running?" and wrong for
# "did the process that owned these files leave them behind?".
function Get-SaiContInstanceRecord {
    param([Parameter(Mandatory = $true)][psobject]$Paths)

    if (-not (Test-Path -LiteralPath $Paths.InstanceFile -PathType Leaf)) { return $null }
    try {
        [xml]$instXml = Get-Content -Raw -Encoding UTF8 -LiteralPath $Paths.InstanceFile
        if ($instXml.saicontInstance.version -ne '1') { return $null }
        $startUtc = [DateTime]::Parse(
            [string]$instXml.saicontInstance.processStartUtc,
            [Globalization.CultureInfo]::InvariantCulture,
            [Globalization.DateTimeStyles]::RoundtripKind).ToUniversalTime()
        return [pscustomobject]@{
            ProcessId = [int]$instXml.saicontInstance.pid
            ProcessStartUtc = $startUtc
            InstanceToken = [string]$instXml.saicontInstance.instanceToken
            Mode = [string]$instXml.saicontInstance.mode
            ExecutablePath = [string]$instXml.saicontInstance.executablePath
        }
    }
    catch { return $null }
}

<#
W2L1-005: clean up lifecycle artifacts left behind by a process that is already
DEAD, matching them against the identity captured while it was still verified.

Why this exists: Remove-SaiContArtifactsIfOwned calls Get-SaiContProcess, which
returns null once the recorded process has exited. Both stop paths invoke cleanup
precisely AFTER proving the process exited -- and the forced path necessarily
killed it -- so ownership could never be proven, the helper returned $false, both
call sites discarded that result, and stop.ps1 still printed STOPPED while
SAICONT.pid / SAICONT.instance.xml / a token-owned SAICONT.stop remained on disk.

Safety: the records are deleted ONLY when they still describe the captured
identity (pid + start time + instance token). A record whose token changed
belongs to a NEW owner and is never touched, so a PID-reuse race cannot make one
stop operation delete a live instance's files. Returns a structured result so the
caller can report a degraded stop instead of an unconditional success.
#>
function Remove-SaiContArtifactsForDeadOwner {
    param(
        [Parameter(Mandatory = $true)][psobject]$Paths,
        [Parameter(Mandatory = $true)][int]$ProcessId,
        [Parameter(Mandatory = $true)][DateTime]$ProcessStartUtc,
        [Parameter(Mandatory = $true)][string]$InstanceToken
    )

    $removed = New-Object System.Collections.Generic.List[string]
    $retained = New-Object System.Collections.Generic.List[string]
    $errors = New-Object System.Collections.Generic.List[string]

    $record = Get-SaiContInstanceRecord -Paths $Paths
    $instanceMatches = $false
    if ($null -ne $record) {
        $instanceMatches = ($record.ProcessId -eq $ProcessId -and
            $record.ProcessStartUtc.Ticks -eq $ProcessStartUtc.Ticks -and
            [String]::Equals([string]$record.InstanceToken, $InstanceToken, [StringComparison]::Ordinal))
    }

    if ($null -eq $record) {
        if (Test-Path -LiteralPath $Paths.InstanceFile -PathType Leaf) {
            $retained.Add('SAICONT.instance.xml (unreadable; not ours to delete)')
        }
    }
    elseif (-not $instanceMatches) {
        $retained.Add('SAICONT.instance.xml (belongs to a different owner)')
    }
    else {
        try {
            Remove-Item -LiteralPath $Paths.InstanceFile -Force -ErrorAction Stop
            $removed.Add('SAICONT.instance.xml')
        }
        catch { $errors.Add("SAICONT.instance.xml: $($_.Exception.Message)") }
    }

    # The PID file carries only a number, so the instance record is what proves
    # ownership. Without a matching instance record the PID file stays put.
    if (Test-Path -LiteralPath $Paths.PidFile -PathType Leaf) {
        $pidValue = 0
        $pidText = ''
        try { $pidText = (Get-Content -Raw -LiteralPath $Paths.PidFile).Trim() } catch { }
        if ($instanceMatches -and [int]::TryParse($pidText, [ref]$pidValue) -and $pidValue -eq $ProcessId) {
            try {
                Remove-Item -LiteralPath $Paths.PidFile -Force -ErrorAction Stop
                $removed.Add('SAICONT.pid')
            }
            catch { $errors.Add("SAICONT.pid: $($_.Exception.Message)") }
        }
        else {
            $retained.Add('SAICONT.pid (does not match the captured owner)')
        }
    }

    # The stop file is self-identifying: its content IS the instance token.
    if (Test-Path -LiteralPath $Paths.StopFile -PathType Leaf) {
        $stopContent = ''
        try { $stopContent = (Get-Content -Raw -LiteralPath $Paths.StopFile).Trim() } catch { }
        if ([String]::Equals($stopContent, $InstanceToken, [StringComparison]::Ordinal)) {
            try {
                Remove-Item -LiteralPath $Paths.StopFile -Force -ErrorAction Stop
                $removed.Add('SAICONT.stop')
            }
            catch { $errors.Add("SAICONT.stop: $($_.Exception.Message)") }
        }
        else {
            $retained.Add('SAICONT.stop (token belongs to another instance)')
        }
    }

    $clean = ($errors.Count -eq 0 -and $retained.Count -eq 0)
    return [pscustomobject]@{
        Clean = $clean
        Removed = $removed.ToArray()
        Retained = $retained.ToArray()
        Errors = $errors.ToArray()
    }
}

function Get-SaiContProcess {
    param(
        [Parameter(Mandatory = $true)]
        [psobject]$Paths
    )

    if (-not (Test-Path -LiteralPath $Paths.PidFile -PathType Leaf)) {
        return $null
    }

    $processIdValue = 0
    $pidText = (Get-Content -Raw -LiteralPath $Paths.PidFile).Trim()
    if (-not [int]::TryParse($pidText, [ref]$processIdValue)) {
        return $null
    }

    $candidate = Get-Process -Id $processIdValue -ErrorAction SilentlyContinue
    if ($null -eq $candidate -or $candidate.HasExited) {
        return $null
    }

    try {
        $expectedPath = [IO.Path]::GetFullPath($Paths.Executable)
        $actualPath = [IO.Path]::GetFullPath($candidate.Path)
        if (-not [String]::Equals($expectedPath, $actualPath, [StringComparison]::OrdinalIgnoreCase)) {
            return $null
        }
    }
    catch {
        return $null
    }

    if (-not (Test-Path -LiteralPath $Paths.InstanceFile -PathType Leaf)) {
        return $null
    }

    try {
        # Windows PowerShell 5.1 otherwise decodes BOM-less UTF-8 as ANSI,
        # which breaks identity checks when the install path contains Unicode.
        [xml]$instXml = Get-Content -Raw -Encoding UTF8 -LiteralPath $Paths.InstanceFile
        if ($instXml.saicontInstance.version -ne '1') {
            return $null
        }
        $recordedPid = [int]$instXml.saicontInstance.pid
        $instanceToken = [string]$instXml.saicontInstance.instanceToken
        $mode = [string]$instXml.saicontInstance.mode
        $recordedPath = [IO.Path]::GetFullPath([string]$instXml.saicontInstance.executablePath)
        $expectedStart = [DateTime]::Parse(
            [string]$instXml.saicontInstance.processStartUtc,
            [Globalization.CultureInfo]::InvariantCulture,
            [Globalization.DateTimeStyles]::RoundtripKind).ToUniversalTime()
        $actualStart = $candidate.StartTime.ToUniversalTime()
        if ($recordedPid -ne $candidate.Id -or
            [String]::IsNullOrWhiteSpace($instanceToken) -or
            -not [String]::Equals($expectedPath, $recordedPath, [StringComparison]::OrdinalIgnoreCase) -or
            $actualStart.Ticks -ne $expectedStart.Ticks) {
            return $null
        }
    }
    catch {
        return $null
    }

    $candidate | Add-Member -NotePropertyName 'InstanceToken' -NotePropertyValue $instanceToken -Force
    $candidate | Add-Member -NotePropertyName 'Mode' -NotePropertyValue $mode -Force
    $candidate | Add-Member -NotePropertyName 'InstanceStartUtc' -NotePropertyValue $expectedStart -Force
    return $candidate
}
