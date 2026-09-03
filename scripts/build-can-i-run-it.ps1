param(
    [Parameter(Mandatory = $true)]
    [string]$ProjectDir,

    [Parameter(Mandatory = $true)]
    [string]$OutputDir
)

$ErrorActionPreference = 'Stop'

$project = (Resolve-Path -LiteralPath $ProjectDir).Path
$go = Get-Command go -ErrorAction SilentlyContinue
if (-not $go) { throw 'Go was not found. Install Go 1.24 or newer to build Can I Run It.' }

New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
$output = Join-Path $OutputDir 'can-i-run-it.exe'
& $go.Source -C $project build -trimpath -ldflags '-s -w' -o $output .
if ($LASTEXITCODE -ne 0) { throw "Can I Run It backend build failed with exit code $LASTEXITCODE." }
if (-not (Test-Path -LiteralPath $output)) { throw "Can I Run It backend was not produced at $output." }
Write-Host "Can I Run It backend copied to $OutputDir"
