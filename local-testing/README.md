# Local Jellyfin Testing

This folder contains the disposable local Jellyfin smoke-test environment for the plugin. It is for developer verification before release, not for production deployment.

## Prerequisites

- Docker Desktop with Compose support
- .NET SDK 9+

## Start the local environment

1. Fast path: publish the plugin and start Jellyfin:

```powershell
./local-testing/bootstrap-local-jellyfin.ps1
```

2. Manual path: publish the plugin into the local plugin mount:

```powershell
./local-testing/publish-local-plugin.ps1
```

3. Start Jellyfin:

```powershell
./local-testing/start-local-jellyfin.ps1
```

4. Open Jellyfin at `http://localhost:8096`.

State is stored under `.local/jellyfin/` and is ignored by git.

On a fresh reset, the only remaining manual steps are the Jellyfin first-run wizard and picking any real YouTube sources you want to test.

## Smoke checklist

Run this checklist before releasing changes that touch sync, metadata generation, source handling, or playback:

1. Plugin loads in Jellyfin and the YouTubeSync settings page opens.
2. A normal channel source with feed `Videos` syncs successfully.
3. The synced channel still appears with year-based seasons.
4. A duplicate channel source with feed `Playlists` syncs successfully.
5. The playlist feed appears grouped by seasons and playlist names are visible after library refresh.
6. Retention still removes old videos when the configured cutoff excludes them.
7. Artwork is downloaded on first sync but not downloaded again on a second sync when files already exist.
8. Playback still resolves for at least one synced item.
9. If managed transcoding is enabled, one playback succeeds and fallback still works when managed mode cannot start.

## Reset the local instance

Stop the container:

```powershell
./local-testing/stop-local-jellyfin.ps1
```

To fully reset local state:

```powershell
./local-testing/reset-local-jellyfin.ps1
```

## Notes

- Keep the local library isolated from production Jellyfin data.
- Use a small set of representative sources. This is a smoke environment, not a long-lived media server.
- The local Jellyfin image includes `yt-dlp`, `ffmpeg`, and `deno`, and its `yt-dlp` wrapper enables `--remote-components ejs:github` by default for YouTube challenge solving.
- Re-run `./local-testing/publish-local-plugin.ps1` after rebuilding the plugin.