using Anthology.Contracts;

namespace Anthology.Releaser.Core;

public sealed record PreparedContentMedia(
    IReadOnlyDictionary<string, IReadOnlyList<string>> ContentImages,
    IReadOnlyDictionary<string, string> BlockImages,
    IReadOnlyList<string> RelativeFiles)
{
    public static PreparedContentMedia Empty { get; } = new(
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase),
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        []);
}

public static class ContentMediaPublisher
{
    private const long MaximumImageBytes = 25L * 1024 * 1024;
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".webp",
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
        var hasLocalImages = selected.Any(content =>
            GetPaths(machine, ContentKey(content.Id)).Count > 0
            || (content.Blocks ?? []).Any(block => GetPaths(machine, BlockKey(content.Id, block.Id)).Count > 0));
        if (!hasLocalImages)
        {
            return PreparedContentMedia.Empty;
        }

        var publicTemplate = workspace.Mirrors
            .Where(mirror => !string.IsNullOrWhiteSpace(mirror.ContentUrl))
            .OrderBy(mirror => mirror.Priority)
            .Select(mirror => mirror.ContentUrl.Trim())
            .FirstOrDefault();
        if (string.IsNullOrWhiteSpace(publicTemplate))
        {
            throw new InvalidOperationException(
                "Для публикации загруженных фотографий укажите HTTPS-шаблон «Аддоны и медиа» хотя бы у одного источника.");
        }

        var contentImages = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
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

        return new PreparedContentMedia(contentImages, blockImages, relativeFiles
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray());
    }

    private static List<string> GetPaths(ReleaserMachineSettings machine, string key) =>
        machine.ContentImagePaths.TryGetValue(key, out var paths) ? paths : [];

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
        if (!SupportedExtensions.Contains(extension))
        {
            throw new InvalidDataException($"Фотография должна быть PNG, JPG, JPEG или WEBP: {source}");
        }
        if (new FileInfo(source).Length > MaximumImageBytes)
        {
            throw new InvalidDataException($"Фотография превышает ограничение 25 МБ: {source}");
        }

        var fileName = $"{NormalizeFilePart(prefix)}-{NormalizeFilePart(Path.GetFileNameWithoutExtension(source))}{extension}";
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
