namespace Anthology.Update.Core;

public static class PathSafety
{
    private static readonly HashSet<string> ReservedWindowsNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    public static string NormalizeRelativePath(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        var normalized = relativePath.Replace('\\', '/');
        if (!string.Equals(normalized, normalized.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException("Leading and trailing whitespace is forbidden.", nameof(relativePath));
        }

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

        foreach (var part in parts)
        {
            if (part.EndsWith(' ') || part.EndsWith('.') || part.Any(char.IsControl))
            {
                throw new ArgumentException("Unsafe Windows filename segment.", nameof(relativePath));
            }

            var stem = part.Split('.')[0];
            if (ReservedWindowsNames.Contains(stem))
            {
                throw new ArgumentException("Reserved Windows device name is forbidden.", nameof(relativePath));
            }
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
