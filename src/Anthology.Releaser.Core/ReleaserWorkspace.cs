using Anthology.Contracts;

namespace Anthology.Releaser.Core;

public sealed class ReleaserWorkspace
{
    public int SchemaVersion { get; set; } = 11;

    public int Revision { get; set; }

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public string UpdatedBy { get; set; } = string.Empty;

    public string Version { get; set; } = "2.1.131";

    public string Channel { get; set; } = "next";

    /// <summary>
    /// Optional version-independent subdirectory for the current launcher channel.
    /// When set, manifest.json and history.json are published below this directory,
    /// while immutable version artifacts remain directly below {version}. The root
    /// manifest.json is then reserved for the schema 4 launcher bootstrap.
    /// </summary>
    public string StableChannelDirectory { get; set; } = string.Empty;

    public List<ReleaseMirrorSet> Mirrors { get; set; } =
    [
        new() { Provider = "github", Priority = 10 },
        new() { Provider = "yandex-disk", Priority = 20 },
        new() { Provider = "google-drive", Priority = 30 },
        new() { Provider = "http", Priority = 40 },
    ];

    public List<ContentDraft> Content { get; set; } = [];

    public List<SocialLinkDraft> SocialLinks { get; set; } = SocialLinkDraft.CreateDefaults();

    public List<ProjectPersonDraft> ProjectPeople { get; set; } = [];

    public List<LiveStreamDraft> LiveStreams { get; set; } = [];

    public ReleaseChangelogDraft Changelog { get; set; } = new();
}

public sealed class ReleaseChangelogDraft
{
    public string Title { get; set; } = "Изменения обновления";
    public string Summary { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public string Warnings { get; set; } = string.Empty;
    public string SourceLanguage { get; set; } = "auto";
    public Dictionary<string, ReleaseChangelogTranslationDraft> Translations { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public ReleaseChangelogTranslationDraft Translation(string language)
    {
        var normalized = AnthologyLanguages.Normalize(language);
        if (!Translations.TryGetValue(normalized, out var translation))
        {
            translation = new ReleaseChangelogTranslationDraft();
            Translations[normalized] = translation;
        }
        return translation;
    }
}

public sealed class ReleaseChangelogTranslationDraft
{
    public string Title { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public string Warnings { get; set; } = string.Empty;
}

public sealed class ProjectPersonDraft
{
    public string Id { get; set; } = $"person-{Guid.NewGuid():N}";
    public string Name { get; set; } = "Новый участник";
    public string Role { get; set; } = "Друг проекта";
    public string Description { get; set; } = string.Empty;
    public string ImageUrl { get; set; } = string.Empty;
    public string SourceLanguage { get; set; } = "auto";
    public Dictionary<string, ProjectPersonTranslationDraft> Translations { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<SocialLinkDraft> Links { get; set; } = SocialLinkDraft.CreateAuthorDefaults();
    public int Order { get; set; } = 100;
    public bool IsVisible { get; set; } = true;

    public ProjectPersonTranslationDraft Translation(string language)
    {
        var normalized = AnthologyLanguages.Normalize(language);
        if (!Translations.TryGetValue(normalized, out var translation))
        {
            translation = new ProjectPersonTranslationDraft();
            Translations[normalized] = translation;
        }
        return translation;
    }
}

public sealed class ProjectPersonTranslationDraft
{
    public string Name { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
}

public sealed class LiveStreamDraft
{
    public string Id { get; set; } = $"stream-{Guid.NewGuid():N}";
    public string Title { get; set; } = "Новая трансляция";
    public string Subtitle { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string SourceLanguage { get; set; } = "auto";
    public Dictionary<string, LiveStreamTranslationDraft> Translations { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public int Order { get; set; } = 100;
    public bool IsVisible { get; set; } = true;

    public LiveStreamTranslationDraft Translation(string language)
    {
        var normalized = AnthologyLanguages.Normalize(language);
        if (!Translations.TryGetValue(normalized, out var translation))
        {
            translation = new LiveStreamTranslationDraft();
            Translations[normalized] = translation;
        }
        return translation;
    }
}

public sealed class LiveStreamTranslationDraft
{
    public string Title { get; set; } = string.Empty;
    public string Subtitle { get; set; } = string.Empty;
}

public sealed class SocialLinkDraft
{
    public string Id { get; set; } = "youtube";

    public string Title { get; set; } = "YouTube";

    public string Subtitle { get; set; } = string.Empty;

    public string Url { get; set; } = string.Empty;

    public int Order { get; set; }

    public bool IsVisible { get; set; } = true;

    public static List<SocialLinkDraft> CreateDefaults() =>
    [
        new()
        {
            Id = "youtube",
            Title = "YouTube",
            Subtitle = "Самаэль Морнингстар",
            Url = "https://www.youtube.com/@Samael-w3p",
            Order = 10,
        },
        new()
        {
            Id = "vk",
            Title = "VK Видео",
            Subtitle = "Трансляции и записи сообщества",
            Url = "https://live.vkvideo.ru/sys_live_prime",
            Order = 20,
        },
        new()
        {
            Id = "discord",
            Title = "Discord",
            Subtitle = "Сервер Anthology и поддержка",
            Url = "https://discord.gg/uYS8JUz7J",
            Order = 30,
        },
        new()
        {
            Id = "moddb",
            Title = "ModDB",
            Subtitle = "Страница проекта и публикации",
            Url = "https://www.moddb.com/mods/anthology",
            Order = 40,
        },
        new()
        {
            Id = "telegram",
            Title = "Telegram",
            Subtitle = "Канал Anomaly Anthology",
            Url = "https://t.me/anomalyanthology",
            Order = 50,
        },
    ];

    public static List<SocialLinkDraft> CreateAuthorDefaults() =>
    [
        CreateAuthor("youtube", "YouTube", 10),
        CreateAuthor("vk", "VK Видео", 20),
        CreateAuthor("discord", "Discord", 30),
        CreateAuthor("moddb", "ModDB", 40),
        CreateAuthor("telegram", "Telegram", 50),
        CreateAuthor("github", "GitHub", 60),
        CreateAuthor("twitch", "Twitch", 70),
    ];

    private static SocialLinkDraft CreateAuthor(string id, string title, int order) => new()
    {
        Id = id,
        Title = title,
        Subtitle = string.Empty,
        Url = string.Empty,
        Order = order,
        IsVisible = false,
    };
}

public sealed class ReleaseMirrorSet
{
    public string Id { get; set; } = $"source-{Guid.NewGuid():N}";

    public string Provider { get; set; } = "http";

    public string GameUrl { get; set; } = string.Empty;

    public string Mo2Url { get; set; } = string.Empty;

    /// <summary>
    /// Version-relative channel artifacts such as launcher, integrity, full
    /// archives, and quick-update ZIPs. This is intentionally separate from
    /// GameUrl/Mo2Url because those fields may be loose-file {path} templates.
    /// </summary>
    public string ArtifactUrl { get; set; } = string.Empty;

    public string ContentUrl { get; set; } = string.Empty;

    /// <summary>
    /// Stable public address of the latest signed manifest. Unlike package URLs,
    /// this address must not contain a version placeholder.
    /// </summary>
    public string ManifestUrl { get; set; } = string.Empty;

    public int Priority { get; set; } = 100;
}

public sealed class ContentDraft
{
    public string Id { get; set; } = $"material-{Guid.NewGuid():N}";

    public ContentKind Kind { get; set; } = ContentKind.News;

    public string Section { get; set; } = "general";

    public string Title { get; set; } = "Новый материал";

    public string Summary { get; set; } = string.Empty;

    public string Body { get; set; } = string.Empty;

    public DateTimeOffset? PublishedAt { get; set; }

    public string SourceLanguage { get; set; } = "auto";

    public Dictionary<string, ContentTranslationDraft> Translations { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public string TitleEn { get; set; } = string.Empty;

    public string SummaryEn { get; set; } = string.Empty;

    public string BodyEn { get; set; } = string.Empty;

    public string TitleDe { get; set; } = string.Empty;

    public string SummaryDe { get; set; } = string.Empty;

    public string BodyDe { get; set; } = string.Empty;

    public string Images { get; set; } = string.Empty;

    public string Videos { get; set; } = string.Empty;

    public List<SocialLinkDraft> AuthorLinks { get; set; } = SocialLinkDraft.CreateAuthorDefaults();

    public List<ContentBlockDraft> Blocks { get; set; } = [];

    public string DownloadFileName { get; set; } = string.Empty;

    public long DownloadSize { get; set; }

    public string DownloadSha256 { get; set; } = string.Empty;

    public string DownloadMirrors { get; set; } = string.Empty;

    public string InstallFolderName { get; set; } = string.Empty;

    public bool IsPublished { get; set; }

    public ContentTranslationDraft Translation(string language)
    {
        var normalized = AnthologyLanguages.Normalize(language);
        if (!Translations.TryGetValue(normalized, out var translation))
        {
            translation = new ContentTranslationDraft();
            Translations[normalized] = translation;
        }
        return translation;
    }
}

public sealed class ContentTranslationDraft
{
    public string Title { get; set; } = string.Empty;

    public string Summary { get; set; } = string.Empty;

    public string Body { get; set; } = string.Empty;
}

public sealed class ContentBlockDraft
{
    public string Id { get; set; } = $"block-{Guid.NewGuid():N}";

    public ContentBlockKind Kind { get; set; } = ContentBlockKind.Section;

    public string Title { get; set; } = "Новый заголовок";

    public string Body { get; set; } = string.Empty;

    public Dictionary<string, ContentBlockTranslationDraft> Translations { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public string TitleEn { get; set; } = string.Empty;

    public string BodyEn { get; set; } = string.Empty;

    public string TitleDe { get; set; } = string.Empty;

    public string BodyDe { get; set; } = string.Empty;

    public string Url { get; set; } = string.Empty;

    public ContentBlockTranslationDraft Translation(string language)
    {
        var normalized = AnthologyLanguages.Normalize(language);
        if (!Translations.TryGetValue(normalized, out var translation))
        {
            translation = new ContentBlockTranslationDraft();
            Translations[normalized] = translation;
        }
        return translation;
    }
}

public sealed class ContentBlockTranslationDraft
{
    public string Title { get; set; } = string.Empty;

    public string Body { get; set; } = string.Empty;
}

public sealed class ReleaserMachineSettings
{
    public string DeveloperName { get; set; } = Environment.UserName;

    public string GameSourceRoot { get; set; } = string.Empty;

    public string Mo2SourceRoot { get; set; } = string.Empty;

    public string OutputRoot { get; set; } = string.Empty;

    public string PrivateKeyPath { get; set; } = string.Empty;

    public string PublicKeyPath { get; set; } = string.Empty;

    public string KeyId { get; set; } = "anthology-production-01";

    public string SharedWorkspaceRoot { get; set; } = string.Empty;

    // Repack work files are written under a unique child of this directory and
    // removed after success, cancellation, or failure. Keep it on a roomy drive.
    public string RepackTemporaryRoot { get; set; } = string.Empty;

    public string RepackOutputRoot { get; set; } = string.Empty;

    public string RepackProjectName { get; set; } = "ANTHOLOGY";

    public string SevenZipPath { get; set; } = string.Empty;

    public string InnoSetupCompilerPath { get; set; } = string.Empty;

    public string InstallerTemplateRoot { get; set; } = string.Empty;

    public bool RepackIncludeMo2 { get; set; } = true;

    public bool RepackOverwriteExisting { get; set; }

    // Google Drive is published directly through rclone. These values are local
    // machine configuration and never enter the shared release workspace.
    public string GoogleDriveRclonePath { get; set; } = string.Empty;

    public string GoogleDriveRcloneConfigPath { get; set; } = string.Empty;

    public string GoogleDriveRemoteName { get; set; } = string.Empty;

    public string GoogleDriveProjectPath { get; set; } = "ANTHOLOGY";

    public string GoogleDriveGamePath { get; set; } = string.Empty;

    public string GoogleDriveMo2Path { get; set; } = string.Empty;

    public string GoogleDriveReleasePath { get; set; } = "AnthologyUpdateChannel";

    public string GoogleDriveManifestPath { get; set; } = "AnthologyUpdateChannel/manifest.json";

    // Navigation/help URL only. It is deliberately never used as a package or
    // manifest mirror because /drive/home is an authenticated HTML application.
    public string GoogleDriveAccountUrl { get; set; } = "https://drive.google.com/drive/home";

    public string GoogleDriveProjectPublicUrl { get; set; } = string.Empty;

    public int GoogleDriveMirrorPriority { get; set; } = 30;

    public bool AutoSync { get; set; }

    public int AutoSyncSeconds { get; set; } = 60;

    public string LastSyncedHash { get; set; } = string.Empty;

    public string TranslationApiUrl { get; set; } = "http://127.0.0.1:5000";

    public string TranslationApiKey { get; set; } = string.Empty;

    public string CommunityApiUrl { get; set; } = "http://127.0.0.1:5249";

    public string CommunityDeveloperToken { get; set; } = string.Empty;

    // Local paths never enter the shared workspace.
    public Dictionary<string, string> ContentArchivePaths { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    // Image sources stay local to this developer machine. The signed catalog only receives
    // HTTPS URLs after the files have been copied into every configured publication root.
    public Dictionary<string, List<string>> ContentImagePaths { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    // Video sources also stay local. During publication they are copied to every configured
    // publication root, while the signed catalog receives only their public HTTPS URLs.
    public Dictionary<string, List<string>> ContentVideoPaths { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public List<QuickReleaseFileDraft> QuickReleaseFiles { get; set; } = [];

    public List<QuickReleaseFolderDraft> QuickReleaseFolders { get; set; } = [];

    public List<QuickDeleteFileDraft> QuickDeleteFiles { get; set; } = [];

    public List<QuickDeleteFolderDraft> QuickDeleteFolders { get; set; } = [];

    public Dictionary<string, string> PublicationRoots { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class QuickReleaseFileDraft
{
    public string Id { get; set; } = $"file-{Guid.NewGuid():N}";

    public string SourcePath { get; set; } = string.Empty;

    public string InstallRoot { get; set; } = "game";

    public string RelativePath { get; set; } = string.Empty;
}

public sealed class QuickReleaseFolderDraft
{
    public string Id { get; set; } = $"folder-{Guid.NewGuid():N}";

    public string SourcePath { get; set; } = string.Empty;

    public string InstallRoot { get; set; } = "game";

    // Empty means the selected folder contents are placed directly in the install root.
    public string RelativePath { get; set; } = string.Empty;
}

public sealed class QuickDeleteFileDraft
{
    public string Id { get; set; } = $"delete-{Guid.NewGuid():N}";

    public string InstallRoot { get; set; } = "game";

    public string RelativePath { get; set; } = string.Empty;
}

public sealed class QuickDeleteFolderDraft
{
    public string Id { get; set; } = $"delete-folder-{Guid.NewGuid():N}";

    public string InstallRoot { get; set; } = "game";

    public string RelativePath { get; set; } = string.Empty;
}

public enum UnifiedReleaseDeliveryMode
{
    Archive,
    LooseFiles,
}

public sealed record LooseFileMirrorOverride(
    string PackageId,
    string Path,
    IReadOnlyList<MirrorManifest> Mirrors);

public sealed record UnifiedReleaseRequest(
    ReleaserWorkspace Workspace,
    ReleaserMachineSettings Machine,
    UnifiedReleaseDeliveryMode DeliveryMode = UnifiedReleaseDeliveryMode.Archive,
    IReadOnlyList<LooseFileMirrorOverride>? LooseFileMirrors = null,
    string? MinimumLauncherVersion = null);

public sealed record UnifiedReleaseResult(
    string Version,
    string ManifestPath,
    IReadOnlyList<string> Artifacts,
    int Files,
    long Bytes,
    int ContentItems,
    // When present, publication is restricted to these generated files. Loose
    // releases use this allow-list so stale archives in the same version folder
    // cannot be copied to mirrors accidentally.
    IReadOnlyList<string>? PublicationFiles = null);

public sealed record PublicationResult(
    int Targets,
    int Files,
    long Bytes,
    IReadOnlyList<string> Destinations);

public sealed record AddonPublicationResult(
    string AddonId,
    string ArtifactPath,
    string ManifestPath,
    PublicationResult Publication);

public sealed record QuickReleaseResult(
    string ManifestPath,
    int AddedFiles,
    int DeletedFiles,
    int AddedFolders,
    int DeletedFolders,
    IReadOnlyList<string> Artifacts,
    PublicationResult Publication);

/// <summary>
/// Result of publishing the editable workspace content and an optional set of
/// quick file changes without rebuilding the full game or MO2 distributions.
/// </summary>
public sealed record ContentBundlePublicationResult(
    string Version,
    string ManifestPath,
    string ContentPath,
    int ContentItems,
    IReadOnlyList<string> PublishedAddonIds,
    IReadOnlyList<string> PreservedAddonIds,
    int AddedFiles,
    int DeletedFiles,
    int AddedFolders,
    int DeletedFolders,
    IReadOnlyList<string> Artifacts,
    /// <summary>
    /// Version-relative files in publication order. Referenced payloads always
    /// precede content.json and manifest.json.
    /// </summary>
    IReadOnlyList<string> PublicationFiles,
    PublicationResult Publication);

public sealed record LauncherPublicationResult(
    string LauncherVersion,
    string ArtifactPath,
    string ManifestPath,
    int Files,
    PublicationResult Publication);

public enum WorkspaceSyncDirection
{
    None,
    Published,
    Received,
}

public sealed record WorkspaceSyncResult(
    WorkspaceSyncDirection Direction,
    ReleaserWorkspace Workspace,
    string Hash,
    string Message);
