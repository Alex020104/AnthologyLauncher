using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Globalization;
using Anthology.Contracts;
using Anthology.Releaser.Core;
using Anthology.Update.Core;

namespace Anthology.Update.Core.Tests;

public sealed class ReleaseHistoryTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "anthology-history-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void SignedHistoryVerifiesAndTamperingFails()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var payload = CreateCatalog(CreateEntry("2.1.1", At("2026-09-01T12:00:00Z")));
        var signed = ManifestSecurity.Sign(payload, key, "history-test");

        Assert.True(ManifestSecurity.Verify(signed, key));
        Assert.False(ManifestSecurity.Verify(
            signed with
            {
                Payload = signed.Payload with
                {
                    Entries = [signed.Payload.Entries[0] with { Version = "2.1.999" }],
                },
            },
            key));
    }

    [Fact]
    public void ValidatorRejectsWrongChannelAndDuplicateVersions()
    {
        var entry = CreateEntry("2.1.1", At("2026-09-01T12:00:00Z"));
        var duplicate = CreateCatalog(entry, entry with { PublishedAt = entry.PublishedAt.AddMinutes(-1) });

        Assert.Throws<InvalidDataException>(() =>
            ReleaseHistoryValidator.ValidateAndThrow(duplicate, "next"));
        Assert.Throws<InvalidDataException>(() =>
            ReleaseHistoryValidator.ValidateAndThrow(CreateCatalog(entry), "stable"));
    }

    [Fact]
    public void SourceResolverDerivesLocalGithubAndYandexSiblingsButNotGoogleIds()
    {
        var localManifest = Path.Combine(_root, "channel", "manifest.json");
        Assert.Equal(
            Path.Combine(_root, "channel", "history.json"),
            ReleaseHistorySourceResolver.Resolve(null, localManifest));
        Assert.Equal(
            "https://raw.githubusercontent.com/owner/repo/branch/history.json?channel=next",
            ReleaseHistorySourceResolver.Resolve(
                null,
                "https://raw.githubusercontent.com/owner/repo/branch/manifest.json?channel=next"));

        var yandex = ReleaseHistorySourceResolver.Resolve(
            null,
            "https://disk.yandex.ru/d/public-key?path=/AnthologyUpdateChannel/manifest.json");
        Assert.NotNull(yandex);
        Assert.Contains(
            "path=/AnthologyUpdateChannel/history.json",
            Uri.UnescapeDataString(new Uri(yandex).Query),
            StringComparison.Ordinal);
        Assert.Null(ReleaseHistorySourceResolver.Resolve(
            null,
            "https://drive.google.com/file/d/manifest-file-id/view"));
        Assert.Equal(
            "https://drive.google.com/file/d/history-file-id/view",
            ReleaseHistorySourceResolver.Resolve(
                "https://drive.google.com/file/d/history-file-id/view",
                "https://drive.google.com/file/d/manifest-file-id/view"));
    }

    [Fact]
    public async Task ClientAcceptsOnlyMatchingSignedChannelAndKey()
    {
        Directory.CreateDirectory(_root);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var otherKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKeyPath = Path.Combine(_root, "public.pem");
        await File.WriteAllTextAsync(publicKeyPath, key.ExportSubjectPublicKeyInfoPem());
        var signed = ManifestSecurity.Sign(
            CreateCatalog(CreateEntry("2.1.2", At("2026-09-02T12:00:00Z"))),
            key,
            "history-test");
        var handler = new StaticResponseHandler(JsonSerializer.SerializeToUtf8Bytes(signed, ManifestJson.Options));
        var client = new ReleaseHistoryClient(new HttpClient(handler));

        var loaded = await client.LoadVerifiedAsync(
            "https://cdn.example/history.json",
            publicKeyPath,
            "next",
            "history-test");
        Assert.Equal("2.1.2", Assert.Single(loaded.Payload.Entries).Version);

        await Assert.ThrowsAsync<InvalidDataException>(() => client.LoadVerifiedAsync(
            "https://cdn.example/history.json",
            publicKeyPath,
            "stable",
            "history-test"));
        await Assert.ThrowsAsync<CryptographicException>(() => client.LoadVerifiedAsync(
            "https://cdn.example/history.json",
            publicKeyPath,
            "next",
            "another-key"));

        var forged = ManifestSecurity.Sign(signed.Payload, otherKey, "history-test");
        handler.Content = JsonSerializer.SerializeToUtf8Bytes(forged, ManifestJson.Options);
        await Assert.ThrowsAsync<CryptographicException>(() => client.LoadVerifiedAsync(
            "https://cdn.example/history.json",
            publicKeyPath,
            "next",
            "history-test"));
    }

    [Fact]
    public async Task BuilderBootstrapsOnlyTrustedVersionManifestsAndWritesCurrentVersion()
    {
        Directory.CreateDirectory(_root);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var otherKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        const string keyId = "history-test";
        var first = CreateManifest("2.1.1", At("2026-09-01T12:00:00Z"), "Первый");
        var legacy = CreateManifest("2.1.0", At("2026-08-31T18:00:00Z"), "Legacy root");
        var forged = CreateManifest("2.1.2", At("2026-09-02T12:00:00Z"), "Поддельный");
        var current = CreateManifest("2.1.3", At("2026-09-03T12:00:00Z"), "Текущий");
        await WriteManifestAsync(Path.Combine(_root, first.Version), ManifestSecurity.Sign(first, key, keyId));
        await WriteManifestAsync(Path.Combine(_root, forged.Version), ManifestSecurity.Sign(forged, otherKey, keyId));
        var legacyRoot = Path.Combine(_root, "legacy-publication-root");
        await WriteManifestAsync(Path.Combine(legacyRoot, legacy.Version), ManifestSecurity.Sign(legacy, key, keyId));

        var oldEntry = CreateEntry("2.0.9", At("2026-08-31T12:00:00Z"));
        var oldHistory = ManifestSecurity.Sign(CreateCatalog(oldEntry), key, keyId);
        await File.WriteAllTextAsync(
            Path.Combine(_root, "history.json"),
            JsonSerializer.Serialize(oldHistory, ManifestJson.Options));

        var signed = await ReleaseHistoryCatalogBuilder.BuildAsync(
            [_root, legacyRoot],
            ManifestSecurity.Sign(current, key, keyId),
            key,
            keyId);
        Assert.True(ManifestSecurity.Verify(signed, key));
        Assert.Equal(["2.1.3", "2.1.1", "2.1.0", "2.0.9"], signed.Payload.Entries.Select(entry => entry.Version));
        Assert.DoesNotContain(signed.Payload.Entries, entry => entry.Version == "2.1.2");

        var versionHistory = await ReleaseHistoryCatalogBuilder.BuildAndWriteVersionAsync(
            _root,
            ManifestSecurity.Sign(current, key, keyId),
            key,
            keyId);
        Assert.Equal(Path.Combine(_root, "2.1.3", "history.json"), versionHistory);
        Assert.True(File.Exists(versionHistory));
    }

    [Fact]
    public async Task ReleasePublicationWritesVerifiedVersionedAndStableHistory()
    {
        Directory.CreateDirectory(_root);
        const string version = "2.1.4";
        const string keyId = "history-test";
        var versionRoot = Path.Combine(_root, version);
        Directory.CreateDirectory(versionRoot);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var privateKeyPath = Path.Combine(_root, "private.pem");
        var publicKeyPath = Path.Combine(_root, "public.pem");
        await File.WriteAllTextAsync(privateKeyPath, key.ExportPkcs8PrivateKeyPem());
        await File.WriteAllTextAsync(publicKeyPath, key.ExportSubjectPublicKeyInfoPem());
        var manifest = ManifestSecurity.Sign(
            CreateManifest(version, At("2026-09-04T12:00:00Z"), "Published"),
            key,
            keyId);
        var manifestPath = Path.Combine(versionRoot, "manifest.json");
        await UnifiedReleaseBuilder.WriteJsonAtomicallyAsync(manifestPath, manifest);
        var workspace = new ReleaserWorkspace
        {
            Version = version,
            Channel = "next",
        };
        var machine = new ReleaserMachineSettings
        {
            OutputRoot = _root,
            PrivateKeyPath = privateKeyPath,
            PublicKeyPath = publicKeyPath,
            KeyId = keyId,
        };
        var release = new UnifiedReleaseResult(
            version,
            manifestPath,
            [],
            0,
            0,
            0,
            ["manifest.json"]);

        _ = await ReleasePublicationService.PublishReleaseAsync(release, workspace, machine);

        var versionedPath = Path.Combine(versionRoot, ReleaseHistoryCatalogBuilder.FileName);
        var stablePath = Path.Combine(_root, ReleaseHistoryCatalogBuilder.FileName);
        Assert.True(File.Exists(versionedPath));
        Assert.True(File.Exists(stablePath));
        var published = JsonSerializer.Deserialize<SignedReleaseHistory>(
            await File.ReadAllTextAsync(stablePath),
            ManifestJson.Options);
        Assert.NotNull(published);
        Assert.True(ManifestSecurity.Verify(published, key));
        Assert.Equal(version, Assert.Single(published.Payload.Entries).Version);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static ReleaseHistoryCatalog CreateCatalog(params ReleaseHistoryEntry[] entries) => new(
        ReleaseHistoryValidator.CurrentSchemaVersion,
        "next",
        entries.Max(entry => entry.PublishedAt),
        entries);

    private static DateTimeOffset At(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);

    private static ReleaseHistoryEntry CreateEntry(string version, DateTimeOffset publishedAt) => new(
        version,
        publishedAt,
        new ReleaseChangelog($"Версия {version}", "Описание", string.Empty, string.Empty));

    private static UpdateManifest CreateManifest(
        string version,
        DateTimeOffset publishedAt,
        string changelogTitle) => new(
        4,
        "next",
        version,
        publishedAt,
        null,
        [],
        new ContentCatalog(
            1,
            version,
            publishedAt,
            [],
            Changelog: new ReleaseChangelog(changelogTitle, "Описание", string.Empty, string.Empty)));

    private static async Task WriteManifestAsync(string directory, SignedUpdateManifest manifest)
    {
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(
            Path.Combine(directory, "manifest.json"),
            JsonSerializer.Serialize(manifest, ManifestJson.Options));
    }

    private sealed class StaticResponseHandler(byte[] content) : HttpMessageHandler
    {
        public byte[] Content { get; set; } = content;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(Content),
            RequestMessage = request,
        });
    }
}
