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
}
