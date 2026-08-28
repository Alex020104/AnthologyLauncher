namespace Anthology.Contracts;

public sealed record AnthologyLanguage(
    string Code,
    string ShortLabel,
    string NativeName,
    string TranslationCode);

public static class AnthologyLanguages
{
    public static readonly IReadOnlyList<AnthologyLanguage> All =
    [
        new("ru", "RU", "Русский", "ru"),
        new("en", "EN", "English", "en"),
        new("de", "DE", "Deutsch", "de"),
        new("pl", "PL", "Polski", "pl"),
        new("fr", "FR", "Français", "fr"),
        new("es", "ES", "Español", "es"),
        new("zh", "ZH", "简体中文", "zh"),
        new("ja", "JA", "日本語", "ja"),
    ];

    public static bool IsSupported(string? code) =>
        All.Any(language => string.Equals(language.Code, Normalize(code), StringComparison.OrdinalIgnoreCase));

    public static string Normalize(string? code)
    {
        var normalized = string.IsNullOrWhiteSpace(code) ? "ru" : code.Trim().ToLowerInvariant();
        return normalized switch
        {
            "zh-cn" or "zh-hans" => "zh",
            _ => normalized,
        };
    }

    public static AnthologyLanguage Get(string? code) =>
        All.FirstOrDefault(language => string.Equals(language.Code, Normalize(code), StringComparison.OrdinalIgnoreCase))
        ?? All[0];
}

public sealed record TextTranslationRequest(
    string Text,
    string TargetLanguage,
    string SourceLanguage = "auto");

public sealed record TextTranslationResponse(
    string OriginalText,
    string TranslatedText,
    string SourceLanguage,
    string TargetLanguage);
