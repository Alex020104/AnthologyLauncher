namespace Anthology.Mo2.Core;

public sealed record Mo2ArchiveDirectory(
    string Path,
    int FileCount,
    long ExpandedBytes);

public sealed class Mo2ManualArchivePackage : IDisposable
{
    private readonly FileStream _archiveLease;
    private bool _disposed;

    internal Mo2ManualArchivePackage(
        string archivePath,
        IReadOnlyList<ArchiveFileEntry> entries,
        IReadOnlyList<Mo2ArchiveDirectory> directories,
        string suggestedRoot,
        FileStream archiveLease)
    {
        ArchivePath = archivePath;
        Entries = entries;
        Directories = directories;
        SuggestedRoot = suggestedRoot;
        _archiveLease = archiveLease;
        FileCount = entries.Count;
        ExpandedBytes = directories.Count == 0 ? 0 : directories[0].ExpandedBytes;
    }

    public string ArchivePath { get; }

    public IReadOnlyList<Mo2ArchiveDirectory> Directories { get; }

    public string SuggestedRoot { get; }

    public int FileCount { get; }

    public long ExpandedBytes { get; }

    internal IReadOnlyList<ArchiveFileEntry> Entries { get; }

    internal void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _archiveLease.Dispose();
    }
}
