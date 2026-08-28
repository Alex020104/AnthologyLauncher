namespace Anthology.Update.Core;

public static class PathSafety
{
    public static string NormalizeRelativePath(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        var normalized = relativePath.Replace('\\', '/').Trim();

        if (normalized[0] == '/'
            || Path.IsPathRooted(normalized)
            || normalized.Contains(':'))
        {
            throw new ArgumentException("Absolute paths and drive prefixes are forbidden.", nameof(relativePath));
        }

        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts.Any(part => part is "." or ".."))
        {
            throw new ArgumentException("Dot segments are forbidden.", nameof(relativePath));
        }

        return string.Join('/', parts);
    }

    public static string ResolveUnderRoot(string root, string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var normalized = NormalizeRelativePath(relativePath)
            .Replace('/', Path.DirectorySeparatorChar);
        var candidate = Path.GetFullPath(Path.Combine(fullRoot, normalized));

        if (!candidate.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Path escapes the selected install root.", nameof(relativePath));
        }

        return candidate;
    }
}
