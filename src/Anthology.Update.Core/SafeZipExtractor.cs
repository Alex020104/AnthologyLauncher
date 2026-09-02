using System.IO.Compression;
using System.Security.Cryptography;
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
        => await ExtractAsync(archivePath, stagingRoot, package, null, null, cancellationToken);

    public static async Task ExtractAsync(
        string archivePath,
        string stagingRoot,
        PackageManifest package,
        IReadOnlyList<PackageFileIntegrity>? integrity,
        CancellationToken cancellationToken = default)
        => await ExtractAsync(archivePath, stagingRoot, package, integrity, null, cancellationToken);

    public static async Task ExtractAsync(
        string archivePath,
        string stagingRoot,
        PackageManifest package,
        IReadOnlyList<PackageFileIntegrity>? integrity,
        IReadOnlyList<string>? filesToExtract,
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
        var expectedIntegrity = integrity?.ToDictionary(
            file => PathSafety.NormalizeRelativePath(file.Path),
            StringComparer.OrdinalIgnoreCase);
        var selected = (filesToExtract ?? package.Files)
            .Select(PathSafety.NormalizeRelativePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!selected.IsSubsetOf(expected))
        {
            throw new InvalidDataException("Selected extraction paths are not declared by the package.");
        }
        var extractedSelected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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

            if (expectedIntegrity is not null
                && (!expectedIntegrity.TryGetValue(relativePath, out var expectedFile)
                    || entry.Length != expectedFile.Size))
            {
                throw new InvalidDataException($"Integrity metadata does not match '{relativePath}'.");
            }

            if (!selected.Contains(relativePath))
            {
                continue;
            }
            extractedSelected.Add(relativePath);

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
            if (expectedIntegrity is null)
            {
                await source.CopyToAsync(target, cancellationToken);
            }
            else
            {
                using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[1024 * 1024];
                while (true)
                {
                    var read = await source.ReadAsync(buffer, cancellationToken);
                    if (read == 0)
                    {
                        break;
                    }
                    hasher.AppendData(buffer, 0, read);
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
                var actualHash = Convert.ToHexStringLower(hasher.GetHashAndReset());
                if (!string.Equals(actualHash, expectedIntegrity[relativePath].Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException($"SHA-256 mismatch for extracted file '{relativePath}'.");
                }
            }
            await target.FlushAsync(cancellationToken);
        }

        var missing = expected.Except(extracted, StringComparer.OrdinalIgnoreCase).ToArray();
        if (missing.Length > 0)
        {
            throw new InvalidDataException($"Archive is missing {missing.Length} declared file(s): {string.Join(", ", missing.Take(5))}.");
        }
        var missingSelected = selected.Except(extractedSelected, StringComparer.OrdinalIgnoreCase).ToArray();
        if (missingSelected.Length > 0)
        {
            throw new InvalidDataException($"Archive is missing {missingSelected.Length} selected file(s): {string.Join(", ", missingSelected.Take(5))}.");
        }
    }

    private static bool IsSymbolicLink(ZipArchiveEntry entry) =>
        ((entry.ExternalAttributes >> 16) & UnixFileTypeMask) == UnixSymbolicLink;
}
