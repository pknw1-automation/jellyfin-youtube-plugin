$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$composeFile = Join-Path $PSScriptRoot "docker-compose.yml"
$localState = Join-Path $repoRoot ".local/jellyfin"

Push-Location $repoRoot
try {
    docker compose -f $composeFile down
}
finally {
    Pop-Location
}

if (Test-Path $localState) {
    Remove-Item -Recurse -Force $localState
}

New-Item -ItemType Directory -Force -Path (Join-Path $localState "media/youtube-test") | Out-Null