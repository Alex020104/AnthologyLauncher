using System.Net;
using System.Text;
using System.Text.Json;
using Anthology.Contracts;

namespace Anthology.Update.Core.Tests;

public sealed class TranslationTests
{
    [Fact]
    public void SupportedLanguageCatalogContainsAllLauncherLanguages()
    {
        Assert.Equal(
            ["ru", "en", "de", "pl", "fr", "es", "zh", "ja"],
            AnthologyLanguages.All.Select(language => language.Code));
        Assert.Equal("zh", AnthologyLanguages.Normalize("zh-Hans"));
        Assert.Equal("zh", AnthologyLanguages.Get("zh").TranslationCode);
    }

    [Theory]
    [InlineData("Шура")]
    [InlineData("alex020104")]
    [InlineData("RATNIY")]
    public void LegacyDeveloperNamesKeepTheirRole(string name)
    {
        Assert.True(AnthologyRoles.IsDeveloper(name));
        Assert.Equal("admin", AnthologyRoles.Resolve(name));
    }

    [Fact]
    public void ContentLocalizationCanOverrideRussianSourceText()
    {
        var document = new ContentDocument(
            "multilingual",
            ContentKind.News,
            "general",
            "Texto original",
            "Resumen",
            "Cuerpo",
            [],
            [],
            Translations: new Dictionary<string, ContentTranslation>
            {
                ["ru"] = new("Русский заголовок", "Описание", "Текст"),
                ["ja"] = new("日本語の見出し", "概要", "本文"),
            });

        Assert.Equal("Русский заголовок", ContentLocalization.Resolve(document, "ru").Title);
        Assert.Equal("日本語の見出し", ContentLocalization.Resolve(document, "ja").Title);
    }

    [Fact]
    public async Task LibreClientUsesAutoDetectionAndProviderLanguageCode()
    {
        var handler = new RecordingHandler();
        using var httpClient = new HttpClient(handler);
        using var client = new LibreTranslationClient(httpClient);

        var result = await client.TranslateAsync(
            new TranslationServiceOptions("https://translate.example", "secret"),
            ["Обновление готово", "Исправлен вылет"],
            "zh",
            "auto");

        Assert.Equal(["更新已准备就绪", "崩溃已修复"], result.Translations);
        Assert.Equal("ru", result.DetectedLanguage);
        Assert.Equal("zh", result.TargetLanguage);
        Assert.Equal("https://translate.example/translate", handler.RequestUri?.AbsoluteUri);
        using var payload = JsonDocument.Parse(handler.RequestBody!);
        Assert.Equal("auto", payload.RootElement.GetProperty("source").GetString());
        Assert.Equal("zh", payload.RootElement.GetProperty("target").GetString());
        Assert.Equal("secret", payload.RootElement.GetProperty("api_key").GetString());
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }
        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            RequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"translatedText\":[\"更新已准备就绪\",\"崩溃已修复\"],\"detectedLanguage\":{\"language\":\"ru\",\"confidence\":99}}",
                    Encoding.UTF8,
                    "application/json"),
            };
        }
    }
}
