using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.YouTubeSync;

/// <summary>
/// Orchestrates the resolution of a YouTube video ID to a playable URL.
/// Results are stored in <see cref="SimpleResolveCache"/> to avoid calling yt-dlp on every request.
/// </summary>
public class ResolveService
{
    private readonly YtDlpService _ytDlpService;
    private readonly SimpleResolveCache _cache;
    private readonly ILogger<ResolveService> _logger;

    /// <summary>Initializes a new instance of the <see cref="ResolveService"/> class.</summary>
    public ResolveService(
        YtDlpService ytDlpService,
        SimpleResolveCache cache,
        ILogger<ResolveService> logger)
    {
        _ytDlpService = ytDlpService;
        _cache = cache;
        _logger = logger;
    }

    /// <summary>
    /// Resolves a YouTube video ID to a direct playback URL.
    /// Returns <c>null</c> if resolution fails or no compatible format is available.
    /// </summary>
    public async Task<string?> ResolveAsync(string videoId, CancellationToken cancellationToken)
    {
        if (_cache.TryGet(videoId, out var cached))
        {
            _logger.LogDebug("Cache hit for video {VideoId}", videoId);
            return cached;
        }

        _logger.LogInformation("Resolving video {VideoId} via yt-dlp", videoId);

        var playbackUrl = await _ytDlpService.GetPlaybackUrlAsync(videoId, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(playbackUrl))
        {
            _logger.LogWarning("yt-dlp returned no playable URL for video {VideoId}", videoId);
            return null;
        }

        var cacheMinutes = Plugin.Instance?.Configuration.CacheMinutes ?? 5;
        _cache.Set(videoId, playbackUrl, cacheMinutes);

        _logger.LogInformation(
            "Resolved {VideoId} via direct playback URL – cached for {Minutes} min",
            videoId,
            cacheMinutes);

        return playbackUrl;
    }
}

