$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$composeFile = Join-Path $PSScriptRoot "docker-compose.yml"

Push-Location $repoRoot
try {
    New-Item -ItemType Directory -Force -Path ".local/jellyfin/media/youtube-test" | Out-Null
    docker compose -f $composeFile up --build -d
}
finally {
    Pop-Location
}