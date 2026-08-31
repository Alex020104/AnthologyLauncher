using System.Globalization;
using System.Text;

namespace Anthology.Mo2.Core;

public sealed record Mo2ExecutableDefinition(
    string Title,
    string Binary,
    string Arguments,
    string WorkingDirectory);

public sealed record Mo2InstanceSnapshot(
    bool Available,
    string Root,
    string? SelectedProfile,
    IReadOnlyList<string> Profiles,
    IReadOnlyList<Mo2ExecutableDefinition> Executables,
    string StatusText,
    string? GamePath = null);

public sealed record Mo2ModEntry(
    string Name,
    bool Enabled,
    bool IsSeparator,
    bool IsUnmanaged,
    int Order,
    string DirectoryPath)
{
    private const string SeparatorSuffix = "_separator";

    public string DisplayName => IsSeparator && Name.EndsWith(SeparatorSuffix, StringComparison.OrdinalIgnoreCase)
        ? Name[..^SeparatorSuffix.Length]
        : Name;
}

public sealed record Mo2ProfileSnapshot(
    string Name,
    string ModListPath,
    IReadOnlyList<Mo2ModEntry> Mods);

public sealed record Mo2ProfileReconcileResult(
    Mo2ProfileSnapshot Profile,
    IReadOnlyList<string> RemovedMissingMods,
    IReadOnlyList<string> AddedDisabledMods,
    string? RecoveryPath)
{
    public bool Changed => RemovedMissingMods.Count > 0 || AddedDisabledMods.Count > 0;
}

public static class Mo2ProfileManager
{
    private const string OrganizerExecutable = "ModOrganizer.exe";
    private const string OrganizerConfiguration = "ModOrganizer.ini";
    private const string ModListFile = "modlist.txt";
    private static readonly (string FileName, string Title)[] AnomalyExecutables =
    [
        ("AnomalyDX11AVX.exe", "Anomaly (DX11-AVX)"),
        ("AnomalyDX11.exe", "Anomaly (DX11)"),
        ("AnomalyDX10AVX.exe", "Anomaly (DX10-AVX)"),
        ("AnomalyDX10.exe", "Anomaly (DX10)"),
        ("AnomalyDX9AVX.exe", "Anomaly (DX9-AVX)"),
        ("AnomalyDX9.exe", "Anomaly (DX9)"),
        ("AnomalyDX8AVX.exe", "Anomaly (DX8-AVX)"),
        ("AnomalyDX8.exe", "Anomaly (DX8)"),
    ];

    public static Mo2InstanceSnapshot Detect(string? root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return new Mo2InstanceSnapshot(false, string.Empty, null, [], [], "Папка MO2 не выбрана");
        }

        var fullRoot = Path.GetFullPath(root);
        if (!File.Exists(Path.Combine(fullRoot, OrganizerExecutable)))
        {
            return new Mo2InstanceSnapshot(false, fullRoot, null, [], [], "ModOrganizer.exe не найден");
        }

        var profilesRoot = Path.Combine(fullRoot, "profiles");
        var profiles = Directory.Exists(profilesRoot)
            ? Directory.GetDirectories(profilesRoot)
                .Where(path => File.Exists(Path.Combine(path, ModListFile)))
                .Select(Path.GetFileName)
                .OfType<string>()
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray()
            : [];

        var iniPath = Path.Combine(fullRoot, OrganizerConfiguration);
        var lines = File.Exists(iniPath) ? File.ReadAllLines(iniPath) : [];
        var selectedProfile = ReadSelectedProfile(lines);
        if (!profiles.Contains(selectedProfile, StringComparer.OrdinalIgnoreCase))
        {
            selectedProfile = profiles.FirstOrDefault();
        }

        var executables = ReadExecutables(lines);
        var gamePath = ReadGeneralValue(lines, "gamePath");
        var status = profiles.Length == 0
            ? "MO2 найден, но профили с modlist.txt отсутствуют"
            : executables.Length == 0
                ? "Профили найдены, но исполняемые файлы MO2 не настроены"
                : $"Профилей: {profiles.Length} · запусков: {executables.Length}";
        return new Mo2InstanceSnapshot(true, fullRoot, selectedProfile, profiles, executables, status, gamePath);
    }

    /// <summary>
    /// Makes a distributed MO2 directory a self-contained portable instance.
    /// The releaser deliberately does not manage ModOrganizer.ini because it is
    /// machine-local state. On a fresh PC the launcher therefore has to create
    /// it once before starting MO2, otherwise the instance/game wizard appears.
    /// </summary>
    /// <returns>True when a missing or incomplete configuration was rebuilt.</returns>
    public static bool EnsurePortableConfiguration(
        string root,
        string gameRoot,
        string? preferredProfile = null)
    {
        var fullRoot = Path.GetFullPath(root);
        var fullGameRoot = Path.GetFullPath(gameRoot);
        var organizer = Path.Combine(fullRoot, OrganizerExecutable);
        var gameBin = Path.Combine(fullGameRoot, "bin");
        if (!File.Exists(organizer))
        {
            throw new FileNotFoundException("ModOrganizer.exe не найден.", organizer);
        }
        if (!Directory.Exists(gameBin))
        {
            throw new DirectoryNotFoundException($"Корень Anomaly не найден: {fullGameRoot}");
        }

        var profilesRoot = Path.Combine(fullRoot, "profiles");
        var profiles = Directory.Exists(profilesRoot)
            ? Directory.GetDirectories(profilesRoot)
                .Where(path => File.Exists(Path.Combine(path, ModListFile)))
                .Select(Path.GetFileName)
                .OfType<string>()
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray()
            : [];
        if (profiles.Length == 0)
        {
            throw new DirectoryNotFoundException($"В portable-сборке MO2 не найден профиль с {ModListFile}: {profilesRoot}");
        }

        var selectedProfile = profiles.FirstOrDefault(profile =>
                                  string.Equals(profile, preferredProfile, StringComparison.OrdinalIgnoreCase))
                              ?? profiles[0];
        var iniPath = Path.Combine(fullRoot, OrganizerConfiguration);
        var rebuild = !File.Exists(iniPath);
        if (!rebuild)
        {
            var current = Detect(fullRoot);
            rebuild = current.Executables.Count == 0
                      || string.IsNullOrWhiteSpace(current.GamePath)
                      || !File.ReadLines(iniPath).Any(line =>
                          line.Equals("gameName=STALKER Anomaly", StringComparison.OrdinalIgnoreCase));
        }

        if (rebuild)
        {
            var executables = AnomalyExecutables
                .Where(item => File.Exists(Path.Combine(gameBin, item.FileName)))
                .ToArray();
            if (executables.Length == 0)
            {
                throw new FileNotFoundException("В папке bin не найдены исполняемые файлы Anomaly.", gameBin);
            }

            var lines = new List<string>
            {
                "[General]",
                "gameName=STALKER Anomaly",
                $"gamePath={EncodeQtByteArray(fullGameRoot)}",
                $"selected_profile={EncodeQtByteArray(selectedProfile)}",
                "first_start=false",
                string.Empty,
                "[customExecutables]",
                $"size={executables.Length}",
            };
            for (var index = 0; index < executables.Length; index++)
            {
                var number = index + 1;
                var executable = executables[index];
                lines.Add($"{number}\\arguments=");
                lines.Add($"{number}\\binary={ToIniPath(Path.Combine(gameBin, executable.FileName))}");
                lines.Add($"{number}\\hide=false");
                lines.Add($"{number}\\ownicon=true");
                lines.Add($"{number}\\steamAppID=");
                lines.Add($"{number}\\title={executable.Title}");
                lines.Add($"{number}\\toolbar=false");
                lines.Add($"{number}\\workingDirectory={ToIniPath(gameBin)}");
            }

            if (File.Exists(iniPath))
            {
                File.Copy(iniPath, iniPath + ".anthology-backup", true);
            }
            WriteNewAtomic(iniPath, lines);
            return true;
        }

        RebaseGamePaths(fullRoot, fullGameRoot);
        SetSelectedProfile(fullRoot, selectedProfile);
        return false;
    }

    public static Mo2ProfileSnapshot ReadProfile(string root, string profileName)
    {
        var profileRoot = ResolveProfileRoot(root, profileName);
        var modListPath = Path.Combine(profileRoot, ModListFile);
        var parsed = new List<(string Name, bool Enabled, bool Unmanaged)>();
        foreach (var line in File.ReadAllLines(modListPath))
        {
            if (!TryParseModLine(line, out var name, out var enabled, out var unmanaged))
            {
                continue;
            }

            parsed.Add((name, enabled, unmanaged));
        }

        // MO2 writes modlist.txt from highest to lowest priority, while its left pane
        // displays priorities from lowest to highest. Mirror the actual MO2 view.
        var modsRoot = Path.Combine(Path.GetFullPath(root), "mods");
        var mods = parsed
            .AsEnumerable()
            .Reverse()
            .Select((item, order) => new Mo2ModEntry(
                item.Name,
                item.Enabled,
                item.Name.EndsWith("_separator", StringComparison.OrdinalIgnoreCase),
                item.Unmanaged,
                order,
                Path.Combine(modsRoot, item.Name)))
            .ToArray();

        return new Mo2ProfileSnapshot(profileName, modListPath, mods);
    }

    public static Mo2ProfileReconcileResult ReconcileProfile(string root, string profileName)
    {
        var profileRoot = ResolveProfileRoot(root, profileName);
        var modListPath = Path.Combine(profileRoot, ModListFile);
        var modsRoot = Path.Combine(Path.GetFullPath(root), "mods");
        if (!Directory.Exists(modsRoot))
        {
            return new Mo2ProfileReconcileResult(ReadProfile(root, profileName), [], [], null);
        }

        var directoryNames = Directory.GetDirectories(modsRoot)
            .Select(Path.GetFileName)
            .OfType<string>()
            .Where(IsManagedModDirectoryName)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var directorySet = new HashSet<string>(directoryNames, StringComparer.OrdinalIgnoreCase);
        var listedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var removedMissing = new List<string>();
        var reconciledLines = new List<string>();

        foreach (var line in File.ReadAllLines(modListPath))
        {
            if (!TryParseModLine(line, out var name, out _, out var unmanaged))
            {
                reconciledLines.Add(line);
                continue;
            }

            if (!unmanaged && (!IsManagedModDirectoryName(name) || !directorySet.Contains(name)))
            {
                removedMissing.Add(name);
                continue;
            }

            reconciledLines.Add(line);
            if (!unmanaged)
            {
                listedDirectories.Add(name);
            }
        }

        var addedDisabled = directoryNames
            .Where(name => !listedDirectories.Contains(name))
            .ToArray();
        if (removedMissing.Count == 0 && addedDisabled.Length == 0)
        {
            return new Mo2ProfileReconcileResult(ReadProfile(root, profileName), [], [], null);
        }

        var insertIndex = reconciledLines.FindIndex(line => TryParseModLine(line, out _, out _, out _));
        if (insertIndex < 0)
        {
            insertIndex = reconciledLines.Count;
        }
        reconciledLines.InsertRange(insertIndex, addedDisabled.Select(name => $"-{name}"));

        var recoveryPath = modListPath + $".anthology-reconcile-{DateTime.UtcNow:yyyyMMdd-HHmmss}.bak";
        File.Copy(modListPath, recoveryPath, overwrite: false);
        WriteAtomicWithBackup(modListPath, reconciledLines);
        return new Mo2ProfileReconcileResult(
            ReadProfile(root, profileName),
            removedMissing.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            addedDisabled,
            recoveryPath);
    }

    private static bool IsManagedModDirectoryName(string name) =>
        !string.IsNullOrWhiteSpace(name)
        && name[0] != '.'
        && !name.Equals("__MACOSX", StringComparison.OrdinalIgnoreCase);

    public static Mo2ProfileSnapshot SetEnabled(string root, string profileName, string modName, bool enabled)
    {
        return Mutate(root, profileName, lines =>
        {
            var index = FindModLine(lines, modName);
            if (index < 0)
            {
                throw new InvalidOperationException($"Мод не найден в профиле: {modName}");
            }

            if (lines[index].StartsWith('*'))
            {
                throw new InvalidOperationException("Неуправляемый мод нельзя отключить через modlist.txt.");
            }

            lines[index] = (enabled ? "+" : "-") + lines[index][1..];
        });
    }

    public static Mo2ProfileSnapshot Move(string root, string profileName, string modName, int direction)
    {
        if (direction is not (-1 or 1))
        {
            throw new ArgumentOutOfRangeException(nameof(direction), "Направление должно быть -1 или 1.");
        }

        return Mutate(root, profileName, lines =>
        {
            var index = FindModLine(lines, modName);
            if (index < 0)
            {
                throw new InvalidOperationException($"Мод не найден в профиле: {modName}");
            }

            // File order is the inverse of priority order shown by MO2.
            var other = index - direction;
            while (other >= 0 && other < lines.Count && !TryParseModLine(lines[other], out _, out _, out _))
            {
                other -= direction;
            }

            if (other < 0 || other >= lines.Count)
            {
                return;
            }

            (lines[index], lines[other]) = (lines[other], lines[index]);
        });
    }

    public static Mo2ProfileSnapshot MoveTo(
        string root,
        string profileName,
        string modName,
        string targetModName)
    {
        if (string.Equals(modName, targetModName, StringComparison.Ordinal))
        {
            return ReadProfile(root, profileName);
        }

        return Mutate(root, profileName, lines =>
        {
            var parsedSlots = lines
                .Select((line, index) => new { Line = line, Index = index })
                .Where(item => TryParseModLine(item.Line, out _, out _, out _))
                .Select(item => item.Index)
                .ToArray();
            var displayLines = parsedSlots
                .Select(index => lines[index])
                .Reverse()
                .ToList();

            var sourceIndex = displayLines.FindIndex(line =>
                TryParseModLine(line, out var name, out _, out _)
                && string.Equals(name, modName, StringComparison.Ordinal));
            var targetIndex = displayLines.FindIndex(line =>
                TryParseModLine(line, out var name, out _, out _)
                && string.Equals(name, targetModName, StringComparison.Ordinal));
            if (sourceIndex < 0 || targetIndex < 0)
            {
                throw new InvalidOperationException("Перетаскиваемый мод или место назначения не найдено в профиле.");
            }

            var sourceLine = displayLines[sourceIndex];
            displayLines.RemoveAt(sourceIndex);
            displayLines.Insert(Math.Clamp(targetIndex, 0, displayLines.Count), sourceLine);

            var fileOrder = displayLines.AsEnumerable().Reverse().ToArray();
            for (var index = 0; index < parsedSlots.Length; index++)
            {
                lines[parsedSlots[index]] = fileOrder[index];
            }
        });
    }

    public static void SetSelectedProfile(string root, string profileName)
    {
        ResolveProfileRoot(root, profileName);
        var path = Path.Combine(Path.GetFullPath(root), OrganizerConfiguration);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("ModOrganizer.ini не найден.", path);
        }

        var lines = File.ReadAllLines(path).ToList();
        var index = lines.FindIndex(line => line.StartsWith("selected_profile=", StringComparison.OrdinalIgnoreCase));
        var value = $"selected_profile={EncodeQtByteArray(profileName)}";
        if (index >= 0 && string.Equals(lines[index], value, StringComparison.Ordinal))
        {
            return;
        }

        if (index >= 0)
        {
            lines[index] = value;
        }
        else
        {
            var general = lines.FindIndex(line => string.Equals(line.Trim(), "[General]", StringComparison.OrdinalIgnoreCase));
            lines.Insert(general >= 0 ? general + 1 : 0, value);
        }

        var backup = path + ".anthology-backup";
        File.Copy(path, backup, true);
        var temporary = path + $".tmp-{Guid.NewGuid():N}";
        try
        {
            File.WriteAllLines(temporary, lines, new UTF8Encoding(false));
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    public static void RebaseGamePaths(string root, string gameRoot)
    {
        var fullRoot = Path.GetFullPath(root);
        var fullGameRoot = Path.GetFullPath(gameRoot);
        var gameBin = Path.Combine(fullGameRoot, "bin");
        if (!Directory.Exists(fullGameRoot) || !Directory.Exists(gameBin))
        {
            throw new DirectoryNotFoundException($"Корень Anomaly не найден: {fullGameRoot}");
        }

        var path = Path.Combine(fullRoot, OrganizerConfiguration);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("ModOrganizer.ini не найден.", path);
        }

        var lines = File.ReadAllLines(path).ToList();
        var changed = false;
        var gamePathLine = lines.FindIndex(line => line.StartsWith("gamePath=", StringComparison.OrdinalIgnoreCase));
        var encodedGamePath = $"gamePath={EncodeQtByteArray(fullGameRoot)}";
        if (gamePathLine >= 0)
        {
            if (!string.Equals(lines[gamePathLine], encodedGamePath, StringComparison.Ordinal))
            {
                lines[gamePathLine] = encodedGamePath;
                changed = true;
            }
        }
        else
        {
            var general = lines.FindIndex(line => string.Equals(line.Trim(), "[General]", StringComparison.OrdinalIgnoreCase));
            lines.Insert(general >= 0 ? general + 1 : 0, encodedGamePath);
            changed = true;
        }

        var rebasedExecutables = new HashSet<int>();
        var inExecutables = false;
        for (var lineIndex = 0; lineIndex < lines.Count; lineIndex++)
        {
            var line = lines[lineIndex].Trim();
            if (line.StartsWith('['))
            {
                inExecutables = string.Equals(line, "[customExecutables]", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (!inExecutables || !TryReadExecutableSetting(line, out var executableIndex, out var field, out var value)
                || !field.Equals("binary", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var fileName = Path.GetFileName(NormalizeIniPath(value));
            if (!fileName.StartsWith("Anomaly", StringComparison.OrdinalIgnoreCase)
                || !fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var target = Path.Combine(gameBin, fileName);
            if (!File.Exists(target))
            {
                continue;
            }

            rebasedExecutables.Add(executableIndex);
            var replacement = $"{executableIndex}\\binary={ToIniPath(target)}";
            if (!string.Equals(lines[lineIndex], replacement, StringComparison.Ordinal))
            {
                lines[lineIndex] = replacement;
                changed = true;
            }
        }

        inExecutables = false;
        for (var lineIndex = 0; lineIndex < lines.Count; lineIndex++)
        {
            var line = lines[lineIndex].Trim();
            if (line.StartsWith('['))
            {
                inExecutables = string.Equals(line, "[customExecutables]", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (!inExecutables || !TryReadExecutableSetting(line, out var executableIndex, out var field, out _)
                || !field.Equals("workingDirectory", StringComparison.OrdinalIgnoreCase)
                || !rebasedExecutables.Contains(executableIndex))
            {
                continue;
            }

            var replacement = $"{executableIndex}\\workingDirectory={ToIniPath(gameBin)}";
            if (!string.Equals(lines[lineIndex], replacement, StringComparison.Ordinal))
            {
                lines[lineIndex] = replacement;
                changed = true;
            }
        }

        if (changed)
        {
            WriteAtomicWithBackup(path, lines);
        }
    }

    public static void AddMod(string root, string profileName, string modName, bool enabled)
    {
        var profileRoot = ResolveProfileRoot(root, profileName);
        var path = Path.Combine(profileRoot, ModListFile);
        var lines = File.ReadAllLines(path).ToList();
        if (FindModLine(lines, modName) >= 0)
        {
            return;
        }

        lines.Insert(0, (enabled ? "+" : "-") + modName);
        WriteAtomicWithBackup(path, lines);
    }

    public static void CreateProfile(string root, string profileName, string? sourceProfile)
    {
        ValidateSimpleName(profileName, "профиля");
        var fullRoot = Path.GetFullPath(root);
        var profilesRoot = Path.GetFullPath(Path.Combine(fullRoot, "profiles"));
        Directory.CreateDirectory(profilesRoot);
        var destination = Path.GetFullPath(Path.Combine(profilesRoot, profileName));
        if (!destination.StartsWith(profilesRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Путь профиля выходит за пределы MO2.");
        }
        if (Directory.Exists(destination))
        {
            throw new InvalidOperationException($"Профиль уже существует: {profileName}");
        }

        Directory.CreateDirectory(destination);
        try
        {
            if (!string.IsNullOrWhiteSpace(sourceProfile))
            {
                var source = ResolveProfileRoot(root, sourceProfile);
                foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.TopDirectoryOnly))
                {
                    File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: false);
                }
            }

            var modList = Path.Combine(destination, ModListFile);
            if (!File.Exists(modList))
            {
                File.WriteAllText(modList, "# Generated by Anthology Next" + Environment.NewLine, new UTF8Encoding(false));
            }
        }
        catch
        {
            Directory.Delete(destination, true);
            throw;
        }
    }

    public static string AddSeparator(string root, string profileName, string displayName)
    {
        ValidateSimpleName(displayName, "разделителя");
        var folderName = displayName.EndsWith("_separator", StringComparison.OrdinalIgnoreCase)
            ? displayName
            : displayName + "_separator";
        var modsRoot = Path.GetFullPath(Path.Combine(Path.GetFullPath(root), "mods"));
        Directory.CreateDirectory(modsRoot);
        var path = Path.GetFullPath(Path.Combine(modsRoot, folderName));
        if (!path.StartsWith(modsRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Путь разделителя выходит за пределы mods.");
        }
        Directory.CreateDirectory(path);
        AddMod(root, profileName, folderName, enabled: true);
        return folderName;
    }

    private static Mo2ProfileSnapshot Mutate(string root, string profileName, Action<List<string>> mutation)
    {
        var profileRoot = ResolveProfileRoot(root, profileName);
        var path = Path.Combine(profileRoot, ModListFile);
        var lines = File.ReadAllLines(path).ToList();
        mutation(lines);

        WriteAtomicWithBackup(path, lines);

        return ReadProfile(root, profileName);
    }

    private static void WriteAtomicWithBackup(string path, IReadOnlyCollection<string> lines)
    {
        var backup = path + ".anthology-backup";
        File.Copy(path, backup, true);
        var temporary = path + $".tmp-{Guid.NewGuid():N}";
        try
        {
            File.WriteAllLines(temporary, lines, new UTF8Encoding(false));
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static void WriteNewAtomic(string path, IReadOnlyCollection<string> lines)
    {
        var temporary = path + $".tmp-{Guid.NewGuid():N}";
        try
        {
            File.WriteAllLines(temporary, lines, new UTF8Encoding(false));
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static string ResolveProfileRoot(string root, string profileName)
    {
        var fullRoot = Path.GetFullPath(root);
        var profilesRoot = Path.GetFullPath(Path.Combine(fullRoot, "profiles"));
        if (!Directory.Exists(profilesRoot))
        {
            throw new DirectoryNotFoundException($"Каталог профилей MO2 не найден: {profilesRoot}");
        }

        var match = Directory.GetDirectories(profilesRoot)
            .FirstOrDefault(path => string.Equals(Path.GetFileName(path), profileName, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            throw new DirectoryNotFoundException($"Профиль MO2 не найден: {profileName}");
        }

        var fullMatch = Path.GetFullPath(match);
        if (!fullMatch.StartsWith(profilesRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Путь профиля выходит за пределы каталога MO2.");
        }

        if (!File.Exists(Path.Combine(fullMatch, ModListFile)))
        {
            throw new FileNotFoundException("В профиле отсутствует modlist.txt.", Path.Combine(fullMatch, ModListFile));
        }

        return fullMatch;
    }

    private static void ValidateSimpleName(string value, string subject)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value is "." or ".."
            || value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || value.Contains(Path.DirectorySeparatorChar)
            || value.Contains(Path.AltDirectorySeparatorChar))
        {
            throw new InvalidDataException($"Некорректное имя {subject}.");
        }
    }

    private static int FindModLine(List<string> lines, string modName)
    {
        for (var index = 0; index < lines.Count; index++)
        {
            if (TryParseModLine(lines[index], out var currentName, out _, out _)
                && string.Equals(currentName, modName, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }

    private static bool TryParseModLine(string line, out string name, out bool enabled, out bool unmanaged)
    {
        name = string.Empty;
        enabled = false;
        unmanaged = false;
        if (string.IsNullOrWhiteSpace(line) || line.Length < 2 || line[0] is not ('+' or '-' or '*'))
        {
            return false;
        }

        name = line[1..];
        enabled = line[0] is '+' or '*';
        unmanaged = line[0] == '*';
        return name.Length > 0;
    }

    private static string? ReadSelectedProfile(IEnumerable<string> lines)
    {
        var value = lines.FirstOrDefault(line => line.StartsWith("selected_profile=", StringComparison.OrdinalIgnoreCase));
        if (value is null)
        {
            return null;
        }

        return DecodeQtByteArray(value[(value.IndexOf('=') + 1)..]);
    }

    private static string? ReadGeneralValue(IEnumerable<string> lines, string key)
    {
        var prefix = key + "=";
        var value = lines.FirstOrDefault(line => line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        if (value is null)
        {
            return null;
        }

        var decoded = DecodeQtByteArray(value[prefix.Length..]);
        return string.IsNullOrWhiteSpace(decoded) ? null : decoded.Replace('\\', Path.DirectorySeparatorChar);
    }

    private static Mo2ExecutableDefinition[] ReadExecutables(IEnumerable<string> lines)
    {
        var inSection = false;
        var values = new Dictionary<int, Dictionary<string, string>>();
        foreach (var sourceLine in lines)
        {
            var line = sourceLine.Trim();
            if (line.StartsWith('['))
            {
                inSection = string.Equals(line, "[customExecutables]", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (!inSection)
            {
                continue;
            }

            var equals = line.IndexOf('=');
            var slash = line.IndexOf('\\');
            if (slash <= 0 || equals <= slash
                || !int.TryParse(line[..slash], NumberStyles.None, CultureInfo.InvariantCulture, out var index))
            {
                continue;
            }

            if (!values.TryGetValue(index, out var executable))
            {
                executable = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                values[index] = executable;
            }

            executable[line[(slash + 1)..equals]] = line[(equals + 1)..];
        }

        return values.OrderBy(pair => pair.Key)
            .Select(pair => pair.Value)
            .Where(value => value.TryGetValue("title", out var title) && !string.IsNullOrWhiteSpace(title))
            .Select(value => new Mo2ExecutableDefinition(
                value["title"],
                NormalizeIniPath(value.GetValueOrDefault("binary")),
                value.GetValueOrDefault("arguments") ?? string.Empty,
                NormalizeIniPath(value.GetValueOrDefault("workingDirectory"))))
            .ToArray();
    }

    private static bool TryReadExecutableSetting(
        string line,
        out int executableIndex,
        out string field,
        out string value)
    {
        executableIndex = 0;
        field = string.Empty;
        value = string.Empty;
        var slash = line.IndexOf('\\');
        var equals = line.IndexOf('=');
        if (slash <= 0 || equals <= slash
            || !int.TryParse(line[..slash], NumberStyles.None, CultureInfo.InvariantCulture, out executableIndex))
        {
            return false;
        }

        field = line[(slash + 1)..equals];
        value = line[(equals + 1)..];
        return true;
    }

    private static string NormalizeIniPath(string? value) =>
        (value ?? string.Empty).Replace('/', Path.DirectorySeparatorChar);

    private static string ToIniPath(string value) =>
        value.Replace(Path.DirectorySeparatorChar, '/');

    internal static string DecodeQtByteArray(string value)
    {
        const string prefix = "@ByteArray(";
        if (!value.StartsWith(prefix, StringComparison.Ordinal) || !value.EndsWith(')'))
        {
            return value;
        }

        var bytes = new List<byte>();
        var content = value[prefix.Length..^1];
        for (var index = 0; index < content.Length; index++)
        {
            if (content[index] == '\\' && index + 3 < content.Length && content[index + 1] == 'x'
                && byte.TryParse(content.AsSpan(index + 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var parsed))
            {
                bytes.Add(parsed);
                index += 3;
            }
            else if (content[index] == '\\' && index + 1 < content.Length && content[index + 1] == '\\')
            {
                bytes.Add((byte)'\\');
                index++;
            }
            else
            {
                bytes.AddRange(Encoding.UTF8.GetBytes(content[index].ToString()));
            }
        }

        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    internal static string EncodeQtByteArray(string value)
    {
        var builder = new StringBuilder("@ByteArray(");
        foreach (var valueByte in Encoding.UTF8.GetBytes(value))
        {
            if (valueByte is >= 0x20 and <= 0x7E && valueByte is not (byte)'\\' and not (byte)')')
            {
                builder.Append((char)valueByte);
            }
            else
            {
                builder.Append("\\x");
                builder.Append(valueByte.ToString("x2", CultureInfo.InvariantCulture));
            }
        }

        return builder.Append(')').ToString();
    }
}
