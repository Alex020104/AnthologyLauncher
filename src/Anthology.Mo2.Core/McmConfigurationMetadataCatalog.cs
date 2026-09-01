using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Anthology.Mo2.Core;

public sealed record McmConfigurationMetadata(
    string? DisplayName,
    string? Description,
    string? CategoryDisplayName,
    string? ControlType,
    double? Minimum,
    double? Maximum,
    double? Step);

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
    private static readonly Regex SlideTextPattern = new(
        "type\\s*=\\s*['\"]slide['\"][^{}]{0,600}?text\\s*=\\s*['\"](?<id>[^'\"]+)['\"]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private readonly IReadOnlyDictionary<string, OptionDefinition> _options;
    private readonly IReadOnlyDictionary<string, string> _translations;
    private readonly IReadOnlyDictionary<string, string> _moduleTitleIds;

    private McmConfigurationMetadataCatalog(
        IReadOnlyDictionary<string, OptionDefinition> options,
        IReadOnlyDictionary<string, string> translations,
        IReadOnlyDictionary<string, string> moduleTitleIds)
    {
        _options = options;
        _translations = translations;
        _moduleTitleIds = moduleTitleIds;
    }

    public static McmConfigurationMetadataCatalog Empty { get; } = new(
        new Dictionary<string, OptionDefinition>(StringComparer.OrdinalIgnoreCase),
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    public static McmConfigurationMetadataCatalog Load(string? mo2Root)
    {
        if (string.IsNullOrWhiteSpace(mo2Root) || !Directory.Exists(mo2Root))
        {
            return Empty;
        }

        var modsRoot = Path.Combine(Path.GetFullPath(mo2Root), "mods");
        if (!Directory.Exists(modsRoot))
        {
            return Empty;
        }

        var options = new Dictionary<string, OptionDefinition>(StringComparer.OrdinalIgnoreCase);
        var translations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var moduleTitleIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var localizationRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (var scriptPath in Directory.EnumerateFiles(modsRoot, "*_mcm.script", SearchOption.AllDirectories))
            {
                var text = ReadText(scriptPath);
                var module = ModulePattern.Match(text).Groups["id"].Value;
                if (string.IsNullOrWhiteSpace(module))
                {
                    module = Path.GetFileNameWithoutExtension(scriptPath).Replace("_mcm", string.Empty, StringComparison.OrdinalIgnoreCase);
                }

                var titleMatch = SlideTextPattern.Match(text);
                if (titleMatch.Success)
                {
                    moduleTitleIds[module] = titleMatch.Groups["id"].Value;
                }

                foreach (Match tableMatch in SimpleTablePattern.Matches(text))
                {
                    var properties = StringPropertyPattern.Matches(tableMatch.Groups["body"].Value)
                        .Cast<Match>()
                        .ToDictionary(
                            match => match.Groups["name"].Value,
                            match => match.Groups["value"].Value,
                            StringComparer.OrdinalIgnoreCase);
                    if (!properties.TryGetValue("id", out var optionId)
                        || !properties.TryGetValue("type", out var type)
                        || type is "title" or "line" or "slide")
                    {
                        continue;
                    }

                    var numbers = NumberPropertyPattern.Matches(tableMatch.Groups["body"].Value)
                        .Cast<Match>()
                        .ToDictionary(
                            match => match.Groups["name"].Value,
                            match => ParseNumber(match.Groups["value"].Value),
                            StringComparer.OrdinalIgnoreCase);
                    options[$"{module}/{optionId}"] = new OptionDefinition(
                        properties.GetValueOrDefault("text"),
                        type,
                        numbers.GetValueOrDefault("min"),
                        numbers.GetValueOrDefault("max"),
                        numbers.GetValueOrDefault("step"));
                }

                var scriptsDirectory = Path.GetDirectoryName(scriptPath);
                var gamedataRoot = scriptsDirectory is null ? null : Directory.GetParent(scriptsDirectory)?.FullName;
                var modRoot = gamedataRoot is null ? null : Directory.GetParent(gamedataRoot)?.FullName;
                if (modRoot is not null)
                {
                    localizationRoots.Add(modRoot);
                }
            }

            foreach (var modRoot in localizationRoots)
            {
                LoadTranslations(Path.Combine(modRoot, "gamedata", "configs", "text", "eng"), translations, overwrite: false);
                LoadTranslations(Path.Combine(modRoot, "gamedata", "configs", "text", "rus"), translations, overwrite: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new McmConfigurationMetadataCatalog(options, translations, moduleTitleIds);
        }

        return new McmConfigurationMetadataCatalog(options, translations, moduleTitleIds);
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

        return displayName is null && description is null && categoryTitle is null && definition is null
            ? null
            : new McmConfigurationMetadata(
                Clean(displayName),
                Clean(description),
                Clean(categoryTitle),
                definition?.ControlType,
                definition?.Minimum,
                definition?.Maximum,
                definition?.Step);
    }

    private string? Translate(string id) => _translations.GetValueOrDefault(id);

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
                var document = XDocument.Parse(ReadText(path), LoadOptions.None);
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
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Xml.XmlException)
            {
                // One malformed localization file must not hide settings from every other mod.
            }
        }
    }

    private static string ReadText(string path)
    {
        var bytes = File.ReadAllBytes(path);
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

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value)
        ? null
        : value.Replace("\\n", " ", StringComparison.Ordinal).Trim();

    private sealed record OptionDefinition(
        string? TextId,
        string ControlType,
        double? Minimum,
        double? Maximum,
        double? Step);
}
