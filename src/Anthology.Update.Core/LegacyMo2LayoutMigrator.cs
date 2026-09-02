using System.Security;
using System.Text.Json;
using Anthology.Contracts;

namespace Anthology.Update.Core;

public sealed record LegacyMo2LayoutFile(string RelativePath, long Size, string Sha256);

public sealed record LegacyMo2LayoutMigrationResult(
    string MigrationId,
    string? QuarantineRoot,
    IReadOnlyList<string> QuarantinedFiles,
    IReadOnlyList<string> ModifiedLegacyFiles,
    IReadOnlyList<string> UnverifiedCorrectedFiles,
    IReadOnlyList<string> Errors);

internal sealed record LegacyMo2LayoutMigrationDefinition(
    string MigrationId,
    string LegacyArchiveSha256,
    string LegacyRootRelativePath,
    IReadOnlyList<LegacyMo2LayoutFile> Files);

/// <summary>
/// Quarantines the files written next to ModOrganizer.exe by the malformed
/// anthology-files-modpack 2.1.157 package. A file is moved only when both the
/// legacy copy and its corrected mods copy match the immutable release bytes.
/// </summary>
public static class LegacyMo2LayoutMigrator
{
    public const string MigrationId = "anthology-files-modpack-2.1.157-root-layout";
    public const string LegacyArchiveSha256 = "fc993e2e9dd4cb37254670bf08523d93c2488c7894e4a9eee2a2b61d2cb86c84";
    public const string LegacyRootRelativePath =
        "[WPN][1.1][SCP][R.A.K Weapon Pack Adaptation Global Anomaly PiP for 3DSS (OBT)]";

    private static readonly SemaphoreSlim Gate = new(1, 1);

    private static readonly IReadOnlyList<LegacyMo2LayoutFile> LegacyFiles = Array.AsReadOnly(
    [
        Entry("gamedata/configs/mod_system_pip_scope_policy.ltx", 3999, "ea53950ded604a10e7640a284697844ec375a3141e41d0d0fd39695d8befcd22"),
        Entry("gamedata/configs/mod_system_weapon_scopes_pip_for_3dss.ltx", 15779, "f2adc82bc58d0785ee50d5005e5e2f7fbc79cb9a8695708dd1b2cde0b6184d96"),
        Entry("gamedata/configs/mod_system_weapons_explosive_pip_for_3dss.ltx", 47167, "1e0ae264640e54f50087356a3a6e893c0792271dc487ffa6df4579faa832c014"),
        Entry("gamedata/configs/mod_system_weapons_integrated_scopes_pip_for_3dss.ltx", 12822, "acc1d077bc9b0edd87c9e5146c2b085c8730f8170740a98a3ce157d63875342a"),
        Entry("gamedata/configs/mod_system_weapons_rifles_pip_for_3dss.ltx", 1000015, "7fff01d1ac222b69e4ad781a4fe0f8c6bc7f6f0b02b79cafa8650059816ba976"),
        Entry("gamedata/configs/mod_system_weapons_shotguns_pip_for_3dss.ltx", 143530, "d240edb4de4794d1bd3e969849f6047f549e7f12f090eee035686362c6ecfc10"),
        Entry("gamedata/configs/mod_system_weapons_smg_pip_for_3dss.ltx", 114296, "7d09c6906341edb760463d93a37fc7f26023bd60f0ee528b0e41a71484cf33e9"),
        Entry("gamedata/configs/mod_system_weapons_snipers_pip_for_3dss.ltx", 207508, "b7605c0080dd0c029cd403b239c157536769e84a54f1292798eb3dbd21daf17e"),
        Entry("gamedata/configs/text/eng/rak_pip_quality_mcm.xml", 1609, "2b8869ca93a7c046f81af51c121b899654b3b3222d6faea7a0f06a15e46b4d10"),
        Entry("gamedata/configs/text/rus/rak_pip_quality_mcm.xml", 1642, "25309b8a41d822109a98ca12f6169375faad4c208557e66c98b34806420f9a9a"),
        Entry("gamedata/scripts/rak_pip_quality_mcm.script", 1439, "902433a1ba30428045d2571dbff3a7aac7634894d31c4829d7ea2b2c229833f6"),
        Entry("gamedata/scripts/zzz_anthology_pip_device_bridge.script", 3975, "9a4831e785ac0b753af6696d943df0953e934c590b39ba78c45ba5aadc18d6bf"),
        Entry("gamedata/shaders/r3/models_scope_reticle.ps", 24023, "10c796f14df7b0c415cb18c013136824d868a5ea00fd10b3a45fec710c777cf1"),
        Entry("gamedata/shaders/r3/models_scope_reticle.s", 870, "fd5487fa545f50c09b062a271da662cf6331bb44402d092f3c0b20fb9c1e3695"),
        Entry("gamedata/shaders/r3/models_scope_reticle.vs", 1362, "276844de66bc4f8327763a2830739947790ac13da88ba605019ee6676eda38c0"),
        Entry("gamedata/shaders/r3/models_scope_reticle_precise.ps", 23992, "57052132c152fe90423de8c07b310136c5229b68ef9181dbad0f979de1c808b7"),
        Entry("gamedata/shaders/r3/models_scope_reticle_precise.s", 878, "09b92ae5b804969f0d7f4f7af5ed7bc6972e4c4899e28a952c5b92a2475a494b"),
        Entry("gamedata/shaders/r3/svp_quality.ps", 629, "686a7cec26412776c587ad1434687b7718c992e0fe2742d311360c18c5d76c37"),
        Entry("gamedata/shaders/r4/models_scope_reticle.ps", 24023, "10c796f14df7b0c415cb18c013136824d868a5ea00fd10b3a45fec710c777cf1"),
        Entry("gamedata/shaders/r4/models_scope_reticle.s", 870, "fd5487fa545f50c09b062a271da662cf6331bb44402d092f3c0b20fb9c1e3695"),
        Entry("gamedata/shaders/r4/models_scope_reticle.vs", 1362, "276844de66bc4f8327763a2830739947790ac13da88ba605019ee6676eda38c0"),
        Entry("gamedata/shaders/r4/models_scope_reticle_precise.ps", 23992, "57052132c152fe90423de8c07b310136c5229b68ef9181dbad0f979de1c808b7"),
        Entry("gamedata/shaders/r4/models_scope_reticle_precise.s", 878, "09b92ae5b804969f0d7f4f7af5ed7bc6972e4c4899e28a952c5b92a2475a494b"),
    ]);

    private static readonly LegacyMo2LayoutMigrationDefinition ProductionDefinition = new(
        MigrationId,
        LegacyArchiveSha256,
        LegacyRootRelativePath,
        LegacyFiles);

    public static IReadOnlyList<LegacyMo2LayoutFile> KnownLegacyFiles => LegacyFiles;

    public static Task<LegacyMo2LayoutMigrationResult> MigrateAsync(
        string modpackRoot,
        string stateRoot,
        CancellationToken cancellationToken = default) =>
        MigrateAsync(modpackRoot, stateRoot, ProductionDefinition, cancellationToken);

    internal static async Task<LegacyMo2LayoutMigrationResult> MigrateAsync(
        string modpackRoot,
        string stateRoot,
        LegacyMo2LayoutMigrationDefinition definition,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modpackRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(stateRoot);
        ArgumentNullException.ThrowIfNull(definition);
        ValidateDefinition(definition);

        await Gate.WaitAsync(cancellationToken);
        try
        {
            return await MigrateCoreAsync(modpackRoot, stateRoot, definition, cancellationToken);
        }
        finally
        {
            Gate.Release();
        }
    }

    private static async Task<LegacyMo2LayoutMigrationResult> MigrateCoreAsync(
        string modpackRoot,
        string stateRoot,
        LegacyMo2LayoutMigrationDefinition definition,
        CancellationToken cancellationToken)
    {
        var quarantined = new List<string>();
        var modified = new List<string>();
        var unverifiedCorrected = new List<string>();
        var errors = new List<string>();
        string? batchRoot = null;
        string? filesRoot = null;
        string? receiptPath = null;
        var batchCreatedAtUtc = DateTimeOffset.UtcNow;

        string root;
        string updaterStateRoot;
        string legacyRoot;
        try
        {
            root = Path.GetFullPath(modpackRoot);
            updaterStateRoot = Path.GetFullPath(stateRoot);
            legacyRoot = PathSafety.ResolveUnderRoot(root, definition.LegacyRootRelativePath);
        }
        catch (Exception exception) when (IsFileSystemException(exception))
        {
            errors.Add($"Migration roots are invalid: {exception.Message}");
            return CreateResult(definition, null, quarantined, modified, unverifiedCorrected, errors);
        }

        if (!Directory.Exists(root) || !Directory.Exists(legacyRoot))
        {
            return CreateResult(definition, null, quarantined, modified, unverifiedCorrected, errors);
        }

        if (IsSameOrDescendant(updaterStateRoot, legacyRoot))
        {
            errors.Add("Updater state root is inside the legacy addon root; quarantine was not attempted.");
            return CreateResult(definition, null, quarantined, modified, unverifiedCorrected, errors);
        }

        if (!HaveSameVolume(root, updaterStateRoot))
        {
            errors.Add("Legacy addon and updater state are on different volumes; non-atomic quarantine was not attempted.");
            return CreateResult(definition, null, quarantined, modified, unverifiedCorrected, errors);
        }

        foreach (var expected in definition.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = PathSafety.NormalizeRelativePath(expected.RelativePath);
            string source;
            string corrected;
            try
            {
                source = PathSafety.ResolveUnderRoot(root, relativePath);
                corrected = PathSafety.ResolveUnderRoot(root, "mods/" + relativePath);
                if (!File.Exists(source))
                {
                    continue;
                }

                EnsureNoReparsePoints(root, relativePath);
                if (!await MatchesAsync(source, expected, cancellationToken))
                {
                    modified.Add(relativePath);
                    continue;
                }

                if (!File.Exists(corrected))
                {
                    unverifiedCorrected.Add(relativePath);
                    continue;
                }

                EnsureNoReparsePoints(root, "mods/" + relativePath);
                if (!await MatchesAsync(corrected, expected, cancellationToken))
                {
                    unverifiedCorrected.Add(relativePath);
                    continue;
                }

                // Narrow the hash-to-move window after hashing the corrected copy.
                EnsureNoReparsePoints(root, relativePath);
                if (!await MatchesAsync(source, expected, cancellationToken))
                {
                    modified.Add(relativePath);
                    continue;
                }

                if (batchRoot is null)
                {
                    var batchName = $"{batchCreatedAtUtc:yyyyMMddTHHmmssfffZ}-{Guid.NewGuid():N}";
                    var batchRelativePath = PathSafety.NormalizeRelativePath(
                        $"legacy-layout-quarantine/{definition.MigrationId}/{batchName}");
                    EnsureNoReparsePoints(updaterStateRoot, batchRelativePath, allowMissingLeaf: true);
                    var newBatchRoot = PathSafety.ResolveUnderRoot(updaterStateRoot, batchRelativePath);
                    var newFilesRoot = Path.Combine(newBatchRoot, "files");
                    var newReceiptPath = Path.Combine(newBatchRoot, "migration.json");
                    Directory.CreateDirectory(newFilesRoot);
                    EnsureNoReparsePoints(updaterStateRoot, batchRelativePath + "/files");
                    await WriteReceiptAsync(
                        newReceiptPath,
                        CreateReceipt(definition, batchCreatedAtUtc, root, newBatchRoot, quarantined, modified, unverifiedCorrected, errors),
                        cancellationToken);
                    batchRoot = newBatchRoot;
                    filesRoot = newFilesRoot;
                    receiptPath = newReceiptPath;
                }

                var destination = PathSafety.ResolveUnderRoot(filesRoot!, relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                EnsureNoReparsePoints(filesRoot!, Path.GetDirectoryName(relativePath)!.Replace('\\', '/'));
                if (File.Exists(destination) || Directory.Exists(destination))
                {
                    errors.Add($"Quarantine destination already exists: {relativePath}");
                    continue;
                }

                File.Move(source, destination);
                quarantined.Add(relativePath);

                // Once the source has moved, finish verification and journaling even
                // if the caller cancels so a completed atomic move is recoverable.
                if (!await MatchesAsync(destination, expected, CancellationToken.None))
                {
                    errors.Add($"Quarantined file failed post-move verification: {relativePath}");
                    TryRestoreUnexpectedMove(source, destination, relativePath, quarantined, errors);
                }

                await TryWriteReceiptAsync(
                    receiptPath!,
                    CreateReceipt(definition, batchCreatedAtUtc, root, batchRoot, quarantined, modified, unverifiedCorrected, errors),
                    errors);
            }
            catch (Exception exception) when (IsFileSystemException(exception))
            {
                errors.Add($"{relativePath}: {exception.Message}");
            }
        }

        CleanupKnownEmptyDirectories(root, definition, errors);

        if (receiptPath is not null)
        {
            await TryWriteReceiptAsync(
                receiptPath,
                CreateReceipt(definition, batchCreatedAtUtc, root, batchRoot!, quarantined, modified, unverifiedCorrected, errors),
                errors);
        }

        return CreateResult(definition, batchRoot, quarantined, modified, unverifiedCorrected, errors);
    }

    private static LegacyMo2LayoutFile Entry(string childPath, long size, string sha256) =>
        new($"{LegacyRootRelativePath}/{childPath}", size, sha256);

    private static async Task<bool> MatchesAsync(
        string path,
        LegacyMo2LayoutFile expected,
        CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length != expected.Size)
        {
            return false;
        }

        var hash = await ArtifactHash.ComputeSha256Async(path, cancellationToken);
        return string.Equals(hash, expected.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    private static void EnsureNoReparsePoints(
        string root,
        string relativePath,
        bool allowMissingLeaf = false)
    {
        var fullRoot = Path.GetFullPath(root);
        var normalized = PathSafety.NormalizeRelativePath(relativePath);
        var parts = normalized.Split('/');
        var current = fullRoot;
        for (var index = 0; index < parts.Length; index++)
        {
            current = Path.Combine(current, parts[index]);
            if (!File.Exists(current) && !Directory.Exists(current))
            {
                if (allowMissingLeaf || index < parts.Length - 1)
                {
                    continue;
                }

                return;
            }

            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException($"Path traverses a reparse point: {normalized}");
            }
        }
    }

    private static void CleanupKnownEmptyDirectories(
        string modpackRoot,
        LegacyMo2LayoutMigrationDefinition definition,
        List<string> errors)
    {
        var legacyRoot = PathSafety.NormalizeRelativePath(definition.LegacyRootRelativePath);
        var directories = definition.Files
            .SelectMany(file => GetParentDirectories(file.RelativePath))
            .Where(path => string.Equals(path, legacyRoot, StringComparison.OrdinalIgnoreCase)
                           || path.StartsWith(legacyRoot + "/", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(path => path.Count(character => character == '/'))
            .ThenByDescending(path => path.Length)
            .ToArray();

        foreach (var relativeDirectory in directories)
        {
            try
            {
                var directory = PathSafety.ResolveUnderRoot(modpackRoot, relativeDirectory);
                if (!Directory.Exists(directory))
                {
                    continue;
                }

                EnsureNoReparsePoints(modpackRoot, relativeDirectory);
                if (!Directory.EnumerateFileSystemEntries(directory).Any())
                {
                    Directory.Delete(directory, recursive: false);
                }
            }
            catch (Exception exception) when (IsFileSystemException(exception))
            {
                errors.Add($"Could not remove empty legacy directory '{relativeDirectory}': {exception.Message}");
            }
        }
    }

    private static IEnumerable<string> GetParentDirectories(string relativePath)
    {
        var current = Path.GetDirectoryName(PathSafety.NormalizeRelativePath(relativePath).Replace('/', Path.DirectorySeparatorChar));
        while (!string.IsNullOrWhiteSpace(current))
        {
            yield return current.Replace('\\', '/');
            current = Path.GetDirectoryName(current);
        }
    }

    private static void TryRestoreUnexpectedMove(
        string source,
        string destination,
        string relativePath,
        List<string> quarantined,
        List<string> errors)
    {
        try
        {
            if (!File.Exists(source) && File.Exists(destination))
            {
                File.Move(destination, source);
                quarantined.Remove(relativePath);
            }
        }
        catch (Exception exception) when (IsFileSystemException(exception))
        {
            errors.Add($"Could not restore post-move verification failure '{relativePath}': {exception.Message}");
        }
    }

    private static MigrationReceipt CreateReceipt(
        LegacyMo2LayoutMigrationDefinition definition,
        DateTimeOffset createdAtUtc,
        string modpackRoot,
        string quarantineRoot,
        IReadOnlyList<string> quarantined,
        IReadOnlyList<string> modified,
        IReadOnlyList<string> unverifiedCorrected,
        IReadOnlyList<string> errors) =>
        new(
            definition.MigrationId,
            definition.LegacyArchiveSha256,
            createdAtUtc,
            modpackRoot,
            quarantineRoot,
            quarantined.ToArray(),
            modified.ToArray(),
            unverifiedCorrected.ToArray(),
            errors.ToArray());

    private static async Task WriteReceiptAsync(
        string path,
        MigrationReceipt receipt,
        CancellationToken cancellationToken)
    {
        var temporary = path + $".tmp-{Guid.NewGuid():N}";
        try
        {
            await using (var stream = new FileStream(
                             temporary,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             32 * 1024,
                             FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, receipt, ManifestJson.Options, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temporary);
            }
            catch (Exception exception) when (IsFileSystemException(exception))
            {
                // The receipt itself is authoritative; a stranded temp file is harmless.
            }
        }
    }

    private static async Task TryWriteReceiptAsync(
        string path,
        MigrationReceipt receipt,
        List<string> errors)
    {
        try
        {
            await WriteReceiptAsync(path, receipt, CancellationToken.None);
        }
        catch (Exception exception) when (IsFileSystemException(exception))
        {
            errors.Add($"Could not update migration receipt: {exception.Message}");
        }
    }

    private static void ValidateDefinition(LegacyMo2LayoutMigrationDefinition definition)
    {
        var migrationId = PathSafety.NormalizeRelativePath(definition.MigrationId);
        if (migrationId.Contains('/'))
        {
            throw new ArgumentException("Migration ID must be one safe path segment.", nameof(definition));
        }

        ValidateSha256(definition.LegacyArchiveSha256, nameof(definition));
        var legacyRoot = PathSafety.NormalizeRelativePath(definition.LegacyRootRelativePath);
        if (legacyRoot.Contains('/') || string.Equals(legacyRoot, "mods", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Legacy root must be one non-mods path segment.", nameof(definition));
        }

        if (definition.Files.Count == 0)
        {
            throw new ArgumentException("Migration allowlist cannot be empty.", nameof(definition));
        }

        var uniquePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in definition.Files)
        {
            var relativePath = PathSafety.NormalizeRelativePath(file.RelativePath);
            if (!relativePath.StartsWith(legacyRoot + "/", StringComparison.OrdinalIgnoreCase)
                || !uniquePaths.Add(relativePath)
                || file.Size < 0)
            {
                throw new ArgumentException("Migration allowlist contains an invalid file entry.", nameof(definition));
            }

            ValidateSha256(file.Sha256, nameof(definition));
        }
    }

    private static void ValidateSha256(string hash, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(hash)
            || hash.Length != 64
            || hash.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException("Expected a 64-character SHA-256 value.", parameterName);
        }
    }

    private static bool HaveSameVolume(string left, string right) =>
        string.Equals(
            Path.GetPathRoot(Path.GetFullPath(left)),
            Path.GetPathRoot(Path.GetFullPath(right)),
            StringComparison.OrdinalIgnoreCase);

    private static bool IsSameOrDescendant(string candidate, string parent)
    {
        var fullCandidate = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullParent = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(fullCandidate, fullParent, StringComparison.OrdinalIgnoreCase)
               || fullCandidate.StartsWith(fullParent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsFileSystemException(Exception exception) =>
        exception is ArgumentException
            or IOException
            or NotSupportedException
            or SecurityException
            or UnauthorizedAccessException;

    private static LegacyMo2LayoutMigrationResult CreateResult(
        LegacyMo2LayoutMigrationDefinition definition,
        string? quarantineRoot,
        IReadOnlyList<string> quarantined,
        IReadOnlyList<string> modified,
        IReadOnlyList<string> unverifiedCorrected,
        IReadOnlyList<string> errors) =>
        new(
            definition.MigrationId,
            quarantineRoot,
            quarantined.ToArray(),
            modified.ToArray(),
            unverifiedCorrected.ToArray(),
            errors.ToArray());

    private sealed record MigrationReceipt(
        string MigrationId,
        string LegacyArchiveSha256,
        DateTimeOffset CreatedAtUtc,
        string OriginalModpackRoot,
        string QuarantineRoot,
        IReadOnlyList<string> QuarantinedFiles,
        IReadOnlyList<string> ModifiedLegacyFiles,
        IReadOnlyList<string> UnverifiedCorrectedFiles,
        IReadOnlyList<string> Errors);
}
