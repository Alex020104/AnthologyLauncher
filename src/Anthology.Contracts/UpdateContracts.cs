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
    IReadOnlyList<string>? DeletedFiles = null,
    IReadOnlyList<string>? DeletedDirectories = null);

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
    IReadOnlyList<ContentDocument> Items,
    IReadOnlyList<SocialLink>? SocialLinks = null,
    IReadOnlyList<ProjectPerson>? ProjectPeople = null,
    IReadOnlyList<LiveStream>? LiveStreams = null,
    ReleaseChangelog? Changelog = null);

public sealed record ReleaseChangelog(
    string Title,
    string Summary,
    string Body,
    string Warnings,
    IReadOnlyDictionary<string, ReleaseChangelogTranslation>? Translations = null);

public sealed record ReleaseChangelogTranslation(
    string Title,
    string Summary,
    string Body,
    string Warnings);

public static class ReleaseChangelogLocalization
{
    public static ReleaseChangelogTranslation Resolve(ReleaseChangelog changelog, string? language)
    {
        ArgumentNullException.ThrowIfNull(changelog);
        var normalized = AnthologyLanguages.Normalize(language);
        var translation = changelog.Translations?
            .FirstOrDefault(pair => string.Equals(pair.Key, normalized, StringComparison.OrdinalIgnoreCase))
            .Value;
        return translation is null
            ? new ReleaseChangelogTranslation(changelog.Title, changelog.Summary, changelog.Body, changelog.Warnings)
            : new ReleaseChangelogTranslation(
                string.IsNullOrWhiteSpace(translation.Title) ? changelog.Title : translation.Title,
                string.IsNullOrWhiteSpace(translation.Summary) ? changelog.Summary : translation.Summary,
                string.IsNullOrWhiteSpace(translation.Body) ? changelog.Body : translation.Body,
                string.IsNullOrWhiteSpace(translation.Warnings) ? changelog.Warnings : translation.Warnings);
    }
}

public sealed record SocialLink(
    string Id,
    string Title,
    string Subtitle,
    string Url);

public sealed record ProjectPerson(
    string Id,
    string Name,
    string Role,
    string Description,
    string? ImageUrl,
    IReadOnlyList<SocialLink> Links,
    int Order = 100,
    IReadOnlyDictionary<string, ProjectPersonTranslation>? Translations = null);

public sealed record ProjectPersonTranslation(
    string Name,
    string Role,
    string Description);

public static class ProjectPersonLocalization
{
    public static ProjectPersonTranslation Resolve(ProjectPerson person, string? language)
    {
        ArgumentNullException.ThrowIfNull(person);
        var normalized = AnthologyLanguages.Normalize(language);
        var translation = person.Translations?
            .FirstOrDefault(pair => string.Equals(pair.Key, normalized, StringComparison.OrdinalIgnoreCase))
            .Value;
        return translation is null
            ? new ProjectPersonTranslation(person.Name, person.Role, person.Description)
            : new ProjectPersonTranslation(
                string.IsNullOrWhiteSpace(translation.Name) ? person.Name : translation.Name,
                string.IsNullOrWhiteSpace(translation.Role) ? person.Role : translation.Role,
                string.IsNullOrWhiteSpace(translation.Description) ? person.Description : translation.Description);
    }
}

public sealed record LiveStream(
    string Id,
    string Title,
    string Subtitle,
    string Url,
    int Order = 100,
    IReadOnlyDictionary<string, LiveStreamTranslation>? Translations = null);

public sealed record LiveStreamTranslation(
    string Title,
    string Subtitle);

public static class LiveStreamLocalization
{
    public static LiveStreamTranslation Resolve(LiveStream stream, string? language)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var normalized = AnthologyLanguages.Normalize(language);
        var translation = stream.Translations?
            .FirstOrDefault(pair => string.Equals(pair.Key, normalized, StringComparison.OrdinalIgnoreCase))
            .Value;
        return translation is null
            ? new LiveStreamTranslation(stream.Title, stream.Subtitle)
            : new LiveStreamTranslation(
                string.IsNullOrWhiteSpace(translation.Title) ? stream.Title : translation.Title,
                string.IsNullOrWhiteSpace(translation.Subtitle) ? stream.Subtitle : translation.Subtitle);
    }
}

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
    DateTimeOffset? PublishedAt = null,
    IReadOnlyList<SocialLink>? AuthorLinks = null);

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
    ProjectSupport,
}
