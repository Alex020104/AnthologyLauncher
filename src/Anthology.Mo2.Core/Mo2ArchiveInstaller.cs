using System.Text;
using SharpCompress.Readers;

namespace Anthology.Mo2.Core;

public sealed record Mo2ArchiveInstallResult(bool Success, string Message, string? ModName = null);

public static class Mo2ArchiveInstaller
{
    public static Mo2ArchiveInstallResult Install(
        string root,
        string profileName,
        string archivePath,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(archivePath))
        {
            return new Mo2ArchiveInstallResult(false, $"Архив не найден: {archivePath}");
        }

        var supportedExtensions = new[] { ".zip", ".7z", ".rar", ".001" };
        if (!supportedExtensions.Contains(Path.GetExtension(archivePath), StringComparer.OrdinalIgnoreCase))
        {
            return new Mo2ArchiveInstallResult(false, "Поддерживаются архивы ZIP, 7z и RAR.");
        }

        var fullRoot = Path.GetFullPath(root);
        var modsRoot = Path.GetFullPath(Path.Combine(fullRoot, "mods"));
        Directory.CreateDirectory(modsRoot);
        var modName = SanitizeModName(Path.GetFileNameWithoutExtension(archivePath));
        var destination = Path.GetFullPath(Path.Combine(modsRoot, modName));
        if (!destination.StartsWith(modsRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            return new Mo2ArchiveInstallResult(false, "Некорректное имя архива.");
        }

        if (Directory.Exists(destination))
        {
            return new Mo2ArchiveInstallResult(false, $"Мод уже существует: {modName}");
        }

        var fileKeys = ReadFileKeys(archivePath, cancellationToken);
        if (fileKeys.Count == 0)
        {
            return new Mo2ArchiveInstallResult(false, "В архиве нет файлов.");
        }

        if (fileKeys.Any(key => key.EndsWith("fomod/moduleconfig.xml", StringComparison.OrdinalIgnoreCase)))
        {
            return new Mo2ArchiveInstallResult(false, "Архив использует мастер FOMOD. Он распознан и не будет установлен как обычный мод без выбора компонентов.");
        }

        var prefix = FindWrapperPrefix(fileKeys);
        var staging = Path.Combine(modsRoot, $".__anthology_install_{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        try
        {
            using var reader = ReaderFactory.OpenReader(archivePath);
            while (reader.MoveToNextEntry())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (reader.Entry.IsDirectory)
                {
                    continue;
                }

                var relative = Normalize(reader.Entry.Key ?? string.Empty);
                if (prefix.Length > 0 && relative.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    relative = relative[prefix.Length..];
                }

                relative = relative.Trim('/');
                if (relative.Length == 0)
                {
                    continue;
                }

                var target = Path.GetFullPath(Path.Combine(staging, relative.Replace('/', Path.DirectorySeparatorChar)));
                if (!target.StartsWith(staging + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException($"Архив содержит опасный путь: {reader.Entry.Key}");
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using var source = reader.OpenEntryStream();
                using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                source.CopyTo(output);
            }

            File.WriteAllText(
                Path.Combine(staging, "meta.ini"),
                $"[General]{Environment.NewLine}gameName=STALKER Anomaly{Environment.NewLine}modid=0{Environment.NewLine}version=local{Environment.NewLine}",
                new UTF8Encoding(false));
            Directory.Move(staging, destination);
            Mo2ProfileManager.AddMod(root, profileName, modName, enabled: true);
            return new Mo2ArchiveInstallResult(true, $"Мод установлен и включён: {modName}", modName);
        }
        catch
        {
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, true);
            }
            if (Directory.Exists(destination))
            {
                Directory.Delete(destination, true);
            }
            throw;
        }
    }

    private static List<string> ReadFileKeys(string archivePath, CancellationToken cancellationToken)
    {
        var keys = new List<string>();
        using var reader = ReaderFactory.OpenReader(archivePath);
        while (reader.MoveToNextEntry())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!reader.Entry.IsDirectory)
            {
                var key = Normalize(reader.Entry.Key ?? string.Empty);
                if (key.Length > 0)
                {
                    keys.Add(key);
                }
            }
        }
        return keys;
    }

    private static string FindWrapperPrefix(IEnumerable<string> paths)
    {
        var values = paths.Where(path => path.Length > 0).ToArray();
        var firstSegments = values
            .Select(path => path.Split('/', 2)[0])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (firstSegments.Length != 1 || firstSegments[0].Equals("gamedata", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        var prefix = firstSegments[0] + "/";
        return values.Any(path => path.StartsWith(prefix + "gamedata/", StringComparison.OrdinalIgnoreCase))
            ? prefix
            : string.Empty;
    }

    private static string SanitizeModName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? $"Anthology Mod {DateTime.Now:yyyyMMdd-HHmmss}" : cleaned;
    }

    private static string Normalize(string path) => path.Replace('\\', '/').TrimStart('/');
}
