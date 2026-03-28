using System.Collections.Generic;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.YouTubeSync;

/// <summary>Named playback targets exposed in the plugin configuration UI.</summary>
public static class PlaybackTargets
{
    /// <summary>Prefer broadly compatible 720p progressive MP4 when available.</summary>
    public const string BroadCompatibility720p = "BroadCompatibility720p";

    /// <summary>Prefer up to 1080p MP4 and allow manifest playback when needed.</summary>
    public const string Balanced1080p = "Balanced1080p";

    /// <summary>Let yt-dlp choose the highest-quality combined stream it can expose.</summary>
    public const string MaximumQuality = "MaximumQuality";
}

/// <summary>Holds all user-configurable settings for the YouTubeSync plugin.</summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Gets or sets the list of YouTube sources (channels/playlists) to sync.</summary>
    public List<SourceDefinition> Sources { get; set; } = new();

    /// <summary>
    /// Gets or sets the path to the yt-dlp executable.
    /// Defaults to "yt-dlp" (expects it to be on PATH).
    /// </summary>
    public string YtDlpPath { get; set; } = "yt-dlp";

    /// <summary>
    /// Gets or sets the base directory where .strm/.nfo files are written.
    /// This directory must be inside a Jellyfin library.
    /// </summary>
    public string LibraryBasePath { get; set; } = "/media/youtube";

    /// <summary>
    /// Gets or sets the externally accessible base URL of this Jellyfin instance.
    /// Used to build resolver URLs written into .strm files.
    /// For remote clients this must be the public URL (e.g. https://jellyfin.example.com).
    /// </summary>
    public string JellyfinBaseUrl { get; set; } = "http://localhost:8096";

    /// <summary>
    /// Gets or sets how many minutes a resolved CDN URL stays in the in-memory cache.
    /// </summary>
    public int CacheMinutes { get; set; } = 5;

    /// <summary>
    /// Gets or sets the playback target used when asking yt-dlp for a playback URL.
    /// </summary>
    public string PlaybackTarget { get; set; } = PlaybackTargets.BroadCompatibility720p;

    /// <summary>
    /// Gets or sets the maximum number of videos to sync per source.
    /// Set to 0 for no limit (not recommended for large channels).
    /// </summary>
    public int MaxVideosPerSource { get; set; } = 200;
}
