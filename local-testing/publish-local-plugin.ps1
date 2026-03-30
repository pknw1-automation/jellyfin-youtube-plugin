param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repoRoot "Jellyfin.Plugin.YouTubeSync/Jellyfin.Plugin.YouTubeSync.csproj"
$publishDir = Join-Path $repoRoot ".local/build/publish"
$pluginDir = Join-Path $repoRoot ".local/jellyfin/plugins/YouTubeSync"
$metaPath = Join-Path $repoRoot "Jellyfin.Plugin.YouTubeSync/meta.json"

New-Item -ItemType Directory -Force -Path $publishDir | Out-Null
New-Item -ItemType Directory -Force -Path $pluginDir | Out-Null

Write-Host "Publishing plugin..."
dotnet publish $projectPath -c $Configuration --no-self-contained -o $publishDir

Write-Host "Copying plugin files to local Jellyfin plugin directory..."
Copy-Item (Join-Path $publishDir "Jellyfin.Plugin.YouTubeSync.dll") $pluginDir -Force
Copy-Item $metaPath $pluginDir -Force

Write-Host "Plugin published to $pluginDir"
Write-Host "Start Jellyfin with: docker compose -f local-testing/docker-compose.yml up --build"