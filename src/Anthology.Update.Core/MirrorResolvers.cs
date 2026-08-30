using System.Net.Http.Json;
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
        string.Equals(mirror.Provider, "yandex-disk", StringComparison.OrdinalIgnoreCase);

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
