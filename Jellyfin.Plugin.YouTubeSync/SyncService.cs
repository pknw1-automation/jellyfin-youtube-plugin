using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.YouTubeSync;

/// <summary>
/// Creates and maintains the .strm / .nfo file tree inside the configured Jellyfin library path.
/// One sub-folder is created per source; each folder gets a Kodi-compatible .nfo file
/// (tvshow.nfo for channels / series playlists, movie.nfo for movie-mode playlists).
/// </summary>
public class SyncService
{
    private sealed record PlaylistSeasonDefinition(string PlaylistId, string Title, int SeasonNumber);

    private const int MaxPerSourceConcurrency = 4;
    private const int MinimumRetentionEntryScanCount = 25;
    private const int EstimatedUploadsPerDayForRetentionScan = 5;
    private static readonly TimeSpan ArtworkDownloadTimeout = TimeSpan.FromSeconds(20);
    private static readonly string[] ArtworkExtensions = new[] { ".jpg", ".jpeg", ".png", ".webp" };

    private static readonly HttpClient HttpClient = new();

    private readonly YtDlpService _ytDlpService;
    private readonly ILogger<SyncService> _logger;

    /// <summary>Initializes a new instance of the <see cref="SyncService"/> class.</summary>
    public SyncService(YtDlpService ytDlpService, ILogger<SyncService> logger)
    {
        _ytDlpService = ytDlpService;
        _logger = logger;
    }

    /// <summary>Syncs all configured sources, reporting progress from 0–100.</summary>
    public async Task SyncAllAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null || config.Sources.Count == 0)
        {
            _logger.LogInformation("No sources configured – skipping sync.");
            progress.Report(100);
            return;
        }

        var sources = config.Sources;
        for (var i = 0; i < sources.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourceProgressBase = (double)i / sources.Count * 100;
            var sourceProgressSpan = 100d / sources.Count;
            await SyncSourceAsync(sources[i], progress, sourceProgressBase, sourceProgressSpan, cancellationToken).ConfigureAwait(false);
            progress.Report(sourceProgressBase + sourceProgressSpan);
        }
    }

    private async Task SyncSourceAsync(
        SourceDefinition source,
        IProgress<double> progress,
        double sourceProgressBase,
        double sourceProgressSpan,
        CancellationToken cancellationToken)
    {
        var config = Plugin.Instance!.Configuration;
        var sourceInfo = await _ytDlpService.GetSourceInfoAsync(source.Url, cancellationToken).ConfigureAwait(false);
        var retentionCutoffUtc = config.VideoRetentionDays > 0
            ? DateTime.UtcNow.AddDays(-config.VideoRetentionDays)
            : (DateTime?)null;

        var name = string.IsNullOrWhiteSpace(source.Name)
            ? sourceInfo?.Title ?? source.Id
            : source.Name;
        var description = string.IsNullOrWhiteSpace(source.Description)
            ? sourceInfo?.Description ?? string.Empty
            : source.Description;
        var thumbnailUrl = string.IsNullOrWhiteSpace(source.ThumbnailUrl)
            ? sourceInfo?.ThumbnailUrl ?? string.Empty
            : source.ThumbnailUrl;
        var posterUrl = sourceInfo?.PosterUrl ?? thumbnailUrl;

        var sourceDir = Path.Combine(config.LibraryBasePath, SanitizeFileName(name));

        _logger.LogInformation("Starting sync for source '{Name}'", name);

        var maxEntryScanCount = GetMaxEntryScanCount(config.VideoRetentionDays, config.MaxVideosPerSource);
        if (maxEntryScanCount > 0)
        {
            _logger.LogInformation(
                "Applying upfront entry scan limit {MaxEntryScanCount} for source '{Name}' with retention {RetentionDays} day(s).",
                maxEntryScanCount,
                name,
                config.VideoRetentionDays);
        }

        var entries = await _ytDlpService
            .GetPlaylistEntriesAsync(source.Url, config.VideoRetentionDays, maxEntryScanCount, cancellationToken)
            .ConfigureAwait(false);

        IReadOnlyList<PlaylistSeasonDefinition> playlistSeasonDefinitions = Array.Empty<PlaylistSeasonDefinition>();

        if (source.Type == SourceType.Channel && source.Feed == ChannelFeed.Playlists)
        {
            playlistSeasonDefinitions = BuildPlaylistSeasonDefinitions(entries);
            entries = await ExpandChannelPlaylistsAsync(entries, playlistSeasonDefinitions, config.VideoRetentionDays, maxEntryScanCount, cancellationToken)
                .ConfigureAwait(false);
        }

        Directory.CreateDirectory(sourceDir);
        await WriteSourceMetadataAsync(
                source,
                sourceDir,
                name,
                description,
                thumbnailUrl,
                posterUrl,
                playlistSeasonDefinitions,
                cancellationToken)
            .ConfigureAwait(false);

        _logger.LogInformation(
            "Fetched {Count} playlist entr{Suffix} for source '{Name}'",
            entries.Count,
            entries.Count == 1 ? "y" : "ies",
            name);

        var videos = new ConcurrentBag<VideoMetadata>();
        var desiredVideoDirectories = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        var desiredSeasonDirectories = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        var metadataProcessed = 0;

        await Parallel.ForEachAsync(
                entries,
                new ParallelOptions
                {
                    CancellationToken = cancellationToken,
                    MaxDegreeOfParallelism = MaxPerSourceConcurrency
                },
                async (entry, innerCancellationToken) =>
                {
                    try
                    {
                        var videoId = GetString(entry, "id");
                        if (string.IsNullOrWhiteSpace(videoId))
                        {
                            return;
                        }

                        var metadata = await _ytDlpService.GetVideoMetadataAsync(videoId, innerCancellationToken).ConfigureAwait(false)
                            ?? BuildFallbackVideoMetadata(entry, videoId, name);

                        NormalizeVideoMetadata(metadata, entry, videoId, name);

                        if (metadata.PublishedUtc is null)
                        {
                            metadata.PublishedUtc = await _ytDlpService.GetVideoPublishedDateAsync(videoId, innerCancellationToken)
                                .ConfigureAwait(false);
                        }

                        if (metadata.PublishedUtc is null)
                        {
                            _logger.LogWarning(
                                "Skipping video {VideoId} during sync for source {SourceName} because no published date could be extracted.",
                                videoId,
                                name);
                            return;
                        }

                        if (retentionCutoffUtc is DateTime cutoffUtc
                            && metadata.PublishedUtc is DateTime publishedUtc
                            && publishedUtc < cutoffUtc)
                        {
                            return;
                        }

                        videos.Add(metadata);

                        var seasonFolder = GetSeasonFolderName(metadata, source);
                        var parentDir = string.IsNullOrEmpty(seasonFolder)
                            ? sourceDir
                            : Path.Combine(sourceDir, seasonFolder);
                        var videoDir = Path.Combine(parentDir, BuildVideoFolderName(metadata.Title, metadata.VideoId));

                        desiredVideoDirectories.TryAdd(videoDir, 0);
                        if (!string.IsNullOrEmpty(seasonFolder))
                        {
                            desiredSeasonDirectories.TryAdd(parentDir, 0);
                        }

                        await WriteVideoShellAsync(metadata, videoDir, config.JellyfinBaseUrl, innerCancellationToken)
                            .ConfigureAwait(false);
                    }
                    finally
                    {
                        ReportPhaseProgress(
                            progress,
                            sourceProgressBase,
                            sourceProgressSpan,
                            Interlocked.Increment(ref metadataProcessed),
                            entries.Count,
                            0.0,
                            0.5,
                            "metadata",
                            name);
                    }
                })
            .ConfigureAwait(false);

        var retainedVideos = videos
            .OrderByDescending(v => v.PublishedUtc ?? DateTime.MinValue)
            .ThenBy(v => v.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();

        _logger.LogInformation(
            "Syncing {Count} video(s) for source '{Name}'",
            retainedVideos.Count,
            name);

        var seasonEpisodeCounters = BuildSeasonEpisodeCounters(retainedVideos, source);
        var filesWritten = 0;

        await Parallel.ForEachAsync(
                retainedVideos,
                new ParallelOptions
                {
                    CancellationToken = cancellationToken,
                    MaxDegreeOfParallelism = MaxPerSourceConcurrency
                },
                async (video, innerCancellationToken) =>
                {
                    try
                    {
                        var seasonFolder = GetSeasonFolderName(video, source);
                        var parentDir = string.IsNullOrEmpty(seasonFolder)
                            ? sourceDir
                            : Path.Combine(sourceDir, seasonFolder);
                        var videoDir = Path.Combine(parentDir, BuildVideoFolderName(video.Title, video.VideoId));
                        var seasonNumber = GetSeasonNumber(video, source);
                        var episodeNumber = GetEpisodeNumber(video, seasonEpisodeCounters, source);

                        await WriteVideoFilesAsync(
                                video,
                                source.Mode,
                                seasonNumber,
                                episodeNumber,
                                videoDir,
                                config.JellyfinBaseUrl,
                                name,
                                innerCancellationToken)
                            .ConfigureAwait(false);
                    }
                    finally
                    {
                        ReportPhaseProgress(
                            progress,
                            sourceProgressBase,
                            sourceProgressSpan,
                            Interlocked.Increment(ref filesWritten),
                            retainedVideos.Count,
                            0.5,
                            1.0,
                            "files",
                            name);
                    }
                })
            .ConfigureAwait(false);

        CleanupObsoleteContent(
            sourceDir,
            source.Mode,
            new HashSet<string>(desiredSeasonDirectories.Keys, StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(desiredVideoDirectories.Keys, StringComparer.OrdinalIgnoreCase));

        _logger.LogInformation("Completed sync for source '{Name}'", name);
    }

    private async Task WriteVideoShellAsync(
        VideoMetadata video,
        string videoDir,
        string jellyfinBaseUrl,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(video.VideoId))
        {
            return;
        }

        var title = string.IsNullOrWhiteSpace(video.Title) ? video.VideoId : video.Title;
        var safeName = SanitizeFileName(title);
        Directory.CreateDirectory(videoDir);

        var strmPath = Path.Combine(videoDir, $"{safeName}.strm");
        var resolverUrl = $"{jellyfinBaseUrl.TrimEnd('/')}/YouTubeSync/resolve/{video.VideoId}";
        await File.WriteAllTextAsync(strmPath, resolverUrl, Encoding.UTF8, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task WriteVideoFilesAsync(
        VideoMetadata video,
        SourceMode sourceMode,
        int? seasonNumber,
        int? episodeNumber,
        string videoDir,
        string jellyfinBaseUrl,
        string sourceName,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(video.VideoId))
        {
            return;
        }

        var title = video.Title;
        if (string.IsNullOrEmpty(title))
        {
            title = video.VideoId;
        }

        var safeName = SanitizeFileName(title);
        Directory.CreateDirectory(videoDir);

        var strmPath = Path.Combine(videoDir, $"{safeName}.strm");
        var nfoPath = Path.Combine(videoDir, $"{safeName}.nfo");

        await WriteVideoShellAsync(video, videoDir, jellyfinBaseUrl, cancellationToken).ConfigureAwait(false);
        _logger.LogDebug("Wrote {StrmPath}", strmPath);

        var nfo = sourceMode == SourceMode.Movies
            ? BuildMovieVideoNfo(video, sourceName)
            : BuildEpisodeNfo(video, sourceName, seasonNumber, episodeNumber);
        await File.WriteAllTextAsync(nfoPath, nfo, Encoding.UTF8, cancellationToken)
            .ConfigureAwait(false);

        await DownloadArtworkAsync(video.ThumbnailUrl, videoDir, new[] { "folder", "poster" }, cancellationToken)
            .ConfigureAwait(false);
    }

    // ── NFO builders ──────────────────────────────────────────────────────────

    private async Task WriteSourceMetadataAsync(
        SourceDefinition source,
        string dir,
        string name,
        string description,
        string thumbnailUrl,
        string posterUrl,
        IReadOnlyList<PlaylistSeasonDefinition> playlistSeasonDefinitions,
        CancellationToken cancellationToken)
    {
        bool isSeries = source.Type == SourceType.Channel || source.Mode == SourceMode.Series;
        var nfoFileName = isSeries ? "tvshow.nfo" : "movie.nfo";
        var nfoPath = Path.Combine(dir, nfoFileName);

        var content = isSeries
            ? BuildTvShowNfo(source, name, description, thumbnailUrl, playlistSeasonDefinitions)
            : BuildCollectionNfo(source, name, description, thumbnailUrl);

        await File.WriteAllTextAsync(nfoPath, content, Encoding.UTF8, cancellationToken).ConfigureAwait(false);

        var artworkDownloads = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        AddArtworkTarget(artworkDownloads, thumbnailUrl, "folder");

        var posterOrThumbnailUrl = string.IsNullOrWhiteSpace(posterUrl) ? thumbnailUrl : posterUrl;
        AddArtworkTarget(artworkDownloads, posterOrThumbnailUrl, "poster");
        AddArtworkTarget(artworkDownloads, posterOrThumbnailUrl, "banner");

        foreach (var artworkDownload in artworkDownloads)
        {
            await DownloadArtworkAsync(artworkDownload.Key, dir, artworkDownload.Value, cancellationToken).ConfigureAwait(false);
        }
    }

    private static string BuildTvShowNfo(
        SourceDefinition source,
        string name,
        string description,
        string thumbnailUrl,
        IReadOnlyList<PlaylistSeasonDefinition> playlistSeasonDefinitions)
    {
        var thumb = string.IsNullOrEmpty(thumbnailUrl)
            ? string.Empty
            : $"\n  <thumb aspect=\"poster\">{Xml(thumbnailUrl)}</thumb>";
        var namedSeasons = playlistSeasonDefinitions.Count == 0
            ? string.Empty
            : string.Concat(
                playlistSeasonDefinitions.Select(definition =>
                    $"\n  <namedseason number=\"{definition.SeasonNumber}\">{Xml(definition.Title)}</namedseason>"));

        return $"""
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <tvshow>
          <title>{Xml(name)}</title>
          <plot>{Xml(description)}</plot>
          <uniqueid type="youtube" default="true">{Xml(source.Id)}</uniqueid>{thumb}{namedSeasons}
        </tvshow>
        """;
    }

    private static string BuildCollectionNfo(SourceDefinition source, string name, string description, string thumbnailUrl)
    {
        var thumb = string.IsNullOrEmpty(thumbnailUrl)
            ? string.Empty
            : $"\n  <thumb aspect=\"poster\">{Xml(thumbnailUrl)}</thumb>";

        return $"""
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <movie>
          <title>{Xml(name)}</title>
          <plot>{Xml(description)}</plot>
          <uniqueid type="youtube" default="true">{Xml(source.Id)}</uniqueid>{thumb}
        </movie>
        """;
    }

    private static string BuildEpisodeNfo(VideoMetadata video, string sourceName, int? seasonNumber, int? episodeNumber)
    {
        var aired = BuildDateTag("aired", video.PublishedUtc);
        var premiered = BuildDateTag("premiered", video.PublishedUtc);
        var thumb = string.IsNullOrEmpty(video.ThumbnailUrl)
            ? string.Empty
            : $"\n  <thumb>{Xml(video.ThumbnailUrl)}</thumb>";
        var season = seasonNumber.HasValue ? $"\n  <season>{seasonNumber.Value}</season>" : string.Empty;
        var episode = episodeNumber.HasValue ? $"\n  <episode>{episodeNumber.Value}</episode>" : string.Empty;
        var runtime = video.DurationSeconds.HasValue ? $"\n  <runtime>{video.DurationSeconds.Value / 60}</runtime>" : string.Empty;
        var studio = string.IsNullOrWhiteSpace(video.ChannelName) ? string.Empty : $"\n  <studio>{Xml(video.ChannelName)}</studio>";

        return $"""
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <episodedetails>
          <title>{Xml(video.Title)}</title>
          <showtitle>{Xml(sourceName)}</showtitle>
          <plot>{Xml(video.Description)}</plot>
                    <uniqueid type="youtube" default="true">{Xml(video.SyncId)}</uniqueid>{aired}{premiered}{season}{episode}{runtime}{studio}{thumb}
        </episodedetails>
        """;
    }

    private static string BuildMovieVideoNfo(VideoMetadata video, string sourceName)
    {
        var premiered = BuildDateTag("premiered", video.PublishedUtc);
        var thumb = string.IsNullOrEmpty(video.ThumbnailUrl)
            ? string.Empty
            : $"\n  <thumb>{Xml(video.ThumbnailUrl)}</thumb>";
        var runtime = video.DurationSeconds.HasValue ? $"\n  <runtime>{video.DurationSeconds.Value / 60}</runtime>" : string.Empty;
        var studio = string.IsNullOrWhiteSpace(video.ChannelName) ? string.Empty : $"\n  <studio>{Xml(video.ChannelName)}</studio>";
        var set = string.IsNullOrWhiteSpace(sourceName) ? string.Empty : $"\n  <set>{Xml(sourceName)}</set>";

        return $"""
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <movie>
          <title>{Xml(video.Title)}</title>
          <plot>{Xml(video.Description)}</plot>
                    <uniqueid type="youtube" default="true">{Xml(video.SyncId)}</uniqueid>{premiered}{runtime}{studio}{set}{thumb}
        </movie>
        """;
    }

    // ── utilities ─────────────────────────────────────────────────────────────

    private static string GetString(JsonNode? node, string key)
    {
        try { return node?[key]?.GetValue<string>() ?? string.Empty; }
        catch { return string.Empty; }
    }

    private static int? GetNullableInt(JsonNode? node, string key)
    {
        try { return node?[key]?.GetValue<int>(); }
        catch { return null; }
    }

    private static VideoMetadata BuildFallbackVideoMetadata(JsonNode entry, string videoId, string sourceName)
    {
        return new VideoMetadata
        {
            VideoId = videoId,
            SyncId = GetString(entry, "__sync_id"),
            Title = GetString(entry, "title"),
            Description = GetString(entry, "description"),
            ThumbnailUrl = GetFallbackVideoThumbnailUrl(entry),
            ChannelName = sourceName,
            PlaylistId = GetString(entry, "__playlist_id"),
            PlaylistTitle = GetString(entry, "__playlist_title"),
            PlaylistSeasonNumber = GetNullableInt(entry, "__playlist_season_number"),
            PlaylistEpisodeNumber = GetNullableInt(entry, "__playlist_episode_number"),
            PublishedUtc = YtDlpService.ParsePublishedDate(entry)
        };
    }

    private static string GetFallbackVideoThumbnailUrl(JsonNode? entry)
    {
        var bestUrl = YtDlpService.GetBestVideoThumbnailUrl(entry);
        if (!string.IsNullOrWhiteSpace(bestUrl))
        {
            return bestUrl;
        }

        return GetString(entry, "thumbnail");
    }

    private async Task<IReadOnlyList<JsonNode>> ExpandChannelPlaylistsAsync(
        IReadOnlyList<JsonNode> playlistEntries,
        IReadOnlyList<PlaylistSeasonDefinition> playlistSeasonDefinitions,
        int videoRetentionDays,
        int maxEntryScanCount,
        CancellationToken cancellationToken)
    {
        var expandedEntries = new List<JsonNode>();
        var discoveredPlaylists = 0;
        var seasonLookup = playlistSeasonDefinitions.ToDictionary(definition => definition.PlaylistId, StringComparer.OrdinalIgnoreCase);

        foreach (var playlistEntry in playlistEntries)
        {
            var playlistUrl = GetChannelPlaylistUrl(playlistEntry);
            if (string.IsNullOrWhiteSpace(playlistUrl))
            {
                continue;
            }

            var playlistId = GetChannelPlaylistId(playlistEntry);
            if (string.IsNullOrWhiteSpace(playlistId)
                || !seasonLookup.TryGetValue(playlistId, out var seasonDefinition))
            {
                continue;
            }

            discoveredPlaylists++;
            var playlistVideos = await _ytDlpService
                .GetPlaylistEntriesAsync(playlistUrl, videoRetentionDays, maxEntryScanCount, cancellationToken)
                .ConfigureAwait(false);

            for (var index = 0; index < playlistVideos.Count; index++)
            {
                var videoEntry = playlistVideos[index];
                var videoId = GetString(videoEntry, "id");
                if (string.IsNullOrWhiteSpace(videoId))
                {
                    continue;
                }

                if (videoEntry is JsonObject videoObject)
                {
                    videoObject["__playlist_id"] = playlistId;
                    videoObject["__playlist_title"] = seasonDefinition.Title;
                    videoObject["__playlist_season_number"] = seasonDefinition.SeasonNumber;
                    videoObject["__playlist_episode_number"] = index + 1;
                    videoObject["__sync_id"] = playlistId + ":" + videoId;
                }

                expandedEntries.Add(videoEntry);
            }
        }

        _logger.LogInformation(
            "Expanded {PlaylistCount} discovered playlist(s) into {VideoCount} unique video entr{Suffix}.",
            discoveredPlaylists,
            expandedEntries.Count,
            expandedEntries.Count == 1 ? "y" : "ies");

        return expandedEntries;
    }

    private static IReadOnlyList<PlaylistSeasonDefinition> BuildPlaylistSeasonDefinitions(IReadOnlyList<JsonNode> playlistEntries)
    {
        var definitions = new List<PlaylistSeasonDefinition>(playlistEntries.Count);
        var seenPlaylistIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var playlistEntry in playlistEntries)
        {
            var playlistId = GetChannelPlaylistId(playlistEntry);
            if (string.IsNullOrWhiteSpace(playlistId) || !seenPlaylistIds.Add(playlistId))
            {
                continue;
            }

            var title = GetString(playlistEntry, "title");
            if (string.IsNullOrWhiteSpace(title))
            {
                title = playlistId;
            }

            definitions.Add(new PlaylistSeasonDefinition(playlistId, title, definitions.Count + 1));
        }

        return definitions;
    }

    private static string GetChannelPlaylistUrl(JsonNode entry)
    {
        var webpageUrl = GetString(entry, "webpage_url");
        if (!string.IsNullOrWhiteSpace(webpageUrl) && webpageUrl.Contains("list=", StringComparison.OrdinalIgnoreCase))
        {
            return webpageUrl;
        }

        var url = GetString(entry, "url");
        if (!string.IsNullOrWhiteSpace(url))
        {
            if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                return url;
            }

            if (url.Contains("list=", StringComparison.OrdinalIgnoreCase))
            {
                return $"https://www.youtube.com/{url.TrimStart('/')}";
            }
        }

        var playlistId = GetString(entry, "id");
        if (string.IsNullOrWhiteSpace(playlistId))
        {
            return string.Empty;
        }

        return $"https://www.youtube.com/playlist?list={playlistId}";
    }

    private static string GetChannelPlaylistId(JsonNode entry)
    {
        var playlistId = GetString(entry, "id");
        if (!string.IsNullOrWhiteSpace(playlistId))
        {
            return playlistId;
        }

        var playlistUrl = GetChannelPlaylistUrl(entry);
        if (string.IsNullOrWhiteSpace(playlistUrl))
        {
            return string.Empty;
        }

        try
        {
            var uri = new Uri(playlistUrl);
            var query = uri.Query.TrimStart('?')
                .Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            foreach (var pair in query)
            {
                var parts = pair.Split('=', 2);
                if (parts.Length == 2 && parts[0].Equals("list", StringComparison.OrdinalIgnoreCase))
                {
                    return Uri.UnescapeDataString(parts[1]);
                }
            }
        }
        catch (UriFormatException)
        {
        }

        return string.Empty;
    }

    private static void NormalizeVideoMetadata(VideoMetadata metadata, JsonNode entry, string videoId, string sourceName)
    {
        if (string.IsNullOrWhiteSpace(metadata.SyncId))
        {
            metadata.SyncId = GetString(entry, "__sync_id");
        }

        if (string.IsNullOrWhiteSpace(metadata.SyncId))
        {
            metadata.SyncId = videoId;
        }

        if (string.IsNullOrWhiteSpace(metadata.VideoId))
        {
            metadata.VideoId = videoId;
        }

        if (string.IsNullOrWhiteSpace(metadata.Title))
        {
            metadata.Title = GetString(entry, "title");
        }

        if (string.IsNullOrWhiteSpace(metadata.Description))
        {
            metadata.Description = GetString(entry, "description");
        }

        if (string.IsNullOrWhiteSpace(metadata.ChannelName))
        {
            metadata.ChannelName = sourceName;
        }

        if (string.IsNullOrWhiteSpace(metadata.PlaylistId))
        {
            metadata.PlaylistId = GetString(entry, "__playlist_id");
        }

        if (string.IsNullOrWhiteSpace(metadata.PlaylistTitle))
        {
            metadata.PlaylistTitle = GetString(entry, "__playlist_title");
        }

        if (metadata.PlaylistSeasonNumber is null)
        {
            metadata.PlaylistSeasonNumber = GetNullableInt(entry, "__playlist_season_number");
        }

        if (metadata.PlaylistEpisodeNumber is null)
        {
            metadata.PlaylistEpisodeNumber = GetNullableInt(entry, "__playlist_episode_number");
        }

        if (metadata.PublishedUtc is null)
        {
            metadata.PublishedUtc = YtDlpService.ParsePublishedDate(entry);
        }
    }

    private static Dictionary<int, Dictionary<string, int>> BuildSeasonEpisodeCounters(
        IReadOnlyList<VideoMetadata> videos,
        SourceDefinition source)
    {
        var counters = new Dictionary<int, Dictionary<string, int>>();
        if (source.Mode == SourceMode.Movies)
        {
            return counters;
        }

        foreach (var seasonGroup in videos
                     .GroupBy(video => GetSeasonNumber(video, source) ?? 0)
                     .OrderBy(group => group.Key))
        {
            var seasonEpisodes = new Dictionary<string, int>(StringComparer.Ordinal);
            var ordered = seasonGroup
                .OrderBy(video => video.PlaylistEpisodeNumber ?? int.MaxValue)
                .ThenBy(video => video.PublishedUtc ?? DateTime.MinValue)
                .ThenBy(video => video.Title, StringComparer.OrdinalIgnoreCase)
                .ToList();

            for (var index = 0; index < ordered.Count; index++)
            {
                seasonEpisodes[ordered[index].SyncId] = index + 1;
            }

            counters[seasonGroup.Key] = seasonEpisodes;
        }

        return counters;
    }

    private static int? GetEpisodeNumber(
        VideoMetadata video,
        IReadOnlyDictionary<int, Dictionary<string, int>> seasonEpisodeCounters,
        SourceDefinition source)
    {
        if (source.Mode == SourceMode.Movies)
        {
            return null;
        }

        var seasonNumber = GetSeasonNumber(video, source) ?? 0;
        return seasonEpisodeCounters.TryGetValue(seasonNumber, out var seasonMap)
            && seasonMap.TryGetValue(video.SyncId, out var episode)
            ? episode
            : null;
    }

    private static int? GetSeasonNumber(VideoMetadata video, SourceDefinition source)
    {
        if (source.Mode == SourceMode.Movies)
        {
            return null;
        }

        if (source.Type == SourceType.Channel && source.Feed == ChannelFeed.Playlists)
        {
            return video.PlaylistSeasonNumber;
        }

        return video.PublishedUtc?.Year;
    }

    private static string GetSeasonFolderName(VideoMetadata video, SourceDefinition source)
    {
        if (source.Mode == SourceMode.Movies)
        {
            return string.Empty;
        }

        if (source.Type == SourceType.Channel && source.Feed == ChannelFeed.Playlists)
        {
            if (video.PlaylistSeasonNumber is not int playlistSeasonNumber)
            {
                return string.Empty;
            }

            return $"Season {playlistSeasonNumber:00}";
        }

        return video.PublishedUtc is DateTime date ? $"Season {date.Year}" : string.Empty;
    }

    private static string BuildVideoFolderName(string title, string videoId)
    {
        var safeTitle = SanitizeFileName(string.IsNullOrWhiteSpace(title) ? videoId : title);
        return $"{safeTitle} [{videoId}]";
    }

    private void CleanupObsoleteContent(
        string sourceDir,
        SourceMode sourceMode,
        HashSet<string> desiredSeasonDirectories,
        HashSet<string> desiredVideoDirectories)
    {
        DeleteLegacyRootVideoFiles(sourceDir);

        if (sourceMode == SourceMode.Movies)
        {
            foreach (var directory in Directory.EnumerateDirectories(sourceDir))
            {
                if (!desiredVideoDirectories.Contains(directory))
                {
                    TryDeleteDirectory(directory);
                }
            }

            return;
        }

        foreach (var seasonDirectory in Directory.EnumerateDirectories(sourceDir))
        {
            if (!Path.GetFileName(seasonDirectory).StartsWith("Season ", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!desiredSeasonDirectories.Contains(seasonDirectory))
            {
                TryDeleteDirectory(seasonDirectory);
                continue;
            }

            foreach (var videoDirectory in Directory.EnumerateDirectories(seasonDirectory))
            {
                if (!desiredVideoDirectories.Contains(videoDirectory))
                {
                    TryDeleteDirectory(videoDirectory);
                }
            }
        }
    }

    private static void DeleteLegacyRootVideoFiles(string sourceDir)
    {
        foreach (var filePath in Directory.EnumerateFiles(sourceDir))
        {
            var fileName = Path.GetFileName(filePath);
            if (fileName.Equals("tvshow.nfo", StringComparison.OrdinalIgnoreCase)
                || fileName.Equals("movie.nfo", StringComparison.OrdinalIgnoreCase)
                || fileName.StartsWith("folder.", StringComparison.OrdinalIgnoreCase)
                || fileName.StartsWith("poster.", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (fileName.EndsWith(".strm", StringComparison.OrdinalIgnoreCase)
                || fileName.EndsWith(".nfo", StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(filePath);
            }
        }
    }

    private async Task DownloadArtworkAsync(string imageUrl, string directory, IReadOnlyList<string> baseNames, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(imageUrl))
        {
            return;
        }

        if (baseNames.Count == 0)
        {
            return;
        }

        if (AreAllArtworkTargetsPresent(directory, baseNames))
        {
            _logger.LogDebug("Skipping artwork download for {ImageUrl} because all targets already exist in {Directory}", imageUrl, directory);
            return;
        }

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(ArtworkDownloadTimeout);

            var bytes = await HttpClient.GetByteArrayAsync(imageUrl, timeoutCts.Token).ConfigureAwait(false);
            var extension = GetImageExtension(imageUrl);

            foreach (var baseName in baseNames)
            {
                if (HasArtworkVariant(directory, baseName))
                {
                    continue;
                }

                var targetPath = Path.Combine(directory, baseName + extension);
                await File.WriteAllBytesAsync(targetPath, bytes, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                "Timed out downloading artwork from {ImageUrl} after {TimeoutSeconds} seconds.",
                imageUrl,
                ArtworkDownloadTimeout.TotalSeconds);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to download artwork from {ImageUrl}", imageUrl);
        }
    }

    private static void AddArtworkTarget(Dictionary<string, List<string>> artworkDownloads, string imageUrl, string baseName)
    {
        if (string.IsNullOrWhiteSpace(imageUrl))
        {
            return;
        }

        if (!artworkDownloads.TryGetValue(imageUrl, out var baseNames))
        {
            baseNames = new List<string>();
            artworkDownloads[imageUrl] = baseNames;
        }

        if (!baseNames.Contains(baseName, StringComparer.OrdinalIgnoreCase))
        {
            baseNames.Add(baseName);
        }
    }

    private static string GetImageExtension(string imageUrl)
    {
        try
        {
            var extension = Path.GetExtension(new Uri(imageUrl).AbsolutePath);
            if (string.Equals(extension, ".jpg", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".jpeg", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".png", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".webp", StringComparison.OrdinalIgnoreCase))
            {
                return extension;
            }
        }
        catch (UriFormatException)
        {
        }

        return ".jpg";
    }

    private static void DeleteArtworkVariants(string directory, string baseName)
    {
        foreach (var extension in ArtworkExtensions)
        {
            var candidatePath = Path.Combine(directory, baseName + extension);
            if (File.Exists(candidatePath))
            {
                File.Delete(candidatePath);
            }
        }
    }

    private static bool AreAllArtworkTargetsPresent(string directory, IReadOnlyList<string> baseNames)
    {
        foreach (var baseName in baseNames)
        {
            if (!HasArtworkVariant(directory, baseName))
            {
                return false;
            }
        }

        return true;
    }

    private static bool HasArtworkVariant(string directory, string baseName)
    {
        foreach (var extension in ArtworkExtensions)
        {
            var candidatePath = Path.Combine(directory, baseName + extension);
            if (File.Exists(candidatePath))
            {
                return true;
            }
        }

        return false;
    }

    private void TryDeleteDirectory(string directoryPath)
    {
        try
        {
            if (Directory.Exists(directoryPath))
            {
                Directory.Delete(directoryPath, recursive: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to delete obsolete synced directory {DirectoryPath}", directoryPath);
        }
    }

    private void ReportPhaseProgress(
        IProgress<double> progress,
        double sourceProgressBase,
        double sourceProgressSpan,
        int completed,
        int total,
        double phaseStartFraction,
        double phaseEndFraction,
        string phaseName,
        string sourceName)
    {
        if (total <= 0)
        {
            progress.Report(sourceProgressBase + sourceProgressSpan * phaseEndFraction);
            return;
        }

        var phaseFraction = phaseStartFraction + ((phaseEndFraction - phaseStartFraction) * completed / total);
        var progressValue = sourceProgressBase + sourceProgressSpan * phaseFraction;
        progress.Report(progressValue);

        if (completed == 1 || completed == total || completed % 25 == 0)
        {
            _logger.LogInformation(
                "Source '{Name}' {Phase} progress: {Completed}/{Total}",
                sourceName,
                phaseName,
                completed,
                total);
        }
    }

    private static int GetMaxEntryScanCount(int videoRetentionDays, int maxVideosPerSource)
    {
        if (videoRetentionDays <= 0)
        {
            return 0;
        }

        var retentionBasedLimit = Math.Max(
            MinimumRetentionEntryScanCount,
            videoRetentionDays * EstimatedUploadsPerDayForRetentionScan);

        if (maxVideosPerSource > 0)
        {
            return Math.Min(retentionBasedLimit, maxVideosPerSource);
        }

        return retentionBasedLimit;
    }

    private static string BuildDateTag(string tagName, DateTime? date)
    {
        return date is DateTime value ? $"\n  <{tagName}>{value:yyyy-MM-dd}</{tagName}>" : string.Empty;
    }

    private static string SanitizeFileName(string name)
    {
        var sb = new StringBuilder(name);
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            sb.Replace(c, '_');
        }

        return sb.ToString();
    }

    private static string Xml(string text) =>
        text.Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal)
            .Replace("'", "&apos;", StringComparison.Ordinal);
}
