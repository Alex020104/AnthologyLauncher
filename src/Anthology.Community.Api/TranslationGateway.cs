using Anthology.Contracts;
using Anthology.Update.Core;

namespace Anthology.Community.Api;

public sealed class TranslationGateway(HttpClient httpClient) : IDisposable
{
    private readonly LibreTranslationClient _client = new(httpClient);

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiUrl);

    private static string ApiUrl => Environment.GetEnvironmentVariable("ANTHOLOGY_TRANSLATION_API")?.Trim() ?? string.Empty;
    private static string ApiKey => Environment.GetEnvironmentVariable("ANTHOLOGY_TRANSLATION_API_KEY")?.Trim() ?? string.Empty;

    public async Task<TextTranslationResponse> TranslateAsync(
        TextTranslationRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException("Сервер автоперевода ещё не настроен.");
        }
        if (string.IsNullOrWhiteSpace(request.Text))
        {
            throw new ArgumentException("Нет текста для перевода.");
        }
        if (!AnthologyLanguages.IsSupported(request.TargetLanguage))
        {
            throw new ArgumentException($"Язык '{request.TargetLanguage}' не поддерживается.");
        }
        if (!string.Equals(request.SourceLanguage, "auto", StringComparison.OrdinalIgnoreCase)
            && !AnthologyLanguages.IsSupported(request.SourceLanguage))
        {
            throw new ArgumentException($"Исходный язык '{request.SourceLanguage}' не поддерживается.");
        }

        var original = request.Text.Trim();
        if (original.Length > 4_000)
        {
            throw new ArgumentException("Одно сообщение для перевода не должно превышать 4000 символов.");
        }

        var result = await _client.TranslateAsync(
            new TranslationServiceOptions(ApiUrl, ApiKey),
            [original],
            request.TargetLanguage,
            request.SourceLanguage,
            cancellationToken);
        return new TextTranslationResponse(
            original,
            result.Translations[0],
            result.DetectedLanguage,
            result.TargetLanguage);
    }

    public void Dispose() => _client.Dispose();
}
