using System.IO;
using System.IO.Compression;
using System.Text;

namespace Anthology.Launcher;

public sealed record BugReportDiagnosticBundle(string Path, IReadOnlyList<string> IncludedFiles);

public sealed class BugReportDiagnosticBundleService(LauncherSettingsStore settingsStore)
{
    private const long MaximumBundleSize = 4_700_000;
    private const int MaximumLogTailBytes = 1_500_000;

    public BugReportDiagnosticBundle Create(string? gameRoot, string? mo2Root)
    {
        var destinationRoot = Path.Combine(settingsStore.DataRoot, "BugReports", "Automatic");
        Directory.CreateDirectory(destinationRoot);
        var destination = Path.Combine(destinationRoot, $"anthology-diagnostics-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.zip");
        var sources = CollectSources(gameRoot, mo2Root);
        WriteBundle(destination, sources, trimLog: false, gameRoot, mo2Root);
        if (new FileInfo(destination).Length > MaximumBundleSize)
        {
            File.Delete(destination);
            WriteBundle(destination, sources, trimLog: true, gameRoot, mo2Root);
        }

        if (new FileInfo(destination).Length > MaximumBundleSize)
        {
            File.Delete(destination);
            throw new InvalidDataException("Автоматический диагностический пакет превышает 5 МБ даже после сокращения xray-лога");
        }

        return new BugReportDiagnosticBundle(destination, sources.Select(source => source.EntryName).ToArray());
    }

    private static List<BundleSource> CollectSources(string? gameRoot, string? mo2Root)
    {
        var result = new List<BundleSource>();
        if (!string.IsNullOrWhiteSpace(gameRoot))
        {
            var root = Path.GetFullPath(gameRoot);
            AddFirst(result, "game/user.ltx", [
                Path.Combine(root, "appdata", "user.ltx"),
                Path.Combine(root, "_appdata_", "user.ltx"),
                Path.Combine(root, "user.ltx"),
            ]);
            AddFirst(result, "game/axr_options.ltx", [
                Path.Combine(root, "gamedata", "configs", "axr_options.ltx"),
            ]);

            var log = new[]
                {
                    Path.Combine(root, "appdata", "logs"),
                    Path.Combine(root, "_appdata_", "logs"),
                    Path.Combine(root, "logs"),
                }
                .Where(Directory.Exists)
                .SelectMany(directory => Directory.EnumerateFiles(directory, "*.log", SearchOption.TopDirectoryOnly))
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault(path => Path.GetFileName(path).Contains("xray", StringComparison.OrdinalIgnoreCase))
                ?? new[]
                    {
                        Path.Combine(root, "appdata", "logs"),
                        Path.Combine(root, "_appdata_", "logs"),
                        Path.Combine(root, "logs"),
                    }
                    .Where(Directory.Exists)
                    .SelectMany(directory => Directory.EnumerateFiles(directory, "*.log", SearchOption.TopDirectoryOnly))
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .FirstOrDefault();
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
                foreach (var profileRoot in Directory.EnumerateDirectories(profilesRoot).Order(StringComparer.OrdinalIgnoreCase))
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

    private static void WriteBundle(
        string destination,
        IReadOnlyList<BundleSource> sources,
        bool trimLog,
        string? gameRoot,
        string? mo2Root)
    {
        using var archive = ZipFile.Open(destination, ZipArchiveMode.Create);
        foreach (var source in sources)
        {
            if (source.IsLog && trimLog && new FileInfo(source.Path).Length > MaximumLogTailBytes)
            {
                var entry = archive.CreateEntry(source.EntryName, CompressionLevel.Optimal);
                using var output = entry.Open();
                using var input = new FileStream(
                    source.Path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                input.Seek(-MaximumLogTailBytes, SeekOrigin.End);
                input.CopyTo(output);
                continue;
            }

            var regularEntry = archive.CreateEntry(source.EntryName, CompressionLevel.Optimal);
            using var regularOutput = regularEntry.Open();
            using var regularInput = new FileStream(
                source.Path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            regularInput.CopyTo(regularOutput);
        }

        var manifest = archive.CreateEntry("diagnostics-manifest.txt", CompressionLevel.Optimal);
        using var manifestStream = manifest.Open();
        using var writer = new StreamWriter(manifestStream, new UTF8Encoding(false));
        writer.WriteLine($"Created UTC: {DateTime.UtcNow:O}");
        writer.WriteLine($"Game root: {gameRoot ?? "not detected"}");
        writer.WriteLine($"MO2 root: {mo2Root ?? "not detected"}");
        writer.WriteLine($"xray log trimmed: {trimLog}");
        writer.WriteLine("Included:");
        foreach (var source in sources)
        {
            writer.WriteLine($"- {source.EntryName}");
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
