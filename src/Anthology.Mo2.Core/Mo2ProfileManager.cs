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
    string StatusText);

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

public static class Mo2ProfileManager
{
    private const string OrganizerExecutable = "ModOrganizer.exe";
    private const string OrganizerConfiguration = "ModOrganizer.ini";
    private const string ModListFile = "modlist.txt";

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
        var status = profiles.Length == 0
            ? "MO2 найден, но профили с modlist.txt отсутствуют"
            : executables.Length == 0
                ? "Профили найдены, но исполняемые файлы MO2 не настроены"
                : $"Профилей: {profiles.Length} · запусков: {executables.Length}";
        return new Mo2InstanceSnapshot(true, fullRoot, selectedProfile, profiles, executables, status);
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

    private static Mo2ProfileSnapshot Mutate(string root, string profileName, Action<List<string>> mutation)
    {
        var profileRoot = ResolveProfileRoot(root, profileName);
        var path = Path.Combine(profileRoot, ModListFile);
        var lines = File.ReadAllLines(path).ToList();
        mutation(lines);

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

        return ReadProfile(root, profileName);
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

    private static string NormalizeIniPath(string? value) =>
        (value ?? string.Empty).Replace('/', Path.DirectorySeparatorChar);

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
