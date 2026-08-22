param(
    [Parameter(Mandatory = $true)]
    [string]$ProjectDir,

    [Parameter(Mandatory = $true)]
    [string]$OutputDir,

    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'

function Find-ZigExecutable {
    if ($env:ACHIEVEMENT_BRIDGE_ZIG_EXE -and (Test-Path -LiteralPath $env:ACHIEVEMENT_BRIDGE_ZIG_EXE)) {
        return (Resolve-Path -LiteralPath $env:ACHIEVEMENT_BRIDGE_ZIG_EXE).Path
    }

    $command = Get-Command zig -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }

    $candidates = @(
        (Join-Path $env:LOCALAPPDATA 'Microsoft\WinGet\Packages\zig.zig_Microsoft.Winget.Source_8wekyb3d8bbwe\zig-x86_64-windows-0.16.0\zig.exe'),
        (Join-Path $env:APPDATA 'Code\User\globalStorage\ziglang.vscode-zig\zig\x86_64-windows-0.16.0\zig.exe')
    )
    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate) { return (Resolve-Path -LiteralPath $candidate).Path }
    }

    throw 'Zig 0.16.0 was not found. Install it or set ACHIEVEMENT_BRIDGE_ZIG_EXE.'
}

$project = (Resolve-Path -LiteralPath $ProjectDir).Path
$zig = Find-ZigExecutable
$version = (& $zig version).Trim()
if ($LASTEXITCODE -ne 0) { throw "Could not execute Zig at $zig." }
if ($version -ne '0.16.0') { throw "Achievement Bridge requires Zig 0.16.0; found $version at $zig." }

$optimize = if ($Configuration -eq 'Release') { 'ReleaseSafe' } else { 'Debug' }
& $zig build "-Doptimize=$optimize" --build-file (Join-Path $project 'build.zig')
if ($LASTEXITCODE -ne 0) { throw "Achievement Bridge build failed with exit code $LASTEXITCODE." }

$binary = Join-Path $project 'zig-out\bin\achievement-bridge.exe'
if (-not (Test-Path -LiteralPath $binary)) { throw "Achievement Bridge output was not produced at $binary." }
$proxy = Join-Path $project 'zig-out\bin\achievement-bridge-cloud.dll'
if (-not (Test-Path -LiteralPath $proxy)) { throw "Achievement Bridge proxy was not produced at $proxy." }
New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
Copy-Item -LiteralPath $binary -Destination (Join-Path $OutputDir 'achievement-bridge.exe') -Force
Copy-Item -LiteralPath $proxy -Destination (Join-Path $OutputDir 'achievement-bridge-cloud.dll') -Force
Write-Host "Achievement Bridge $version and live Steam proxy copied to $OutputDir"
