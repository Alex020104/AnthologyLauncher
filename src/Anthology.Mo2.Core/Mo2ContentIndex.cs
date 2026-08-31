namespace Anthology.Mo2.Core;

public sealed record Mo2ModConflictSummary(
    string ModName,
    int FileCount,
    int ConflictingFiles,
    int WinningConflicts,
    int LosingConflicts)
{
    public bool HasConflicts => ConflictingFiles > 0;
}

public sealed record Mo2ConflictEntry(
    string RelativePath,
    string Winner,
    IReadOnlyList<string> Providers);

public sealed record Mo2VirtualEntry(
    string Name,
    string RelativePath,
    bool IsDirectory,
    string Source,
    int ProviderCount,
    long Size,
    DateTime LastWriteTimeUtc,
    string? FullPath)
{
    public IReadOnlyList<string> Providers { get; init; } = [];
}

public sealed record Mo2DownloadEntry(
    string Name,
    string FullPath,
    long Size,
    DateTime LastWriteTimeUtc,
    bool HasMetadata);

public sealed record Mo2SaveEntry(
    string Name,
    string SaveName,
    string FullPath,
    long Size,
    DateTime LastWriteTimeUtc,
    string? PreviewPath,
    bool HasScop,
    bool HasScoc);

public sealed record Mo2ContentOverview(
    int EnabledMods,
    int UniqueFiles,
    int ConflictFiles,
    IReadOnlyDictionary<string, Mo2ModConflictSummary> Conflicts,
    IReadOnlyList<Mo2DownloadEntry> Downloads,
    IReadOnlyList<Mo2SaveEntry> Saves);

public sealed class Mo2ContentIndex
{
    private const string BaseGameSource = "ИГРА";
    private readonly Dictionary<string, List<FileProvider>> _files;
    private readonly string? _gameRoot;

    private Mo2ContentIndex(
        Dictionary<string, List<FileProvider>> files,
        string? gameRoot,
        Mo2ContentOverview overview)
    {
        _files = files;
        _gameRoot = gameRoot;
        Overview = overview;
    }

    public Mo2ContentOverview Overview { get; }

    public static Mo2ContentIndex Build(
        Mo2InstanceSnapshot instance,
        Mo2ProfileSnapshot profile,
        CancellationToken cancellationToken = default)
    {
        var files = new Dictionary<string, List<FileProvider>>(StringComparer.OrdinalIgnoreCase);
        var modFileCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = true,
            ReturnSpecialDirectories = false,
        };

        foreach (var mod in profile.Mods.Where(item => item.Enabled && !item.IsSeparator))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(mod.DirectoryPath))
            {
                continue;
            }

            var fileCount = 0;
            foreach (var file in Directory.EnumerateFiles(mod.DirectoryPath, "*", options))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = NormalizeRelativePath(Path.GetRelativePath(mod.DirectoryPath, file));
                if (relative.Length == 0)
                {
                    continue;
                }

                if (!files.TryGetValue(relative, out var providers))
                {
                    providers = [];
                    files[relative] = providers;
                }

                providers.Add(new FileProvider(mod.Name, mod.Order, file));
                fileCount++;
            }

            modFileCounts[mod.Name] = fileCount;
        }

        var mutable = modFileCounts.ToDictionary(
            pair => pair.Key,
            pair => new MutableConflict(pair.Value),
            StringComparer.OrdinalIgnoreCase);
        var conflictingFiles = 0;
        foreach (var pair in files.Where(pair => pair.Value.Select(item => item.ModName).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1))
        {
            conflictingFiles++;
            var ordered = pair.Value.OrderBy(item => item.Priority).ToArray();
            var winner = ordered[^1].ModName;
            foreach (var provider in ordered.DistinctBy(item => item.ModName, StringComparer.OrdinalIgnoreCase))
            {
                var conflict = mutable[provider.ModName];
                conflict.Paths.Add(pair.Key);
                if (string.Equals(provider.ModName, winner, StringComparison.OrdinalIgnoreCase))
                {
                    conflict.Winning++;
                }
                else
                {
                    conflict.Losing++;
                }
            }
        }

        var conflicts = mutable.ToDictionary(
            pair => pair.Key,
            pair => new Mo2ModConflictSummary(
                pair.Key,
                pair.Value.FileCount,
                pair.Value.Paths.Count,
                pair.Value.Winning,
                pair.Value.Losing),
            StringComparer.OrdinalIgnoreCase);
        var downloads = Mo2WorkspaceReader.ReadDownloads(instance.Root);
        var saves = Mo2WorkspaceReader.ReadSaves(instance.GamePath);
        var overview = new Mo2ContentOverview(
            profile.Mods.Count(item => item.Enabled && !item.IsSeparator),
            files.Count,
            conflictingFiles,
            conflicts,
            downloads,
            saves);
        return new Mo2ContentIndex(files, instance.GamePath, overview);
    }

    public IReadOnlyList<Mo2VirtualEntry> Browse(string? relativePath)
    {
        var current = NormalizeRelativePath(relativePath ?? string.Empty);
        var prefix = current.Length == 0 ? string.Empty : current + "/";
        var entries = new Dictionary<string, MutableVirtualEntry>(StringComparer.OrdinalIgnoreCase);

        foreach (var pair in _files)
        {
            if (!pair.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var remainder = pair.Key[prefix.Length..];
            if (remainder.Length == 0)
            {
                continue;
            }

            var slash = remainder.IndexOf('/');
            var name = slash >= 0 ? remainder[..slash] : remainder;
            var isDirectory = slash >= 0;
            if (!entries.TryGetValue(name, out var entry))
            {
                entry = new MutableVirtualEntry(name, CombineRelative(current, name), isDirectory);
                entries[name] = entry;
            }

            entry.IsDirectory |= isDirectory;
            foreach (var provider in pair.Value)
            {
                entry.Providers.Add(provider.ModName);
                if (!isDirectory && provider.Priority >= entry.WinnerPriority)
                {
                    entry.Source = provider.ModName;
                    entry.WinnerPriority = provider.Priority;
                    var info = new FileInfo(provider.FullPath);
                    entry.Size = info.Exists ? info.Length : 0;
                    entry.LastWriteTimeUtc = info.Exists ? info.LastWriteTimeUtc : DateTime.MinValue;
                    entry.FullPath = provider.FullPath;
                }
            }
        }

        AddPhysicalEntries(current, entries);
        return entries.Values
            .OrderByDescending(item => item.IsDirectory)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .Select(item => new Mo2VirtualEntry(
                item.Name,
                item.RelativePath,
                item.IsDirectory,
                item.Source,
                item.Providers.Count,
                item.Size,
                item.LastWriteTimeUtc,
                item.FullPath)
            {
                Providers = item.Providers.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            })
            .ToArray();
    }

    public IReadOnlyList<Mo2ConflictEntry> GetConflicts(string modName, int limit = 500)
    {
        return _files
            .Where(pair => pair.Value.Count > 1
                           && pair.Value.Any(provider => string.Equals(provider.ModName, modName, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(1, limit))
            .Select(pair =>
            {
                var ordered = pair.Value.OrderBy(provider => provider.Priority).ToArray();
                return new Mo2ConflictEntry(
                    pair.Key,
                    ordered[^1].ModName,
                    ordered.Select(provider => provider.ModName).Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
            })
            .ToArray();
    }

    public IReadOnlyList<Mo2VirtualEntry> Search(
        string query,
        int limit = 1500,
        CancellationToken cancellationToken = default)
    {
        var normalizedQuery = NormalizeRelativePath(query);
        limit = Math.Clamp(limit, 1, 2000);
        var results = new List<Mo2VirtualEntry>(Math.Min(limit, 1500));
        foreach (var pair in _files
                     .Where(item => normalizedQuery.Length == 0
                                    || item.Key.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase))
                     .OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var orderedProviders = pair.Value
                .OrderBy(provider => provider.Priority)
                .ToArray();
            var winner = orderedProviders[^1];
            var info = new FileInfo(winner.FullPath);
            var providers = orderedProviders
                .Select(provider => provider.ModName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (!string.IsNullOrWhiteSpace(_gameRoot)
                && File.Exists(Path.Combine(_gameRoot, pair.Key.Replace('/', Path.DirectorySeparatorChar))))
            {
                providers.Insert(0, BaseGameSource);
            }

            results.Add(new Mo2VirtualEntry(
                Path.GetFileName(pair.Key),
                pair.Key,
                false,
                winner.ModName,
                providers.Count,
                info.Exists ? info.Length : 0,
                info.Exists ? info.LastWriteTimeUtc : DateTime.MinValue,
                winner.FullPath)
            {
                Providers = providers,
            });
            if (results.Count >= limit)
            {
                return results;
            }
        }

        return results;
    }

    private void AddPhysicalEntries(string current, Dictionary<string, MutableVirtualEntry> entries)
    {
        if (string.IsNullOrWhiteSpace(_gameRoot) || !Directory.Exists(_gameRoot))
        {
            return;
        }

        var root = Path.GetFullPath(_gameRoot);
        var path = Path.GetFullPath(Path.Combine(root, current.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.Equals(root, StringComparison.OrdinalIgnoreCase)
            && !path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!Directory.Exists(path))
        {
            return;
        }

        foreach (var fileSystemEntry in Directory.EnumerateFileSystemEntries(path, "*", new EnumerationOptions { IgnoreInaccessible = true }))
        {
            var name = Path.GetFileName(fileSystemEntry);
            var isDirectory = Directory.Exists(fileSystemEntry);
            if (!entries.TryGetValue(name, out var entry))
            {
                entry = new MutableVirtualEntry(name, CombineRelative(current, name), isDirectory)
                {
                    Source = BaseGameSource,
                };
                entries[name] = entry;
            }

            entry.IsDirectory |= isDirectory;
            entry.Providers.Add(BaseGameSource);
            if (!isDirectory && entry.WinnerPriority == int.MinValue)
            {
                var info = new FileInfo(fileSystemEntry);
                entry.Size = info.Exists ? info.Length : 0;
                entry.LastWriteTimeUtc = info.Exists ? info.LastWriteTimeUtc : DateTime.MinValue;
                entry.FullPath = fileSystemEntry;
            }
        }
    }

    private static string NormalizeRelativePath(string path) =>
        path.Replace('\\', '/').Trim().Trim('/');

    private static string CombineRelative(string current, string name) =>
        current.Length == 0 ? name : $"{current}/{name}";

    private sealed record FileProvider(string ModName, int Priority, string FullPath);

    private sealed class MutableConflict(int fileCount)
    {
        public int FileCount { get; } = fileCount;
        public HashSet<string> Paths { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int Winning { get; set; }
        public int Losing { get; set; }
    }

    private sealed class MutableVirtualEntry(string name, string relativePath, bool isDirectory)
    {
        public string Name { get; } = name;
        public string RelativePath { get; } = relativePath;
        public bool IsDirectory { get; set; } = isDirectory;
        public HashSet<string> Providers { get; } = new(StringComparer.OrdinalIgnoreCase);
        public string Source { get; set; } = BaseGameSource;
        public int WinnerPriority { get; set; } = int.MinValue;
        public long Size { get; set; }
        public DateTime LastWriteTimeUtc { get; set; }
        public string? FullPath { get; set; }
    }
}

public static class Mo2WorkspaceReader
{
    private static readonly HashSet<string> ArchiveExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".7z", ".rar", ".001",
    };

    public static IReadOnlyList<Mo2DownloadEntry> ReadDownloads(string root)
    {
        var downloads = Path.Combine(Path.GetFullPath(root), "downloads");
        if (!Directory.Exists(downloads))
        {
            return [];
        }

        return Directory.EnumerateFiles(downloads, "*", SearchOption.TopDirectoryOnly)
            .Where(path => ArchiveExtensions.Contains(Path.GetExtension(path)))
            .Select(path => new FileInfo(path))
            .OrderByDescending(info => info.LastWriteTimeUtc)
            .Select(info => new Mo2DownloadEntry(
                info.Name,
                info.FullName,
                info.Length,
                info.LastWriteTimeUtc,
                File.Exists(info.FullName + ".meta")))
            .ToArray();
    }

    public static IReadOnlyList<Mo2SaveEntry> ReadSaves(string? gameRoot)
    {
        if (string.IsNullOrWhiteSpace(gameRoot))
        {
            return [];
        }

        var root = Path.GetFullPath(gameRoot);
        var candidates = new[]
        {
            Path.Combine(root, "appdata", "savedgames"),
            Path.Combine(root, "_appdata_", "savedgames"),
            Path.Combine(root, "savedgames"),
        };
        var saveDirectories = candidates.Where(Directory.Exists).ToArray();
        if (saveDirectories.Length == 0)
        {
            return [];
        }

        return saveDirectories
            .SelectMany(directory => Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
            .Where(path => Path.GetExtension(path).Equals(".scop", StringComparison.OrdinalIgnoreCase)
                           || Path.GetExtension(path).Equals(".scoc", StringComparison.OrdinalIgnoreCase))
            .GroupBy(
                path => Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path)),
                StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var parts = group.Select(path => new FileInfo(path)).ToArray();
                var persistent = parts.FirstOrDefault(info => info.Extension.Equals(".scop", StringComparison.OrdinalIgnoreCase));
                var container = parts.FirstOrDefault(info => info.Extension.Equals(".scoc", StringComparison.OrdinalIgnoreCase));
                var primary = persistent ?? container!;
                var saveName = Path.GetFileName(group.Key);
                var previewPath = group.Key + ".dds";
                return new Mo2SaveEntry(
                    primary.Name,
                    saveName,
                    primary.FullName,
                    parts.Sum(info => info.Length),
                    parts.Max(info => info.LastWriteTimeUtc),
                    File.Exists(previewPath) ? previewPath : null,
                    persistent is not null,
                    container is not null);
            })
            .OrderByDescending(save => save.LastWriteTimeUtc)
            .ToArray();
    }
}
