using Anthology.Contracts;
using Anthology.Update.Core;

namespace Anthology.Releaser.Core;

public sealed record PreparedContentMedia(
    IReadOnlyDictionary<string, IReadOnlyList<string>> ContentImages,
    IReadOnlyDictionary<string, IReadOnlyList<ContentVideo>> ContentVideos,
    IReadOnlyDictionary<string, string> BlockImages,
    IReadOnlyList<string> RelativeFiles)
{
    public static PreparedContentMedia Empty { get; } = new(
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase),
        new Dictionary<string, IReadOnlyList<ContentVideo>>(StringComparer.OrdinalIgnoreCase),
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        []);
}

public static class ContentMediaPublisher
{
    private const long MaximumImageBytes = 25L * 1024 * 1024;
    private const long MaximumVideoBytes = 2L * 1024 * 1024 * 1024;
    private static readonly HashSet<string> SupportedImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".webp",
    };
    private static readonly HashSet<string> SupportedVideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".webm", ".ogv",
    };

    public static string ContentKey(string contentId) => $"content/{contentId.Trim()}";

    public static string BlockKey(string contentId, string blockId) =>
        $"block/{contentId.Trim()}/{blockId.Trim()}";

    public static async Task<PreparedContentMedia> PrepareAsync(
        ReleaserWorkspace workspace,
        ReleaserMachineSettings machine,
        string versionRoot,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(machine);
        var selected = workspace.Content.Where(content => content.IsPublished).ToArray();
        var hasLocalMedia = selected.Any(content =>
            GetPaths(machine, ContentKey(content.Id)).Count > 0
            || GetVideoPaths(machine, ContentKey(content.Id)).Count > 0
            || (content.Blocks ?? []).Any(block => GetPaths(machine, BlockKey(content.Id, block.Id)).Count > 0));
        if (!hasLocalMedia)
        {
            return PreparedContentMedia.Empty;
        }

        var publicTemplate = workspace.Mirrors
            .Where(mirror => !string.IsNullOrWhiteSpace(mirror.ContentUrl))
            // Public Yandex/Google sharing links open an HTML landing page and
            // therefore cannot be used directly by <img>. Prefer a raw/CDN
            // source for inline launcher media even when a disk mirror has a
            // higher download priority.
            .OrderBy(mirror => InlineMediaProviderPriority(mirror.Provider))
            .ThenBy(mirror => mirror.Priority)
            .Select(mirror => mirror.ContentUrl.Trim())
            .FirstOrDefault();
        if (string.IsNullOrWhiteSpace(publicTemplate))
        {
            throw new InvalidOperationException(
                "Для публикации загруженных фотографий и видео укажите HTTPS-шаблон «Аддоны и медиа» хотя бы у одного источника.");
        }
        var videoPublicTemplate = workspace.Mirrors
            .Where(mirror => !string.IsNullOrWhiteSpace(mirror.ContentUrl))
            // A public Yandex.Disk folder can be resolved by the launcher through
            // the official download API. Prefer it for video so large files do not
            // inherit GitHub's repository file-size limit.
            .OrderBy(mirror => VideoMediaProviderPriority(mirror.Provider))
            .ThenBy(mirror => mirror.Priority)
            .Select(mirror => mirror.ContentUrl.Trim())
            .FirstOrDefault() ?? publicTemplate;

        var contentImages = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        var contentVideos = new Dictionary<string, IReadOnlyList<ContentVideo>>(StringComparer.OrdinalIgnoreCase);
        var blockImages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var relativeFiles = new List<string>();
        foreach (var content in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var normalizedContentId = NormalizeId(content.Id);
            var contentUrls = new List<string>();
            var contentPaths = GetPaths(machine, ContentKey(content.Id));
            for (var index = 0; index < contentPaths.Count; index++)
            {
                var prepared = await PrepareImageAsync(
                    contentPaths[index],
                    normalizedContentId,
                    $"{index + 1:00}",
                    publicTemplate,
                    workspace.Version,
                    versionRoot,
                    progress,
                    cancellationToken);
                contentUrls.Add(prepared.Url);
                relativeFiles.Add(prepared.RelativePath);
            }
            if (contentUrls.Count > 0)
            {
                contentImages[content.Id] = contentUrls;
            }

            var videoPaths = GetVideoPaths(machine, ContentKey(content.Id));
            var videoItems = new List<ContentVideo>();
            for (var index = 0; index < videoPaths.Count; index++)
            {
                var prepared = await PrepareVideoAsync(
                    videoPaths[index],
                    normalizedContentId,
                    $"{index + 1:00}",
                    videoPublicTemplate,
                    workspace.Version,
                    versionRoot,
                    progress,
                    cancellationToken);
                videoItems.Add(new ContentVideo(Path.GetFileNameWithoutExtension(videoPaths[index]), prepared.Url));
                relativeFiles.Add(prepared.RelativePath);
            }
            if (videoItems.Count > 0)
            {
                contentVideos[content.Id] = videoItems;
            }

            foreach (var block in content.Blocks ?? [])
            {
                var blockPaths = GetPaths(machine, BlockKey(content.Id, block.Id));
                if (blockPaths.Count == 0)
                {
                    continue;
                }
                var blockPath = blockPaths[0];
                var prepared = await PrepareImageAsync(
                    blockPath,
                    normalizedContentId,
                    NormalizeFilePart(block.Id),
                    publicTemplate,
                    workspace.Version,
                    versionRoot,
                    progress,
                    cancellationToken);
                blockImages[BlockKey(content.Id, block.Id)] = prepared.Url;
                relativeFiles.Add(prepared.RelativePath);
            }
        }

        return new PreparedContentMedia(contentImages, contentVideos, blockImages, relativeFiles
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray());
    }

    private static List<string> GetPaths(ReleaserMachineSettings machine, string key) =>
        machine.ContentImagePaths.TryGetValue(key, out var paths) ? paths : [];

    private static List<string> GetVideoPaths(ReleaserMachineSettings machine, string key) =>
        machine.ContentVideoPaths.TryGetValue(key, out var paths) ? paths : [];

    private static int InlineMediaProviderPriority(string provider) =>
        UnifiedReleaseBuilder.NormalizeProvider(provider) switch
        {
            "github" => 0,
            "http" => 1,
            "google-drive" => 2,
            "yandex-disk" => 3,
            _ => 4,
        };

    private static int VideoMediaProviderPriority(string provider) =>
        UnifiedReleaseBuilder.NormalizeProvider(provider) switch
        {
            "yandex-disk" => 0,
            "http" => 1,
            "github" => 2,
            "google-drive" => 3,
            _ => 4,
        };

    private static async Task<(string RelativePath, string Url)> PrepareImageAsync(
        string sourcePath,
        string contentId,
        string prefix,
        string publicTemplate,
        string version,
        string versionRoot,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var source = Path.GetFullPath(sourcePath);
        if (!File.Exists(source))
        {
            throw new FileNotFoundException("Загруженная фотография больше не найдена.", source);
        }
        var extension = Path.GetExtension(source).ToLowerInvariant();
        if (!SupportedImageExtensions.Contains(extension))
        {
            throw new InvalidDataException($"Фотография должна быть PNG, JPG, JPEG или WEBP: {source}");
        }
        if (new FileInfo(source).Length > MaximumImageBytes)
        {
            throw new InvalidDataException($"Фотография превышает ограничение 25 МБ: {source}");
        }

        // The public URL must change when an editor replaces an image while
        // keeping the same local file name. WebView2 and raw/CDN mirrors may
        // otherwise retain a previously cached 404 or the old bitmap.
        var contentHash = await ArtifactHash.ComputeSha256Async(source, cancellationToken);
        var fileName = $"{NormalizeFilePart(prefix)}-{NormalizeFilePart(Path.GetFileNameWithoutExtension(source))}-{contentHash[..12]}{extension}";
        var relativePath = Path.Combine("addons", contentId, "media", fileName);
        var destination = Path.Combine(Path.GetFullPath(versionRoot), relativePath);
        progress?.Report($"Подготовка фотографии {Path.GetFileName(source)}…");
        await CopyFileAtomicallyAsync(source, destination, cancellationToken);

        var publicUrl = UnifiedReleaseBuilder.ExpandUrl(
            publicTemplate,
            version,
            contentId,
            $"media/{fileName}");
        if (!Uri.TryCreate(publicUrl, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new InvalidDataException(
                $"Шаблон источника сформировал небезопасный адрес фотографии: {publicUrl}");
        }

        return (relativePath, uri.AbsoluteUri);
    }

    private static async Task<(string RelativePath, string Url)> PrepareVideoAsync(
        string sourcePath,
        string contentId,
        string prefix,
        string publicTemplate,
        string version,
        string versionRoot,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var source = Path.GetFullPath(sourcePath);
        if (!File.Exists(source))
        {
            throw new FileNotFoundException("Загруженный видеофайл больше не найден.", source);
        }

        var extension = Path.GetExtension(source).ToLowerInvariant();
        if (!SupportedVideoExtensions.Contains(extension))
        {
            throw new InvalidDataException($"Видеофайл должен быть MP4, WEBM или OGV: {source}");
        }
        if (new FileInfo(source).Length > MaximumVideoBytes)
        {
            throw new InvalidDataException($"Видеофайл превышает ограничение 2 ГБ: {source}");
        }

        var contentHash = await ArtifactHash.ComputeSha256Async(source, cancellationToken);
        var fileName = $"video-{NormalizeFilePart(prefix)}-{NormalizeFilePart(Path.GetFileNameWithoutExtension(source))}-{contentHash[..12]}{extension}";
        var relativePath = Path.Combine("addons", contentId, "media", fileName);
        var destination = Path.Combine(Path.GetFullPath(versionRoot), relativePath);
        progress?.Report($"Подготовка видео {Path.GetFileName(source)}…");
        await CopyFileAtomicallyAsync(source, destination, cancellationToken);

        var publicUrl = UnifiedReleaseBuilder.ExpandUrl(
            publicTemplate,
            version,
            contentId,
            $"media/{fileName}");
        if (!Uri.TryCreate(publicUrl, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new InvalidDataException(
                $"Шаблон источника сформировал небезопасный адрес видео: {publicUrl}");
        }

        return (relativePath, uri.AbsoluteUri);
    }

    private static async Task CopyFileAtomicallyAsync(
        string source,
        string destination,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + $".tmp-{Guid.NewGuid():N}";
        try
        {
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous))
            {
                await input.CopyToAsync(output, cancellationToken);
                await output.FlushAsync(cancellationToken);
            }
            File.Move(temporary, destination, true);
        }
        catch
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
            throw;
        }
    }

    private static string NormalizeId(string value)
    {
        var result = NormalizeFilePart(value).Trim('-');
        if (result.Length is < 2 or > 80)
        {
            throw new InvalidDataException("ID материала должен содержать от 2 до 80 латинских символов, цифр, точек, дефисов или подчёркиваний.");
        }
        return result;
    }

    private static string NormalizeFilePart(string value)
    {
        var chars = value.Trim().ToLowerInvariant()
            .Select(character => character is >= 'a' and <= 'z'
                                      or >= '0' and <= '9'
                                      or '-' or '_' or '.'
                ? character
                : '-')
            .ToArray();
        var normalized = new string(chars).Trim('-', '.');
        while (normalized.Contains("--", StringComparison.Ordinal))
        {
            normalized = normalized.Replace("--", "-", StringComparison.Ordinal);
        }
        return string.IsNullOrWhiteSpace(normalized) ? "image" : normalized;
    }
}
