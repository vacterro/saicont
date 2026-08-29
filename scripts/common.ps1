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
    }
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
            [Math]::Abs(($actualStart - $expectedStart).TotalMilliseconds) -gt 1000) {
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
