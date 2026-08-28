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

public sealed class YandexDiskMirrorResolver(HttpClient httpClient) : IMirrorResolver
{
    public bool CanResolve(MirrorManifest mirror) =>
        string.Equals(mirror.Provider, "yandex-disk", StringComparison.OrdinalIgnoreCase);

    public async ValueTask<Uri> ResolveAsync(MirrorManifest mirror, CancellationToken cancellationToken)
    {
        var endpoint = new Uri(
            "https://cloud-api.yandex.net/v1/disk/public/resources/download?public_key="
            + Uri.EscapeDataString(mirror.Url));
        var response = await httpClient.GetFromJsonAsync<YandexDownloadResponse>(endpoint, cancellationToken)
            ?? throw new InvalidDataException("Yandex Disk returned an empty download response.");
        return new Uri(response.Href, UriKind.Absolute);
    }

    private sealed record YandexDownloadResponse(string Href);
}
