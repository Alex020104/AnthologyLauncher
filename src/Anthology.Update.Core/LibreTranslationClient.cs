using System.Net.Http.Json;
using System.Text.Json;
using Anthology.Contracts;

namespace Anthology.Update.Core;

public sealed record TranslationServiceOptions(
    string BaseUrl,
    string ApiKey = "");

public sealed record TranslationBatchResult(
    IReadOnlyList<string> Translations,
    string DetectedLanguage,
    string TargetLanguage);

public sealed class LibreTranslationClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly bool _ownsClient;

    public LibreTranslationClient(HttpClient? httpClient = null)
    {
        _ownsClient = httpClient is null;
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
    }

    public async Task<TranslationBatchResult> TranslateAsync(
        TranslationServiceOptions options,
        IReadOnlyList<string> texts,
        string targetLanguage,
        string sourceLanguage = "auto",
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(texts);
        if (texts.Count == 0)
        {
            return new TranslationBatchResult([], sourceLanguage, AnthologyLanguages.Normalize(targetLanguage));
        }

        var endpoint = GetEndpoint(options.BaseUrl);
        var target = AnthologyLanguages.Get(targetLanguage);
        var source = string.Equals(sourceLanguage, "auto", StringComparison.OrdinalIgnoreCase)
            ? "auto"
            : AnthologyLanguages.Get(sourceLanguage).TranslationCode;
        var payload = new Dictionary<string, object?>
        {
            ["q"] = texts,
            ["source"] = source,
            ["target"] = target.TranslationCode,
            ["format"] = "text",
        };
        if (!string.IsNullOrWhiteSpace(options.ApiKey))
        {
            payload["api_key"] = options.ApiKey.Trim();
        }

        using var response = await _httpClient.PostAsJsonAsync(endpoint, payload, ManifestJson.Options, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var details = ReadError(raw);
            throw new InvalidOperationException($"Сервис перевода вернул {(int)response.StatusCode}: {details}");
        }

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement;
        var translated = ReadTranslations(root, texts.Count);
        var detected = root.TryGetProperty("detectedLanguage", out var detectedNode)
            && detectedNode.ValueKind == JsonValueKind.Object
            && detectedNode.TryGetProperty("language", out var languageNode)
                ? languageNode.GetString() ?? sourceLanguage
                : sourceLanguage;
        return new TranslationBatchResult(translated, AnthologyLanguages.Normalize(detected), target.Code);
    }

    public async Task<bool> CheckAsync(TranslationServiceOptions options, CancellationToken cancellationToken = default)
    {
        try
        {
            var endpoint = new Uri(GetBaseUri(options.BaseUrl), "languages");
            using var response = await _httpClient.GetAsync(endpoint, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (Exception exception) when (exception is HttpRequestException or UriFormatException or OperationCanceledException)
        {
            return false;
        }
    }

    private static string[] ReadTranslations(JsonElement root, int expectedCount)
    {
        if (!root.TryGetProperty("translatedText", out var translatedNode))
        {
            throw new InvalidDataException("Сервис перевода не вернул translatedText.");
        }

        if (translatedNode.ValueKind == JsonValueKind.String)
        {
            if (expectedCount != 1)
            {
                throw new InvalidDataException("Сервис перевода вернул один текст вместо массива.");
            }
            return [translatedNode.GetString() ?? string.Empty];
        }

        if (translatedNode.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Некорректный формат translatedText.");
        }

        var result = translatedNode.EnumerateArray().Select(item => item.GetString() ?? string.Empty).ToArray();
        if (result.Length != expectedCount)
        {
            throw new InvalidDataException("Сервис перевода вернул неполный массив результатов.");
        }
        return result;
    }

    private static string ReadError(string raw)
    {
        try
        {
            using var document = JsonDocument.Parse(raw);
            return document.RootElement.TryGetProperty("error", out var error)
                ? error.GetString() ?? raw
                : raw;
        }
        catch (JsonException)
        {
            return raw.Length <= 500 ? raw : raw[..500];
        }
    }

    private static Uri GetEndpoint(string baseUrl) => new(GetBaseUri(baseUrl), "translate");

    private static Uri GetBaseUri(string baseUrl)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)))
        {
            throw new InvalidOperationException("URL перевода должен использовать HTTPS или локальный HTTP.");
        }
        return new Uri(uri.AbsoluteUri.EndsWith('/') ? uri.AbsoluteUri : uri.AbsoluteUri + "/");
    }

    public void Dispose()
    {
        if (_ownsClient) _httpClient.Dispose();
    }
}
