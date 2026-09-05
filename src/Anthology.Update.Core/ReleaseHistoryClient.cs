using System.Security.Cryptography;
using System.Text.Json;
using Anthology.Contracts;

namespace Anthology.Update.Core;

public sealed class ReleaseHistoryClient(HttpClient httpClient)
{
    private const int MaximumDocumentBytes = 2 * 1024 * 1024;

    public async Task<SignedReleaseHistory> LoadVerifiedAsync(
        string source,
        string publicKeyPath,
        string expectedChannel,
        string? expectedKeyId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(publicKeyPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedChannel);

        var history = await LoadAsync(source.Trim(), cancellationToken);
        if (!string.IsNullOrWhiteSpace(expectedKeyId)
            && !string.Equals(history.Signature.KeyId, expectedKeyId.Trim(), StringComparison.Ordinal))
        {
            throw new CryptographicException(
                $"Release history was signed by unknown key '{history.Signature.KeyId}'.");
        }

        var keyPath = Path.GetFullPath(publicKeyPath);
        if (!File.Exists(keyPath))
        {
            throw new FileNotFoundException("Release-history public key was not found.", keyPath);
        }

        using var publicKey = ECDsa.Create();
        publicKey.ImportFromPem(await File.ReadAllTextAsync(keyPath, cancellationToken));
        if (!ManifestSecurity.Verify(history, publicKey))
        {
            throw new CryptographicException("Release-history signature verification failed.");
        }

        ReleaseHistoryValidator.ValidateAndThrow(history.Payload, expectedChannel);
        return history;
    }

    private async Task<SignedReleaseHistory> LoadAsync(
        string source,
        CancellationToken cancellationToken)
    {
        if (Uri.TryCreate(source, UriKind.Absolute, out var uri)
            && uri.Scheme is "http" or "https")
        {
            if (uri.Scheme != Uri.UriSchemeHttps && !uri.IsLoopback)
            {
                throw new InvalidDataException("Remote release history must use HTTPS.");
            }

            var downloadUri = YandexDiskMirrorResolver.IsYandexDiskUrl(source)
                ? await YandexDiskMirrorResolver.ResolvePublicDownloadAsync(httpClient, source, cancellationToken)
                : WebShareMirrorResolver.IsKnownShareUrl(source)
                    ? WebShareMirrorResolver.ResolveShareUrl(source)
                    : uri;
            using var request = CreateRequest(downloadUri);
            using var response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > MaximumDocumentBytes)
            {
                throw new InvalidDataException("Release history exceeds the 2 MiB safety limit.");
            }

            await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var bytes = await ReadBoundedAsync(responseStream, cancellationToken);
            return JsonSerializer.Deserialize<SignedReleaseHistory>(bytes, ManifestJson.Options)
                   ?? throw new InvalidDataException("Release history is empty or invalid JSON.");
        }

        var path = uri?.IsFile == true ? uri.LocalPath : Path.GetFullPath(source);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Release history was not found.", path);
        }
        if (new FileInfo(path).Length > MaximumDocumentBytes)
        {
            throw new InvalidDataException("Release history exceeds the 2 MiB safety limit.");
        }

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            32 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await JsonSerializer.DeserializeAsync<SignedReleaseHistory>(
                   stream,
                   ManifestJson.Options,
                   cancellationToken)
               ?? throw new InvalidDataException("Release history is empty or invalid JSON.");
    }

    private static async Task<byte[]> ReadBoundedAsync(
        Stream source,
        CancellationToken cancellationToken)
    {
        await using var buffer = new MemoryStream();
        var block = new byte[32 * 1024];
        while (true)
        {
            var read = await source.ReadAsync(block, cancellationToken);
            if (read == 0)
            {
                break;
            }
            if (buffer.Length + read > MaximumDocumentBytes)
            {
                throw new InvalidDataException("Release history exceeds the 2 MiB safety limit.");
            }
            await buffer.WriteAsync(block.AsMemory(0, read), cancellationToken);
        }
        return buffer.ToArray();
    }

    private static HttpRequestMessage CreateRequest(Uri source)
    {
        var requestUri = source;
        var bypassSharedCache = source.Host.Equals(
            "raw.githubusercontent.com",
            StringComparison.OrdinalIgnoreCase);
        if (bypassSharedCache)
        {
            var builder = new UriBuilder(source);
            var existingQuery = builder.Query.TrimStart('?');
            var cacheBuster = $"anthology_history_cb={Guid.NewGuid():N}";
            builder.Query = string.IsNullOrEmpty(existingQuery)
                ? cacheBuster
                : $"{existingQuery}&{cacheBuster}";
            requestUri = builder.Uri;
        }

        var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        if (bypassSharedCache)
        {
            request.Headers.CacheControl = new()
            {
                NoCache = true,
                NoStore = true,
                MaxAge = TimeSpan.Zero,
            };
            request.Headers.Pragma.ParseAdd("no-cache");
        }
        return request;
    }
}

public static class ReleaseHistorySourceResolver
{
    public const string FileName = "history.json";

    public static string? Resolve(string? configuredSource, string? manifestSource)
    {
        if (!string.IsNullOrWhiteSpace(configuredSource))
        {
            return configuredSource.Trim();
        }
        if (string.IsNullOrWhiteSpace(manifestSource))
        {
            return null;
        }

        var source = manifestSource.Trim();
        if (Path.IsPathFullyQualified(source))
        {
            try
            {
                var fullPath = Path.GetFullPath(source);
                return Path.Combine(Path.GetDirectoryName(fullPath)!, FileName);
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
            {
                return null;
            }
        }

        if (Uri.TryCreate(source, UriKind.Absolute, out var uri))
        {
            if (uri.IsFile)
            {
                return Path.Combine(Path.GetDirectoryName(uri.LocalPath)!, FileName);
            }
            if (uri.Scheme is not ("http" or "https"))
            {
                return null;
            }

            if (YandexDiskMirrorResolver.IsYandexDiskUrl(source))
            {
                return ResolveYandexSibling(uri);
            }
            if (uri.Host.Equals("drive.google.com", StringComparison.OrdinalIgnoreCase)
                || uri.Host.Equals("docs.google.com", StringComparison.OrdinalIgnoreCase)
                || uri.Host.Equals("drive.usercontent.google.com", StringComparison.OrdinalIgnoreCase))
            {
                // Google share links are keyed by unrelated file IDs, so a sibling
                // URL cannot be inferred. channel.json must provide one explicitly.
                return null;
            }

            var builder = new UriBuilder(uri)
            {
                Path = ReplaceLastPathSegment(uri.AbsolutePath, FileName),
            };
            return builder.Uri.AbsoluteUri;
        }

        try
        {
            var fullPath = Path.GetFullPath(source);
            return Path.Combine(Path.GetDirectoryName(fullPath)!, FileName);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static string ResolveYandexSibling(Uri source)
    {
        var parts = source.Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .Select(pair => new KeyValuePair<string, string>(
                Uri.UnescapeDataString(pair[0]),
                pair.Length == 2 ? Uri.UnescapeDataString(pair[1]) : string.Empty))
            .ToList();
        var pathIndex = parts.FindIndex(pair => pair.Key.Equals("path", StringComparison.OrdinalIgnoreCase));
        if (pathIndex < 0)
        {
            var builderWithoutPath = new UriBuilder(source)
            {
                Path = ReplaceLastPathSegment(source.AbsolutePath, FileName),
            };
            return builderWithoutPath.Uri.AbsoluteUri;
        }

        var path = ReplaceLastPathSegment(parts[pathIndex].Value.Replace('\\', '/'), FileName);
        parts[pathIndex] = new KeyValuePair<string, string>(parts[pathIndex].Key, path);
        var builder = new UriBuilder(source)
        {
            Query = string.Join('&', parts.Select(pair =>
                $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}")),
        };
        return builder.Uri.AbsoluteUri;
    }

    private static string ReplaceLastPathSegment(string path, string replacement)
    {
        var slash = path.LastIndexOf('/');
        return slash < 0 ? replacement : path[..(slash + 1)] + replacement;
    }
}
