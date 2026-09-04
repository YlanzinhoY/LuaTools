param(
    [Parameter(Mandatory = $true)]
    [string]$ProjectDir,

    [Parameter(Mandatory = $true)]
    [string]$OutputDir,

    [string]$Configuration = "Debug",

    [switch]$SelfContained
)

$ErrorActionPreference = "Stop"

$project = Join-Path $ProjectDir "LuaTools.TorrentHost.csproj"
if (-not (Test-Path -LiteralPath $project)) {
    throw "Torrent host project not found: $project"
}

New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
$selfContainedValue = if ($SelfContained) { "true" } else { "false" }

& dotnet publish $project `
    --configuration $Configuration `
    --runtime win-x64 `
    --self-contained $selfContainedValue `
    --output $OutputDir `
    -p:PublishSingleFile=false `
    -p:DebugType=embedded

if ($LASTEXITCODE -ne 0) {
    throw "Torrent host publish failed with exit code $LASTEXITCODE."
}

$hostExe = Join-Path $OutputDir "LuaTools.TorrentHost.exe"
if (-not (Test-Path -LiteralPath $hostExe)) {
    throw "Torrent host output not found: $hostExe"
}
