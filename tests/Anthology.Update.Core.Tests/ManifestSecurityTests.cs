using System.Security.Cryptography;
using Anthology.Contracts;

namespace Anthology.Update.Core.Tests;

public sealed class ManifestSecurityTests
{
    [Fact]
    public void SignedManifestVerifiesAndTamperingFails()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var signed = ManifestSecurity.Sign(CreateManifest(), key, "test-key");

        Assert.True(ManifestSecurity.Verify(signed, key));

        var tampered = signed with
        {
            Payload = signed.Payload with { Version = "tampered" },
        };
        Assert.False(ManifestSecurity.Verify(tampered, key));
    }

    [Fact]
    public void ValidatorRejectsPathTraversal()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var manifest = CreateManifest();
        var package = manifest.Packages[0] with { Files = ["../outside.bin"] };
        var signed = ManifestSecurity.Sign(manifest with { Packages = [package] }, key, "test-key");

        var errors = ManifestValidator.Validate(signed);

        Assert.Contains(errors, error => error.Contains("unsafe path", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ValidatorRejectsDirectoryDeletionOutsideInstallRoot()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var manifest = CreateManifest() with { SchemaVersion = 4 };
        var package = manifest.Packages[0] with { DeletedDirectories = ["../other-game"] };
        var signed = ManifestSecurity.Sign(manifest with { Packages = [package] }, key, "test-key");

        var errors = ManifestValidator.Validate(signed);

        Assert.Contains(errors, error => error.Contains("unsafe directory deletion path", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ValidatorAcceptsArbitraryWebSocialAndDownloadLinks()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var content = new ContentCatalog(
            1,
            "2.1.140",
            DateTimeOffset.UtcNow,
            [
                new ContentDocument(
                    "custom-links",
                    ContentKind.Mod,
                    "community",
                    "Проект",
                    "Описание",
                    "Текст",
                    ["http://media.example.test/cover.png"],
                    [new ContentVideo("Видео", "http://video.example.test/watch/42")],
                    new ContentDownload(
                        "archive.7z",
                        42,
                        new string('b', 64),
                        [new MirrorManifest("http", "https://drive.google.com/file/d/example/view")]),
                    AuthorLinks:
                    [
                        new SocialLink("discord", "Discord", "Канал", "https://discordapp.com/channels/1/2"),
                        new SocialLink("telegram", "Статья", "Telegra.ph", "https://telegra.ph/example"),
                        new SocialLink("boosty", "Boosty", "Автор", "http://creator.example.test/profile"),
                    ]),
            ],
            [new SocialLink("website", "Сайт", "Официальная страница", "https://example.test")]);
        var manifest = CreateManifest() with { SchemaVersion = 4, Content = content };
        var signed = ManifestSecurity.Sign(manifest, key, "test-key");

        var errors = ManifestValidator.Validate(signed);

        Assert.Empty(errors);
    }

    [Fact]
    public void ValidatorStillRejectsNonWebSocialLinks()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var content = new ContentCatalog(
            1,
            "2.1.140",
            DateTimeOffset.UtcNow,
            [],
            [new SocialLink("unsafe", "Опасная ссылка", string.Empty, "javascript:alert(1)")]);
        var signed = ManifestSecurity.Sign(CreateManifest() with { SchemaVersion = 4, Content = content }, key, "test-key");

        var errors = ManifestValidator.Validate(signed);

        Assert.Contains(errors, error => error.Contains("unsafe URL", StringComparison.OrdinalIgnoreCase));
    }

    private static UpdateManifest CreateManifest() => new(
        1,
        "next",
        "2026.08.28.1",
        new DateTimeOffset(2026, 8, 28, 0, 0, 0, TimeSpan.Zero),
        null,
        [
            new PackageManifest(
                "anthology-test",
                "Anthology Test",
                "2026.08.28.1",
                PackageKind.Mod,
                "modpack",
                "zip",
                12,
                new string('a', 64),
                [new MirrorManifest("github", "https://example.com/package.zip")],
                ["mods/test/file.txt"]),
        ]);
}
