using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Anthology.Contracts;

namespace Anthology.Update.Core;

/// <summary>
/// Computes a stable identity for the complete file table of a loose package.
/// Individual file hashes remain the authority used during download and repair.
/// </summary>
public static class LoosePackageHash
{
    public static string ComputeSha256(IEnumerable<PackageLooseFile> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> sizeBytes = stackalloc byte[sizeof(long)];
        foreach (var file in files
                     .Select(file => file with { Path = PathSafety.NormalizeRelativePath(file.Path) })
                     .OrderBy(file => file.Path, StringComparer.Ordinal))
        {
            hash.AppendData(Encoding.UTF8.GetBytes(file.Path));
            hash.AppendData([0]);
            BinaryPrimitives.WriteInt64LittleEndian(sizeBytes, file.Size);
            hash.AppendData(sizeBytes);
            hash.AppendData(Convert.FromHexString(file.Sha256));
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
}
