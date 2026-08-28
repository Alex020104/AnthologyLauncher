using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Anthology.Contracts;

namespace Anthology.Update.Core.Tests;

public sealed class UpdateCoordinatorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"anthology-update-flow-{Guid.NewGuid():N}");

    [Fact]
    public async Task SignedPackageIsDownloadedExtractedInstalledAndRecorded()
    {
        const string relativePath = "gamedata/configs/anthology-test.ltx";
        var archiveBytes = CreateArchive((relativePath, "working = true"));
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var signed = CreateSignedManifest(key, archiveBytes, [relativePath]);
        var manifestPath = await WriteTrustFilesAsync(key, signed);
        var gameRoot = Path.Combine(_root, "game");
        var stateRoot = Path.Combine(_root, "state");
        Directory.CreateDirectory(gameRoot);
        using var client = new HttpClient(new ArtifactHandler(archiveBytes));
        var coordinator = new UpdateCoordinator(client);

        var check = await coordinator.CheckAsync(manifestPath, GetPublicKeyPath(), "next", stateRoot);
        Assert.True(check.HasUpdates);

        var result = await coordinator.ApplyAsync(
            check,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["game"] = gameRoot },
            stateRoot);

        Assert.Equal(1, result.InstalledPackages);
        Assert.Equal("working = true", await File.ReadAllTextAsync(Path.Combine(gameRoot, relativePath)));
        var secondCheck = await coordinator.CheckAsync(manifestPath, GetPublicKeyPath(), "next", stateRoot);
        Assert.False(secondCheck.HasUpdates);
    }

    [Fact]
    public async Task ManifestSignedByAnotherKeyIsRejected()
    {
        var archiveBytes = CreateArchive(("gamedata/test.txt", "test"));
        using var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var trustedKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var signed = CreateSignedManifest(signingKey, archiveBytes, ["gamedata/test.txt"]);
        var manifestPath = await WriteTrustFilesAsync(trustedKey, signed);
        using var client = new HttpClient(new ArtifactHandler(archiveBytes));

        await Assert.ThrowsAsync<CryptographicException>(() =>
            new UpdateCoordinator(client).CheckAsync(
                manifestPath,
                GetPublicKeyPath(),
                "next",
                Path.Combine(_root, "state")));
    }

    [Fact]
    public async Task ArchiveWithUndeclaredFileIsRejectedBeforeInstallation()
    {
        var archiveBytes = CreateArchive(
            ("gamedata/declared.txt", "declared"),
            ("gamedata/undeclared.txt", "undeclared"));
        var archivePath = Path.Combine(_root, "unsafe.zip");
        Directory.CreateDirectory(_root);
        await File.WriteAllBytesAsync(archivePath, archiveBytes);
        var package = CreatePackage(archiveBytes, ["gamedata/declared.txt"]);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            SafeZipExtractor.ExtractAsync(archivePath, Path.Combine(_root, "staging"), package));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    private async Task<string> WriteTrustFilesAsync(ECDsa trustedKey, SignedUpdateManifest signed)
    {
        Directory.CreateDirectory(_root);
        var manifestPath = Path.Combine(_root, "manifest.json");
        await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(signed, ManifestJson.Options));
        await File.WriteAllTextAsync(GetPublicKeyPath(), trustedKey.ExportSubjectPublicKeyInfoPem());
        return manifestPath;
    }

    private string GetPublicKeyPath() => Path.Combine(_root, "trusted.pub.pem");

    private static SignedUpdateManifest CreateSignedManifest(
        ECDsa key,
        byte[] archiveBytes,
        IReadOnlyList<string> files)
    {
        var package = CreatePackage(archiveBytes, files);
        var manifest = new UpdateManifest(
            1,
            "next",
            "1.0.0",
            new DateTimeOffset(2026, 8, 28, 0, 0, 0, TimeSpan.Zero),
            null,
            [package]);
        return ManifestSecurity.Sign(manifest, key, "test-key-01");
    }

    private static PackageManifest CreatePackage(byte[] archiveBytes, IReadOnlyList<string> files) => new(
        "anthology-core",
        "Anthology Core",
        "1.0.0",
        PackageKind.Game,
        "game",
        "zip",
        archiveBytes.Length,
        Convert.ToHexString(SHA256.HashData(archiveBytes)).ToLowerInvariant(),
        [new MirrorManifest("direct", "https://updates.invalid/anthology-core.zip", 10)],
        files);

    private static byte[] CreateArchive(params (string Path, string Content)[] files)
    {
        using var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var file in files)
            {
                var entry = archive.CreateEntry(file.Path);
                using var writer = new StreamWriter(entry.Open());
                writer.Write(file.Content);
            }
        }

        return memory.ToArray();
    }

    private sealed class ArtifactHandler(byte[] artifact) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(artifact),
                RequestMessage = request,
            });
    }
}
