using System.Linq;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.YouTubeSync;

/// <summary>
/// Selects the best Jellyfin-compatible (progressive, ≤1080p) format
/// from a yt-dlp JSON response.
///
/// Selection uses a tiered fallback strategy (mirrors <c>b[ext=mp4][height&lt;=1080]/b[ext=mp4][height&lt;=720]/b</c>):
///   Tier 1: Progressive MP4 stream ≤1080p  – highest resolution, then highest bitrate.
///   Tier 2: Progressive MP4 stream ≤720p   – fallback when no ≤1080p MP4 exists.
///   Tier 3: Any progressive stream          – last resort when no MP4 is available.
///   DASH-only (split video/audio) streams are always rejected.
/// </summary>
public class FormatSelector
{
    private readonly ILogger<FormatSelector> _logger;

    /// <summary>Initializes a new instance of the <see cref="FormatSelector"/> class.</summary>
    public FormatSelector(ILogger<FormatSelector> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Returns the direct CDN URL for the best progressive format, or <c>null</c> when
    /// no compatible progressive format exists (i.e. only split DASH streams are available).
    /// </summary>
    public string? SelectBestFormat(JsonNode videoInfo)
    {
        var formats = videoInfo["formats"]?.AsArray();
        if (formats is null || formats.Count == 0)
        {
            _logger.LogWarning("yt-dlp response contains no 'formats' array.");
            return null;
        }

        // Tier 1: best progressive MP4 ≤1080p (mirrors: b[ext=mp4][height<=1080])
        var best = PickBest(formats, requireMp4: true, maxHeight: 1080)
            // Tier 2: best progressive MP4 ≤720p (mirrors: b[ext=mp4][height<=720])
            ?? PickBest(formats, requireMp4: true, maxHeight: 720)
            // Tier 3: best progressive stream of any container (mirrors: b)
            ?? PickBest(formats, requireMp4: false, maxHeight: null);

        if (best is null)
        {
            _logger.LogInformation(
                "No progressive format found. "
                + "Only DASH or split streams are available. "
                + "DASH proxy is not supported in v1.");
            return null;
        }

        var url = best["url"]?.GetValue<string>();
        _logger.LogDebug(
            "Selected format: id={FormatId} height={Height} tbr={Tbr}",
            GetString(best, "format_id"),
            GetInt(best, "height"),
            GetDouble(best, "tbr"));

        return url;
    }

    private static JsonNode? PickBest(JsonArray formats, bool requireMp4, int? maxHeight)
    {
        var query = formats
            .Where(f => f is not null)
            .Where(IsProgressive);

        if (requireMp4)
        {
            query = query.Where(IsMp4);
        }

        if (maxHeight.HasValue)
        {
            var limit = maxHeight.Value;
            query = query.Where(f => GetInt(f, "height") <= limit);
        }

        return query
            .OrderByDescending(f => GetInt(f, "height"))
            .ThenByDescending(f => GetDouble(f, "tbr"))
            .FirstOrDefault();
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static bool IsProgressive(JsonNode? f)
    {
        var vcodec = GetString(f, "vcodec");
        var acodec = GetString(f, "acodec");
        return vcodec != "none" && vcodec.Length > 0
            && acodec != "none" && acodec.Length > 0;
    }

    private static bool IsMp4(JsonNode? f)
        => GetString(f, "ext") == "mp4";

    private static string GetString(JsonNode? node, string key)
    {
        try
        {
            return node?[key]?.GetValue<string>() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static int GetInt(JsonNode? node, string key)
    {
        try
        {
            return node?[key]?.GetValue<int>() ?? 0;
        }
        catch
        {
            return 0;
        }
    }

    private static double GetDouble(JsonNode? node, string key)
    {
        try
        {
            return node?[key]?.GetValue<double>() ?? 0d;
        }
        catch
        {
            return 0d;
        }
    }
}
