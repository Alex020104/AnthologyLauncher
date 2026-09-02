using System.Text;
using System.Globalization;

namespace Anthology.Mo2.Core;

public enum AnomalyConfigurationKind
{
    Anomaly,
    Mcm,
}

public enum AnomalyConfigurationStorageFormat
{
    SectionKeyValue,
    ConsoleCommand,
}

public sealed class AnomalyConfigurationEntry
{
    public required AnomalyConfigurationKind Kind { get; init; }

    public required string Category { get; init; }

    public required string Key { get; init; }

    public required string Value { get; set; }

    public required string OriginalValue { get; set; }

    public required string SourcePath { get; init; }

    public required string TargetPath { get; init; }

    public required string StorageSection { get; init; }

    public required int LineIndex { get; init; }

    public AnomalyConfigurationStorageFormat StorageFormat { get; init; }

    public string? DisplayName { get; init; }

    public string? Description { get; init; }

    public string? CategoryDisplayName { get; init; }

    public string MenuPath { get; init; } = string.Empty;

    public string? MenuDisplayName { get; init; }

    public int MenuOrder { get; init; } = int.MaxValue;

    public int DisplayOrder { get; init; } = int.MaxValue;

    public string? ControlType { get; init; }

    public double? Minimum { get; init; }

    public double? Maximum { get; init; }

    public double? Step { get; init; }

    public string? DefaultValue { get; init; }

    public bool IsDirty => !string.Equals(Value, OriginalValue, StringComparison.Ordinal);

    public bool IsBoolean => Value.Equals("true", StringComparison.OrdinalIgnoreCase)
                             || Value.Equals("false", StringComparison.OrdinalIgnoreCase)
                             || Value.Equals("on", StringComparison.OrdinalIgnoreCase)
                             || Value.Equals("off", StringComparison.OrdinalIgnoreCase);

    public bool BooleanValue => Value.Equals("true", StringComparison.OrdinalIgnoreCase)
                                || Value.Equals("on", StringComparison.OrdinalIgnoreCase);

    public void ToggleBoolean()
    {
        if (!IsBoolean)
        {
            return;
        }

        var usesOnOff = Value.Equals("on", StringComparison.OrdinalIgnoreCase)
                        || Value.Equals("off", StringComparison.OrdinalIgnoreCase);
        Value = usesOnOff
            ? BooleanValue ? "off" : "on"
            : BooleanValue ? "false" : "true";
    }
}

public sealed record AnomalyConfigurationSnapshot(
    string? AnomalyPath,
    string? McmPath,
    IReadOnlyList<AnomalyConfigurationEntry> AnomalySettings,
    IReadOnlyList<AnomalyConfigurationEntry> McmSettings,
    string StatusText)
{
    public static AnomalyConfigurationSnapshot Empty { get; } = new(
        null,
        null,
        [],
        [],
        "Настройки Anomaly и MCM ещё не загружены");

    public bool AnomalyAvailable => AnomalyPath is not null && AnomalySettings.Count > 0;

    public bool McmAvailable => McmPath is not null && McmSettings.Count > 0;

    public int DirtyCount => AnomalySettings.Count(item => item.IsDirty)
                             + McmSettings.Count(item => item.IsDirty);
}

public sealed record AnomalyConfigurationWriteResult(
    bool Success,
    string Message,
    int ChangedValues = 0,
    IReadOnlyList<string>? BackupPaths = null);

public static class AnomalyConfigurationManager
{
    private const string McmSection = "mcm";

    public static AnomalyConfigurationSnapshot Load(string? gameRoot, string? mo2Root)
    {
        var (mcmSource, mcmTarget) = ResolveMcm(gameRoot);
        var userPath = ResolveUserLtx(gameRoot);
        var metadataCatalog = McmConfigurationMetadataCatalog.Load(mo2Root, gameRoot);

        var anomalySettings = mcmSource is null || mcmTarget is null
            ? []
            : ParseAnomaly(mcmSource, mcmTarget, metadataCatalog);
        if (userPath is not null)
        {
            var representedCommands = anomalySettings
                .Select(item => item.Key.Split('/').Last())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            anomalySettings.AddRange(ParseUserLtx(userPath, metadataCatalog, representedCommands));
        }
        var mcmSettings = mcmSource is null || mcmTarget is null
            ? []
            : ParseMcm(mcmSource, mcmTarget, metadataCatalog);

        var status = anomalySettings.Count == 0 && mcmSettings.Count == 0
            ? "Оригинальные user.ltx и axr_options.ltx не найдены. Проверьте установленную игру."
            : $"Anomaly: {anomalySettings.Count} параметров · MCM: {mcmSettings.Count} параметров · DB: {metadataCatalog.DatabaseAssetCount} файлов из {metadataCatalog.DatabaseArchiveCount} архивов";

        return new AnomalyConfigurationSnapshot(
            mcmTarget ?? userPath,
            mcmTarget,
            anomalySettings,
            mcmSettings,
            status);
    }

    public static AnomalyConfigurationWriteResult Save(AnomalyConfigurationSnapshot snapshot)
    {
        var dirty = snapshot.AnomalySettings.Concat(snapshot.McmSettings)
            .Where(item => item.IsDirty)
            .ToArray();
        if (dirty.Length == 0)
        {
            return new AnomalyConfigurationWriteResult(true, "Изменённых параметров нет");
        }

        foreach (var entry in dirty)
        {
            if (string.IsNullOrWhiteSpace(entry.Value))
            {
                return new AnomalyConfigurationWriteResult(false, $"Параметр {entry.Key} не может быть пустым");
            }

            if (entry.Value.Length > 4096 || entry.Value.IndexOfAny(['\r', '\n', '\0']) >= 0)
            {
                return new AnomalyConfigurationWriteResult(false, $"Недопустимое значение параметра {entry.Key}");
            }
        }

        var backupPaths = new List<string>();
        try
        {
            foreach (var targetGroup in dirty.GroupBy(item => item.TargetPath, StringComparer.OrdinalIgnoreCase))
            {
                var entries = targetGroup.ToArray();
                var targetPath = Path.GetFullPath(targetGroup.Key);
                var sourcePath = File.Exists(targetPath)
                    ? targetPath
                    : Path.GetFullPath(entries[0].SourcePath);
                if (!File.Exists(sourcePath))
                {
                    throw new FileNotFoundException("Файл настроек больше не существует", sourcePath);
                }

                var document = TextFileDocument.Read(sourcePath);
                foreach (var entry in entries)
                {
                    var lineIndex = FindCurrentLine(document.Lines, entry);
                    if (lineIndex < 0)
                    {
                        throw new InvalidDataException($"Параметр {entry.Key} изменился на диске. Обновите список и повторите.");
                    }

                    document.Lines[lineIndex] = ReplaceValue(document.Lines[lineIndex], entry);
                }

                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                var backupPath = CreateBackup(sourcePath, targetPath);
                backupPaths.Add(backupPath);
                WriteAtomically(targetPath, document);
            }

            foreach (var entry in dirty)
            {
                entry.OriginalValue = entry.Value;
            }

            return new AnomalyConfigurationWriteResult(
                true,
                $"Сохранено параметров: {dirty.Length}. Резервные копии созданы автоматически.",
                dirty.Length,
                backupPaths);
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or InvalidDataException)
        {
            return new AnomalyConfigurationWriteResult(false, exception.Message, BackupPaths: backupPaths);
        }
    }

    public static AnomalyConfigurationWriteResult RestoreLatestBackup(string? targetPath)
    {
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            return new AnomalyConfigurationWriteResult(false, "Файл настроек не найден");
        }

        var fullTarget = Path.GetFullPath(targetPath);
        var directory = Path.GetDirectoryName(fullTarget);
        if (directory is null || !Directory.Exists(directory))
        {
            return new AnomalyConfigurationWriteResult(false, "Папка настроек не найдена");
        }

        var backup = Directory.EnumerateFiles(directory, Path.GetFileName(fullTarget) + ".anthology-backup-*")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
        if (backup is null)
        {
            return new AnomalyConfigurationWriteResult(false, "Резервная копия для этого файла ещё не создавалась");
        }

        try
        {
            if (File.Exists(fullTarget))
            {
                CreateBackup(fullTarget, fullTarget, "before-restore");
            }

            var tempPath = fullTarget + ".anthology-restore-" + Guid.NewGuid().ToString("N") + ".tmp";
            File.Copy(backup, tempPath, overwrite: true);
            File.Move(tempPath, fullTarget, overwrite: true);
            return new AnomalyConfigurationWriteResult(true, $"Восстановлена резервная копия {Path.GetFileName(backup)}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new AnomalyConfigurationWriteResult(false, exception.Message);
        }
    }

    private static (string? Source, string? Target) ResolveMcm(string? gameRoot)
    {
        var gameMcm = string.IsNullOrWhiteSpace(gameRoot)
            ? null
            : Path.Combine(Path.GetFullPath(gameRoot), "gamedata", "configs", "axr_options.ltx");

        if (gameMcm is not null && File.Exists(gameMcm))
        {
            return (gameMcm, gameMcm);
        }

        return (null, null);
    }

    private static string? ResolveUserLtx(string? gameRoot)
    {
        if (string.IsNullOrWhiteSpace(gameRoot))
        {
            return null;
        }

        var fullGameRoot = Path.GetFullPath(gameRoot);
        var defaultPath = Path.Combine(fullGameRoot, "appdata", "user.ltx");
        var fsgamePath = Path.Combine(fullGameRoot, "fsgame.ltx");
        if (File.Exists(fsgamePath))
        {
            foreach (var rawLine in File.ReadLines(fsgamePath))
            {
                var line = rawLine.Trim();
                if (!line.StartsWith("$app_data_root$", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var parts = line.Split('|', StringSplitOptions.TrimEntries);
                var configured = parts.LastOrDefault();
                if (string.IsNullOrWhiteSpace(configured))
                {
                    break;
                }

                var appDataRoot = Path.IsPathRooted(configured)
                    ? Path.GetFullPath(configured)
                    : Path.GetFullPath(Path.Combine(fullGameRoot, configured));
                var configuredPath = Path.Combine(appDataRoot, "user.ltx");
                if (File.Exists(configuredPath))
                {
                    return configuredPath;
                }

                break;
            }
        }

        return File.Exists(defaultPath) ? defaultPath : null;
    }

    private static List<AnomalyConfigurationEntry> ParseAnomaly(
        string sourcePath,
        string targetPath,
        McmConfigurationMetadataCatalog metadataCatalog)
    {
        var document = TextFileDocument.Read(sourcePath);
        var result = new List<AnomalyConfigurationEntry>();
        var section = string.Empty;
        for (var index = 0; index < document.Lines.Count; index++)
        {
            var trimmed = document.Lines[index].Trim();
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                section = trimmed[1..^1].Trim();
                continue;
            }

            if (!section.Equals("options", StringComparison.OrdinalIgnoreCase)
                || !TryParseMcmLine(document.Lines[index], out var key, out var value))
            {
                continue;
            }

            var slash = key.IndexOf('/');
            var metadata = metadataCatalog.ResolveAnomaly(key, index);

            result.Add(new AnomalyConfigurationEntry
            {
                Kind = AnomalyConfigurationKind.Anomaly,
                Category = slash > 0 ? key[..slash] : "other",
                Key = key,
                Value = value,
                OriginalValue = value,
                SourcePath = sourcePath,
                TargetPath = targetPath,
                StorageSection = "options",
                LineIndex = index,
                DisplayName = metadata.DisplayName,
                Description = metadata.Description,
                CategoryDisplayName = metadata.CategoryDisplayName,
                MenuPath = metadata.MenuPath,
                MenuDisplayName = metadata.MenuDisplayName,
                MenuOrder = metadata.MenuOrder,
                DisplayOrder = metadata.DisplayOrder,
            });
        }

        return result;
    }

    private static List<AnomalyConfigurationEntry> ParseUserLtx(
        string path,
        McmConfigurationMetadataCatalog metadataCatalog,
        HashSet<string> representedCommands)
    {
        var document = TextFileDocument.Read(path);
        var result = new List<AnomalyConfigurationEntry>();
        for (var index = 0; index < document.Lines.Count; index++)
        {
            if (!TryParseConsoleLine(document.Lines[index], out var key, out var command, out var value)
                || (!command.Equals("bind", StringComparison.OrdinalIgnoreCase)
                    && !command.Equals("bind_sec", StringComparison.OrdinalIgnoreCase)
                    && representedCommands.Contains(command)))
            {
                continue;
            }

            var (category, menuPath) = ResolveConsoleMenuPath(command);
            var metadataKey = $"{menuPath}/{key.Replace('/', '_')}";
            var metadata = metadataCatalog.ResolveAnomaly(metadataKey, index);
            result.Add(new AnomalyConfigurationEntry
            {
                Kind = AnomalyConfigurationKind.Anomaly,
                Category = category,
                Key = key,
                Value = value,
                OriginalValue = value,
                SourcePath = path,
                TargetPath = path,
                StorageSection = string.Empty,
                StorageFormat = AnomalyConfigurationStorageFormat.ConsoleCommand,
                LineIndex = index,
                DisplayName = metadata.DisplayName,
                Description = metadata.Description,
                CategoryDisplayName = metadata.CategoryDisplayName,
                MenuPath = menuPath,
                MenuDisplayName = metadata.MenuDisplayName,
                MenuOrder = metadata.MenuOrder,
                DisplayOrder = index,
            });
        }

        return result;
    }

    private static (string Category, string MenuPath) ResolveConsoleMenuPath(string command)
    {
        if (command.Equals("bind", StringComparison.OrdinalIgnoreCase)
            || command.Equals("bind_sec", StringComparison.OrdinalIgnoreCase))
        {
            return ("control", "control/keybind");
        }

        if (command.StartsWith("snd_", StringComparison.OrdinalIgnoreCase))
        {
            return ("sound", "sound/general");
        }

        if (command.StartsWith("hud_", StringComparison.OrdinalIgnoreCase))
        {
            return ("video", "video/hud");
        }

        if (command.Equals("renderer", StringComparison.OrdinalIgnoreCase)
            || command.Equals("vid_mode", StringComparison.OrdinalIgnoreCase)
            || command.Equals("fov", StringComparison.OrdinalIgnoreCase)
            || command.Equals("hud_fov", StringComparison.OrdinalIgnoreCase)
            || command.StartsWith("rs_", StringComparison.OrdinalIgnoreCase))
        {
            return ("video", "video/basic");
        }

        if (command.StartsWith("r1_", StringComparison.OrdinalIgnoreCase)
            || command.StartsWith("r2_", StringComparison.OrdinalIgnoreCase)
            || command.StartsWith("r3_", StringComparison.OrdinalIgnoreCase)
            || command.StartsWith("r4_", StringComparison.OrdinalIgnoreCase)
            || command.StartsWith("r__", StringComparison.OrdinalIgnoreCase)
            || command.StartsWith("texture_", StringComparison.OrdinalIgnoreCase))
        {
            return ("video", "video/advanced");
        }

        if (command.StartsWith("mouse_", StringComparison.OrdinalIgnoreCase)
            || command.StartsWith("cam_", StringComparison.OrdinalIgnoreCase)
            || command.StartsWith("con_", StringComparison.OrdinalIgnoreCase))
        {
            return ("control", "control/general");
        }

        if (command.StartsWith("ai_", StringComparison.OrdinalIgnoreCase)
            || command.StartsWith("alife_", StringComparison.OrdinalIgnoreCase))
        {
            return ("alife", "alife/general");
        }

        return command.StartsWith("g_", StringComparison.OrdinalIgnoreCase)
            ? ("gameplay", "gameplay/general")
            : ("other", "other/general");
    }

    private static List<AnomalyConfigurationEntry> ParseMcm(
        string sourcePath,
        string targetPath,
        McmConfigurationMetadataCatalog metadataCatalog)
    {
        var document = TextFileDocument.Read(sourcePath);
        var result = new List<AnomalyConfigurationEntry>();
        var section = string.Empty;
        for (var index = 0; index < document.Lines.Count; index++)
        {
            var trimmed = document.Lines[index].Trim();
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                section = trimmed[1..^1].Trim();
                continue;
            }

            if (!section.Equals(McmSection, StringComparison.OrdinalIgnoreCase)
                || !TryParseMcmLine(document.Lines[index], out var key, out var value))
            {
                continue;
            }

            var slash = key.IndexOf('/');
            var metadata = metadataCatalog.Resolve(key);
            var menuPath = metadata?.MenuPath ?? (key.Contains('/') ? key[..key.LastIndexOf('/')] : key);
            result.Add(new AnomalyConfigurationEntry
            {
                Kind = AnomalyConfigurationKind.Mcm,
                Category = slash > 0 ? key[..slash] : "Общие параметры MCM",
                Key = key,
                Value = value,
                OriginalValue = value,
                SourcePath = sourcePath,
                TargetPath = targetPath,
                StorageSection = McmSection,
                LineIndex = index,
                DisplayName = metadata?.DisplayName,
                Description = metadata?.Description,
                CategoryDisplayName = metadata?.CategoryDisplayName,
                MenuPath = menuPath,
                MenuDisplayName = metadata?.MenuDisplayName,
                MenuOrder = metadata?.MenuOrder ?? int.MaxValue,
                DisplayOrder = metadata?.DisplayOrder ?? index,
                ControlType = metadata?.ControlType,
                Minimum = metadata?.Minimum,
                Maximum = metadata?.Maximum,
                Step = metadata?.Step,
                DefaultValue = metadata?.DefaultValue,
            });
        }

        return result;
    }

    private static int FindCurrentLine(List<string> lines, AnomalyConfigurationEntry entry)
    {
        if (entry.LineIndex >= 0 && entry.LineIndex < lines.Count && LineMatches(lines[entry.LineIndex], entry))
        {
            return entry.LineIndex;
        }

        if (entry.StorageFormat == AnomalyConfigurationStorageFormat.ConsoleCommand)
        {
            for (var index = 0; index < lines.Count; index++)
            {
                if (LineMatches(lines[index], entry))
                {
                    return index;
                }
            }

            return -1;
        }

        var currentSection = string.Empty;
        for (var index = 0; index < lines.Count; index++)
        {
            var trimmed = lines[index].Trim();
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                currentSection = trimmed[1..^1].Trim();
                continue;
            }

            if (!currentSection.Equals(entry.StorageSection, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (LineMatches(lines[index], entry))
            {
                return index;
            }
        }

        return -1;
    }

    private static bool LineMatches(string line, AnomalyConfigurationEntry entry)
    {
        var parsed = entry.StorageFormat == AnomalyConfigurationStorageFormat.ConsoleCommand
            ? TryParseConsoleLine(line, out var key, out _, out _)
            : TryParseMcmLine(line, out key, out _);
        return parsed && key.Equals(entry.Key, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryParseConsoleLine(
        string line,
        out string key,
        out string command,
        out string value)
    {
        key = string.Empty;
        command = string.Empty;
        value = string.Empty;
        var trimmed = line.Trim();
        if (trimmed.Length == 0
            || IsComment(trimmed)
            || (trimmed.StartsWith('[') && trimmed.EndsWith(']')))
        {
            return false;
        }

        var separator = trimmed.IndexOfAny([' ', '\t']);
        if (separator <= 0)
        {
            return false;
        }

        command = trimmed[..separator].Trim();
        var remainder = trimmed[separator..].Trim();
        if (remainder.Length == 0)
        {
            return false;
        }

        if (command.Equals("bind", StringComparison.OrdinalIgnoreCase)
            || command.Equals("bind_sec", StringComparison.OrdinalIgnoreCase))
        {
            separator = remainder.IndexOfAny([' ', '\t']);
            if (separator <= 0)
            {
                return false;
            }

            var action = remainder[..separator].Trim();
            value = remainder[separator..].Trim();
            key = $"{command}/{action}";
            return value.Length > 0;
        }

        key = command;
        value = remainder;
        return true;
    }

    private static bool TryParseMcmLine(string line, out string key, out string value)
    {
        key = string.Empty;
        value = string.Empty;
        var trimmed = line.Trim();
        if (trimmed.Length == 0 || IsComment(trimmed))
        {
            return false;
        }

        var equals = trimmed.IndexOf('=');
        if (equals <= 0)
        {
            return false;
        }

        key = trimmed[..equals].Trim();
        value = trimmed[(equals + 1)..].Trim();
        return key.Length > 0 && value.Length > 0;
    }

    private static string ReplaceValue(string line, AnomalyConfigurationEntry entry) =>
        entry.StorageFormat == AnomalyConfigurationStorageFormat.ConsoleCommand
            ? ReplaceConsoleValue(line, entry.Value)
            : ReplaceSectionValue(line, entry.Value);

    private static string ReplaceSectionValue(string line, string value)
    {
        var equals = line.IndexOf('=');
        if (equals < 0)
        {
            throw new InvalidDataException("Строка MCM имеет неизвестный формат");
        }

        var valueStart = equals + 1;
        while (valueStart < line.Length && char.IsWhiteSpace(line[valueStart]))
        {
            valueStart++;
        }

        return line[..valueStart] + value;
    }

    private static string ReplaceConsoleValue(string line, string value)
    {
        var trimmedStart = line.Length - line.TrimStart().Length;
        var position = trimmedStart;
        var fieldCount = line.AsSpan(trimmedStart).StartsWith("bind ", StringComparison.OrdinalIgnoreCase)
                         || line.AsSpan(trimmedStart).StartsWith("bind_sec ", StringComparison.OrdinalIgnoreCase)
            ? 2
            : 1;

        for (var field = 0; field < fieldCount; field++)
        {
            while (position < line.Length && !char.IsWhiteSpace(line[position]))
            {
                position++;
            }

            while (position < line.Length && char.IsWhiteSpace(line[position]))
            {
                position++;
            }
        }

        if (position >= line.Length)
        {
            throw new InvalidDataException("Строка user.ltx имеет неизвестный формат");
        }

        return line[..position] + value;
    }

    private static bool IsComment(string trimmed) =>
        trimmed.StartsWith(';') || trimmed.StartsWith('#') || trimmed.StartsWith("//", StringComparison.Ordinal);

    private static string CreateBackup(string sourcePath, string targetPath, string? marker = null)
    {
        var suffix = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
        if (!string.IsNullOrWhiteSpace(marker))
        {
            suffix += "-" + marker;
        }

        var backupPath = targetPath + ".anthology-backup-" + suffix;
        File.Copy(sourcePath, backupPath, overwrite: false);
        return backupPath;
    }

    private static void WriteAtomically(string targetPath, TextFileDocument document)
    {
        var tempPath = targetPath + ".anthology-write-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(tempPath, document.Compose(), document.Encoding);
            File.Move(tempPath, targetPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private sealed class TextFileDocument
    {
        private TextFileDocument(List<string> lines, string newline, bool endsWithNewline, Encoding encoding)
        {
            Lines = lines;
            Newline = newline;
            EndsWithNewline = endsWithNewline;
            Encoding = encoding;
        }

        public List<string> Lines { get; }

        public string Newline { get; }

        public bool EndsWithNewline { get; }

        public Encoding Encoding { get; }

        public static TextFileDocument Read(string path)
        {
            var bytes = File.ReadAllBytes(path);
            var encoding = DetectEncoding(bytes, out var preambleLength);
            var text = encoding.GetString(bytes, preambleLength, bytes.Length - preambleLength);
            var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            var endsWithNewline = text.EndsWith('\n');
            var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();
            if (endsWithNewline && lines.Count > 0)
            {
                lines.RemoveAt(lines.Count - 1);
            }

            return new TextFileDocument(lines, newline, endsWithNewline, encoding);
        }

        public string Compose()
        {
            var text = string.Join(Newline, Lines);
            return EndsWithNewline ? text + Newline : text;
        }

        private static Encoding DetectEncoding(byte[] bytes, out int preambleLength)
        {
            if (bytes.AsSpan().StartsWith(Encoding.UTF8.GetPreamble()))
            {
                preambleLength = Encoding.UTF8.GetPreamble().Length;
                return new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
            }

            if (bytes.AsSpan().StartsWith(Encoding.Unicode.GetPreamble()))
            {
                preambleLength = Encoding.Unicode.GetPreamble().Length;
                return Encoding.Unicode;
            }

            if (bytes.AsSpan().StartsWith(Encoding.BigEndianUnicode.GetPreamble()))
            {
                preambleLength = Encoding.BigEndianUnicode.GetPreamble().Length;
                return Encoding.BigEndianUnicode;
            }

            try
            {
                var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
                _ = utf8.GetString(bytes);
                preambleLength = 0;
                return utf8;
            }
            catch (DecoderFallbackException)
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                preambleLength = 0;
                return Encoding.GetEncoding(1251);
            }
        }
    }
}
