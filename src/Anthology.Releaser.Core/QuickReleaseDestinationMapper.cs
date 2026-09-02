using Anthology.Update.Core;

namespace Anthology.Releaser.Core;

/// <summary>
/// Maps files selected in the releaser to paths relative to an updater install root.
/// In particular, an MO2 addon selected from the physical <c>mods</c> directory must
/// remain below <c>mods</c> when the updater applies the package to the MO2 root.
/// </summary>
public static class QuickReleaseDestinationMapper
{
    public static string CreateFileDestination(
        string installRoot,
        string configuredSourceRoot,
        string sourcePath) =>
        CreateDefaultDestination(installRoot, configuredSourceRoot, sourcePath, isFolder: false);

    public static string CreateFolderDestination(
        string installRoot,
        string configuredSourceRoot,
        string sourcePath) =>
        CreateDefaultDestination(installRoot, configuredSourceRoot, sourcePath, isFolder: true);

    public static string NormalizeFileDestination(
        string installRoot,
        string configuredSourceRoot,
        string sourcePath,
        string destinationPath) =>
        NormalizeExistingDestination(
            installRoot,
            configuredSourceRoot,
            sourcePath,
            destinationPath,
            isFolder: false);

    public static string NormalizeFolderDestination(
        string installRoot,
        string configuredSourceRoot,
        string sourcePath,
        string destinationPath) =>
        NormalizeExistingDestination(
            installRoot,
            configuredSourceRoot,
            sourcePath,
            destinationPath,
            isFolder: true);

    private static string CreateDefaultDestination(
        string installRoot,
        string configuredSourceRoot,
        string sourcePath,
        bool isFolder)
    {
        var fullSourcePath = Path.GetFullPath(sourcePath);
        if (IsModpackRoot(installRoot)
            && TryGetMo2ModsDestination(fullSourcePath, isFolder, out var modsDestination))
        {
            return modsDestination;
        }

        return CreateLegacyDefault(configuredSourceRoot, fullSourcePath, isFolder);
    }

    private static string NormalizeExistingDestination(
        string installRoot,
        string configuredSourceRoot,
        string sourcePath,
        string destinationPath,
        bool isFolder)
    {
        var normalized = NormalizeDestination(destinationPath, isFolder);
        if (!IsModpackRoot(installRoot)
            || HasModsPrefix(normalized)
            || !TryGetMo2ModsDestination(Path.GetFullPath(sourcePath), isFolder, out var expectedDestination))
        {
            return normalized;
        }

        // Repair destinations produced by older releasers. When the configured MO2 root
        // was the mods directory (or a stale/missing path), the UI stored only AddonName
        // or the selected leaf name. Deliberately edited destinations such as profiles/x
        // or tools/x are left untouched.
        var expectedWithoutMods = expectedDestination.Equals("mods", StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : expectedDestination["mods/".Length..];
        var legacyDefault = CreateLegacyDefault(configuredSourceRoot, Path.GetFullPath(sourcePath), isFolder);
        var leafName = isFolder
            ? Path.GetFileName(Path.TrimEndingDirectorySeparator(sourcePath))
            : Path.GetFileName(sourcePath);

        if (PathsEqual(normalized, expectedWithoutMods)
            || PathsEqual(normalized, legacyDefault)
            || PathsEqual(normalized, leafName))
        {
            return expectedDestination;
        }

        return normalized;
    }

    private static string CreateLegacyDefault(
        string configuredSourceRoot,
        string fullSourcePath,
        bool isFolder)
    {
        var leafName = isFolder
            ? Path.GetFileName(Path.TrimEndingDirectorySeparator(fullSourcePath))
            : Path.GetFileName(fullSourcePath);
        if (string.IsNullOrWhiteSpace(configuredSourceRoot))
        {
            return NormalizeDestination(leafName, isFolder);
        }

        var relative = Path.GetRelativePath(Path.GetFullPath(configuredSourceRoot), fullSourcePath);
        if (!IsUnderRoot(relative))
        {
            return NormalizeDestination(leafName, isFolder);
        }

        return relative == "."
            ? string.Empty
            : PathSafety.NormalizeRelativePath(relative);
    }

    private static bool TryGetMo2ModsDestination(
        string fullSourcePath,
        bool isFolder,
        out string destination)
    {
        var current = isFolder
            ? new DirectoryInfo(fullSourcePath)
            : new FileInfo(fullSourcePath).Directory;
        while (current is not null)
        {
            if (current.Name.Equals("mods", StringComparison.OrdinalIgnoreCase)
                && current.Parent is not null)
            {
                var relative = Path.GetRelativePath(current.Parent.FullName, fullSourcePath);
                if (IsUnderRoot(relative) && relative != ".")
                {
                    destination = PathSafety.NormalizeRelativePath(relative);
                    return HasModsPrefix(destination);
                }
            }

            current = current.Parent;
        }

        destination = string.Empty;
        return false;
    }

    private static string NormalizeDestination(string path, bool isFolder) =>
        isFolder && string.IsNullOrWhiteSpace(path)
            ? string.Empty
            : PathSafety.NormalizeRelativePath(path);

    private static bool IsUnderRoot(string relativePath) =>
        !Path.IsPathRooted(relativePath)
        && !relativePath.Equals("..", StringComparison.Ordinal)
        && !relativePath.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
        && !relativePath.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);

    private static bool IsModpackRoot(string installRoot) =>
        installRoot.Trim().Equals("modpack", StringComparison.OrdinalIgnoreCase)
        || installRoot.Trim().Equals("mo2", StringComparison.OrdinalIgnoreCase);

    private static bool HasModsPrefix(string path) =>
        path.Equals("mods", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("mods/", StringComparison.OrdinalIgnoreCase);

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            left.Replace('\\', '/').Trim('/'),
            right.Replace('\\', '/').Trim('/'),
            StringComparison.OrdinalIgnoreCase);
}
