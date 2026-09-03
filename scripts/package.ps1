[CmdletBinding()]
param([string]$OutputDirectory, [switch]$Rebuild)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$version = (Get-Content -Raw -LiteralPath (Join-Path $projectRoot 'VERSION')).Trim()
if ([String]::IsNullOrWhiteSpace($OutputDirectory)) { $OutputDirectory = Join-Path $projectRoot 'dist' }
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
[IO.Directory]::CreateDirectory($outputRoot) | Out-Null

$executable = Join-Path $projectRoot 'bin\SAICONT.exe'
if ($Rebuild -or -not (Test-Path -LiteralPath $executable -PathType Leaf)) {
    & (Join-Path $projectRoot 'build.ps1') | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE" }
}

$files = [ordered]@{
    'bin\SAICONT.exe' = 'bin\SAICONT.exe'
    'SAICONT.config.xml' = 'SAICONT.config.xml'
    'README.md' = 'README.md'
    'CHANGELOG.md' = 'CHANGELOG.md'
    'VERSION' = 'VERSION'
    'docs\OPERATIONS.md' = 'docs\OPERATIONS.md'
    'SAICONT_GUI.cmd' = 'SAICONT_GUI.cmd'
    'SAICONT_TERMINAL.cmd' = 'SAICONT_TERMINAL.cmd'
    'SAICONT_WIN.cmd' = 'SAICONT_WIN.cmd'
    'scripts\common.ps1' = 'scripts\common.ps1'
    'scripts\gui_win.ps1' = 'scripts\gui_win.ps1'
    'scripts\gui.cmd' = 'scripts\gui.cmd'
    'scripts\gui.ps1' = 'scripts\gui.ps1'
    'scripts\install.ps1' = 'scripts\install.ps1'
    'scripts\SAICONT.vbs' = 'scripts\SAICONT.vbs'
    'scripts\start.ps1' = 'scripts\start.ps1'
    'scripts\status.ps1' = 'scripts\status.ps1'
    'scripts\stop.ps1' = 'scripts\stop.ps1'
    'scripts\terminal.ps1' = 'scripts\terminal.ps1'
    'scripts\uninstall.ps1' = 'scripts\uninstall.ps1'
}

$stage = Join-Path ([IO.Path]::GetTempPath()) ('SAICONT-package-' + [Guid]::NewGuid().ToString('N'))
$fixedTime = [DateTime]::SpecifyKind([DateTime]'2000-01-01T00:00:00', [DateTimeKind]::Utc)
try {
    foreach ($entry in $files.GetEnumerator()) {
        $source = Join-Path $projectRoot $entry.Key
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Missing package input: $($entry.Key)" }
        $destination = Join-Path $stage $entry.Value
        [IO.Directory]::CreateDirectory((Split-Path -Parent $destination)) | Out-Null
        Copy-Item -LiteralPath $source -Destination $destination
        (Get-Item -LiteralPath $destination).LastWriteTimeUtc = $fixedTime
    }

    $manifestLines = foreach ($relative in ($files.Values | Sort-Object)) {
        $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $stage $relative)).Hash.ToLowerInvariant()
        "$hash  $($relative.Replace('\','/'))"
    }
    $manifestPath = Join-Path $stage 'SHA256SUMS.txt'
    [IO.File]::WriteAllText($manifestPath, (($manifestLines -join "`n") + "`n"), [Text.UTF8Encoding]::new($false))
    (Get-Item -LiteralPath $manifestPath).LastWriteTimeUtc = $fixedTime

    $packaged = @(Get-ChildItem -LiteralPath $stage -Recurse -File | ForEach-Object { $_.FullName.Substring($stage.Length + 1).Replace('\','/') })
    $forbidden = @($packaged | Where-Object { $_ -match '(^|/)(\.git|\.saipen|run|logs|SAICONT_FULL_ROADMAP)(/|$)|\.(state|tmp|pdb)$' })
    if ($forbidden.Count -ne 0) { throw "Forbidden package entries: $($forbidden -join ', ')" }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zipPath = Join-Path $outputRoot "SAICONT-$version-win-x64.zip"
    if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
    $archive = [IO.Compression.ZipFile]::Open($zipPath, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in (Get-ChildItem -LiteralPath $stage -Recurse -File | Sort-Object { $_.FullName.Substring($stage.Length + 1) })) {
            $relative = $file.FullName.Substring($stage.Length + 1).Replace('\','/')
            $zipEntry = $archive.CreateEntry($relative, [IO.Compression.CompressionLevel]::Optimal)
            $zipEntry.LastWriteTime = [DateTimeOffset]$fixedTime
            $input = $file.OpenRead(); $output = $zipEntry.Open()
            try { $input.CopyTo($output) } finally { $output.Dispose(); $input.Dispose() }
        }
    }
    finally { $archive.Dispose() }

    Write-Output "PASS: portable package manifest entries=$($packaged.Count)"
    Write-Output "PACKAGE path=$zipPath sha256=$((Get-FileHash -Algorithm SHA256 -LiteralPath $zipPath).Hash.ToLowerInvariant())"
}
finally {
    if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
}
