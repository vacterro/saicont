[CmdletBinding()]
param(
    [string]$Config
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')
$paths = Get-SaiContPaths

if (-not (Test-Path -LiteralPath $paths.Executable -PathType Leaf)) {
    Write-Output "Building SAICONT..."
    & (Join-Path $paths.ProjectRoot 'build.ps1')
    if ($LASTEXITCODE -ne 0) {
        throw "Build failed with exit code $LASTEXITCODE"
    }
}

$configPath = if ($Config) { $Config } else { $paths.Configuration }
if (-not (Test-Path -LiteralPath $configPath -PathType Leaf)) {
    throw "Configuration not found: $configPath"
}

& $paths.Executable --gui --config $configPath
exit $LASTEXITCODE
