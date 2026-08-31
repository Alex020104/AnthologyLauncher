using System.Net;
using System.Security.Cryptography;
using Anthology.Contracts;

namespace Anthology.Update.Core.Tests;

public sealed class ArtifactDownloaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"anthology-downloader-{Guid.NewGuid():N}");

    [Fact]
    public async Task FailedMirrorFallsBackAndVerifiedArtifactIsCommitted()
    {
        var payload = "verified Anthology payload"u8.ToArray();
        using var client = new HttpClient(new MirrorHandler(payload));
        var package = new PackageManifest(
            "test-package",
            "Test package",
            "1.0.0",
            PackageKind.Mod,
            "mods",
            "zip",
            payload.Length,
            Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant(),
            [
                new MirrorManifest("direct", "https://failed.invalid/package.zip", 10),
                new MirrorManifest("direct", "https://working.invalid/package.zip", 20),
            ],
            ["gamedata/test.txt"]);
        var destination = Path.Combine(_root, "package.zip");

        var result = await new ArtifactDownloader(client).DownloadAsync(package, destination);

        Assert.Equal("direct", result.Provider);
        Assert.Equal(payload, await File.ReadAllBytesAsync(destination));
        Assert.False(File.Exists(destination + ".partial"));
    }

    [Fact]
    public async Task LocalFileMirrorCopiesVerifiedArtifactWithoutHttp()
    {
        var payload = "local developer artifact"u8.ToArray();
        Directory.CreateDirectory(_root);
        var source = Path.Combine(_root, "source.zip");
        var destination = Path.Combine(_root, "downloaded.zip");
        await File.WriteAllBytesAsync(source, payload);
        var package = new PackageManifest(
            "local-package",
            "Local package",
            "1.0.0",
            PackageKind.Mod,
            "mods",
            "zip",
            payload.Length,
            Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant(),
            [new MirrorManifest("local-file", new Uri(source).AbsoluteUri, 10)],
            ["mods/local/file.txt"]);
        using var client = new HttpClient(new RejectHttpHandler());

        var result = await new ArtifactDownloader(client).DownloadAsync(package, destination);

        Assert.Equal("local-file", result.Provider);
        Assert.Equal(payload, await File.ReadAllBytesAsync(destination));
    }

    [Fact]
    public async Task BundleFileMirrorStaysInsideInstallMediaRoot()
    {
        var payload = "bundled install payload"u8.ToArray();
        var mediaRoot = Path.Combine(_root, "InstallMedia");
        var packagesRoot = Path.Combine(mediaRoot, "packages");
        Directory.CreateDirectory(packagesRoot);
        await File.WriteAllBytesAsync(Path.Combine(packagesRoot, "base.zip"), payload);
        var destination = Path.Combine(_root, "bundle-download.zip");
        var package = new PackageManifest(
            "bundle-package",
            "Bundle package",
            "1.0.0",
            PackageKind.Game,
            "game",
            "zip",
            payload.Length,
            Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant(),
            [new MirrorManifest("bundle-file", "bundle:///packages/base.zip", 10)],
            ["bin/placeholder.txt"]);
        using var client = new HttpClient(new RejectHttpHandler());
        var downloader = new ArtifactDownloader(client, [new BundleFileMirrorResolver(mediaRoot)]);

        var result = await downloader.DownloadAsync(package, destination);

        Assert.Equal("bundle-file", result.Provider);
        Assert.Equal(payload, await File.ReadAllBytesAsync(destination));
        await Assert.ThrowsAsync<AggregateException>(() => new ArtifactDownloader(
            client,
            [new BundleFileMirrorResolver(mediaRoot)]).DownloadAsync(
            package with { Mirrors = [new MirrorManifest("bundle-file", "bundle:///../outside.zip")] },
            Path.Combine(_root, "unsafe-bundle.zip")));
    }

    [Fact]
    public async Task PreferredProviderIsTriedBeforeManifestPriority()
    {
        var payload = "preferred source"u8.ToArray();
        var handler = new PreferredHandler(payload);
        using var client = new HttpClient(handler);
        var package = new PackageManifest(
            "preferred-package",
            "Preferred package",
            "2.1.131",
            PackageKind.Game,
            "game",
            "zip",
            payload.Length,
            Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant(),
            [
                new MirrorManifest("github", "https://github.invalid/package.zip", 10),
                new MirrorManifest("http", "https://http.invalid/package.zip", 100),
            ],
            ["file.txt"]);

        var result = await new ArtifactDownloader(client).DownloadAsync(
            package,
            Path.Combine(_root, "preferred.zip"),
            preferredProvider: "http");

        Assert.Equal("http", result.Provider);
        Assert.Equal("http.invalid", handler.FirstHost);
    }

    [Fact]
    public async Task GoogleDriveSharePageIsConvertedToDirectDownload()
    {
        const string fileId = "15ZukQ_Byhw_0B2Ew69_BriGLfxtF_vrj";
        var resolver = new WebShareMirrorResolver();
        var mirror = new MirrorManifest(
            "http",
            $"https://drive.google.com/file/d/{fileId}/view?usp=sharing");

        Assert.True(resolver.CanResolve(mirror));
        var resolved = await resolver.ResolveAsync(mirror, CancellationToken.None);

        Assert.Equal("drive.usercontent.google.com", resolved.Host);
        Assert.Contains($"id={fileId}", resolved.Query, StringComparison.Ordinal);
        Assert.Contains("export=download", resolved.Query, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://www.dropbox.com/scl/fi/test/archive.7z?rlkey=key", "www.dropbox.com", "dl=1")]
    [InlineData("https://github.com/owner/repo/blob/main/archive.7z", "raw.githubusercontent.com", "/owner/repo/main/archive.7z")]
    [InlineData("https://gitlab.com/owner/repo/-/blob/main/archive.7z", "gitlab.com", "/owner/repo/-/raw/main/archive.7z")]
    [InlineData("https://huggingface.co/owner/repo/blob/main/archive.7z", "huggingface.co", "/owner/repo/resolve/main/archive.7z")]
    public void CommonShareLinksAreConvertedToDownloadUrls(
        string source,
        string expectedHost,
        string expectedPart)
    {
        var resolved = WebShareMirrorResolver.ResolveShareUrl(source);

        Assert.Equal(expectedHost, resolved.Host);
        Assert.Contains(expectedPart, resolved.AbsoluteUri, StringComparison.Ordinal);
    }

    [Fact]
    public void YandexPublicLinkIsRecognizedWithoutProviderPrefix()
    {
        using var client = new HttpClient(new RejectHttpHandler());
        var resolver = new YandexDiskMirrorResolver(client);

        Assert.True(resolver.CanResolve(new MirrorManifest(
            "http",
            "https://disk.yandex.ru/d/example?path=/Anthology/archive.7z")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    private sealed class MirrorHandler(byte[] payload) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri?.Host == "failed.invalid")
            {
                throw new HttpRequestException("Simulated mirror outage.");
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload),
                RequestMessage = request,
            });
        }
    }

    private sealed class RejectHttpHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("HTTP should not be used for a local-file mirror.");
    }

    private sealed class PreferredHandler(byte[] payload) : HttpMessageHandler
    {
        public string? FirstHost { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            FirstHost ??= request.RequestUri?.Host;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload),
                RequestMessage = request,
            });
        }
    }
}
