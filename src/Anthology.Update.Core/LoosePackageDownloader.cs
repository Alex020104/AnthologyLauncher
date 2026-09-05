using Anthology.Contracts;

namespace Anthology.Update.Core;

/// <summary>
/// Downloads and verifies ordinary package files into a disposable staging
/// directory. Files are never written directly into the live game tree.
/// </summary>
public sealed class LoosePackageDownloader
{
    public const int DefaultMaximumParallelDownloads = 4;
    public const int DefaultMaximumAttempts = 3;

    private readonly HttpClient _httpClient;
    private readonly IReadOnlyList<IMirrorResolver>? _resolvers;
    private readonly int _maximumParallelDownloads;
    private readonly int _maximumAttempts;

    public LoosePackageDownloader(
        HttpClient httpClient,
        IEnumerable<IMirrorResolver>? resolvers = null,
        int maximumParallelDownloads = DefaultMaximumParallelDownloads,
        int maximumAttempts = DefaultMaximumAttempts)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumParallelDownloads, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumParallelDownloads, 16);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumAttempts, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumAttempts, 10);
        _httpClient = httpClient;
        _resolvers = resolvers?.ToArray();
        _maximumParallelDownloads = maximumParallelDownloads;
        _maximumAttempts = maximumAttempts;
    }

    public async Task DownloadAsync(
        PackageManifest package,
        string stagingRoot,
        IReadOnlyCollection<string> filesToDownload,
        IProgress<DownloadProgress>? progress = null,
        string? preferredProvider = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingRoot);
        ArgumentNullException.ThrowIfNull(filesToDownload);
        if (package.LooseFiles is null)
        {
            throw new ArgumentException("Package does not contain loose-file metadata.", nameof(package));
        }

        var entries = package.LooseFiles.ToDictionary(
            file => PathSafety.NormalizeRelativePath(file.Path),
            StringComparer.OrdinalIgnoreCase);
        var selected = filesToDownload
            .Select(PathSafety.NormalizeRelativePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => entries.TryGetValue(path, out var entry)
                ? entry with { Path = path }
                : throw new InvalidDataException($"Loose package '{package.Id}' does not declare '{path}'."))
            .ToArray();
        var root = Path.GetFullPath(stagingRoot);
        Directory.CreateDirectory(root);
        if (selected.Length == 0)
        {
            return;
        }

        var totalBytes = selected.Aggregate(0L, static (total, file) => checked(total + file.Size));
        var reportedBytes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var progressLock = new object();
        var downloader = new ArtifactDownloader(_httpClient, _resolvers);
        await Parallel.ForEachAsync(
            selected,
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = _maximumParallelDownloads,
            },
            async (file, token) =>
            {
                var destination = PathSafety.ResolveUnderRoot(root, file.Path);
                var mirrors = ResolveMirrors(package, file);
                var singleFilePackage = new PackageManifest(
                    package.Id,
                    package.DisplayName,
                    package.Version,
                    package.Kind,
                    package.InstallRoot,
                    "raw",
                    file.Size,
                    file.Sha256,
                    mirrors,
                    [file.Path]);
                var fileProgress = progress is null
                    ? null
                    : new InlineProgress<DownloadProgress>(value =>
                    {
                        long aggregate;
                        lock (progressLock)
                        {
                            reportedBytes.TryGetValue(file.Path, out var previous);
                            reportedBytes[file.Path] = Math.Max(previous, Math.Min(file.Size, value.DownloadedBytes));
                            aggregate = reportedBytes.Values.Sum();
                        }
                        progress.Report(new DownloadProgress(aggregate, totalBytes, value.Provider));
                    });

                Exception? lastError = null;
                for (var attempt = 1; attempt <= _maximumAttempts; attempt++)
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        await downloader.DownloadAsync(
                            singleFilePackage,
                            destination,
                            fileProgress,
                            preferredProvider,
                            token);
                        lock (progressLock)
                        {
                            reportedBytes[file.Path] = file.Size;
                        }
                        return;
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception exception) when (exception is AggregateException
                                                       or HttpRequestException
                                                       or IOException
                                                       or InvalidDataException
                                                       or NotSupportedException
                                                       or UnauthorizedAccessException)
                    {
                        lastError = exception;
                        if (attempt < _maximumAttempts)
                        {
                            await Task.Delay(TimeSpan.FromMilliseconds(100 * attempt), token);
                        }
                    }
                }

                throw new InvalidOperationException(
                    $"Failed to download loose file '{file.Path}' after {_maximumAttempts} attempts.",
                    lastError);
            });
    }

    public static IReadOnlyList<MirrorManifest> ResolveMirrors(
        PackageManifest package,
        PackageLooseFile file)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(file);
        var normalizedPath = PathSafety.NormalizeRelativePath(file.Path);
        var escapedPath = string.Join('/', normalizedPath
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.EscapeDataString));
        var templatedMirrors = package.Mirrors.Select(mirror =>
        {
            if (!mirror.Url.Contains("{path}", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Loose mirror '{mirror.Provider}' for package '{package.Id}' has no '{{path}}' placeholder.");
            }

            return mirror with
            {
                Url = mirror.Url.Replace("{path}", escapedPath, StringComparison.OrdinalIgnoreCase),
            };
        });

        // Per-file links (for example Google Drive file IDs) extend the package
        // folder mirrors instead of replacing them. This keeps Yandex as a real
        // fallback while still allowing providers that require an exact URL for
        // every object.
        return (file.Mirrors ?? [])
            .Concat(templatedMirrors)
            .DistinctBy(
                mirror => $"{mirror.Provider.Trim().ToLowerInvariant()}\0{mirror.Url.Trim()}",
                StringComparer.OrdinalIgnoreCase)
            .OrderBy(mirror => mirror.Priority)
            .ToArray();
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
