using System.Net.Http.Json;
using System.Text;
using Anthology.Contracts;

namespace Anthology.Update.Core;

public interface IMirrorResolver
{
    bool CanResolve(MirrorManifest mirror);

    ValueTask<Uri> ResolveAsync(MirrorManifest mirror, CancellationToken cancellationToken);
}

public sealed class DirectMirrorResolver : IMirrorResolver
{
    public bool CanResolve(MirrorManifest mirror) =>
        !string.Equals(mirror.Provider, "yandex-disk", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(mirror.Provider, "local-file", StringComparison.OrdinalIgnoreCase);

    public ValueTask<Uri> ResolveAsync(MirrorManifest mirror, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new Uri(mirror.Url, UriKind.Absolute));
    }
}

public sealed class WebShareMirrorResolver : IMirrorResolver
{
    private static readonly string[] ProviderNames =
    [
        "google-drive",
        "dropbox",
        "onedrive",
        "github",
        "gitlab",
        "huggingface",
    ];

    public bool CanResolve(MirrorManifest mirror) =>
        ProviderNames.Contains(mirror.Provider, StringComparer.OrdinalIgnoreCase)
        || IsKnownShareUrl(mirror.Url);

    public ValueTask<Uri> ResolveAsync(MirrorManifest mirror, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(ResolveShareUrl(mirror.Url));
    }

    public static bool IsKnownShareUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var host = uri.Host.ToLowerInvariant();
        return host is "drive.google.com" or "docs.google.com"
            || host.EndsWith(".dropbox.com", StringComparison.Ordinal)
            || host is "dropbox.com" or "1drv.ms" or "onedrive.live.com"
            || host is "github.com" or "www.github.com" or "gitlab.com" or "www.gitlab.com"
            || host is "huggingface.co" or "www.huggingface.co";
    }

    public static Uri ResolveShareUrl(string value)
    {
        var uri = new Uri(value, UriKind.Absolute);
        var host = uri.Host.ToLowerInvariant();
        if (host is "drive.google.com" or "docs.google.com")
        {
            var fileId = GetGoogleDriveFileId(uri);
            if (!string.IsNullOrWhiteSpace(fileId))
            {
                return new Uri(
                    "https://drive.usercontent.google.com/download?id="
                    + Uri.EscapeDataString(fileId)
                    + "&export=download&confirm=t");
            }
        }

        if (host is "dropbox.com" || host.EndsWith(".dropbox.com", StringComparison.Ordinal))
        {
            var builder = new UriBuilder(uri)
            {
                Query = SetQueryParameter(uri.Query, "dl", "1"),
            };
            return builder.Uri;
        }

        if (host is "1drv.ms" or "onedrive.live.com")
        {
            var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(uri.AbsoluteUri))
                .TrimEnd('=')
                .Replace('/', '_')
                .Replace('+', '-');
            return new Uri($"https://api.onedrive.com/v1.0/shares/u!{encoded}/root/content");
        }

        if (host is "github.com" or "www.github.com")
        {
            var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length >= 5 && segments[2].Equals("blob", StringComparison.OrdinalIgnoreCase))
            {
                return new Uri(
                    $"https://raw.githubusercontent.com/{segments[0]}/{segments[1]}/{string.Join('/', segments.Skip(3))}");
            }
        }

        if ((host is "gitlab.com" or "www.gitlab.com")
            && uri.AbsolutePath.Contains("/-/blob/", StringComparison.OrdinalIgnoreCase))
        {
            return new UriBuilder(uri)
            {
                Path = uri.AbsolutePath.Replace("/-/blob/", "/-/raw/", StringComparison.OrdinalIgnoreCase),
            }.Uri;
        }

        if ((host is "huggingface.co" or "www.huggingface.co")
            && uri.AbsolutePath.Contains("/blob/", StringComparison.OrdinalIgnoreCase))
        {
            return new UriBuilder(uri)
            {
                Path = uri.AbsolutePath.Replace("/blob/", "/resolve/", StringComparison.OrdinalIgnoreCase),
            }.Uri;
        }

        return uri;
    }

    private static string? GetGoogleDriveFileId(Uri uri)
    {
        var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length >= 3
            && segments[0].Equals("file", StringComparison.OrdinalIgnoreCase)
            && segments[1].Equals("d", StringComparison.OrdinalIgnoreCase))
        {
            return Uri.UnescapeDataString(segments[2]);
        }

        return GetQueryParameter(uri.Query, "id");
    }

    private static string? GetQueryParameter(string query, string name) =>
        query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .Where(pair => pair.Length == 2 && pair[0].Equals(name, StringComparison.OrdinalIgnoreCase))
            .Select(pair => Uri.UnescapeDataString(pair[1]))
            .FirstOrDefault();

    private static string SetQueryParameter(string query, string name, string value)
    {
        var parts = query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(part => !part.Split('=', 2)[0].Equals(name, StringComparison.OrdinalIgnoreCase))
            .ToList();
        parts.Add($"{Uri.EscapeDataString(name)}={Uri.EscapeDataString(value)}");
        return string.Join('&', parts);
    }
}

public sealed class LocalFileMirrorResolver : IMirrorResolver
{
    public bool CanResolve(MirrorManifest mirror) =>
        string.Equals(mirror.Provider, "local-file", StringComparison.OrdinalIgnoreCase);

    public ValueTask<Uri> ResolveAsync(MirrorManifest mirror, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var uri = new Uri(mirror.Url, UriKind.Absolute);
        if (!uri.IsFile)
        {
            throw new InvalidDataException("local-file mirror must use a file URI.");
        }

        return ValueTask.FromResult(uri);
    }
}

public sealed class BundleFileMirrorResolver
    : IMirrorResolver
{
    private readonly string _bundleRoot;

    public BundleFileMirrorResolver(string bundleRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bundleRoot);
        _bundleRoot = Path.GetFullPath(bundleRoot);
    }

    public bool CanResolve(MirrorManifest mirror) =>
        string.Equals(mirror.Provider, "bundle-file", StringComparison.OrdinalIgnoreCase);

    public ValueTask<Uri> ResolveAsync(MirrorManifest mirror, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var uri = new Uri(mirror.Url, UriKind.Absolute);
        if (!string.Equals(uri.Scheme, "bundle", StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new InvalidDataException("bundle-file mirror must use a clean bundle:/// relative URI.");
        }

        var relative = Uri.UnescapeDataString(uri.AbsolutePath).TrimStart('/');
        if (!string.IsNullOrWhiteSpace(uri.Host))
        {
            relative = $"{uri.Host}/{relative}";
        }

        var path = PathSafety.ResolveUnderRoot(_bundleRoot, relative);
        return ValueTask.FromResult(new Uri(path));
    }
}

public sealed class YandexDiskMirrorResolver(HttpClient httpClient) : IMirrorResolver
{
    public bool CanResolve(MirrorManifest mirror) =>
        string.Equals(mirror.Provider, "yandex-disk", StringComparison.OrdinalIgnoreCase)
        || IsYandexDiskUrl(mirror.Url);

    public static bool IsYandexDiskUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && (uri.Host.Equals("disk.yandex.ru", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("yadi.sk", StringComparison.OrdinalIgnoreCase));

    public async ValueTask<Uri> ResolveAsync(MirrorManifest mirror, CancellationToken cancellationToken)
        => await ResolvePublicDownloadAsync(httpClient, mirror.Url, cancellationToken);

    public static async Task<Uri> ResolvePublicDownloadAsync(
        HttpClient client,
        string publicUrl,
        CancellationToken cancellationToken)
    {
        var publicKey = publicUrl;
        string? publicPath = null;
        if (Uri.TryCreate(publicUrl, UriKind.Absolute, out var sourceUri))
        {
            publicKey = sourceUri.GetLeftPart(UriPartial.Path);
            foreach (var part in sourceUri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var pair = part.Split('=', 2);
                if (pair.Length == 2 && string.Equals(pair[0], "path", StringComparison.OrdinalIgnoreCase))
                {
                    publicPath = Uri.UnescapeDataString(pair[1]);
                    break;
                }
            }
        }

        var pathParameter = string.IsNullOrWhiteSpace(publicPath)
            ? string.Empty
            : "&path=" + Uri.EscapeDataString(publicPath);
        var endpoint = new Uri(
            "https://cloud-api.yandex.net/v1/disk/public/resources/download?public_key="
            + Uri.EscapeDataString(publicKey)
            + pathParameter);
        var response = await client.GetFromJsonAsync<YandexDownloadResponse>(endpoint, cancellationToken)
            ?? throw new InvalidDataException("Yandex Disk returned an empty download response.");
        return new Uri(response.Href, UriKind.Absolute);
    }

    private sealed record YandexDownloadResponse(string Href);
}
