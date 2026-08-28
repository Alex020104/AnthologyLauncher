using System.Net;
using System.Net.Http.Headers;
using Anthology.Contracts;

namespace Anthology.Update.Core;

public sealed record DownloadProgress(long DownloadedBytes, long TotalBytes, string Provider);

public sealed record DownloadResult(string Path, string Provider, bool Resumed);

public sealed class ArtifactDownloader
{
    private readonly HttpClient _httpClient;
    private readonly IReadOnlyList<IMirrorResolver> _resolvers;

    public ArtifactDownloader(HttpClient httpClient, IEnumerable<IMirrorResolver>? resolvers = null)
    {
        _httpClient = httpClient;
        _resolvers = resolvers?.ToArray()
            ?? [new YandexDiskMirrorResolver(httpClient), new LocalFileMirrorResolver(), new DirectMirrorResolver()];
    }

    public async Task<DownloadResult> DownloadAsync(
        PackageManifest package,
        string destination,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        var destinationFullPath = Path.GetFullPath(destination);
        Directory.CreateDirectory(Path.GetDirectoryName(destinationFullPath)!);
        var partialPath = destinationFullPath + ".partial";
        var failures = new List<Exception>();

        foreach (var mirror in package.Mirrors.OrderBy(item => item.Priority))
        {
            try
            {
                var resolver = _resolvers.FirstOrDefault(item => item.CanResolve(mirror))
                    ?? throw new NotSupportedException($"No resolver for provider '{mirror.Provider}'.");
                var uri = await resolver.ResolveAsync(mirror, cancellationToken);
                var resumed = await DownloadFromMirrorAsync(
                    uri,
                    mirror.Provider,
                    package.Size,
                    partialPath,
                    progress,
                    cancellationToken);

                var actualSize = new FileInfo(partialPath).Length;
                if (actualSize != package.Size)
                {
                    throw new InvalidDataException(
                        $"Size mismatch for '{package.Id}': expected {package.Size}, got {actualSize}.");
                }

                var actualHash = await ArtifactHash.ComputeSha256Async(partialPath, cancellationToken);
                if (!string.Equals(actualHash, package.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        $"SHA-256 mismatch for '{package.Id}' from '{mirror.Provider}'.");
                }

                File.Move(partialPath, destinationFullPath, true);
                return new DownloadResult(destinationFullPath, mirror.Provider, resumed);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (exception is HttpRequestException
                                               or IOException
                                               or InvalidDataException
                                               or NotSupportedException
                                               or UnauthorizedAccessException
                                               or UriFormatException)
            {
                failures.Add(new InvalidOperationException(
                    $"Mirror '{mirror.Provider}' failed: {exception.Message}", exception));

                if (exception is InvalidDataException && File.Exists(partialPath))
                {
                    File.Delete(partialPath);
                }
            }
        }

        throw new AggregateException($"All mirrors failed for package '{package.Id}'.", failures);
    }

    private async Task<bool> DownloadFromMirrorAsync(
        Uri uri,
        string provider,
        long expectedSize,
        string partialPath,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (uri.IsFile)
        {
            return await CopyFromLocalFileAsync(
                uri.LocalPath,
                provider,
                expectedSize,
                partialPath,
                progress,
                cancellationToken);
        }

        var existingLength = File.Exists(partialPath) ? new FileInfo(partialPath).Length : 0;
        if (existingLength >= expectedSize)
        {
            if (existingLength == expectedSize)
            {
                return true;
            }

            File.Delete(partialPath);
            existingLength = 0;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        if (existingLength > 0)
        {
            request.Headers.Range = new RangeHeaderValue(existingLength, null);
        }

        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();

        var serverResumed = existingLength > 0 && response.StatusCode == HttpStatusCode.PartialContent;
        if (existingLength > 0 && !serverResumed)
        {
            existingLength = 0;
        }

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var target = new FileStream(
            partialPath,
            serverResumed ? FileMode.Append : FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        var buffer = new byte[1024 * 1024];
        var downloaded = existingLength;
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            downloaded += read;
            progress?.Report(new DownloadProgress(downloaded, expectedSize, provider));
        }

        await target.FlushAsync(cancellationToken);
        return serverResumed;
    }

    private static async Task<bool> CopyFromLocalFileAsync(
        string sourcePath,
        string provider,
        long expectedSize,
        string partialPath,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var sourceFullPath = Path.GetFullPath(sourcePath);
        if (!File.Exists(sourceFullPath))
        {
            throw new FileNotFoundException("Local mirror artifact was not found.", sourceFullPath);
        }

        var sourceLength = new FileInfo(sourceFullPath).Length;
        if (sourceLength != expectedSize)
        {
            throw new InvalidDataException(
                $"Local mirror size mismatch: expected {expectedSize}, got {sourceLength}.");
        }

        var existingLength = File.Exists(partialPath) ? new FileInfo(partialPath).Length : 0;
        if (existingLength > sourceLength)
        {
            File.Delete(partialPath);
            existingLength = 0;
        }

        if (existingLength == sourceLength)
        {
            progress?.Report(new DownloadProgress(existingLength, expectedSize, provider));
            return true;
        }

        await using var source = new FileStream(
            sourceFullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        source.Position = existingLength;
        await using var target = new FileStream(
            partialPath,
            existingLength > 0 ? FileMode.Append : FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        var buffer = new byte[1024 * 1024];
        var copied = existingLength;
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            copied += read;
            progress?.Report(new DownloadProgress(copied, expectedSize, provider));
        }

        await target.FlushAsync(cancellationToken);
        return existingLength > 0;
    }
}
