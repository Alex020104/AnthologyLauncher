using System.Security.Cryptography;
using System.Text;

namespace Anthology.Update.Core.Tests;

public sealed class LegacyMo2LayoutMigratorTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"anthology-legacy-layout-{Guid.NewGuid():N}");

    [Fact]
    public async Task ExactLegacyDuplicateIsMovedToRecoverableQuarantineAndRunIsIdempotent()
    {
        var modpackRoot = Path.Combine(_root, "mo2");
        var stateRoot = Path.Combine(_root, "launcher-data", "Updater");
        var payload = Encoding.UTF8.GetBytes("immutable release payload");
        var definition = CreateDefinition(
            "Legacy Addon/gamedata/configs/exact.ltx",
            payload);
        var relativePath = definition.Files.Single().RelativePath;
        var source = await WriteFileAsync(modpackRoot, relativePath, payload);
        var corrected = await WriteFileAsync(modpackRoot, "mods/" + relativePath, payload);
        var unrelatedEmptyDirectory = Path.Combine(modpackRoot, "Other Addon", "empty");
        Directory.CreateDirectory(unrelatedEmptyDirectory);

        var result = await LegacyMo2LayoutMigrator.MigrateAsync(
            modpackRoot,
            stateRoot,
            definition);

        Assert.Equal([relativePath], result.QuarantinedFiles);
        Assert.Empty(result.ModifiedLegacyFiles);
        Assert.Empty(result.UnverifiedCorrectedFiles);
        Assert.Empty(result.Errors);
        Assert.False(File.Exists(source));
        Assert.True(File.Exists(corrected));
        Assert.False(Directory.Exists(Path.Combine(modpackRoot, "Legacy Addon")));
        Assert.True(Directory.Exists(unrelatedEmptyDirectory));
        Assert.NotNull(result.QuarantineRoot);

        var quarantined = PathSafety.ResolveUnderRoot(
            Path.Combine(result.QuarantineRoot!, "files"),
            relativePath);
        Assert.Equal(payload, await File.ReadAllBytesAsync(quarantined));
        var receipt = await File.ReadAllTextAsync(Path.Combine(result.QuarantineRoot!, "migration.json"));
        Assert.Contains(definition.MigrationId, receipt, StringComparison.Ordinal);
        Assert.Contains(definition.LegacyArchiveSha256, receipt, StringComparison.Ordinal);
        Assert.Contains(relativePath, receipt, StringComparison.Ordinal);

        var second = await LegacyMo2LayoutMigrator.MigrateAsync(
            modpackRoot,
            stateRoot,
            definition);

        Assert.Null(second.QuarantineRoot);
        Assert.Empty(second.QuarantinedFiles);
        Assert.Single(Directory.EnumerateFiles(
            Path.Combine(stateRoot, "legacy-layout-quarantine"),
            "migration.json",
            SearchOption.AllDirectories));
    }

    [Fact]
    public async Task ModifiedUnknownAndFilesWithoutVerifiedModsCopyRemainUntouched()
    {
        var modpackRoot = Path.Combine(_root, "mo2-protected");
        var stateRoot = Path.Combine(_root, "launcher-data-protected", "Updater");
        var official = Encoding.UTF8.GetBytes("official");
        var userEdit = Encoding.UTF8.GetBytes("useredit");
        var wrongCorrected = Encoding.UTF8.GetBytes("wrongcpy");
        var definition = CreateDefinition(
            ("Legacy Addon/gamedata/modified.ltx", official),
            ("Legacy Addon/gamedata/missing-copy.ltx", official),
            ("Legacy Addon/gamedata/wrong-copy.ltx", official),
            ("Legacy Addon/gamedata/exact.ltx", official));

        var modified = await WriteFileAsync(modpackRoot, definition.Files[0].RelativePath, userEdit);
        await WriteFileAsync(modpackRoot, "mods/" + definition.Files[0].RelativePath, official);
        var missingCopy = await WriteFileAsync(modpackRoot, definition.Files[1].RelativePath, official);
        var wrongCopy = await WriteFileAsync(modpackRoot, definition.Files[2].RelativePath, official);
        await WriteFileAsync(modpackRoot, "mods/" + definition.Files[2].RelativePath, wrongCorrected);
        var exact = await WriteFileAsync(modpackRoot, definition.Files[3].RelativePath, official);
        await WriteFileAsync(modpackRoot, "mods/" + definition.Files[3].RelativePath, official);
        var unknown = await WriteFileAsync(
            modpackRoot,
            "Legacy Addon/user-created/keep.txt",
            Encoding.UTF8.GetBytes("keep me"));

        var result = await LegacyMo2LayoutMigrator.MigrateAsync(
            modpackRoot,
            stateRoot,
            definition);

        Assert.Equal([definition.Files[3].RelativePath], result.QuarantinedFiles);
        Assert.Equal([definition.Files[0].RelativePath], result.ModifiedLegacyFiles);
        Assert.Equal(
            [definition.Files[1].RelativePath, definition.Files[2].RelativePath],
            result.UnverifiedCorrectedFiles);
        Assert.True(File.Exists(modified));
        Assert.Equal(userEdit, await File.ReadAllBytesAsync(modified));
        Assert.True(File.Exists(missingCopy));
        Assert.True(File.Exists(wrongCopy));
        Assert.False(File.Exists(exact));
        Assert.Equal("keep me", await File.ReadAllTextAsync(unknown));
        Assert.True(Directory.Exists(Path.Combine(modpackRoot, "Legacy Addon")));
    }

    [Fact]
    public void ProductionAllowlistIsPinnedToTheExactMalformedRelease()
    {
        Assert.Equal(
            "fc993e2e9dd4cb37254670bf08523d93c2488c7894e4a9eee2a2b61d2cb86c84",
            LegacyMo2LayoutMigrator.LegacyArchiveSha256);
        Assert.Equal(23, LegacyMo2LayoutMigrator.KnownLegacyFiles.Count);
        Assert.Equal(1_656_660, LegacyMo2LayoutMigrator.KnownLegacyFiles.Sum(file => file.Size));
        Assert.Equal(
            23,
            LegacyMo2LayoutMigrator.KnownLegacyFiles
                .Select(file => file.RelativePath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count());
        Assert.All(LegacyMo2LayoutMigrator.KnownLegacyFiles, file =>
        {
            Assert.StartsWith(
                LegacyMo2LayoutMigrator.LegacyRootRelativePath + "/",
                file.RelativePath,
                StringComparison.Ordinal);
            Assert.DoesNotContain("/../", "/" + file.RelativePath + "/", StringComparison.Ordinal);
            Assert.Equal(64, file.Sha256.Length);
            Assert.All(file.Sha256, character => Assert.True(Uri.IsHexDigit(character)));
        });
    }

    private static LegacyMo2LayoutMigrationDefinition CreateDefinition(
        string relativePath,
        byte[] payload) =>
        CreateDefinition((relativePath, payload));

    private static LegacyMo2LayoutMigrationDefinition CreateDefinition(
        params (string RelativePath, byte[] Payload)[] files) =>
        new(
            "test-legacy-layout",
            new string('a', 64),
            "Legacy Addon",
            files.Select(file => new LegacyMo2LayoutFile(
                    file.RelativePath,
                    file.Payload.LongLength,
                    Convert.ToHexStringLower(SHA256.HashData(file.Payload))))
                .ToArray());

    private static async Task<string> WriteFileAsync(
        string root,
        string relativePath,
        byte[] payload)
    {
        var path = PathSafety.ResolveUnderRoot(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, payload);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
