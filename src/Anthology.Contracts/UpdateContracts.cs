namespace Anthology.Contracts;

public sealed record SignedUpdateManifest(
    UpdateManifest Payload,
    ManifestSignature Signature);

public sealed record UpdateManifest(
    int SchemaVersion,
    string Channel,
    string Version,
    DateTimeOffset PublishedAt,
    string? MinimumLauncherVersion,
    IReadOnlyList<PackageManifest> Packages,
    ContentCatalog? Content = null);

public sealed record ManifestSignature(
    string Algorithm,
    string KeyId,
    string Value);

public sealed record PackageManifest(
    string Id,
    string DisplayName,
    string Version,
    PackageKind Kind,
    string InstallRoot,
    string ArchiveFormat,
    long Size,
    string Sha256,
    IReadOnlyList<MirrorManifest> Mirrors,
    IReadOnlyList<string> Files,
    PackageUpdateMode UpdateMode = PackageUpdateMode.Merge,
    bool PruneInstallRoot = false,
    IReadOnlyList<string>? PreservedPaths = null,
    IReadOnlyList<string>? DeletedFiles = null);

public sealed record MirrorManifest(
    string Provider,
    string Url,
    int Priority = 100,
    string? Region = null);

public enum PackageKind
{
    Launcher,
    Game,
    Modpack,
    Database,
    Engine,
    Mod,
    Tool,
}

public enum PackageUpdateMode
{
    Merge,
    ManagedExact,
}

public sealed record ContentCatalog(
    int SchemaVersion,
    string Version,
    DateTimeOffset PublishedAt,
    IReadOnlyList<ContentDocument> Items);

public sealed record ContentDocument(
    string Id,
    ContentKind Kind,
    string Section,
    string Title,
    string Summary,
    string Body,
    IReadOnlyList<string> Images,
    IReadOnlyList<ContentVideo> Videos,
    ContentDownload? Download = null,
    IReadOnlyDictionary<string, ContentTranslation>? Translations = null,
    IReadOnlyList<ContentBlock>? Blocks = null,
    DateTimeOffset? PublishedAt = null);

public sealed record ContentTranslation(
    string Title,
    string Summary,
    string Body);

public static class ContentLocalization
{
    public static ContentTranslation Resolve(ContentDocument document, string? language)
    {
        ArgumentNullException.ThrowIfNull(document);
        var normalized = AnthologyLanguages.Normalize(language);
        if (document.Translations is not null)
        {
            if (document.Translations.TryGetValue(normalized, out var translation))
            {
                return translation;
            }

            translation = document.Translations
                .FirstOrDefault(pair => string.Equals(pair.Key, normalized, StringComparison.OrdinalIgnoreCase))
                .Value;
            if (translation is not null)
            {
                return translation;
            }
        }

        return new ContentTranslation(document.Title, document.Summary, document.Body);
    }
}

public sealed record ContentVideo(
    string Title,
    string Url);

public sealed record ContentBlock(
    string Id,
    ContentBlockKind Kind,
    string Title,
    string Body,
    string? Url = null,
    IReadOnlyDictionary<string, ContentBlockTranslation>? Translations = null);

public sealed record ContentBlockTranslation(
    string Title,
    string Body);

public static class ContentBlockLocalization
{
    public static ContentBlockTranslation Resolve(ContentBlock block, string? language)
    {
        ArgumentNullException.ThrowIfNull(block);
        var normalized = AnthologyLanguages.Normalize(language);
        if (block.Translations is not null)
        {
            var translation = block.Translations
                .FirstOrDefault(pair => string.Equals(pair.Key, normalized, StringComparison.OrdinalIgnoreCase))
                .Value;
            if (translation is not null)
            {
                return new ContentBlockTranslation(
                    string.IsNullOrWhiteSpace(translation.Title) ? block.Title : translation.Title,
                    string.IsNullOrWhiteSpace(translation.Body) ? block.Body : translation.Body);
            }
        }

        return new ContentBlockTranslation(block.Title, block.Body);
    }
}

public enum ContentBlockKind
{
    Section,
    Image,
    Link,
    Article,
}

public sealed record ContentDownload(
    string FileName,
    long Size,
    string Sha256,
    IReadOnlyList<MirrorManifest> Mirrors,
    string? InstallName = null,
    bool ReplaceExisting = false);

public enum ContentKind
{
    Mod,
    News,
    Information,
}
