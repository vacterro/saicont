[CmdletBinding()]
param([string]$ScanRoot)

$ErrorActionPreference = 'Stop'
$projectRoot = if ([String]::IsNullOrWhiteSpace($ScanRoot)) { Split-Path -Parent $PSScriptRoot } else { [IO.Path]::GetFullPath($ScanRoot) }
$sourceFiles = @(Get-ChildItem -LiteralPath (Join-Path $projectRoot 'src') -Filter '*.cs' -File | Where-Object { $_.Name -ne 'SelfTests.cs' })
$violations = New-Object Collections.Generic.List[string]

$banned = [ordered]@{
    'network-listener' = '\b(HttpListener|TcpListener|UdpClient|Kestrel|WebHost)\b'
    'database-runtime' = '\b(System\.Data|SqlConnection|SQLiteConnection|DbContext|MongoClient)\b'
    'ui-automation' = '\b(SendKeys|keybd_event|mouse_event|SetWindowsHookEx|RegisterHotKey|UIAutomation)\b'
    'provider-switching' = '\b(SwitchProvider|SwitchModel|SelectProvider|SelectModel)\b'
    'arbitrary-process-launch' = '(\bProcess\s*\.\s*Start\s*\(|\bProcessStartInfo\b)'
}

foreach ($file in $sourceFiles) {
    $text = Get-Content -Raw -LiteralPath $file.FullName
    foreach ($entry in $banned.GetEnumerator()) {
        if ($text -match $entry.Value) { $violations.Add("$($entry.Key):$($file.Name)") }
    }
}

$dependencyManifests = @(Get-ChildItem -LiteralPath $projectRoot -Recurse -File | Where-Object {
    $_.FullName -notmatch '[\\/](\.git|\.saipen|bin|logs|run)[\\/]' -and
    ($_.Name -eq 'packages.config' -or $_.Extension -in @('.csproj','.fsproj','.vbproj','.db','.sqlite','.sqlite3'))
})
foreach ($file in $dependencyManifests) { $violations.Add("external-runtime-artifact:$($file.Name)") }

$buildText = Get-Content -Raw -LiteralPath (Join-Path $projectRoot 'build.ps1')
$references = [regex]::Matches($buildText, "'/reference:([^']+)'" ) | ForEach-Object { $_.Groups[1].Value }
$allowedReferences = @('System.Core.dll','System.Web.Extensions.dll','System.Windows.Forms.dll','System.Drawing.dll')
foreach ($reference in $references) {
    if ($reference -notin $allowedReferences) { $violations.Add("external-reference:$reference") }
}

if ($violations.Count -gt 0) {
    $violations | Sort-Object -Unique | ForEach-Object { Write-Error "BOUNDARY VIOLATION: $_" }
    exit 1
}

Write-Output "PASS: product boundary guard sources=$($sourceFiles.Count) references=$($references.Count)"
