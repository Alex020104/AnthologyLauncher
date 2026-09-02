using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Anthology.Mo2.Core;

public sealed record McmConfigurationMetadata(
    string? DisplayName,
    string? Description,
    string? CategoryDisplayName,
    string MenuPath,
    string? MenuDisplayName,
    int MenuOrder,
    int DisplayOrder,
    string? ControlType,
    double? Minimum,
    double? Maximum,
    double? Step,
    string? DefaultValue);

public sealed class McmConfigurationMetadataCatalog
{
    private static readonly Regex ModulePattern = new(
        "\\b(?:local\\s+)?op\\s*=\\s*\\{\\s*id\\s*=\\s*['\"](?<id>[^'\"]+)['\"]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex SimpleTablePattern = new(
        @"\{(?<body>[^{}]{1,1800})\}",
        RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex StringPropertyPattern = new(
        "\\b(?<name>id|type|text)\\s*=\\s*['\"](?<value>[^'\"]+)['\"]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex NumberPropertyPattern = new(
        @"\b(?<name>min|max|step)\s*=\s*(?<value>-?(?:\d+(?:\.\d*)?|\.\d+))",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex DefaultStringPropertyPattern = new(
        "\\bdef\\s*=\\s*['\"](?<value>[^'\"]*)['\"]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex DefaultLiteralPropertyPattern = new(
        @"\bdef\s*=\s*(?<value>true|false|-?(?:\d+(?:\.\d*)?|\.\d+))",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex SlideTextPattern = new(
        "type\\s*=\\s*['\"]slide['\"][^{}]{0,600}?text\\s*=\\s*['\"](?<id>[^'\"]+)['\"]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private readonly IReadOnlyDictionary<string, OptionDefinition> _options;
    private readonly IReadOnlyDictionary<string, string> _translations;
    private readonly IReadOnlyDictionary<string, string> _moduleTitleIds;
    private readonly IReadOnlyDictionary<string, int> _nodeOrder;

    private McmConfigurationMetadataCatalog(
        IReadOnlyDictionary<string, OptionDefinition> options,
        IReadOnlyDictionary<string, string> translations,
        IReadOnlyDictionary<string, string> moduleTitleIds,
        IReadOnlyDictionary<string, int> nodeOrder,
        int databaseArchiveCount,
        int databaseAssetCount)
    {
        _options = options;
        _translations = translations;
        _moduleTitleIds = moduleTitleIds;
        _nodeOrder = nodeOrder;
        DatabaseArchiveCount = databaseArchiveCount;
        DatabaseAssetCount = databaseAssetCount;
    }

    public int DatabaseArchiveCount { get; }

    public int DatabaseAssetCount { get; }

    public static McmConfigurationMetadataCatalog Empty { get; } = new(
        new Dictionary<string, OptionDefinition>(StringComparer.OrdinalIgnoreCase),
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
        0,
        0);

    public static McmConfigurationMetadataCatalog Load(string? mo2Root, string? gameRoot = null)
    {
        var options = new Dictionary<string, OptionDefinition>(StringComparer.OrdinalIgnoreCase);
        var translations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var moduleTitleIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var nodeOrder = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var databaseArchiveCount = 0;
        var databaseAssetCount = 0;
        if (!string.IsNullOrWhiteSpace(gameRoot) && Directory.Exists(gameRoot))
        {
            var fullGameRoot = Path.GetFullPath(gameRoot);
            LoadDatabaseMetadata(
                fullGameRoot,
                options,
                translations,
                moduleTitleIds,
                nodeOrder,
                ref databaseArchiveCount,
                ref databaseAssetCount);

            var gameDataRoot = Path.Combine(fullGameRoot, "gamedata");
            LoadScripts(Path.Combine(gameDataRoot, "scripts"), options, moduleTitleIds, nodeOrder);
            var gameTextRoot = Path.Combine(gameDataRoot, "configs", "text");
            LoadTranslations(Path.Combine(gameTextRoot, "eng"), translations, overwrite: true);
            LoadTranslations(Path.Combine(gameTextRoot, "rus"), translations, overwrite: true);
        }

        if (string.IsNullOrWhiteSpace(mo2Root) || !Directory.Exists(mo2Root))
        {
            return new McmConfigurationMetadataCatalog(
                options,
                translations,
                moduleTitleIds,
                nodeOrder,
                databaseArchiveCount,
                databaseAssetCount);
        }

        var modsRoot = Path.Combine(Path.GetFullPath(mo2Root), "mods");
        if (!Directory.Exists(modsRoot))
        {
            return new McmConfigurationMetadataCatalog(
                options,
                translations,
                moduleTitleIds,
                nodeOrder,
                databaseArchiveCount,
                databaseAssetCount);
        }
        var modRoots = ResolveActiveModRoots(Path.GetFullPath(mo2Root), modsRoot);

        try
        {
            foreach (var modRoot in modRoots)
            {
                LoadScripts(modRoot, options, moduleTitleIds, nodeOrder);
            }

            // Localization-only mods are valid MO2 overrides too. Load every enabled mod
            // in profile priority order so the launcher resolves exactly the same text as the game.
            foreach (var modRoot in modRoots)
            {
                LoadTranslations(Path.Combine(modRoot, "gamedata", "configs", "text", "eng"), translations, overwrite: true);
                LoadTranslations(Path.Combine(modRoot, "gamedata", "configs", "text", "rus"), translations, overwrite: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new McmConfigurationMetadataCatalog(
                options,
                translations,
                moduleTitleIds,
                nodeOrder,
                databaseArchiveCount,
                databaseAssetCount);
        }

        return new McmConfigurationMetadataCatalog(
            options,
            translations,
            moduleTitleIds,
            nodeOrder,
            databaseArchiveCount,
            databaseAssetCount);
    }

    private static void LoadDatabaseMetadata(
        string gameRoot,
        Dictionary<string, OptionDefinition> options,
        Dictionary<string, string> translations,
        Dictionary<string, string> moduleTitleIds,
        Dictionary<string, int> nodeOrder,
        ref int archiveCount,
        ref int assetCount)
    {
        foreach (var archivePath in EnumerateDatabaseArchives(gameRoot))
        {
            try
            {
                using var reader = new XRayDatabaseReader(archivePath);
                archiveCount++;

                foreach (var entry in reader.Entries.Where(IsMcmScriptEntry))
                {
                    try
                    {
                        ProcessMcmScript(
                            DecodeText(reader.Read(entry)),
                            Path.GetFileNameWithoutExtension(entry.Name),
                            options,
                            moduleTitleIds,
                            nodeOrder);
                        assetCount++;
                    }
                    catch (Exception exception) when (exception is IOException or InvalidDataException)
                    {
                        // Continue with the other metadata entries from the same archive.
                    }
                }

                foreach (var language in new[] { "eng", "rus" })
                {
                    var prefix = $"configs\\text\\{language}\\";
                    foreach (var entry in reader.Entries.Where(item =>
                                 item.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                                 && item.Name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)))
                    {
                        try
                        {
                            LoadTranslationDocument(DecodeText(reader.Read(entry)), translations, overwrite: true);
                            assetCount++;
                        }
                        catch (Exception exception) when (exception is IOException
                                                           or InvalidDataException
                                                           or System.Xml.XmlException)
                        {
                            // Continue with the other localization entries from the same archive.
                        }
                    }
                }
            }
            catch (Exception exception) when (exception is IOException
                                               or UnauthorizedAccessException
                                               or InvalidDataException
                                               or System.Xml.XmlException)
            {
                // A damaged or unsupported optional archive must not hide metadata from
                // the remaining game databases, loose gamedata, or enabled MO2 mods.
            }
        }
    }

    private static IEnumerable<string> EnumerateDatabaseArchives(string gameRoot)
    {
        var databaseRoot = Path.Combine(gameRoot, "db");
        if (!Directory.Exists(databaseRoot))
        {
            yield break;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in new[]
                 {
                     Path.Combine(databaseRoot, "configs"),
                     databaseRoot,
                     Path.Combine(databaseRoot, "mods"),
                 })
        {
            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (var archive in Directory.EnumerateFiles(directory, "*.xdb*", SearchOption.TopDirectoryOnly)
                         .OrderBy(GetDatabaseOrder)
                         .ThenBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
            {
                var fullPath = Path.GetFullPath(archive);
                if (seen.Add(fullPath))
                {
                    yield return fullPath;
                }
            }
        }
    }

    private static int GetDatabaseOrder(string path)
    {
        var name = Path.GetFileName(path);
        if (name.StartsWith("configs.xdb", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("scripts.xdb", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        return name.Contains("anthology", StringComparison.OrdinalIgnoreCase) ? 1 : 2;
    }

    private static bool IsMcmScriptEntry(XRayDatabaseEntry entry) =>
        entry.Name.StartsWith("scripts\\", StringComparison.OrdinalIgnoreCase)
        && entry.Name.EndsWith("_mcm.script", StringComparison.OrdinalIgnoreCase);

    private static void LoadScripts(
        string root,
        Dictionary<string, OptionDefinition> options,
        Dictionary<string, string> moduleTitleIds,
        Dictionary<string, int> nodeOrder)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        foreach (var scriptPath in Directory.EnumerateFiles(root, "*_mcm.script", SearchOption.AllDirectories))
        {
            ProcessMcmScript(
                ReadText(scriptPath),
                Path.GetFileNameWithoutExtension(scriptPath),
                options,
                moduleTitleIds,
                nodeOrder);
        }
    }

    private static void ProcessMcmScript(
        string text,
        string sourceName,
        Dictionary<string, OptionDefinition> options,
        Dictionary<string, string> moduleTitleIds,
        Dictionary<string, int> nodeOrder)
    {
        var module = ModulePattern.Match(text).Groups["id"].Value;
        if (string.IsNullOrWhiteSpace(module))
        {
            module = sourceName.Replace("_mcm", string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        var titleMatch = SlideTextPattern.Match(text);
        if (titleMatch.Success)
        {
            moduleTitleIds[module] = titleMatch.Groups["id"].Value;
        }

        var order = 0;
        foreach (Match idMatch in Regex.Matches(
                     text,
                     "\\bid\\s*=\\s*['\"](?<id>[^'\"]+)['\"]",
                     RegexOptions.IgnoreCase))
        {
            nodeOrder[$"{module}/{idMatch.Groups["id"].Value}"] = order++;
        }

        foreach (Match tableMatch in SimpleTablePattern.Matches(text))
        {
            var properties = StringPropertyPattern.Matches(tableMatch.Groups["body"].Value)
                .Cast<Match>()
                .GroupBy(match => match.Groups["name"].Value, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group.First().Groups["value"].Value,
                    StringComparer.OrdinalIgnoreCase);
            if (!properties.TryGetValue("id", out var optionId)
                || !properties.TryGetValue("type", out var type)
                || type.Equals("title", StringComparison.OrdinalIgnoreCase)
                || type.Equals("line", StringComparison.OrdinalIgnoreCase)
                || type.Equals("slide", StringComparison.OrdinalIgnoreCase)
                || type.Equals("desc", StringComparison.OrdinalIgnoreCase)
                || type.Equals("image", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var numbers = NumberPropertyPattern.Matches(tableMatch.Groups["body"].Value)
                .Cast<Match>()
                .GroupBy(match => match.Groups["name"].Value, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => ParseNumber(group.First().Groups["value"].Value),
                    StringComparer.OrdinalIgnoreCase);
            options[$"{module}/{optionId}"] = new OptionDefinition(
                properties.GetValueOrDefault("text"),
                type,
                numbers.GetValueOrDefault("min"),
                numbers.GetValueOrDefault("max"),
                numbers.GetValueOrDefault("step"),
                ParseDefaultValue(tableMatch.Groups["body"].Value),
                nodeOrder.GetValueOrDefault($"{module}/{optionId}", int.MaxValue));
        }
    }

    public McmConfigurationMetadata? Resolve(string key)
    {
        var slash = key.IndexOf('/');
        if (slash <= 0 || slash >= key.Length - 1)
        {
            return null;
        }

        var module = key[..slash];
        var option = key[(slash + 1)..];
        _options.TryGetValue(key, out var definition);
        definition ??= _options.GetValueOrDefault($"{module}/{option.Split('/').Last()}");
        var normalizedOption = option.Replace('/', '_');
        var standardLabelId = $"ui_mcm_{module}_{normalizedOption}";
        var labelId = definition?.TextId ?? standardLabelId;
        var displayName = Translate(labelId)
                          ?? Translate(standardLabelId);
        var description = Translate(labelId + "_desc")
                          ?? Translate(standardLabelId + "_desc");
        var categoryTitle = _moduleTitleIds.TryGetValue(module, out var moduleTitleId)
            ? Translate(moduleTitleId)
            : null;
        categoryTitle ??= Translate($"ui_mcm_{module}_title")
                          ?? Translate($"ui_mcm_menu_{module}");

        var menuPath = key[..key.LastIndexOf('/')];
        var menuSegment = menuPath.Split('/').Last();
        var menuTitle = menuPath.Equals(module, StringComparison.OrdinalIgnoreCase)
            ? categoryTitle
            : Translate($"ui_mcm_menu_{menuSegment}")
              ?? Translate($"ui_mcm_{module}_{menuSegment}")
              ?? Translate($"ui_mcm_{module}_{menuSegment}_title");
        var menuOrder = _nodeOrder.GetValueOrDefault($"{module}/{menuSegment}", int.MaxValue);

        return displayName is null && description is null && categoryTitle is null && definition is null
            ? null
            : new McmConfigurationMetadata(
                Clean(displayName),
                Clean(description),
                Clean(categoryTitle),
                menuPath,
                Clean(menuTitle),
                menuOrder,
                definition?.Order ?? int.MaxValue,
                definition?.ControlType,
                definition?.Minimum,
                definition?.Maximum,
                definition?.Step,
                definition?.DefaultValue);
    }

    public McmConfigurationMetadata ResolveAnomaly(string key, int displayOrder)
    {
        var segments = key.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var menuPath = segments.Length > 1 ? string.Join('/', segments[..^1]) : "other";
        var menuSegment = menuPath.Split('/').Last();
        var category = segments.Length > 1 ? segments[0] : "other";
        var normalized = key.Replace('/', '_');
        var displayName = Translate($"ui_mm_{normalized}");
        var description = Translate($"ui_mm_{normalized}_desc");
        var categoryTitle = Translate($"ui_mm_menu_{category}") ?? Translate($"ui_mm_title_{category}");
        var menuTitle = Translate($"ui_mm_menu_{menuSegment}") ?? Translate($"ui_mm_title_{menuSegment}");

        return new McmConfigurationMetadata(
            Clean(displayName),
            Clean(description),
            Clean(categoryTitle),
            menuPath,
            Clean(menuTitle),
            AnomalyMenuOrder(menuPath),
            displayOrder,
            null,
            null,
            null,
            null,
            null);
    }

    private string? Translate(string id) => _translations.GetValueOrDefault(id);

    private static int AnomalyMenuOrder(string menuPath)
    {
        var segments = menuPath.Split('/');
        var top = Array.IndexOf(["video", "sound", "control", "gameplay", "alife", "other"], segments[0]);
        var child = segments.Length > 1 ? segments[1] : string.Empty;
        var childOrder = child switch
        {
            "basic" or "general" => 0,
            "advanced" or "environment" => 1,
            "hud" or "radio" or "keybind" or "economy_diff" => 2,
            "player" or "gameplay_diff" => 3,
            "mask" or "disguise" => 4,
            "weather" or "fast_travel" => 5,
            "night" or "backpack_travel" => 6,
            "event" => 7,
            "warfare" => 8,
            "dynamic_news" => 9,
            _ => 50,
        };
        return (top < 0 ? 99 : top) * 100 + childOrder;
    }

    private static IReadOnlyList<string> ResolveActiveModRoots(string mo2Root, string modsRoot)
    {
        var fallback = Directory.EnumerateDirectories(modsRoot)
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var iniPath = Path.Combine(mo2Root, "ModOrganizer.ini");
        if (!File.Exists(iniPath))
        {
            return fallback;
        }

        var selectedProfile = ReadText(iniPath).Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("selected_profile=", StringComparison.OrdinalIgnoreCase))
            .Select(line => line[(line.IndexOf('=') + 1)..].Trim())
            .FirstOrDefault();
        if (string.IsNullOrWhiteSpace(selectedProfile))
        {
            return fallback;
        }

        var modListPath = Path.Combine(mo2Root, "profiles", selectedProfile, "modlist.txt");
        if (!File.Exists(modListPath))
        {
            return fallback;
        }

        var active = new List<string>();
        foreach (var rawLine in ReadText(modListPath).Split('\n'))
        {
            var line = rawLine.Trim();
            if (!line.StartsWith('+') || line.Length == 1)
            {
                continue;
            }

            var root = Path.Combine(modsRoot, line[1..].Trim());
            if (Directory.Exists(root))
            {
                active.Add(root);
            }
        }

        return active.Count > 0 ? active : fallback;
    }

    private static void LoadTranslations(string directory, Dictionary<string, string> translations, bool overwrite)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        foreach (var path in Directory.EnumerateFiles(directory, "*.xml", SearchOption.AllDirectories))
        {
            try
            {
                LoadTranslationDocument(ReadText(path), translations, overwrite);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Xml.XmlException)
            {
                // One malformed localization file must not hide settings from every other mod.
            }
        }
    }

    private static void LoadTranslationDocument(
        string text,
        Dictionary<string, string> translations,
        bool overwrite)
    {
        var document = XDocument.Parse(text, LoadOptions.None);
        foreach (var element in document.Descendants().Where(item => item.Name.LocalName == "string"))
        {
            var id = element.Attribute("id")?.Value;
            var value = element.Descendants().FirstOrDefault(item => item.Name.LocalName == "text")?.Value;
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            if (overwrite || !translations.ContainsKey(id))
            {
                translations[id] = value.Trim();
            }
        }
    }

    private static string ReadText(string path) => DecodeText(File.ReadAllBytes(path));

    private static string DecodeText(byte[] bytes)
    {
        if (bytes.AsSpan().StartsWith(Encoding.UTF8.GetPreamble()))
        {
            return Encoding.UTF8.GetString(bytes, Encoding.UTF8.GetPreamble().Length, bytes.Length - Encoding.UTF8.GetPreamble().Length);
        }

        try
        {
            return new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(1251).GetString(bytes);
        }
    }

    private static double? ParseNumber(string value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) ? result : null;

    private static string? ParseDefaultValue(string tableBody)
    {
        var stringMatch = DefaultStringPropertyPattern.Match(tableBody);
        if (stringMatch.Success)
        {
            return stringMatch.Groups["value"].Value.Trim();
        }

        var literalMatch = DefaultLiteralPropertyPattern.Match(tableBody);
        if (!literalMatch.Success)
        {
            return null;
        }

        var value = literalMatch.Groups["value"].Value.Trim();
        return value.Equals("true", StringComparison.OrdinalIgnoreCase)
            || value.Equals("false", StringComparison.OrdinalIgnoreCase)
                ? value.ToLowerInvariant()
                : value;
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value)
        ? null
        : value.Replace("\\n", " ", StringComparison.Ordinal).Trim();

    private sealed record OptionDefinition(
        string? TextId,
        string ControlType,
        double? Minimum,
        double? Maximum,
        double? Step,
        string? DefaultValue,
        int Order);
}
