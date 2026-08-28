using System.IO.Compression;
using Anthology.Contracts;

namespace Anthology.Update.Core;

public static class SafeZipExtractor
{
    private const long MaximumExpandedSize = 100L * 1024 * 1024 * 1024;
    private const int UnixFileTypeMask = 0xF000;
    private const int UnixSymbolicLink = 0xA000;

    public static async Task ExtractAsync(
        string archivePath,
        string stagingRoot,
        PackageManifest package,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingRoot);
        ArgumentNullException.ThrowIfNull(package);

        var expected = package.Files
            .Select(PathSafety.NormalizeRelativePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (expected.Count != package.Files.Count)
        {
            throw new InvalidDataException("Manifest contains duplicate package paths.");
        }

        Directory.CreateDirectory(Path.GetFullPath(stagingRoot));
        using var archive = ZipFile.OpenRead(Path.GetFullPath(archivePath));
        var extracted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long expandedSize = 0;

        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(entry.Name))
            {
                continue;
            }

            if (IsSymbolicLink(entry))
            {
                throw new InvalidDataException($"Symbolic link is forbidden in package: '{entry.FullName}'.");
            }

            var relativePath = PathSafety.NormalizeRelativePath(entry.FullName);
            if (!expected.Contains(relativePath))
            {
                throw new InvalidDataException($"Archive contains undeclared file '{relativePath}'.");
            }

            if (!extracted.Add(relativePath))
            {
                throw new InvalidDataException($"Archive contains duplicate file '{relativePath}'.");
            }

            expandedSize = checked(expandedSize + entry.Length);
            if (expandedSize > MaximumExpandedSize)
            {
                throw new InvalidDataException("Expanded package exceeds the 100 GiB safety limit.");
            }

            var destination = PathSafety.ResolveUnderRoot(stagingRoot, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using var source = entry.Open();
            await using var target = new FileStream(
                destination,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await source.CopyToAsync(target, cancellationToken);
            await target.FlushAsync(cancellationToken);
        }

        var missing = expected.Except(extracted, StringComparer.OrdinalIgnoreCase).ToArray();
        if (missing.Length > 0)
        {
            throw new InvalidDataException($"Archive is missing {missing.Length} declared file(s): {string.Join(", ", missing.Take(5))}.");
        }
    }

    private static bool IsSymbolicLink(ZipArchiveEntry entry) =>
        ((entry.ExternalAttributes >> 16) & UnixFileTypeMask) == UnixSymbolicLink;
}
