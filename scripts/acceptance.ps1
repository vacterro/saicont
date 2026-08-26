[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$unicodeMarker = 'T' + [char]0x00E4 + 'ht'
$stageRoot = [IO.Path]::GetFullPath((Join-Path $temporaryRoot ("SAICONT release ($unicodeMarker) " + [Guid]::NewGuid().ToString('N'))))
if (-not $stageRoot.StartsWith($temporaryRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing acceptance staging outside the temporary root: $stageRoot"
}

$primaryError = $null
try {
    New-Item -ItemType Directory -Path $stageRoot | Out-Null
    foreach ($file in @('build.ps1', 'SAICONT.config.xml', 'VERSION', 'README.md', 'CHANGELOG.md', '.gitignore')) {
        Copy-Item -LiteralPath (Join-Path $projectRoot $file) -Destination (Join-Path $stageRoot $file)
    }
    foreach ($directory in @('src', 'scripts', 'docs')) {
        Copy-Item -LiteralPath (Join-Path $projectRoot $directory) -Destination (Join-Path $stageRoot $directory) -Recurse
    }

    foreach ($generated in @('bin', 'run', 'logs')) {
        if (Test-Path -LiteralPath (Join-Path $stageRoot $generated)) {
            throw "Release staging unexpectedly contains generated path: $generated"
        }
    }
    Write-Output "PASS: Generated-artifact-free source staged at path with spaces, parentheses, and Unicode"

    & (Join-Path $stageRoot 'build.ps1') | Out-Host
    $executable = Join-Path $stageRoot 'bin\SAICONT.exe'
    $configuration = Join-Path $stageRoot 'SAICONT.config.xml'
    & $executable --validate-config --config $configuration | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'Clean-stage configuration validation failed.' }
    & $executable --self-test | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'Clean-stage deterministic tests failed.' }

    $landing = & $executable
    if (@($landing | Where-Object { $_ -match 'SAICONT / TERMINAL CONTINUITY' }).Count -ne 1) {
        throw 'TERMISAI landing page did not launch from the clean stage.'
    }
    Write-Output 'PASS: TERMISAI landing page launched from clean stage'

    $sourceConfigHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $configuration).Hash
    $builtConfigHash = (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $stageRoot 'bin\SAICONT.config.xml')).Hash
    if ($sourceConfigHash -ne $builtConfigHash) {
        throw 'Built configuration differs from canonical source XML.'
    }
    Write-Output 'PASS: Built configuration is byte-identical to canonical XML'

    & (Join-Path $stageRoot 'scripts\smoke.ps1') | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'Clean-stage smoke suite failed.' }

    $secretPatterns = @('ghp_[A-Za-z0-9]{20,}', 'github_pat_[A-Za-z0-9_]{20,}', 'sk-[A-Za-z0-9]{20,}', 'AKIA[0-9A-Z]{16}')
    $releaseFiles = Get-ChildItem -LiteralPath $stageRoot -Recurse -File | Where-Object {
        $_.FullName -notmatch '[\\/](bin|run|logs)[\\/]'
    }
    foreach ($pattern in $secretPatterns) {
        $credentialMatches = @($releaseFiles | Select-String -Pattern $pattern -List)
        if ($credentialMatches.Count -gt 0) {
            $firstMatch = $credentialMatches[0]
            throw "Potential credential pattern found in release source: pattern=$pattern file=$($firstMatch.Path) line=$($firstMatch.LineNumber)"
        }
    }
    Write-Output 'PASS: Release-source credential pattern scan clean'
    Write-Output 'STATUS: CLEAN_STAGE_ACCEPTANCE PASS'
}
catch {
    $primaryError = $_
}
finally {
    $cleanupError = $null
    $resolvedStage = [IO.Path]::GetFullPath($stageRoot)
    try {
        if ($resolvedStage.StartsWith($temporaryRoot, [StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $resolvedStage)) {
            $stagedExecutable = [IO.Path]::GetFullPath((Join-Path $resolvedStage 'bin\SAICONT.exe'))
            Get-CimInstance Win32_Process -Filter "Name='SAICONT.exe'" -ErrorAction SilentlyContinue | ForEach-Object {
                $actualExecutable = if ([String]::IsNullOrWhiteSpace([string]$_.ExecutablePath)) { '' } else { [IO.Path]::GetFullPath([string]$_.ExecutablePath) }
                if ([String]::Equals($actualExecutable, $stagedExecutable, [StringComparison]::OrdinalIgnoreCase)) {
                    Stop-Process -Id $_.ProcessId -Force -ErrorAction Stop
                    Wait-Process -Id $_.ProcessId -Timeout 5 -ErrorAction SilentlyContinue
                }
            }
            Remove-Item -LiteralPath $resolvedStage -Recurse -Force
        }
    }
    catch {
        $cleanupError = $_
    }
    if ($null -ne $primaryError) {
        if ($null -ne $cleanupError) {
            Write-Warning "Acceptance cleanup also failed: $($cleanupError.Exception.Message)"
        }
        throw $primaryError
    }
    if ($null -ne $cleanupError) {
        throw $cleanupError
    }
}
