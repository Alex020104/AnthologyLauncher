using System.Globalization;
using System.Net;
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
        "\\b(?<name>id|type|text|hint)\\s*=\\s*['\"](?<value>[^'\"]+)['\"]",
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
    private readonly IReadOnlyDictionary<string, OptionDefinition> _uniqueModuleLeafOptions;
    private readonly IReadOnlyDictionary<string, string> _translations;
    private readonly IReadOnlyDictionary<string, string> _moduleTitleIds;
    private readonly IReadOnlyDictionary<string, string> _menuTitleIds;
    private readonly IReadOnlyDictionary<string, int> _nodeOrder;
    private readonly IReadOnlyDictionary<string, AnomalyOptionDefinition> _anomalyOptions;
    private readonly IReadOnlyDictionary<string, AnomalyOptionDefinition> _anomalyCommands;

    private McmConfigurationMetadataCatalog(
        IReadOnlyDictionary<string, OptionDefinition> options,
        IReadOnlyDictionary<string, string> translations,
        IReadOnlyDictionary<string, string> moduleTitleIds,
        IReadOnlyDictionary<string, string> menuTitleIds,
        IReadOnlyDictionary<string, int> nodeOrder,
        IReadOnlyDictionary<string, AnomalyOptionDefinition> anomalyOptions,
        IReadOnlyDictionary<string, AnomalyOptionDefinition> anomalyCommands,
        int databaseArchiveCount,
        int databaseAssetCount)
    {
        _options = options;
        _uniqueModuleLeafOptions = BuildUniqueModuleLeafOptions(options);
        _translations = translations;
        _moduleTitleIds = moduleTitleIds;
        _menuTitleIds = menuTitleIds;
        _nodeOrder = nodeOrder;
        _anomalyOptions = anomalyOptions;
        _anomalyCommands = anomalyCommands;
        DatabaseArchiveCount = databaseArchiveCount;
        DatabaseAssetCount = databaseAssetCount;
    }

    public int DatabaseArchiveCount { get; }

    public int DatabaseAssetCount { get; }

    public static McmConfigurationMetadataCatalog Empty { get; } = new(
        new Dictionary<string, OptionDefinition>(StringComparer.OrdinalIgnoreCase),
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
        new Dictionary<string, AnomalyOptionDefinition>(StringComparer.OrdinalIgnoreCase),
        new Dictionary<string, AnomalyOptionDefinition>(StringComparer.OrdinalIgnoreCase),
        0,
        0);

    public static McmConfigurationMetadataCatalog Load(string? mo2Root, string? gameRoot = null)
    {
        var options = new Dictionary<string, OptionDefinition>(StringComparer.OrdinalIgnoreCase);
        var translations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var moduleTitleIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var menuTitleIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var nodeOrder = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var anomalyOptions = new Dictionary<string, AnomalyOptionDefinition>(StringComparer.OrdinalIgnoreCase);
        var anomalyCommands = new Dictionary<string, AnomalyOptionDefinition>(StringComparer.OrdinalIgnoreCase);
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
                menuTitleIds,
                nodeOrder,
                anomalyOptions,
                anomalyCommands,
                ref databaseArchiveCount,
                ref databaseAssetCount);

            var gameDataRoot = Path.Combine(fullGameRoot, "gamedata");
            LoadScripts(
                Path.Combine(gameDataRoot, "scripts"),
                options,
                moduleTitleIds,
                menuTitleIds,
                nodeOrder,
                anomalyOptions,
                anomalyCommands);
            var gameTextRoot = Path.Combine(gameDataRoot, "configs", "text");
            LoadTranslations(Path.Combine(gameTextRoot, "eng"), translations, overwrite: false);
            LoadTranslations(Path.Combine(gameTextRoot, "rus"), translations, overwrite: true);
        }

        if (string.IsNullOrWhiteSpace(mo2Root) || !Directory.Exists(mo2Root))
        {
            return new McmConfigurationMetadataCatalog(
                options,
                translations,
                moduleTitleIds,
                menuTitleIds,
                nodeOrder,
                anomalyOptions,
                anomalyCommands,
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
                menuTitleIds,
                nodeOrder,
                anomalyOptions,
                anomalyCommands,
                databaseArchiveCount,
                databaseAssetCount);
        }
        var modRoots = ResolveActiveModRoots(Path.GetFullPath(mo2Root), modsRoot);

        try
        {
            foreach (var modRoot in modRoots)
            {
                LoadScripts(
                    modRoot,
                    options,
                    moduleTitleIds,
                    menuTitleIds,
                    nodeOrder,
                    anomalyOptions,
                    anomalyCommands);
            }

            // Localization-only mods are valid MO2 overrides too. Load every enabled mod
            // in profile priority order so the launcher resolves exactly the same text as the game.
            foreach (var modRoot in modRoots)
            {
                LoadTranslations(Path.Combine(modRoot, "gamedata", "configs", "text", "eng"), translations, overwrite: false);
                LoadTranslations(Path.Combine(modRoot, "gamedata", "configs", "text", "rus"), translations, overwrite: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new McmConfigurationMetadataCatalog(
                options,
                translations,
                moduleTitleIds,
                menuTitleIds,
                nodeOrder,
                anomalyOptions,
                anomalyCommands,
                databaseArchiveCount,
                databaseAssetCount);
        }

        return new McmConfigurationMetadataCatalog(
            options,
            translations,
            moduleTitleIds,
            menuTitleIds,
            nodeOrder,
            anomalyOptions,
            anomalyCommands,
            databaseArchiveCount,
            databaseAssetCount);
    }

    private static void LoadDatabaseMetadata(
        string gameRoot,
        Dictionary<string, OptionDefinition> options,
        Dictionary<string, string> translations,
        Dictionary<string, string> moduleTitleIds,
        Dictionary<string, string> menuTitleIds,
        Dictionary<string, int> nodeOrder,
        Dictionary<string, AnomalyOptionDefinition> anomalyOptions,
        Dictionary<string, AnomalyOptionDefinition> anomalyCommands,
        ref int archiveCount,
        ref int assetCount)
    {
        foreach (var archivePath in EnumerateDatabaseArchives(gameRoot))
        {
            try
            {
                using var reader = new XRayDatabaseReader(archivePath);
                archiveCount++;

                foreach (var entry in reader.Entries.Where(IsSettingsScriptEntry))
                {
                    try
                    {
                        var script = DecodeText(reader.Read(entry));
                        if (IsAnomalyOptionsScript(entry.Name))
                        {
                            ProcessAnomalyOptionsScript(script, anomalyOptions, anomalyCommands);
                        }
                        else if (IsMcmSchemaScript(entry.Name))
                        {
                            ProcessMcmSchemaScript(
                                script,
                                Path.GetFileNameWithoutExtension(entry.Name),
                                options,
                                moduleTitleIds,
                                menuTitleIds,
                                nodeOrder);
                        }
                        else
                        {
                            ProcessMcmScript(
                                script,
                                Path.GetFileNameWithoutExtension(entry.Name),
                                options,
                                moduleTitleIds,
                                menuTitleIds,
                                nodeOrder);
                        }
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
                            LoadTranslationDocument(
                                DecodeText(reader.Read(entry)),
                                translations,
                                overwrite: language.Equals("rus", StringComparison.OrdinalIgnoreCase));
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

    private static bool IsSettingsScriptEntry(XRayDatabaseEntry entry) =>
        entry.Name.StartsWith("scripts\\", StringComparison.OrdinalIgnoreCase)
        && (entry.Name.EndsWith("_mcm.script", StringComparison.OrdinalIgnoreCase)
            || IsMcmSchemaScript(entry.Name)
            || IsAnomalyOptionsScript(entry.Name));

    private static bool IsAnomalyOptionsScript(string path) =>
        path.EndsWith("ui_options.script", StringComparison.OrdinalIgnoreCase);

    private static bool IsMcmSchemaScript(string path) =>
        path.EndsWith("_mcm_schema.script", StringComparison.OrdinalIgnoreCase);

    private static void LoadScripts(
        string root,
        Dictionary<string, OptionDefinition> options,
        Dictionary<string, string> moduleTitleIds,
        Dictionary<string, string> menuTitleIds,
        Dictionary<string, int> nodeOrder,
        Dictionary<string, AnomalyOptionDefinition> anomalyOptions,
        Dictionary<string, AnomalyOptionDefinition> anomalyCommands)
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
                menuTitleIds,
                nodeOrder);
        }

        foreach (var scriptPath in Directory.EnumerateFiles(root, "*_mcm_schema.script", SearchOption.AllDirectories))
        {
            ProcessMcmSchemaScript(
                ReadText(scriptPath),
                Path.GetFileNameWithoutExtension(scriptPath),
                options,
                moduleTitleIds,
                menuTitleIds,
                nodeOrder);
        }

        foreach (var scriptPath in Directory.EnumerateFiles(root, "ui_options.script", SearchOption.AllDirectories))
        {
            ProcessAnomalyOptionsScript(ReadText(scriptPath), anomalyOptions, anomalyCommands);
        }
    }

    private static void ProcessMcmScript(
        string text,
        string sourceName,
        Dictionary<string, OptionDefinition> options,
        Dictionary<string, string> moduleTitleIds,
        Dictionary<string, string> menuTitleIds,
        Dictionary<string, int> nodeOrder)
    {
        var uncommented = RemoveLuaComments(text);
        var variables = ReadLuaStringVariables(uncommented);
        var returnedModule = Regex.Match(
            uncommented,
            "\\breturn\\s+op\\s*,\\s*['\"](?<module>[^'\"]+)['\"]",
            RegexOptions.IgnoreCase).Groups["module"].Value;
        string? parsedRootId = null;
        if (TryFindAssignedTable(uncommented, "op", out var root))
        {
            var rootProperties = ReadLuaProperties(uncommented, root);
            parsedRootId = ResolveLuaScalar(rootProperties.GetValueOrDefault("id"), variables);
            var parsedModule = string.IsNullOrWhiteSpace(returnedModule) ? parsedRootId : returnedModule;
            var rootPath = !string.IsNullOrWhiteSpace(returnedModule)
                           && !string.IsNullOrWhiteSpace(parsedRootId)
                           && !parsedRootId.Equals(returnedModule, StringComparison.OrdinalIgnoreCase)
                ? $"{returnedModule}/{parsedRootId}"
                : parsedModule;
            if (!string.IsNullOrWhiteSpace(parsedModule)
                && !string.IsNullOrWhiteSpace(rootPath)
                && TryGetLuaGroup(rootProperties, out var group))
            {
                var parsedCount = 0;
                var structuralOrder = 0;
                WalkMcmTree(
                    uncommented,
                    parsedModule,
                    rootPath,
                    group,
                    variables,
                    options,
                    moduleTitleIds,
                    menuTitleIds,
                    nodeOrder,
                    ref structuralOrder,
                    ref parsedCount);
                if (parsedCount > 0)
                {
                    return;
                }
            }
        }

        // Some old addons build their MCM table dynamically. Keep the compact
        // parser as a compatibility fallback for those scripts.
        var module = !string.IsNullOrWhiteSpace(returnedModule)
            ? returnedModule
            : parsedRootId ?? ModulePattern.Match(uncommented).Groups["id"].Value;
        if (string.IsNullOrWhiteSpace(module))
        {
            module = sourceName.Replace("_mcm", string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        var titleMatch = SlideTextPattern.Match(uncommented);
        if (titleMatch.Success)
        {
            moduleTitleIds[module] = titleMatch.Groups["id"].Value;
            menuTitleIds[module] = titleMatch.Groups["id"].Value;
        }

        var order = 0;
        foreach (Match idMatch in Regex.Matches(
                     uncommented,
                     "\\bid\\s*=\\s*['\"](?<id>[^'\"]+)['\"]",
                     RegexOptions.IgnoreCase))
        {
            nodeOrder[$"{module}/{idMatch.Groups["id"].Value}"] = order++;
        }

        foreach (Match tableMatch in SimpleTablePattern.Matches(uncommented))
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
                properties.GetValueOrDefault("hint"),
                type,
                numbers.GetValueOrDefault("min"),
                numbers.GetValueOrDefault("max"),
                numbers.GetValueOrDefault("step"),
                ParseDefaultValue(tableMatch.Groups["body"].Value),
                nodeOrder.GetValueOrDefault($"{module}/{optionId}", int.MaxValue));
        }
    }

    private static void WalkMcmTree(
        string text,
        string module,
        string path,
        LuaTableSpan group,
        IReadOnlyDictionary<string, string> variables,
        Dictionary<string, OptionDefinition> options,
        Dictionary<string, string> moduleTitleIds,
        Dictionary<string, string> menuTitleIds,
        Dictionary<string, int> nodeOrder,
        ref int order,
        ref int parsedCount)
    {
        var children = EnumerateLuaTableChildren(text, group)
            .Select(span => (Span: span, Properties: ReadLuaProperties(text, span)))
            .ToArray();
        var titleId = children
            .Where(item => IsLayoutElement(item.Properties.GetValueOrDefault("type")))
            .Select(item => ResolveLuaExpression(item.Properties.GetValueOrDefault("text"), variables))
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        if (!string.IsNullOrWhiteSpace(titleId))
        {
            menuTitleIds[path] = titleId;
            if (path.Equals(module, StringComparison.OrdinalIgnoreCase))
            {
                moduleTitleIds[module] = titleId;
            }
        }

        foreach (var child in children)
        {
            var id = ResolveLuaExpression(child.Properties.GetValueOrDefault("id"), variables);
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            var childPath = $"{path}/{id}";
            nodeOrder[childPath] = order++;
            if (TryGetLuaGroup(child.Properties, out var nestedGroup))
            {
                WalkMcmTree(
                    text,
                    module,
                    childPath,
                    nestedGroup,
                    variables,
                    options,
                    moduleTitleIds,
                    menuTitleIds,
                    nodeOrder,
                    ref order,
                    ref parsedCount);
                continue;
            }

            var type = child.Properties.GetValueOrDefault("type");
            if (string.IsNullOrWhiteSpace(type) || IsLayoutElement(type))
            {
                continue;
            }

            options[childPath] = new OptionDefinition(
                ResolveLuaExpression(child.Properties.GetValueOrDefault("text"), variables),
                ResolveLuaExpression(child.Properties.GetValueOrDefault("hint"), variables),
                type,
                ParseNumber(child.Properties.GetValueOrDefault("min")),
                ParseNumber(child.Properties.GetValueOrDefault("max")),
                ParseNumber(child.Properties.GetValueOrDefault("step")),
                ParseLuaDefaultValue(child.Properties.GetValueOrDefault("def")),
                nodeOrder[childPath]);
            parsedCount++;
        }
    }

    private static Dictionary<string, string> ReadLuaStringVariables(string text)
    {
        var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in Regex.Matches(
                     text,
                     "(?m)^\\s*(?:local\\s+)?(?<name>[A-Za-z_][A-Za-z0-9_]*)\\s*=\\s*['\"](?<value>[^'\"]*)['\"]"))
        {
            variables[match.Groups["name"].Value] = match.Groups["value"].Value;
        }

        return variables;
    }

    private static string? ResolveLuaScalar(
        string? value,
        IReadOnlyDictionary<string, string> variables)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return variables.TryGetValue(trimmed, out var resolved) ? resolved : trimmed;
    }

    private static string? ResolveLuaExpression(
        string? value,
        IReadOnlyDictionary<string, string> variables)
    {
        var scalar = ResolveLuaScalar(value, variables);
        if (string.IsNullOrWhiteSpace(scalar))
        {
            return scalar;
        }

        var formatted = Regex.Match(
            scalar,
            "^string_format\\(\\s*['\"](?<format>[^'\"]*)['\"]\\s*,\\s*(?<args>[^)]*)\\)$",
            RegexOptions.IgnoreCase);
        if (!formatted.Success)
        {
            return scalar;
        }

        var result = formatted.Groups["format"].Value;
        foreach (var rawArgument in formatted.Groups["args"].Value.Split(','))
        {
            var argument = ResolveLuaScalar(rawArgument.Trim().Trim('\'', '"'), variables);
            var marker = result.IndexOf("%s", StringComparison.Ordinal);
            if (marker < 0 || string.IsNullOrWhiteSpace(argument))
            {
                break;
            }

            result = result[..marker] + argument + result[(marker + 2)..];
        }

        return result.Contains("%s", StringComparison.Ordinal) ? scalar : result;
    }

    private static bool IsLayoutElement(string? type) =>
        type is not null && (type.Equals("title", StringComparison.OrdinalIgnoreCase)
                             || type.Equals("line", StringComparison.OrdinalIgnoreCase)
                             || type.Equals("slide", StringComparison.OrdinalIgnoreCase)
                             || type.Equals("desc", StringComparison.OrdinalIgnoreCase)
                             || type.Equals("image", StringComparison.OrdinalIgnoreCase));

    private static void ProcessMcmSchemaScript(
        string text,
        string sourceName,
        Dictionary<string, OptionDefinition> options,
        Dictionary<string, string> moduleTitleIds,
        Dictionary<string, string> menuTitleIds,
        Dictionary<string, int> nodeOrder)
    {
        var uncommented = RemoveLuaComments(text);
        var schemaMarker = sourceName.IndexOf("_mcm_schema", StringComparison.OrdinalIgnoreCase);
        var module = schemaMarker > 0 ? sourceName[..schemaMarker] : sourceName;
        if (!TryFindAssignedTable(uncommented, "OPTION_DEFS", out var definitionTable))
        {
            return;
        }

        var definitions = new Dictionary<string, OptionDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, table) in EnumerateLuaKeyedTableChildren(uncommented, definitionTable))
        {
            var properties = ReadLuaProperties(uncommented, table);
            var type = properties.GetValueOrDefault("type");
            if (string.IsNullOrWhiteSpace(type) || IsLayoutElement(type))
            {
                continue;
            }

            definitions[key] = new OptionDefinition(
                properties.GetValueOrDefault("text"),
                properties.GetValueOrDefault("hint"),
                type,
                ParseNumber(properties.GetValueOrDefault("min")),
                ParseNumber(properties.GetValueOrDefault("max")),
                ParseNumber(properties.GetValueOrDefault("step")),
                ParseLuaDefaultValue(properties.GetValueOrDefault("def")),
                int.MaxValue);
        }

        moduleTitleIds[module] = $"ui_mcm_{module}_title";
        menuTitleIds[module] = $"ui_mcm_{module}_title";
        var optionOrder = 0;
        if (TryFindAssignedTable(uncommented, "PANELS", out var panels))
        {
            var panelOrder = 0;
            foreach (var panel in EnumerateLuaTableChildren(uncommented, panels))
            {
                var panelProperties = ReadLuaProperties(uncommented, panel);
                var panelId = panelProperties.GetValueOrDefault("id");
                if (string.IsNullOrWhiteSpace(panelId))
                {
                    continue;
                }

                var menuPath = $"{module}/{panelId}";
                nodeOrder[menuPath] = panelOrder++;
                menuTitleIds[menuPath] = panelProperties.GetValueOrDefault("text")
                                               ?? $"ui_mcm_{module}_panel_{panelId}";
                if (!TryGetLuaTableProperty(uncommented, panel, "groups", out var groups))
                {
                    continue;
                }

                foreach (var group in EnumerateLuaTableChildren(uncommented, groups))
                {
                    if (!TryGetLuaTableProperty(uncommented, group, "options", out var optionKeys))
                    {
                        continue;
                    }

                    foreach (var key in EnumerateTopLevelLuaStrings(uncommented, optionKeys))
                    {
                        if (!definitions.TryGetValue(key, out var definition))
                        {
                            continue;
                        }

                        options[$"{menuPath}/{key}"] = definition with { Order = optionOrder++ };
                    }
                }
            }
        }

        // Z.H.O.P.A. and similar schemas generate per-faction options at run
        // time. Recreate those deterministic keys without executing addon Lua.
        if (TryFindAssignedTable(uncommented, "FACTIONS", out var factions)
            && TryFindAssignedTable(uncommented, "FACTION_TASK_OPTIONS", out var factionTasks))
        {
            var factionIds = EnumerateLuaTableChildren(uncommented, factions)
                .Select(item => ReadLuaProperties(uncommented, item).GetValueOrDefault("id"))
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Cast<string>()
                .ToArray();
            var taskKeys = EnumerateTopLevelLuaStrings(uncommented, factionTasks).ToArray();
            var menuPath = $"{module}/faction_weights";
            if (!nodeOrder.ContainsKey(menuPath))
            {
                nodeOrder[menuPath] = nodeOrder.Count;
                menuTitleIds[menuPath] = $"ui_mcm_{module}_panel_faction_weights";
            }

            foreach (var faction in factionIds)
            {
                foreach (var taskKey in taskKeys)
                {
                    if (!definitions.TryGetValue(taskKey, out var definition))
                    {
                        continue;
                    }

                    var key = $"faction_{faction}_{taskKey}";
                    options[$"{menuPath}/{key}"] = definition with
                    {
                        HintId = $"{module}_faction_task_{taskKey}",
                        Order = optionOrder++,
                    };
                }
            }
        }
    }

    private static void ProcessAnomalyOptionsScript(
        string text,
        Dictionary<string, AnomalyOptionDefinition> options,
        Dictionary<string, AnomalyOptionDefinition> commands)
    {
        var uncommented = RemoveLuaComments(text);
        if (!TryFindAssignedTable(uncommented, "options", out var root))
        {
            return;
        }

        var order = 0;
        WalkAnomalyTree(uncommented, string.Empty, root, null, options, commands, ref order);
    }

    private static void WalkAnomalyTree(
        string text,
        string path,
        LuaTableSpan group,
        string? inheritedTitleId,
        Dictionary<string, AnomalyOptionDefinition> options,
        Dictionary<string, AnomalyOptionDefinition> commands,
        ref int order)
    {
        var children = EnumerateLuaTableChildren(text, group)
            .Select(span => (Span: span, Properties: ReadLuaProperties(text, span)))
            .ToArray();
        var groupTitleId = children
                               .Where(item => IsLayoutElement(item.Properties.GetValueOrDefault("type")))
                               .Select(item => item.Properties.GetValueOrDefault("text"))
                               .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
                           ?? inheritedTitleId;

        foreach (var child in children)
        {
            var id = child.Properties.GetValueOrDefault("id");
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            var childPath = string.IsNullOrWhiteSpace(path) ? id : $"{path}/{id}";
            if (TryGetLuaGroup(child.Properties, out var nestedGroup))
            {
                WalkAnomalyTree(text, childPath, nestedGroup, groupTitleId, options, commands, ref order);
                continue;
            }

            var type = child.Properties.GetValueOrDefault("type");
            if (string.IsNullOrWhiteSpace(type) || IsLayoutElement(type))
            {
                continue;
            }

            var menuPath = childPath.Contains('/') ? childPath[..childPath.LastIndexOf('/')] : "other/general";
            var normalizedPath = childPath.Replace('/', '_');
            var definition = new AnomalyOptionDefinition(
                childPath,
                child.Properties.GetValueOrDefault("cmd"),
                menuPath,
                child.Properties.GetValueOrDefault("text") ?? $"ui_mm_{normalizedPath}",
                child.Properties.GetValueOrDefault("hint"),
                groupTitleId,
                order++,
                type,
                ParseNumber(child.Properties.GetValueOrDefault("min")),
                ParseNumber(child.Properties.GetValueOrDefault("max")),
                ParseNumber(child.Properties.GetValueOrDefault("step")),
                ParseLuaDefaultValue(child.Properties.GetValueOrDefault("def")));
            options[childPath] = definition;
            if (!string.IsNullOrWhiteSpace(definition.Command))
            {
                commands[definition.Command] = definition;
            }
        }
    }

    private static bool TryFindAssignedTable(string text, string variable, out LuaTableSpan table)
    {
        table = default;
        var bestLength = -1;
        foreach (Match match in Regex.Matches(
                     text,
                     $@"\b{Regex.Escape(variable)}\s*=\s*\{{",
                     RegexOptions.IgnoreCase))
        {
            var open = text.IndexOf('{', match.Index + match.Length - 1);
            var close = FindMatchingBrace(text, open);
            if (open < 0 || close < 0 || close - open <= bestLength)
            {
                continue;
            }

            table = new LuaTableSpan(open, close);
            bestLength = close - open;
        }

        return bestLength >= 0;
    }

    private static LuaProperties ReadLuaProperties(string text, LuaTableSpan table)
    {
        var result = new LuaProperties();
        var position = table.Open + 1;
        while (position < table.Close)
        {
            SkipLuaTrivia(text, ref position, table.Close);
            if (position >= table.Close)
            {
                break;
            }

            if (text[position] == '{')
            {
                var nestedEnd = FindMatchingBrace(text, position);
                position = nestedEnd >= 0 ? nestedEnd + 1 : table.Close;
                continue;
            }

            if (!IsLuaIdentifierStart(text[position]))
            {
                position++;
                continue;
            }

            var nameStart = position++;
            while (position < table.Close && IsLuaIdentifierPart(text[position]))
            {
                position++;
            }
            var name = text[nameStart..position];
            SkipLuaWhitespace(text, ref position, table.Close);
            if (position >= table.Close || text[position] != '=')
            {
                continue;
            }

            position++;
            SkipLuaWhitespace(text, ref position, table.Close);
            if (position >= table.Close)
            {
                break;
            }

            if (text[position] is '\'' or '"')
            {
                result[name] = ReadLuaString(text, ref position, table.Close);
                continue;
            }

            if (text[position] == '{')
            {
                var nestedEnd = FindMatchingBrace(text, position);
                if (nestedEnd < 0)
                {
                    break;
                }

                if (name.Equals("gr", StringComparison.OrdinalIgnoreCase))
                {
                    result.Group = new LuaTableSpan(position, nestedEnd);
                }
                position = nestedEnd + 1;
                continue;
            }

            var valueStart = position;
            var parenthesisDepth = 0;
            var bracketDepth = 0;
            while (position < table.Close)
            {
                var current = text[position];
                if (current is '\'' or '"')
                {
                    _ = ReadLuaString(text, ref position, table.Close);
                    continue;
                }
                if (current == '(')
                {
                    parenthesisDepth++;
                }
                else if (current == ')' && parenthesisDepth > 0)
                {
                    parenthesisDepth--;
                }
                else if (current == '[')
                {
                    bracketDepth++;
                }
                else if (current == ']' && bracketDepth > 0)
                {
                    bracketDepth--;
                }
                else if (current == ',' && parenthesisDepth == 0 && bracketDepth == 0)
                {
                    break;
                }
                else if (current == '{' && parenthesisDepth == 0 && bracketDepth == 0)
                {
                    var nestedEnd = FindMatchingBrace(text, position);
                    position = nestedEnd >= 0 ? nestedEnd + 1 : table.Close;
                    continue;
                }
                position++;
            }

            var value = text[valueStart..position].Trim();
            if (!string.IsNullOrWhiteSpace(value))
            {
                result[name] = value;
            }
        }

        return result;
    }

    private static IEnumerable<LuaTableSpan> EnumerateLuaTableChildren(string text, LuaTableSpan group)
    {
        var position = group.Open + 1;
        while (position < group.Close)
        {
            SkipLuaTrivia(text, ref position, group.Close);
            if (position >= group.Close)
            {
                yield break;
            }

            if (text[position] is '\'' or '"')
            {
                _ = ReadLuaString(text, ref position, group.Close);
                continue;
            }

            if (text[position] != '{')
            {
                position++;
                continue;
            }

            var close = FindMatchingBrace(text, position);
            if (close < 0 || close > group.Close)
            {
                yield break;
            }

            yield return new LuaTableSpan(position, close);
            position = close + 1;
        }
    }

    private static IEnumerable<(string Key, LuaTableSpan Table)> EnumerateLuaKeyedTableChildren(
        string text,
        LuaTableSpan group)
    {
        var position = group.Open + 1;
        while (position < group.Close)
        {
            SkipLuaTrivia(text, ref position, group.Close);
            if (position >= group.Close)
            {
                yield break;
            }

            if (!IsLuaIdentifierStart(text[position]))
            {
                if (text[position] == '{')
                {
                    var nestedEnd = FindMatchingBrace(text, position);
                    position = nestedEnd >= 0 ? nestedEnd + 1 : group.Close;
                }
                else if (text[position] is '\'' or '"')
                {
                    _ = ReadLuaString(text, ref position, group.Close);
                }
                else
                {
                    position++;
                }
                continue;
            }

            var nameStart = position++;
            while (position < group.Close && IsLuaIdentifierPart(text[position]))
            {
                position++;
            }
            var name = text[nameStart..position];
            SkipLuaWhitespace(text, ref position, group.Close);
            if (position >= group.Close || text[position] != '=')
            {
                continue;
            }

            position++;
            SkipLuaWhitespace(text, ref position, group.Close);
            if (position >= group.Close || text[position] != '{')
            {
                continue;
            }

            var close = FindMatchingBrace(text, position);
            if (close < 0 || close > group.Close)
            {
                yield break;
            }

            yield return (name, new LuaTableSpan(position, close));
            position = close + 1;
        }
    }

    private static bool TryGetLuaTableProperty(
        string text,
        LuaTableSpan table,
        string property,
        out LuaTableSpan value)
    {
        var position = table.Open + 1;
        while (position < table.Close)
        {
            SkipLuaTrivia(text, ref position, table.Close);
            if (position >= table.Close)
            {
                break;
            }

            string? name = null;
            if (IsLuaIdentifierStart(text[position]))
            {
                var start = position++;
                while (position < table.Close && IsLuaIdentifierPart(text[position]))
                {
                    position++;
                }
                name = text[start..position];
            }
            else if (text[position] == '[')
            {
                position++;
                SkipLuaWhitespace(text, ref position, table.Close);
                if (position < table.Close && text[position] is '\'' or '"')
                {
                    name = ReadLuaString(text, ref position, table.Close);
                    SkipLuaWhitespace(text, ref position, table.Close);
                    if (position < table.Close && text[position] == ']')
                    {
                        position++;
                    }
                }
            }
            else
            {
                if (text[position] == '{')
                {
                    var close = FindMatchingBrace(text, position);
                    position = close >= 0 ? close + 1 : table.Close;
                }
                else if (text[position] is '\'' or '"')
                {
                    _ = ReadLuaString(text, ref position, table.Close);
                }
                else
                {
                    position++;
                }
                continue;
            }

            SkipLuaWhitespace(text, ref position, table.Close);
            if (position >= table.Close || text[position] != '=')
            {
                continue;
            }

            position++;
            SkipLuaWhitespace(text, ref position, table.Close);
            if (position < table.Close && text[position] == '{')
            {
                var close = FindMatchingBrace(text, position);
                if (close < 0 || close > table.Close)
                {
                    break;
                }

                if (name is not null && name.Equals(property, StringComparison.OrdinalIgnoreCase))
                {
                    value = new LuaTableSpan(position, close);
                    return true;
                }

                position = close + 1;
            }
        }

        value = default;
        return false;
    }

    private static IEnumerable<string> EnumerateTopLevelLuaStrings(string text, LuaTableSpan table)
    {
        var position = table.Open + 1;
        while (position < table.Close)
        {
            SkipLuaTrivia(text, ref position, table.Close);
            if (position >= table.Close)
            {
                yield break;
            }

            if (text[position] is '\'' or '"')
            {
                yield return ReadLuaString(text, ref position, table.Close);
                continue;
            }

            if (text[position] == '{')
            {
                var close = FindMatchingBrace(text, position);
                position = close >= 0 ? close + 1 : table.Close;
                continue;
            }

            position++;
        }
    }

    private static bool TryGetLuaGroup(LuaProperties properties, out LuaTableSpan group)
    {
        if (properties.Group is { } value)
        {
            group = value;
            return true;
        }

        group = default;
        return false;
    }

    private static int FindMatchingBrace(string text, int open)
    {
        if (open < 0 || open >= text.Length || text[open] != '{')
        {
            return -1;
        }

        var depth = 0;
        for (var position = open; position < text.Length; position++)
        {
            if (text[position] is '\'' or '"')
            {
                _ = ReadLuaString(text, ref position, text.Length);
                position--;
                continue;
            }

            if (text[position] == '{')
            {
                depth++;
            }
            else if (text[position] == '}' && --depth == 0)
            {
                return position;
            }
        }

        return -1;
    }

    private static string ReadLuaString(string text, ref int position, int end)
    {
        var quote = text[position++];
        var builder = new StringBuilder();
        while (position < end)
        {
            var current = text[position++];
            if (current == quote)
            {
                return builder.ToString();
            }

            if (current == '\\' && position < end)
            {
                var escaped = text[position++];
                builder.Append(escaped switch
                {
                    'n' => '\n',
                    'r' => '\r',
                    't' => '\t',
                    _ => escaped,
                });
            }
            else
            {
                builder.Append(current);
            }
        }

        return builder.ToString();
    }

    private static void SkipLuaTrivia(string text, ref int position, int end)
    {
        while (position < end && (char.IsWhiteSpace(text[position]) || text[position] == ','))
        {
            position++;
        }
    }

    private static void SkipLuaWhitespace(string text, ref int position, int end)
    {
        while (position < end && char.IsWhiteSpace(text[position]))
        {
            position++;
        }
    }

    private static bool IsLuaIdentifierStart(char value) => char.IsLetter(value) || value == '_';

    private static bool IsLuaIdentifierPart(char value) => char.IsLetterOrDigit(value) || value == '_';

    private static string RemoveLuaComments(string text)
    {
        var result = text.ToCharArray();
        var position = 0;
        while (position < text.Length)
        {
            if (text[position] is '\'' or '"')
            {
                var quote = text[position++];
                while (position < text.Length)
                {
                    if (text[position] == '\\' && position + 1 < text.Length)
                    {
                        position += 2;
                        continue;
                    }
                    if (text[position++] == quote)
                    {
                        break;
                    }
                }
                continue;
            }

            if (position + 1 >= text.Length || text[position] != '-' || text[position + 1] != '-')
            {
                position++;
                continue;
            }

            var commentStart = position;
            var block = position + 3 < text.Length && text[position + 2] == '[' && text[position + 3] == '[';
            position += block ? 4 : 2;
            if (block)
            {
                while (position + 1 < text.Length && !(text[position] == ']' && text[position + 1] == ']'))
                {
                    position++;
                }
                position = Math.Min(text.Length, position + 2);
            }
            else
            {
                while (position < text.Length && text[position] is not ('\r' or '\n'))
                {
                    position++;
                }
            }

            for (var index = commentStart; index < position; index++)
            {
                if (result[index] is not ('\r' or '\n'))
                {
                    result[index] = ' ';
                }
            }
        }

        return new string(result);
    }

    private static Dictionary<string, OptionDefinition> BuildUniqueModuleLeafOptions(
        IReadOnlyDictionary<string, OptionDefinition> options) =>
        options
            .Select(pair =>
            {
                var segments = pair.Key.Split(
                    '/',
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                return new
                {
                    Lookup = segments.Length >= 2 ? $"{segments[0]}/{segments[^1]}" : string.Empty,
                    pair.Value,
                };
            })
            .Where(item => item.Lookup.Length > 0)
            .GroupBy(item => item.Lookup, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Take(2).Count() == 1)
            .ToDictionary(
                group => group.Key,
                group => group.First().Value,
                StringComparer.OrdinalIgnoreCase);

    public McmConfigurationMetadata? Resolve(string key)
    {
        var slash = key.IndexOf('/');
        if (slash <= 0 || slash >= key.Length - 1)
        {
            var fallbackName = HumanizeIdentifier(key);
            return new McmConfigurationMetadata(
                fallbackName,
                $"Параметр Mod Configuration Menu «{fallbackName}».",
                "Общие параметры MCM",
                "mcm/general",
                "Основные",
                int.MaxValue,
                int.MaxValue,
                null,
                null,
                null,
                null,
                null);
        }

        var module = key[..slash];
        var option = key[(slash + 1)..];
        _options.TryGetValue(key, out var definition);
        definition ??= _uniqueModuleLeafOptions.GetValueOrDefault(
            $"{module}/{option.Split('/').Last()}");
        var normalizedOption = option.Replace('/', '_');
        var standardLabelId = $"ui_mcm_{module}_{normalizedOption}";
        var labelId = definition?.TextId ?? standardLabelId;
        var leaf = option.Split('/').Last();
        var displayName = TranslateHintCaption(definition?.HintId, "ui_mcm_")
                          ?? (string.IsNullOrWhiteSpace(definition?.HintId)
                              ? TranslateToken(labelId)
                              : null)
                          ?? FirstTranslation(
                              standardLabelId,
                              $"ui_mcm_{module}_{leaf}",
                              $"ui_mcm_{normalizedOption}")
                          ?? HumanizeIdentifier(leaf);
        var description = TranslateHintDescription(definition?.HintId, "ui_mcm_")
                          ?? FirstTranslation(
                              string.IsNullOrWhiteSpace(definition?.HintId)
                                  ? AppendDescriptionSuffix(labelId)
                                  : null,
                              AppendDescriptionSuffix(standardLabelId),
                              $"ui_mcm_{module}_{leaf}_desc")
                          ?? $"Настройка «{displayName}» модуля «{HumanizeIdentifier(module)}».";
        var categoryTitle = _moduleTitleIds.TryGetValue(module, out var moduleTitleId)
            ? TranslateToken(moduleTitleId)
            : null;
        categoryTitle ??= FirstTranslation(
                             $"ui_mcm_{module}_title",
                             $"ui_mcm_menu_{module}",
                             $"ui_mcm_{module}")
                         ?? HumanizeIdentifier(module);

        var menuPath = key[..key.LastIndexOf('/')];
        var menuSegment = menuPath.Split('/').Last();
        var menuTitle = _menuTitleIds.TryGetValue(menuPath, out var menuTitleId)
            ? TranslateToken(menuTitleId)
            : null;
        menuTitle ??= menuPath.Equals(module, StringComparison.OrdinalIgnoreCase)
            ? categoryTitle
            : FirstTranslation(
                  $"ui_mcm_menu_{menuSegment}",
                  $"ui_mcm_{module}_{menuSegment}",
                  $"ui_mcm_{module}_{menuSegment}_title")
              ?? HumanizeIdentifier(menuSegment);
        var menuOrder = _nodeOrder.GetValueOrDefault(menuPath, int.MaxValue);

        return new McmConfigurationMetadata(
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
        var incomingSegments = key.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var lookupCommand = incomingSegments.LastOrDefault() ?? key;
        _anomalyOptions.TryGetValue(key, out var definition);
        definition ??= _anomalyCommands.GetValueOrDefault(lookupCommand);

        var resolvedKey = definition?.Key ?? key;
        var segments = resolvedKey.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var menuPath = definition?.MenuPath
                       ?? (segments.Length > 1 ? string.Join('/', segments[..^1]) : "other/general");
        var menuSegment = menuPath.Split('/').Last();
        var category = segments.Length > 1 ? segments[0] : "other";
        var normalized = resolvedKey.Replace('/', '_');
        var labelId = definition?.LabelId ?? $"ui_mm_{normalized}";
        var displayName = TranslateToken(labelId)
                          ?? FirstTranslation($"ui_mm_{normalized}", $"ui_mm_{lookupCommand}")
                          ?? HumanizeIdentifier(segments.LastOrDefault() ?? key);
        var description = TranslateHintDescription(definition?.HintId, "ui_mm_")
                          ?? FirstTranslation(
                              AppendDescriptionSuffix(labelId),
                              $"ui_mm_{normalized}_desc",
                              $"ui_mm_{lookupCommand}_desc")
                          ?? $"Настройка оригинальной Anomaly «{displayName}».";
        var categoryTitle = FirstTranslation($"ui_mm_menu_{category}", $"ui_mm_title_{category}")
                            ?? HumanizeAnomalyMenu(category);
        var normalizedMenu = menuPath.Replace('/', '_');
        var menuTitle = TranslateToken(definition?.MenuTitleId)
                        ?? FirstTranslation(
                            $"ui_mm_title_{normalizedMenu}",
                            $"ui_mm_menu_{normalizedMenu}",
                            $"ui_mm_menu_{menuSegment}",
                            $"ui_mm_title_{menuSegment}")
                        ?? HumanizeAnomalyMenu(menuSegment);

        return new McmConfigurationMetadata(
            Clean(displayName),
            Clean(description),
            Clean(categoryTitle),
            menuPath,
            Clean(menuTitle),
            AnomalyMenuOrder(menuPath),
            definition?.Order ?? displayOrder,
            definition?.ControlType,
            definition?.Minimum,
            definition?.Maximum,
            definition?.Step,
            definition?.DefaultValue);
    }

    private string? Translate(string id) => _translations.GetValueOrDefault(id);

    private string? FirstTranslation(params string?[] ids)
    {
        foreach (var id in ids)
        {
            var translated = TranslateToken(id);
            if (!string.IsNullOrWhiteSpace(translated))
            {
                return translated;
            }
        }

        return null;
    }

    private string? TranslateToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var translated = Translate(token);
        if (!string.IsNullOrWhiteSpace(translated)
            && !translated.Equals(token, StringComparison.OrdinalIgnoreCase))
        {
            return translated;
        }

        return LooksLikeLocalizationId(token) || LooksLikeTechnicalIdentifier(token) ? null : token;
    }

    private string? TranslateHintCaption(string? hint, string prefix)
    {
        if (string.IsNullOrWhiteSpace(hint))
        {
            return null;
        }

        return TranslateToken(BuildHintLocalizationId(hint, prefix));
    }

    private string? TranslateHintDescription(string? hint, string prefix)
    {
        if (string.IsNullOrWhiteSpace(hint))
        {
            return null;
        }

        return TranslateToken(AppendDescriptionSuffix(BuildHintLocalizationId(hint, prefix)));
    }

    private static string BuildHintLocalizationId(string hint, string prefix)
    {
        var normalized = hint.EndsWith("_desc", StringComparison.OrdinalIgnoreCase)
            ? hint[..^5]
            : hint;
        return normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? normalized
            : prefix + normalized;
    }

    private string HumanizeIdentifier(string value)
    {
        var leaf = value.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault() ?? value;
        if (KnownIdentifierTitles.TryGetValue(leaf, out var known))
        {
            return known;
        }

        var keyBinding = Regex.Match(
            leaf,
            @"^bind(?<secondary>_sec)?_(?<action>.+)$",
            RegexOptions.IgnoreCase);
        if (keyBinding.Success)
        {
            var actionId = keyBinding.Groups["action"].Value;
            var action = KnownKeyBindingTitles.GetValueOrDefault(actionId) ?? HumanizeWords(actionId);
            return keyBinding.Groups["secondary"].Success
                ? $"Дополнительная клавиша: {action}"
                : $"Клавиша: {action}";
        }

        var levelMatch = Regex.Match(leaf, @"^lvl_(?<level>.+)_priority$", RegexOptions.IgnoreCase);
        if (levelMatch.Success)
        {
            var level = levelMatch.Groups["level"].Value;
            var levelTitle = FirstTranslation(
                                 level,
                                 $"st_level_name_{level}",
                                 $"st_level_{level}",
                                 $"level_name_{level}",
                                 $"ui_st_level_{level}")
                             ?? KnownLevelTitles.GetValueOrDefault(level)
                             ?? HumanizeWords(level);
            return $"Приоритет локации «{levelTitle}»";
        }

        return HumanizeWords(leaf);
    }

    private string HumanizeWords(string value)
    {
        var separated = Regex.Replace(value, "([a-zа-я0-9])([A-ZА-Я])", "$1 $2");
        separated = Regex.Replace(separated, "([A-Za-zА-Яа-я])([0-9])", "$1 $2");
        separated = Regex.Replace(separated, "([0-9])([A-Za-zА-Яа-я])", "$1 $2");
        var words = separated
            .Replace('_', ' ')
            .Replace('-', ' ')
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(word => !IgnoredTechnicalWords.Contains(word))
            .Select(word => TranslateSemanticWord(word) ?? word)
            .ToArray();
        if (words.Length == 0)
        {
            return "Параметр";
        }

        var result = string.Join(' ', words);
        return char.ToUpperInvariant(result[0]) + result[1..];
    }

    private string? TranslateSemanticWord(string word)
    {
        if (KnownWords.TryGetValue(word, out var known))
        {
            return known;
        }

        return FirstTranslation(
            $"st_faction_{word}",
            $"st_name_{word}",
            $"ui_st_{word}");
    }

    private static string HumanizeAnomalyMenu(string value) =>
        KnownAnomalyMenus.GetValueOrDefault(value)
        ?? (value.Length == 0 ? "Основные" : char.ToUpperInvariant(value[0]) + value[1..].Replace('_', ' '));

    private static string AppendDescriptionSuffix(string? id) =>
        string.IsNullOrWhiteSpace(id) ? string.Empty : id + "_desc";

    private static bool LooksLikeLocalizationId(string value)
    {
        var normalized = value.TrimStart('!');
        return (normalized.StartsWith("ui_", StringComparison.OrdinalIgnoreCase)
                || normalized.StartsWith("st_", StringComparison.OrdinalIgnoreCase)
                || normalized.StartsWith("mcm_", StringComparison.OrdinalIgnoreCase))
               && !normalized.Any(char.IsWhiteSpace);
    }

    private static bool LooksLikeTechnicalIdentifier(string value) =>
        !value.Any(char.IsWhiteSpace)
        && (value.Contains('_') || value.Contains('/') || value.Contains('\\'));

    private static readonly HashSet<string> IgnoredTechnicalWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "ui", "st", "mm", "mcm", "opt", "option", "cfg",
    };

    private static readonly Dictionary<string, string> KnownAnomalyMenus =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["video"] = "Видео",
            ["basic"] = "Основные",
            ["advanced"] = "Расширенные",
            ["hud"] = "Интерфейс",
            ["player"] = "Игрок",
            ["weather"] = "Погода",
            ["night"] = "Ночь",
            ["sound"] = "Звук",
            ["general"] = "Основные",
            ["radio"] = "Радио",
            ["control"] = "Управление",
            ["keybind"] = "Назначение клавиш",
            ["gameplay"] = "Геймплей",
            ["alife"] = "Мир Зоны",
            ["warfare"] = "Война группировок",
            ["other"] = "Другое",
        };

    private static readonly Dictionary<string, string> KnownIdentifierTitles =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["enabled"] = "Включено",
            ["enable"] = "Включить",
            ["debug_mode"] = "Режим отладки",
            ["modifier"] = "Клавиша-модификатор",
            ["second_key"] = "Дополнительная клавиша",
            ["alarm_priority"] = "Приоритет будильника",
            ["status_key"] = "Клавиша просмотра состояния",
            ["inputMethod"] = "Способ управления",
            ["BHSMode"] = "Совместимость с системой здоровья тела",
            ["amount_artefact"] = "Количество артефактов",
            ["allow_exo"] = "Разрешить экзоскелеты",
            ["g_game_difficulty"] = "Сложность игры",
            ["g_autopickup"] = "Автоматический подбор предметов",
            ["g_dynamic_music"] = "Динамическая музыка",
            ["g_important_save"] = "Важные сохранения",
            ["g_crouch_toggle"] = "Переключение приседания",
            ["g_lookout_toggle"] = "Переключение выглядывания",
            ["g_freelook_toggle"] = "Переключение свободного обзора",
            ["g_sleep_time"] = "Длительность сна",
            ["g_hit_pwr_modif"] = "Множитель урона игрока",
            ["vid_mode"] = "Разрешение экрана",
            ["renderer"] = "Тип рендера",
            ["rs_v_sync"] = "Вертикальная синхронизация",
            ["rs_refresh_60hz"] = "Принудительная частота 60 Гц",
            ["rs_vis_distance"] = "Дальность видимости",
            ["r2_sun"] = "Солнце и динамические тени",
            ["r2_sun_quality"] = "Качество солнечных теней",
            ["r2_ssao"] = "Затенение SSAO",
            ["r2_volumetric_lights"] = "Объёмный свет",
            ["r2_steep_parallax"] = "Глубокий параллакс",
            ["r2_detail_bump"] = "Детальный рельеф",
            ["r2_dof_enable"] = "Глубина резкости",
            ["r3_dynamic_wet_surfaces"] = "Мокрые поверхности",
            ["r4_enable_tessellation"] = "Тесселяция",
            ["r__tf_aniso"] = "Анизотропная фильтрация",
            ["texture_lod"] = "Качество текстур",
            ["fov"] = "Угол обзора",
            ["hud_fov"] = "Положение оружия",
            ["snd_volume_eff"] = "Громкость эффектов",
            ["snd_volume_music"] = "Громкость музыки",
            ["snd_efx"] = "Звуковые эффекты EFX",
            ["snd_acceleration"] = "Аппаратное ускорение звука",
            ["snd_targets"] = "Количество источников звука",
            ["snd_cache_size"] = "Размер кэша звука",
            ["mouse_sens"] = "Чувствительность мыши",
            ["mouse_invert"] = "Инверсия мыши",
            ["cam_inert"] = "Инерция камеры",
            ["hud_draw"] = "Показывать интерфейс",
            ["hud_crosshair"] = "Показывать прицел",
            ["hud_crosshair_dist"] = "Расстояние до цели",
            ["hud_weapon"] = "Показывать оружие",
            ["g_3d_pda"] = "Трёхмерный КПК",
            ["discord_status"] = "Статус Discord",
            ["_preset"] = "Профиль качества",
        };

    private static readonly Dictionary<string, string> KnownKeyBindingTitles =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["up"] = "вверх", ["jump"] = "прыжок", ["crouch"] = "присесть", ["accel"] = "шаг",
            ["sprint_toggle"] = "бег", ["forward"] = "вперёд", ["back"] = "назад",
            ["lstrafe"] = "шаг влево", ["rstrafe"] = "шаг вправо", ["llookout"] = "выглянуть влево",
            ["rlookout"] = "выглянуть вправо", ["torch"] = "фонарь", ["night_vision"] = "прибор ночного видения",
            ["wpn_next"] = "следующее оружие", ["wpn_fire"] = "стрельба", ["wpn_reload"] = "перезарядка",
            ["wpn_func"] = "функция оружия", ["wpn_firemode_prev"] = "предыдущий режим огня",
            ["wpn_firemode_next"] = "следующий режим огня", ["pause"] = "пауза", ["scores"] = "статистика",
            ["screenshot"] = "снимок экрана", ["quit"] = "выход", ["console"] = "консоль",
            ["inventory"] = "инвентарь", ["quick_save"] = "быстрое сохранение", ["quick_load"] = "быстрая загрузка",
            ["safemode"] = "безопасный режим", ["editor"] = "редактор",
        };

    private static readonly Dictionary<string, string> KnownLevelTitles =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["l01_escape"] = "Кордон",
            ["l02_garbage"] = "Свалка",
            ["l03_agroprom"] = "Агропром",
            ["l04_darkvalley"] = "Тёмная Долина",
            ["l05_bar"] = "Бар",
            ["l06_rostok"] = "Дикая территория",
            ["l07_military"] = "Армейские склады",
            ["l08_yantar"] = "Янтарь",
            ["l09_deadcity"] = "Мёртвый город",
            ["l10_limansk"] = "Лиманск",
            ["l10_radar"] = "Радар",
            ["l10_red_forest"] = "Рыжий лес",
            ["l11_hospital"] = "Заброшенный госпиталь",
            ["l11_pripyat"] = "Припять",
            ["l12_stancia"] = "ЧАЭС",
            ["l13_generators"] = "Генераторы",
            ["jupiter"] = "Юпитер",
            ["zaton"] = "Затон",
            ["pripyat"] = "Припять",
        };

    private static readonly Dictionary<string, string> KnownWords =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["enable"] = "включить", ["enabled"] = "включено", ["disable"] = "отключить", ["disabled"] = "отключено",
            ["allow"] = "разрешить", ["use"] = "использовать", ["show"] = "показывать", ["hide"] = "скрывать",
            ["auto"] = "автоматически", ["automatic"] = "автоматический", ["debug"] = "отладка", ["mode"] = "режим",
            ["main"] = "основные", ["settings"] = "настройки", ["general"] = "общие", ["advanced"] = "расширенные",
            ["volume"] = "громкость", ["sound"] = "звук", ["music"] = "музыка", ["distance"] = "дистанция",
            ["range"] = "диапазон", ["speed"] = "скорость", ["duration"] = "длительность", ["delay"] = "задержка",
            ["chance"] = "вероятность", ["probability"] = "вероятность", ["factor"] = "множитель", ["power"] = "сила",
            ["drain"] = "расход", ["recover"] = "восстановление", ["weight"] = "вес", ["cost"] = "стоимость",
            ["repair"] = "ремонт", ["degradation"] = "износ", ["status"] = "состояние", ["key"] = "клавиша",
            ["keybind"] = "назначение клавиши", ["modifier"] = "модификатор", ["second"] = "дополнительная",
            ["position"] = "положение", ["pos"] = "положение", ["size"] = "размер", ["offset"] = "смещение",
            ["anchor"] = "привязка", ["color"] = "цвет", ["blur"] = "размытие", ["zoom"] = "увеличение",
            ["opacity"] = "прозрачность", ["scale"] = "масштаб", ["quality"] = "качество", ["amount"] = "количество",
            ["limit"] = "ограничение", ["max"] = "максимум", ["min"] = "минимум", ["player"] = "игрок",
            ["actor"] = "персонаж", ["npc"] = "NPC", ["ai"] = "ИИ", ["hud"] = "интерфейс", ["pda"] = "КПК",
            ["weapon"] = "оружие", ["stamina"] = "выносливость", ["climb"] = "карабканье", ["detection"] = "определение",
            ["animation"] = "анимация", ["randomization"] = "случайный выбор", ["trigger"] = "срабатывание",
            ["grunt"] = "звук усилия", ["hardcore"] = "хардкорный", ["input"] = "управление", ["method"] = "способ",
            ["ray"] = "луч", ["steps"] = "шаги", ["throttle"] = "частота проверки", ["unstuck"] = "выход из застревания",
            ["friendly"] = "дружественные", ["squad"] = "отряд", ["squads"] = "отряды", ["spawn"] = "появление",
            ["spawns"] = "появления", ["active"] = "активные", ["safe"] = "безопасная", ["smart"] = "точка Зоны",
            ["talk"] = "реплика", ["faction"] = "группировка", ["factions"] = "группировки", ["can"] = "могут",
            ["between"] = "между", ["carry"] = "переносимый", ["ignore"] = "игнорировать", ["story"] = "сюжетный",
            ["id"] = "идентификатор", ["immersion"] = "иммерсивные", ["msgs"] = "сообщения", ["messages"] = "сообщения",
            ["artifact"] = "артефакт", ["artifacts"] = "артефакты", ["artefact"] = "артефакт", ["consumable"] = "расходники",
            ["upgrade"] = "улучшение", ["kit"] = "комплект", ["strict"] = "строгий",
            ["per"] = "за", ["width"] = "ширина", ["check"] = "проверка", ["alternative"] = "альтернативное",
            ["priority"] = "приоритет", ["resource"] = "ресурс", ["count"] = "количество", ["target"] = "цель",
            ["random"] = "случайный", ["offline"] = "вне симуляции", ["multiplier"] = "множитель",
            ["army"] = "Военные", ["bandit"] = "Бандиты", ["csky"] = "Чистое небо", ["dolg"] = "Долг",
            ["ecolog"] = "Учёные", ["freedom"] = "Свобода", ["killer"] = "Наёмники", ["monolith"] = "Монолит",
            ["renegade"] = "Ренегаты", ["stalker"] = "Одиночки", ["greh"] = "Грех", ["isg"] = "ООН",
            ["bind"] = "клавиша", ["sec"] = "дополнительная", ["jump"] = "прыжок", ["crouch"] = "приседание",
            ["sprint"] = "бег", ["forward"] = "вперёд", ["back"] = "назад", ["torch"] = "фонарь",
            ["night"] = "ночное", ["vision"] = "видение", ["inventory"] = "инвентарь", ["reload"] = "перезарядка",
            ["save"] = "сохранение", ["load"] = "загрузка", ["quick"] = "быстрое", ["preset"] = "профиль качества",
            ["monitor"] = "монитор", ["persistent"] = "постоянная", ["weather"] = "погода",
            ["clear"] = "ясная погода", ["cloudy"] = "облачно", ["foggy"] = "туман", ["partly"] = "переменная облачность",
            ["rain"] = "дождь", ["storm"] = "гроза", ["occurrence"] = "частота появления", ["period"] = "период",
            ["companion"] = "напарники", ["equipment"] = "снаряжение", ["exo"] = "экзоскелеты",
            ["bullet"] = "пули", ["ammo"] = "боеприпасы", ["casing"] = "гильзы", ["powder"] = "порох",
            ["salvage"] = "разбор", ["propellant"] = "метательный заряд", ["good"] = "исправные",
            ["threshold"] = "порог",
            ["lifetime"] = "время существования", ["meshes"] = "модели", ["mutant"] = "мутанты",
            ["last"] = "последний", ["release"] = "выпуск", ["version"] = "версия", ["welcome"] = "приветственное",
            ["message"] = "сообщение", ["shown"] = "показано", ["tab"] = "вкладка", ["attachments"] = "модули оружия",
            ["devices"] = "устройства", ["food"] = "еда", ["grenades"] = "гранаты", ["meds"] = "медицина",
            ["slots"] = "слоты", ["manual"] = "ручной", ["press"] = "нажатие", ["type"] = "тип",
            ["alpha"] = "прозрачность", ["font"] = "шрифт", ["scheme"] = "схема", ["indexers"] = "индексы",
            ["session"] = "сеанс", ["start"] = "запуск", ["inert"] = "инерция", ["camera"] = "камера",
            ["crosshair"] = "прицел", ["autopickup"] = "автоматический подбор", ["discord"] = "Discord",
            ["update"] = "обновление", ["rate"] = "частота", ["first"] = "от первого лица", ["death"] = "смерть",
            ["direction"] = "направление", ["smoothing"] = "сглаживание", ["freelook"] = "свободный обзор",
            ["while"] = "во время", ["reloading"] = "перезарядки", ["dynamic"] = "динамический",
            ["aggression"] = "агрессивность", ["expansion"] = "расширения", ["keep"] = "сохранять",
            ["base"] = "базу", ["linked"] = "связанные", ["level"] = "уровни", ["targeting"] = "выбор цели",
            ["participate"] = "участвовать", ["warfare"] = "войне группировок", ["apply"] = "применять",
            ["aimmode"] = "режим прицеливания", ["remember"] = "запоминать", ["fun"] = "развлекательные функции",
            ["allowed"] = "разрешены", ["backup"] = "резервные", ["tail"] = "хвостовые", ["sounds"] = "звуки",
            ["bounce"] = "отражение", ["tick"] = "опрос", ["interval"] = "интервал",
            ["length"] = "длина", ["fall"] = "затухание", ["replaced"] = "заменённый", ["guns"] = "оружие",
            ["indoor"] = "в помещении", ["milliseconds"] = "миллисекунды", ["combat"] = "бой",
            ["assault"] = "штурм", ["camper"] = "оборона", ["guard"] = "охрана", ["snipe"] = "снайперский режим",
            ["support"] = "поддержка", ["far"] = "далеко", ["near"] = "близко", ["normal"] = "обычно",
            ["formation"] = "построение", ["line"] = "линия", ["spread"] = "рассредоточиться",
            ["help"] = "помочь", ["wounded"] = "раненым", ["loot"] = "обыскивать", ["corpses"] = "тела",
            ["items"] = "предметы", ["movement"] = "движение", ["follow"] = "следовать", ["patrol"] = "патруль",
            ["relax"] = "отдых", ["wait"] = "ждать", ["waypoint"] = "точку маршрута", ["add"] = "добавить",
            ["deselect"] = "снять выбор", ["look"] = "смотреть", ["move"] = "двигаться", ["retreat"] = "отступить",
            ["select"] = "выбрать", ["readiness"] = "готовность", ["attack"] = "атака", ["defend"] = "оборона",
            ["hurry"] = "быстрее", ["stance"] = "стойка", ["prone"] = "лёжа", ["sneak"] = "красться",
            ["stand"] = "стоять", ["best"] = "лучшее", ["pistol"] = "пистолет", ["rifle"] = "винтовка",
            ["shotgun"] = "дробовик", ["smg"] = "пистолет-пулемёт", ["sniper"] = "снайперская винтовка",
        };

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
        var availableRoots = Directory.EnumerateDirectories(modsRoot)
            .Select(Path.GetFullPath)
            .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
            .ThenBy(path => path, StringComparer.Ordinal)
            .ToArray();
        var iniPath = Path.Combine(mo2Root, "ModOrganizer.ini");
        if (!File.Exists(iniPath))
        {
            // A plain mods directory without MO2 state is also used by tests and
            // portable addon packs; in that mode every directory is intentional.
            return availableRoots;
        }

        var selectedProfile = ReadText(iniPath).Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("selected_profile=", StringComparison.OrdinalIgnoreCase))
            .Select(line => Mo2ProfileManager.DecodeQtByteArray(line[(line.IndexOf('=') + 1)..].Trim()))
            .FirstOrDefault();
        if (string.IsNullOrWhiteSpace(selectedProfile))
        {
            return [];
        }

        var profilesRoot = Path.Combine(mo2Root, "profiles");
        var profileRoot = ResolveNamedChildDirectory(profilesRoot, selectedProfile);
        if (profileRoot is null)
        {
            return [];
        }

        var modListPath = Path.Combine(profileRoot, "modlist.txt");
        if (!File.Exists(modListPath))
        {
            return [];
        }

        var active = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in ReadText(modListPath).Split('\n'))
        {
            var line = rawLine.Trim();
            if (!line.StartsWith('+') || line.Length == 1)
            {
                continue;
            }

            var root = ResolveNamedChildDirectory(modsRoot, line[1..].Trim(), availableRoots);
            if (root is not null && seen.Add(root))
            {
                active.Add(root);
            }
        }

        // modlist.txt is stored from highest to lowest priority. Metadata must be
        // applied in the opposite direction so the highest-priority enabled mod
        // wins last, exactly as it does in MO2's virtual filesystem.
        active.Reverse();
        return active;
    }

    private static string? ResolveNamedChildDirectory(
        string parentRoot,
        string requestedName,
        IReadOnlyList<string>? knownChildren = null)
    {
        if (string.IsNullOrWhiteSpace(requestedName)
            || Path.IsPathRooted(requestedName)
            || requestedName.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
        {
            return null;
        }

        var fullParent = Path.GetFullPath(parentRoot);
        if (!Directory.Exists(fullParent))
        {
            return null;
        }

        var exact = Path.GetFullPath(Path.Combine(fullParent, requestedName));
        if (IsContainedChild(fullParent, exact) && Directory.Exists(exact))
        {
            return exact;
        }

        var children = knownChildren ?? Directory.EnumerateDirectories(fullParent)
            .Select(Path.GetFullPath)
            .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
            .ThenBy(path => path, StringComparer.Ordinal)
            .ToArray();
        var normalizedRequested = NormalizeMo2Name(requestedName);
        var matches = children
            .Where(path => IsContainedChild(fullParent, path))
            .Where(path => NormalizeMo2Name(Path.GetFileName(path)).Equals(
                normalizedRequested,
                StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private static bool IsContainedChild(string parentRoot, string candidate)
    {
        var relative = Path.GetRelativePath(parentRoot, candidate);
        return !Path.IsPathRooted(relative)
               && !relative.Equals(".", StringComparison.Ordinal)
               && !relative.Equals("..", StringComparison.Ordinal)
               && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
               && !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);
    }

    private static string NormalizeMo2Name(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormKC)
            .Trim()
            .Replace('\u2010', '-')
            .Replace('\u2011', '-')
            .Replace('\u2012', '-')
            .Replace('\u2013', '-')
            .Replace('\u2014', '-')
            .Replace('\u2212', '-');
        return Regex.Replace(normalized, @"\s+", " ");
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
        // Some addons put a legal XML comment before an XML declaration. The
        // declaration is then no longer at the document start and XDocument
        // rejects the whole string table. XRay accepts these files, so remove
        // the optional declaration before parsing to mirror the game.
        var normalizedText = Regex.Replace(
            text.TrimStart('\uFEFF'),
            @"<\?xml[^?]*\?>",
            string.Empty,
            RegexOptions.IgnoreCase);
        IReadOnlyList<(string Id, string Value)> entries;
        try
        {
            var document = XDocument.Parse(normalizedText, LoadOptions.None);
            entries = document.Descendants()
                .Where(item => item.Name.LocalName == "string")
                .Select(element => (
                    Id: element.Attribute("id")?.Value ?? string.Empty,
                    Value: element.Descendants()
                               .FirstOrDefault(item => item.Name.LocalName == "text")?.Value
                           ?? string.Empty))
                .ToArray();
        }
        catch (System.Xml.XmlException)
        {
            entries = ExtractTolerantTranslationEntries(normalizedText);
        }

        foreach (var (id, value) in entries)
        {
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

    private static List<(string Id, string Value)> ExtractTolerantTranslationEntries(string text)
    {
        // XRay string tables in the wild sometimes contain malformed decorative
        // pseudo-comments. Extract each complete string independently so one bad
        // neighbour cannot discard every usable localization in the file.
        var withoutPseudoComments = Regex.Replace(
            text,
            @"<!\s*-+.*?-+\s*>",
            string.Empty,
            RegexOptions.Singleline);
        var result = new List<(string Id, string Value)>();
        foreach (Match block in Regex.Matches(
                     withoutPseudoComments,
                     "<string\\b[^>]*\\bid\\s*=\\s*(?<quote>['\"])(?<id>.*?)\\k<quote>[^>]*>(?<body>.*?)</string\\s*>",
                     RegexOptions.IgnoreCase | RegexOptions.Singleline))
        {
            var textElement = Regex.Match(
                block.Groups["body"].Value,
                @"<text\b[^>]*>(?<value>.*?)</text\s*>",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (!textElement.Success)
            {
                continue;
            }

            var value = Regex.Replace(
                textElement.Groups["value"].Value,
                @"<!\[CDATA\[(?<content>.*?)\]\]>",
                "${content}",
                RegexOptions.Singleline);
            value = Regex.Replace(value, @"<[^>]+>", string.Empty, RegexOptions.Singleline);
            result.Add((
                WebUtility.HtmlDecode(block.Groups["id"].Value).Trim(),
                WebUtility.HtmlDecode(value).Trim()));
        }

        return result;
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

    private static double? ParseNumber(string? value) =>
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

    private static string? ParseLuaDefaultValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Contains('(')
            || value.StartsWith('{'))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Equals("true", StringComparison.OrdinalIgnoreCase)
               || trimmed.Equals("false", StringComparison.OrdinalIgnoreCase)
            ? trimmed.ToLowerInvariant()
            : trimmed;
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value)
        ? null
        : Regex.Replace(
                value.Trim().TrimStart('!').Replace("\\n", " ", StringComparison.Ordinal),
                @"%c\[[^\]]*\]",
                string.Empty,
                RegexOptions.IgnoreCase)
            .Trim();

    private sealed record OptionDefinition(
        string? TextId,
        string? HintId,
        string ControlType,
        double? Minimum,
        double? Maximum,
        double? Step,
        string? DefaultValue,
        int Order);

    private sealed record AnomalyOptionDefinition(
        string Key,
        string? Command,
        string MenuPath,
        string? LabelId,
        string? HintId,
        string? MenuTitleId,
        int Order,
        string ControlType,
        double? Minimum,
        double? Maximum,
        double? Step,
        string? DefaultValue);

    private readonly record struct LuaTableSpan(int Open, int Close);

    private sealed class LuaProperties : Dictionary<string, string>
    {
        public LuaProperties() : base(StringComparer.OrdinalIgnoreCase)
        {
        }

        public LuaTableSpan? Group { get; set; }
    }
}
