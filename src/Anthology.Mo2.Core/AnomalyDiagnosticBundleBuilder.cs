using System.IO.Compression;
using System.Text;

namespace Anthology.Mo2.Core;

public sealed record AnomalyDiagnosticBundle(string Path, IReadOnlyList<string> IncludedFiles);

public static class AnomalyDiagnosticBundleBuilder
{
    private const long MaximumBundleSize = 4_700_000;
    private const int MaximumLogTailBytes = 1_500_000;

    public static AnomalyDiagnosticBundle Create(string destinationRoot, string? gameRoot, string? mo2Root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationRoot);
        var outputRoot = Path.GetFullPath(destinationRoot);
        Directory.CreateDirectory(outputRoot);
        var destination = Path.Combine(
            outputRoot,
            $"anthology-diagnostics-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.zip");
        var sources = CollectSources(gameRoot, mo2Root);
        var included = WriteBundle(destination, sources, trimLog: false, gameRoot, mo2Root);
        if (new FileInfo(destination).Length > MaximumBundleSize)
        {
            File.Delete(destination);
            included = WriteBundle(destination, sources, trimLog: true, gameRoot, mo2Root);
        }

        if (new FileInfo(destination).Length > MaximumBundleSize)
        {
            File.Delete(destination);
            throw new InvalidDataException(
                "Автоматический диагностический пакет превышает 5 МБ даже после сокращения xray-лога");
        }

        return new AnomalyDiagnosticBundle(destination, included);
    }

    private static List<BundleSource> CollectSources(string? gameRoot, string? mo2Root)
    {
        var result = new List<BundleSource>();
        if (!string.IsNullOrWhiteSpace(gameRoot))
        {
            var root = Path.GetFullPath(gameRoot);
            AddFirst(result, "game/user.ltx",
            [
                Path.Combine(root, "appdata", "user.ltx"),
                Path.Combine(root, "_appdata_", "user.ltx"),
                Path.Combine(root, "user.ltx"),
            ]);
            AddFirst(result, "game/axr_options.ltx",
            [
                Path.Combine(root, "gamedata", "configs", "axr_options.ltx"),
            ]);

            var logs = new[]
                {
                    Path.Combine(root, "appdata", "logs"),
                    Path.Combine(root, "_appdata_", "logs"),
                    Path.Combine(root, "logs"),
                }
                .Where(Directory.Exists)
                .SelectMany(EnumerateLogs)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .ToArray();
            var log = logs.FirstOrDefault(path =>
                          Path.GetFileName(path).Contains("xray", StringComparison.OrdinalIgnoreCase))
                      ?? logs.FirstOrDefault();
            if (log is not null)
            {
                result.Add(new BundleSource(log, "game/xray_latest.log", IsLog: true));
            }
        }

        if (!string.IsNullOrWhiteSpace(mo2Root))
        {
            var profilesRoot = Path.Combine(Path.GetFullPath(mo2Root), "profiles");
            if (Directory.Exists(profilesRoot))
            {
                foreach (var profileRoot in EnumerateDirectories(profilesRoot).Order(StringComparer.OrdinalIgnoreCase))
                {
                    var profileName = SafeEntryName(Path.GetFileName(profileRoot));
                    foreach (var fileName in new[] { "modlist.txt", "user.ltx", "axr_options.ltx" })
                    {
                        var profileFile = Path.Combine(profileRoot, fileName);
                        if (File.Exists(profileFile))
                        {
                            result.Add(new BundleSource(
                                profileFile,
                                $"mo2-profiles/{profileName}/{fileName}",
                                IsLog: false));
                        }
                    }
                }
            }
        }

        return result;
    }

    private static List<string> WriteBundle(
        string destination,
        IReadOnlyList<BundleSource> sources,
        bool trimLog,
        string? gameRoot,
        string? mo2Root)
    {
        var included = new List<string>();
        var skipped = new List<string>();
        using var archive = ZipFile.Open(destination, ZipArchiveMode.Create);
        foreach (var source in sources)
        {
            try
            {
                using var input = new FileStream(
                    source.Path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                var shouldTrim = source.IsLog && trimLog && input.Length > MaximumLogTailBytes;
                if (shouldTrim)
                {
                    input.Seek(-MaximumLogTailBytes, SeekOrigin.End);
                }

                var entry = archive.CreateEntry(source.EntryName, CompressionLevel.Optimal);
                using var output = entry.Open();
                input.CopyTo(output);
                included.Add(source.EntryName);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                skipped.Add($"{source.EntryName}: {exception.GetType().Name}");
            }
        }

        var manifest = archive.CreateEntry("diagnostics-manifest.txt", CompressionLevel.Optimal);
        using var manifestStream = manifest.Open();
        using var writer = new StreamWriter(manifestStream, new UTF8Encoding(false));
        writer.WriteLine($"Created UTC: {DateTime.UtcNow:O}");
        writer.WriteLine($"Game root: {gameRoot ?? "not detected"}");
        writer.WriteLine($"MO2 root: {mo2Root ?? "not detected"}");
        writer.WriteLine($"xray log trimmed: {trimLog}");
        writer.WriteLine("Included:");
        foreach (var entryName in included)
        {
            writer.WriteLine($"- {entryName}");
        }
        if (skipped.Count > 0)
        {
            writer.WriteLine("Skipped:");
            foreach (var entryName in skipped)
            {
                writer.WriteLine($"- {entryName}");
            }
        }

        return included;
    }

    private static string[] EnumerateLogs(string directory)
    {
        try
        {
            return Directory.EnumerateFiles(directory, "*.log", SearchOption.TopDirectoryOnly).ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static string[] EnumerateDirectories(string directory)
    {
        try
        {
            return Directory.EnumerateDirectories(directory).ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static void AddFirst(List<BundleSource> result, string entryName, IEnumerable<string> candidates)
    {
        var path = candidates.FirstOrDefault(File.Exists);
        if (path is not null)
        {
            result.Add(new BundleSource(path, entryName, IsLog: false));
        }
    }

    private static string SafeEntryName(string value) => string.Concat(value.Select(character =>
        character is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|' ? '_' : character));

    private sealed record BundleSource(string Path, string EntryName, bool IsLog);
}
