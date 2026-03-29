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
    private const int MaxPerSourceConcurrency = 4;
    private const int MinimumRetentionEntryScanCount = 25;
    private const int EstimatedUploadsPerDayForRetentionScan = 5;
    private static readonly TimeSpan ArtworkDownloadTimeout = TimeSpan.FromSeconds(20);

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

        Directory.CreateDirectory(sourceDir);
        await WriteSourceMetadataAsync(source, sourceDir, name, description, thumbnailUrl, posterUrl, cancellationToken).ConfigureAwait(false);

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

                        var seasonFolder = GetSeasonFolderName(metadata.PublishedUtc, source.Mode);
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

        var seasonEpisodeCounters = BuildSeasonEpisodeCounters(retainedVideos, source.Mode);
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
                        var seasonFolder = GetSeasonFolderName(video.PublishedUtc, source.Mode);
                        var parentDir = string.IsNullOrEmpty(seasonFolder)
                            ? sourceDir
                            : Path.Combine(sourceDir, seasonFolder);
                        var videoDir = Path.Combine(parentDir, BuildVideoFolderName(video.Title, video.VideoId));
                        var seasonNumber = GetSeasonNumber(video.PublishedUtc, source.Mode);
                        var episodeNumber = GetEpisodeNumber(video, seasonEpisodeCounters, source.Mode);

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
        CancellationToken cancellationToken)
    {
        bool isSeries = source.Type == SourceType.Channel || source.Mode == SourceMode.Series;
        var nfoFileName = isSeries ? "tvshow.nfo" : "movie.nfo";
        var nfoPath = Path.Combine(dir, nfoFileName);

        var content = isSeries
            ? BuildTvShowNfo(source, name, description, thumbnailUrl)
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

    private static string BuildTvShowNfo(SourceDefinition source, string name, string description, string thumbnailUrl)
    {
        var thumb = string.IsNullOrEmpty(thumbnailUrl)
            ? string.Empty
            : $"\n  <thumb aspect=\"poster\">{Xml(thumbnailUrl)}</thumb>";

        return $"""
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <tvshow>
          <title>{Xml(name)}</title>
          <plot>{Xml(description)}</plot>
          <uniqueid type="youtube" default="true">{Xml(source.Id)}</uniqueid>{thumb}
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
          <uniqueid type="youtube" default="true">{Xml(video.VideoId)}</uniqueid>{aired}{premiered}{season}{episode}{runtime}{studio}{thumb}
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
          <uniqueid type="youtube" default="true">{Xml(video.VideoId)}</uniqueid>{premiered}{runtime}{studio}{set}{thumb}
        </movie>
        """;
    }

    // ── utilities ─────────────────────────────────────────────────────────────

    private static string GetString(JsonNode? node, string key)
    {
        try { return node?[key]?.GetValue<string>() ?? string.Empty; }
        catch { return string.Empty; }
    }

    private static VideoMetadata BuildFallbackVideoMetadata(JsonNode entry, string videoId, string sourceName)
    {
        return new VideoMetadata
        {
            VideoId = videoId,
            Title = GetString(entry, "title"),
            Description = GetString(entry, "description"),
            ThumbnailUrl = GetFallbackVideoThumbnailUrl(entry),
            ChannelName = sourceName,
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

    private static void NormalizeVideoMetadata(VideoMetadata metadata, JsonNode entry, string videoId, string sourceName)
    {
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

        if (metadata.PublishedUtc is null)
        {
            metadata.PublishedUtc = YtDlpService.ParsePublishedDate(entry);
        }
    }

    private static Dictionary<int, Dictionary<string, int>> BuildSeasonEpisodeCounters(
        IReadOnlyList<VideoMetadata> videos,
        SourceMode sourceMode)
    {
        var counters = new Dictionary<int, Dictionary<string, int>>();
        if (sourceMode == SourceMode.Movies)
        {
            return counters;
        }

        foreach (var seasonGroup in videos
                     .GroupBy(video => GetSeasonNumber(video.PublishedUtc, sourceMode) ?? 0)
                     .OrderBy(group => group.Key))
        {
            var seasonEpisodes = new Dictionary<string, int>(StringComparer.Ordinal);
            var ordered = seasonGroup
                .OrderBy(video => video.PublishedUtc ?? DateTime.MinValue)
                .ThenBy(video => video.Title, StringComparer.OrdinalIgnoreCase)
                .ToList();

            for (var index = 0; index < ordered.Count; index++)
            {
                seasonEpisodes[ordered[index].VideoId] = index + 1;
            }

            counters[seasonGroup.Key] = seasonEpisodes;
        }

        return counters;
    }

    private static int? GetEpisodeNumber(
        VideoMetadata video,
        IReadOnlyDictionary<int, Dictionary<string, int>> seasonEpisodeCounters,
        SourceMode sourceMode)
    {
        if (sourceMode == SourceMode.Movies)
        {
            return null;
        }

        var seasonNumber = GetSeasonNumber(video.PublishedUtc, sourceMode) ?? 0;
        return seasonEpisodeCounters.TryGetValue(seasonNumber, out var seasonMap)
            && seasonMap.TryGetValue(video.VideoId, out var episode)
            ? episode
            : null;
    }

    private static int? GetSeasonNumber(DateTime? publishedUtc, SourceMode sourceMode)
    {
        if (sourceMode == SourceMode.Movies)
        {
            return null;
        }

        return publishedUtc?.Year;
    }

    private static string GetSeasonFolderName(DateTime? publishedUtc, SourceMode sourceMode)
    {
        if (sourceMode == SourceMode.Movies)
        {
            return string.Empty;
        }

        return publishedUtc is DateTime date ? $"Season {date.Year}" : string.Empty;
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

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(ArtworkDownloadTimeout);

            var bytes = await HttpClient.GetByteArrayAsync(imageUrl, timeoutCts.Token).ConfigureAwait(false);
            var extension = GetImageExtension(imageUrl);

            foreach (var baseName in baseNames)
            {
                DeleteArtworkVariants(directory, baseName);
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
        foreach (var extension in new[] { ".jpg", ".jpeg", ".png", ".webp" })
        {
            var candidatePath = Path.Combine(directory, baseName + extension);
            if (File.Exists(candidatePath))
            {
                File.Delete(candidatePath);
            }
        }
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
