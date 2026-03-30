$ErrorActionPreference = "Stop"

& (Join-Path $PSScriptRoot "publish-local-plugin.ps1")
& (Join-Path $PSScriptRoot "start-local-jellyfin.ps1")