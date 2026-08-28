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
