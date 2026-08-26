[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$compiler = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$sourceRoot = Join-Path $projectRoot 'src'
$outputRoot = Join-Path $projectRoot 'bin'
$outputPath = Join-Path $outputRoot 'SAICONT.exe'
$configurationPath = Join-Path $projectRoot 'SAICONT.config.xml'
$outputConfigurationPath = Join-Path $outputRoot 'SAICONT.config.xml'

if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) {
    throw "C# compiler not found: $compiler"
}

if (-not (Test-Path -LiteralPath $outputRoot -PathType Container)) {
    New-Item -ItemType Directory -Path $outputRoot | Out-Null
}

$sources = @(Get-ChildItem -LiteralPath $sourceRoot -Filter '*.cs' -File | Sort-Object Name | ForEach-Object FullName)
if ($sources.Count -eq 0) {
    throw "No C# sources found in $sourceRoot"
}

$arguments = @(
    '/nologo',
    '/target:exe',
    '/platform:x64',
    '/optimize+',
    '/warnaserror+',
    "/out:$outputPath",
    '/reference:System.Core.dll'
) + $sources

& $compiler @arguments
if ($LASTEXITCODE -ne 0) {
    throw "C# compiler failed with exit code $LASTEXITCODE"
}

Copy-Item -LiteralPath $configurationPath -Destination $outputConfigurationPath -Force

Write-Output $outputPath
