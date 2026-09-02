using Anthology.Contracts;

namespace Anthology.Update.Core;

/// <summary>
/// Defines package identities whose write scope is narrower than their configured
/// install root. These rules are security boundaries, not destination mapping.
/// </summary>
public static class PackageInstallScopePolicy
{
    public const string Mo2ModsOnlyPackageId = "anthology-files-modpack";

    private const string Mo2InstallRoot = "modpack";
    private const string Mo2ModsPrefix = "mods/";

    public static bool IsMo2ModsOnlyPackage(string? packageId) =>
        string.Equals(packageId, Mo2ModsOnlyPackageId, StringComparison.OrdinalIgnoreCase);

    public static bool IsAllowedMo2ModsPath(string relativePath)
    {
        var normalized = PathSafety.NormalizeRelativePath(relativePath);
        return normalized.StartsWith(Mo2ModsPrefix, StringComparison.OrdinalIgnoreCase);
    }

    public static IReadOnlyList<string> Validate(PackageManifest package)
    {
        ArgumentNullException.ThrowIfNull(package);
        if (!IsMo2ModsOnlyPackage(package.Id))
        {
            return [];
        }

        var errors = new List<string>();
        ValidateIdentity(package.Id, package.InstallRoot, errors);
        if (package.PruneInstallRoot)
        {
            errors.Add(
                $"Package '{package.Id}' cannot prune the MO2 root; it may change only 'mods/**'.");
        }

        AppendPathErrors(package.Id, "file", package.Files, errors);
        AppendPathErrors(package.Id, "preserved path", package.PreservedPaths ?? [], errors);
        AppendPathErrors(package.Id, "deleted file", package.DeletedFiles ?? [], errors);
        AppendPathErrors(package.Id, "deleted directory", package.DeletedDirectories ?? [], errors);
        return errors;
    }

    public static void ValidateAndThrow(PackageManifest package)
    {
        var errors = Validate(package);
        if (errors.Count > 0)
        {
            throw new InvalidDataException(string.Join(Environment.NewLine, errors));
        }
    }

    internal static void AppendIntegrityArtifactErrors(
        PackageArtifactIntegrity artifact,
        ICollection<string> errors)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentNullException.ThrowIfNull(errors);
        if (!IsMo2ModsOnlyPackage(artifact.PackageId))
        {
            return;
        }

        ValidateIdentity(artifact.PackageId, artifact.InstallRoot, errors);
        AppendPathErrors(
            artifact.PackageId,
            "integrity archive file",
            (artifact.ArchiveFiles ?? []).Select(file => file.Path),
            errors);
        AppendPathErrors(
            artifact.PackageId,
            "managed integrity file",
            artifact.ManagedFiles ?? [],
            errors);
    }

    internal static void ValidateArchiveEntryAndThrow(
        PackageManifest package,
        string relativePath,
        bool isDirectory)
    {
        if (!IsMo2ModsOnlyPackage(package.Id))
        {
            return;
        }

        var normalized = PathSafety.NormalizeRelativePath(relativePath);
        var allowed = normalized.StartsWith(Mo2ModsPrefix, StringComparison.OrdinalIgnoreCase)
                      || isDirectory && normalized.Equals("mods", StringComparison.OrdinalIgnoreCase);
        if (!allowed)
        {
            throw new InvalidDataException(
                $"Package '{package.Id}' archive entry '{relativePath}' is outside the allowed 'mods/**' scope.");
        }
    }

    internal static void ValidateResolvedTargetsAndThrow(
        PackageManifest package,
        IEnumerable<string> relativePaths)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(relativePaths);
        if (!IsMo2ModsOnlyPackage(package.Id))
        {
            return;
        }

        foreach (var path in relativePaths)
        {
            if (!IsAllowedMo2ModsPath(path))
            {
                throw new InvalidDataException(
                    $"Package '{package.Id}' resolved target '{path}' is outside the allowed 'mods/**' scope.");
            }
        }
    }

    private static void ValidateIdentity(
        string packageId,
        string installRoot,
        ICollection<string> errors)
    {
        if (!string.Equals(installRoot, Mo2InstallRoot, StringComparison.OrdinalIgnoreCase))
        {
            errors.Add($"Package '{packageId}' must use install root '{Mo2InstallRoot}'.");
        }
    }

    private static void AppendPathErrors(
        string packageId,
        string pathKind,
        IEnumerable<string> paths,
        ICollection<string> errors)
    {
        foreach (var path in paths)
        {
            try
            {
                if (!IsAllowedMo2ModsPath(path))
                {
                    errors.Add(
                        $"Package '{packageId}' {pathKind} '{path}' is outside the allowed 'mods/**' scope.");
                }
            }
            catch (ArgumentException)
            {
                // General manifest/catalog validation reports unsafe path syntax.
            }
        }
    }
}
